using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutumnOS.NativeHost.Windows;
using AutumnOS.Runtime;
using Microsoft.Win32.SafeHandles;

// No-argument execution is inert. Only fixed cases, exact QA02 input bytes,
// fresh synthetic data and newly created native owners can be exercised.
if (args.Length == 0) return 0;
if (args.Length != 7 || args[0] != "--execute" || args[2] != "--prepared" || args[4] != "--workspace" || args[6] != "--reviewed-qa02-owned-only") return 64;
if (args[1] is not ("normal-close" or "browser-failure" or "root-stall" or "pipe-close")) return 64;
return await Matrix.RunAsync(args[1], args[3], args[5]);

internal static class Matrix
{
    private const string Build = "T06-20261112-release-v1-qa02";
    private const string Source = "sha256:51f74ba89506bcef946eb9341d0f2c44a5c5bb0aa4ab3f1e21a412dd4ed7aa0b";
    private const string TableHash = "c9c15fec51c37d1e2b1de41348cc9991209b12ef5c857038f14a0bb138bcc596";
    private const string DriverRevision = "qa02-negative-02-kernelbase";
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static async Task<int> RunAsync(string mode, string preparedArgument, string workspaceArgument)
    {
        string workspace = Plain(workspaceArgument), prepared = Plain(preparedArgument);
        Need(NativeLibrary.TryLoad("KernelBase.dll", out IntPtr handlesLibrary), "HANDLE_IDENTITY_LIBRARY_UNAVAILABLE");
        try { Need(NativeLibrary.TryGetExport(handlesLibrary, "CompareObjectHandles", out _), "HANDLE_IDENTITY_EXPORT_UNAVAILABLE"); }
        finally { NativeLibrary.Free(handlesLibrary); }
        string expected = Path.Combine(workspace, "artifacts", "packages", Build, "AutumnOS-1.0.0-rc.1-win-x64-development");
        if (prepared != expected) throw new InvalidDataException("EXACT_QA02_PREPARED_DIRECTORY_REQUIRED");
        string tablePath = Path.Combine(workspace, "artifacts", "builds", Build, "native-payload-table.json");
        if (Hash(tablePath) != TableHash) throw new InvalidDataException("QA02_FROZEN_TABLE_HASH_MISMATCH");
        VerifyAssembly(typeof(NativeProcessOwner).Assembly, prepared);
        VerifyAssembly(typeof(RuntimeIsolationPolicy).Assembly, prepared);
        using JsonDocument table = JsonDocument.Parse(File.ReadAllBytes(tablePath));
        if (table.RootElement.GetProperty("buildId").GetString() != Build || table.RootElement.GetProperty("sourceSnapshotId").GetString() != Source)
            throw new InvalidDataException("QA02_SOURCE_BINDING_MISMATCH");
        var host = Input(Path.Combine(prepared, "NativeHost"), table.RootElement.GetProperty("host"));
        var runtime = Input(Path.Combine(prepared, "WebView2Runtime"), table.RootElement.GetProperty("runtime"));
        string run = Path.Combine(workspace, "artifacts", "native-negative-20261112", "runs", mode + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        string fixture = Path.Combine(run, "synthetic-package"); Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "index.html"), "<!doctype html><html><head><meta charset=\"utf-8\"><script src=\"app.js\" defer></script></head><body>AutumnOS owned negative fixture</body></html>", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture, "app.js"), "'use strict'; window.chrome.webview.addEventListener('message', e => { if (e.data && e.data.marker === 'owned-qa02-post') window.chrome.webview.postMessage({kind:'owned-page-ack',marker:e.data.marker}); });", new UTF8Encoding(false));
        NativeHostFileBinding[] packageFiles = Directory.GetFiles(fixture).Select(path => new NativeHostFileBinding(Path.GetFileName(path), new FileInfo(path).Length, Hash(path))).ToArray();
        string scratch = Path.Combine(workspace, "artifacts", "neg-qa02", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        var binding = new NativeHostBinding(Guid.NewGuid(), "synthetic-native-negative", 1, HashText("owned-qa02-fixture"), Guid.NewGuid(), 1, Guid.NewGuid(), 1, 1);
        RuntimeHostStaging? stage = null; NativeProcessOwner? owner = null; OriginalSet? originals = null;
        bool passed = false, baseline = false, receipt = false, fence = false, pageAck = false, injected = false, rescue = false;
        bool normalAck = false, nativeDeadlineObserved = false, fenceUnconfirmed = false, stageRemoved = false;
        string? errorCode = null, safeErrorCode = null; int? errorHresult = null;
        var clock = Stopwatch.StartNew();
        try
        {
            Progress(mode, "creating_fresh_owned_stage");
            stage = await RuntimeHostStaging.CreateAsync(scratch, binding, "index.html", host, runtime, new(fixture, packageFiles));
            owner = NativeProcessOwner.Create(stage.ManifestPath, binding);
            await owner.StartAsync();
            originals = Capture(owner);
            string origin = "https://" + Guid.NewGuid().ToString("N") + ".autumn.invalid/";
            var challenge = RuntimeIsolationPolicy.CreateParserGateChallenge(binding.InstanceId, origin, "index.html");
            var proof = await owner.ExchangeAsync(NativeViewKind.PrepareDocument, JsonSerializer.SerializeToUtf8Bytes(new { origin, nonce = challenge.Isolation.Nonce }));
            Need(proof.Kind == NativeViewKind.Proof, "NATIVE_PROOF_REQUIRED");
            using (var document = JsonDocument.Parse(proof.Payload))
                Need(RuntimeIsolationPolicy.ValidateNativeResult(challenge.Isolation, document.RootElement.GetProperty("nativeResult").GetString()) is not null &&
                    RuntimeIsolationPolicy.ValidateParserGateBinding(challenge, document.RootElement.GetProperty("bindingResult").GetString()), "NATIVE_PROOF_REJECTED");
            Need((await owner.ExchangeAsync(NativeViewKind.ReleaseDocument, [])).Kind == NativeViewKind.DocumentReady, "DOCUMENT_READY_REQUIRED");
            var first = await owner.ExchangeAsync(NativeViewKind.Poll, []);
            Need(first.Kind == NativeViewKind.Frame && first.Payload.Length <= 1024 * 1024, "BASELINE_FRAME_REQUIRED");
            using (var png = new MemoryStream(first.Payload, false))
            using (var image = Image.FromStream(png, true, true)) Need(image.Width == 800 && image.Height == 600, "BASELINE_DECODED_FRAME_SIZE_REQUIRED");
            baseline = true;
            byte[] postBytes = Encoding.UTF8.GetBytes("{\"marker\":\"owned-qa02-post\"}");
            var post = await owner.ExchangeAsync(NativeViewKind.Delivery, Inner(binding, stage.Nonce, 1, false, postBytes));
            CheckReceipt(post, binding, stage.Nonce, 1, false, HashBytes(postBytes)); receipt = true;
            for (int poll = 0; poll < 8 && !pageAck; poll++)
            {
                var packet = await owner.ExchangeAsync(NativeViewKind.Poll, []);
                if (packet.Kind == NativeViewKind.SdkRequest)
                { using var message = JsonDocument.Parse(packet.Payload); pageAck = message.RootElement.GetProperty("kind").GetString() == "owned-page-ack" && message.RootElement.GetProperty("marker").GetString() == "owned-qa02-post"; }
            }
            Need(pageAck, "ACTUAL_PAGE_ACK_REQUIRED");
            var barrier = await owner.ExchangeAsync(NativeViewKind.Delivery, Inner(binding, stage.Nonce, 2, true, []));
            CheckReceipt(barrier, binding, stage.Nonce, 2, true, null); fence = true;
            Progress(mode, "native_frame_post_page_ack_and_fence_confirmed");
            switch (mode)
            {
                case "normal-close":
                    normalAck = (await owner.ExchangeAsync(NativeViewKind.Close, [])).Kind == NativeViewKind.Closed;
                    Need(normalAck, "NORMAL_CLOSE_ACK_REQUIRED"); break;
                case "browser-failure":
                    using (var process = Upgrade(originals.Browser, 0x101001)) { Need(TerminateProcess(process, 0xe0a70012), "OWNED_BROWSER_TERMINATION_REQUEST_FAILED"); injected = true; }
                    break;
                case "root-stall":
                    using (var process = Upgrade(originals.Root, 0x101800))
                    {
                        int status = NtSuspendProcess(process); Need(status == 0, "OWNED_ROOT_SUSPEND_NOT_SUPPORTED_OR_REJECTED"); injected = true;
                        byte[] blocked = Inner(binding with { ChannelGeneration = 2 }, stage.Nonce, 3, false, postBytes);
                        Task<NativeViewPacket> delivery = owner.ExchangeAsync(NativeViewKind.Delivery, blocked);
                        Task<NativeViewPacket> queuedFence = owner.ExchangeAsync(NativeViewKind.Delivery, Inner(binding with { ChannelGeneration = 2 }, stage.Nonce, 4, true, []));
                        try { _ = await delivery.WaitAsync(TimeSpan.FromSeconds(40)); throw new InvalidDataException("STALLED_DELIVERY_RETURNED_A_REPLY"); }
                        catch (OperationCanceledException) { nativeDeadlineObserved = true; }
                        NativeViewPacket? fenceReply = null;
                        try { fenceReply = await queuedFence.WaitAsync(TimeSpan.FromSeconds(40)); }
                        catch (Exception fault) when (fault is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException) { fenceUnconfirmed = true; }
                        Need(nativeDeadlineObserved && fenceUnconfirmed && fenceReply is null, "STALLED_EXCHANGE_WAS_NOT_UNCONFIRMED");
                    }
                    break;
                case "pipe-close": await owner.RequestCloseAsync(); injected = true; break;
            }
            await owner.OriginalGroupRetired.WaitAsync(TimeSpan.FromSeconds(60));
            Need(owner.ResourcesRetired && owner.Snapshot.OriginalProcessExited && owner.Snapshot.OriginalThreadExited &&
                owner.Snapshot.OriginalJobEmpty && owner.Snapshot.AllObservedOriginalProcessHandlesSignaled, "PRODUCT_ORIGINAL_RETIREMENT_PREDICATE_REQUIRED");
            originals.RequireRetired();
            if (mode == "normal-close") Need(!owner.Snapshot.OwnedJobRecoveryRequested && !owner.Snapshot.OriginalStationRecoveryRequested,
                "NORMAL_CLOSE_USED_FORCED_RECOVERY");
            if (mode == "root-stall") Need(owner.Snapshot.OwnedJobRecoveryRequested, "PRODUCT_JOB_RECOVERY_NOT_OBSERVED");
            passed = true;
        }
        catch (Exception error)
        {
            if (error is RuntimeHostStagingRollbackException rollback) stage = rollback.PendingOwner;
            errorCode = error.GetType().Name; errorHresult = error.HResult;
            safeErrorCode = error is HarnessRejectException rejection ? rejection.SafeCode : null;
            Progress(mode, "failed_or_not_confirmed");
        }
        finally
        {
            if (owner is not null && !owner.ResourcesRetired)
            {
                await owner.RequestCloseAsync();
                try { await owner.OriginalGroupRetired.WaitAsync(TimeSpan.FromSeconds(35)); }
                catch (TimeoutException)
                {
                    // Rescue cannot turn this case into a product pass.
                    passed = false; rescue = true;
                    if (originals is not null && !TerminateJobObject(originals.Job, 0xe0a70013))
                    {
                        File.WriteAllText(Path.Combine(run, "PENDING-OWNER.json"), JsonSerializer.Serialize(new { status = "failed_rescue_request_and_cleanup_pending", mode, build_id = Build, source_snapshot_id = Source, snapshot = owner.Snapshot }));
                        await owner.OriginalGroupRetired;
                    }
                    try { await owner.OriginalGroupRetired.WaitAsync(TimeSpan.FromSeconds(35)); }
                    catch (TimeoutException)
                    {
                        File.WriteAllText(Path.Combine(run, "PENDING-OWNER.json"), JsonSerializer.Serialize(new { status = "failed_cleanup_pending", mode, build_id = Build, source_snapshot_id = Source, snapshot = owner.Snapshot }));
                        // Keep the real owner/pins alive; no successful result,
                        // next case or stage disposal follows an unproven exit.
                        await owner.OriginalGroupRetired;
                    }
                }
            }
            if (owner?.ResourcesRetired == true || owner is null)
            {
                stage?.Dispose(); stageRemoved = stage is null || !File.Exists(stage.ManifestPath);
            }
            originals?.Dispose();
        }
        var result = new { status = passed && stageRemoved && !rescue ? "passed" : "failed", mode,
            build_id = Build, source_snapshot_id = Source, table_sha256 = TableHash, elapsed_seconds = clock.Elapsed.TotalSeconds,
            driver_revision = DriverRevision, harness_assembly_sha256 = Hash(typeof(Matrix).Assembly.Location),
            baseline_frame = baseline, actual_native_post_receipt = receipt, actual_page_message_ack = pageAck, actual_native_fence_receipt = fence,
            fault_injected_only_into_new_owned_instance = injected, normal_close_ack = normalAck, actual_io_deadline_observed = nativeDeadlineObserved,
            stalled_fence_unconfirmed = fenceUnconfirmed, snapshot = owner?.Snapshot, original_duplicate_handles_and_job_empty_confirmed = passed,
            owned_staging_removed = stageRemoved, harness_rescue_requested = rescue, error_type = errorCode, error_hresult = errorHresult, safe_error_code = safeErrorCode,
            observed_os = Environment.OSVersion.VersionString, scope = "qa02_frozen_native_components_not_Shell_UI_or_hardware_matrix",
            shell_runtime_maintenance_leases = "not_observed_by_this_harness", permissions_commit = "not_observed_by_this_harness", full_isolation = "not_completed" };
        string report = Path.Combine(run, "result.json");
        using (var stream = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) JsonSerializer.Serialize(stream, result, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(report); return passed && stageRemoved && !rescue ? 0 : 1;
    }

    private static RuntimeHostStagingInput Input(string path, JsonElement rows)
    {
        var files = rows.EnumerateArray().Select(row => new NativeHostFileBinding(row.GetProperty("path").GetString()!, row.GetProperty("bytes").GetInt64(), row.GetProperty("sha256").GetString()!)).ToArray();
        foreach (var file in files) Need(new FileInfo(Path.Combine(path, file.Path)).Length == file.Bytes && Hash(Path.Combine(path, file.Path)) == file.Sha256, "FROZEN_PAYLOAD_INPUT_MISMATCH");
        return new(Plain(path), files);
    }
    private static void VerifyAssembly(Assembly assembly, string prepared)
    {
        var values = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value);
        Need(values["BuildId"] == Build && values["SourceSnapshotId"] == Source &&
            Hash(assembly.Location) == Hash(Path.Combine(prepared, Path.GetFileName(assembly.Location))), "FROZEN_COMPONENT_ASSEMBLY_MISMATCH");
    }
    private static OriginalSet Capture(NativeProcessOwner owner)
    {
        object gate = typeof(NativeProcessOwner).GetField("peerSync", Hidden)!.GetValue(owner)!;
        lock (gate)
        {
            owner.RequireOriginalPeer();
            Need(owner.Snapshot.OriginalQueryJobBound && !owner.Snapshot.OriginalProcessExited && !owner.Snapshot.OriginalThreadExited,
                "LIVE_BOUND_OS_OWNER_REQUIRED");
            object root = typeof(NativeProcessOwner).GetField("restrictedRoot", Hidden)!.GetValue(owner)
                ?? throw new HarnessRejectException("ORIGINAL_RESTRICTED_ROOT_NOT_BOUND");
            object browser = typeof(NativeProcessOwner).GetField("boundBrowser", Hidden)!.GetValue(owner)
                ?? throw new HarnessRejectException("ORIGINAL_BROWSER_NOT_BOUND");
            object job = typeof(NativeProcessOwner).GetField("witness", Hidden)!.GetValue(owner)
                ?? throw new HarnessRejectException("ORIGINAL_QUERY_JOB_NOT_BOUND");
            IntPtr Handle(object value) => (IntPtr)value.GetType().GetField("Handle", Hidden)!.GetValue(value)!;
            IntPtr jobHandle = (IntPtr)job.GetType().GetProperty("Handle", Hidden)!.GetValue(job)!;
            return new(Duplicate(Handle(root)), Duplicate(Handle(browser)), Duplicate(jobHandle));
        }
    }
    private static SafeProcessHandle Upgrade(SafeFileHandle original, uint rights)
    {
        uint pid = GetProcessId(original); Need(pid != 0 && WaitForSingleObject(original, 0) == 258, "LIVE_ORIGINAL_OBJECT_REQUIRED");
        SafeProcessHandle upgraded = OpenProcess(rights, false, pid);
        if (upgraded.IsInvalid || !CompareObjectHandles(original, upgraded)) { upgraded.Dispose(); throw new HarnessRejectException("ORIGINAL_KERNEL_OBJECT_IDENTITY_OR_FAULT_RIGHTS_REJECTED"); }
        return upgraded;
    }
    private sealed record OriginalSet(SafeFileHandle Root, SafeFileHandle Browser, SafeFileHandle Job) : IDisposable
    {
        internal void RequireRetired()
        {
            Need(WaitForSingleObject(Root, 0) == 0 && WaitForSingleObject(Browser, 0) == 0, "ORIGINAL_APP_PROCESS_HANDLES_NOT_SIGNALED");
            byte[] accounting = new byte[48], ids = new byte[8 + 4096 * 8];
            Need(QueryInformationJobObject(Job, 1, accounting, 48, out _) && QueryInformationJobObject(Job, 3, ids, (uint)ids.Length, out _) &&
                BinaryPrimitives.ReadUInt32LittleEndian(accounting.AsSpan(40)) == 0 && BinaryPrimitives.ReadUInt32LittleEndian(ids) == 0 && BinaryPrimitives.ReadUInt32LittleEndian(ids.AsSpan(4)) == 0,
                "ORIGINAL_JOB_EMPTY_NOT_OBSERVED");
        }
        public void Dispose() { Root.Dispose(); Browser.Dispose(); Job.Dispose(); }
    }
    private static SafeFileHandle Duplicate(IntPtr source)
    { Need(source != IntPtr.Zero, "ORIGINAL_OBJECT_REQUIRED"); Need(DuplicateHandle(GetCurrentProcess(), source, GetCurrentProcess(), out SafeFileHandle copy, 0, false, 2), "ORIGINAL_OBJECT_DUPLICATION_FAILED"); return copy; }
    private static byte[] Inner(NativeHostBinding binding, string nonce, long sequence, bool fence, byte[] payload)
    {
        var metadata = new Dictionary<string, object> { ["nonce"] = nonce, ["authorityId"] = binding.AuthorityId.ToString("D"), ["account"] = binding.Account,
            ["accountEpoch"] = binding.AccountEpoch, ["application"] = binding.ApplicationBinding, ["instanceId"] = binding.InstanceId.ToString("D"),
            ["instanceEpoch"] = binding.InstanceEpoch, ["documentId"] = binding.DocumentId.ToString("D"), ["documentGeneration"] = binding.DocumentGeneration, ["channelGeneration"] = binding.ChannelGeneration };
        if (fence) { metadata["fenceId"] = sequence; metadata["cutoff"] = 2L; metadata["nextChannelGeneration"] = binding.ChannelGeneration + 1; }
        else { metadata["deliveryId"] = sequence == 1 ? 1L : 2L; metadata["bytes"] = payload.Length; metadata["sha256"] = HashBytes(payload); metadata["frozenRevision"] = 1L; }
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(metadata); byte[] bytes = new byte[24 + json.Length + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x31444c41); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1); bytes[6] = fence ? (byte)2 : (byte)1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)json.Length); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)payload.Length); BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), sequence);
        json.CopyTo(bytes, 24); payload.CopyTo(bytes, 24 + json.Length); return bytes;
    }
    private static void CheckReceipt(NativeViewPacket packet, NativeHostBinding binding, string nonce, long sequence, bool fence, string? hash)
    {
        byte[] b = packet.Payload;
        Need(packet.Kind == NativeViewKind.Receipt && b.Length >= 24 && BinaryPrimitives.ReadUInt32LittleEndian(b) == 0x31444c41 &&
            BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(4)) == 1 && b[6] == (fence ? 4 : 3) && b[7] == 0 &&
            BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(16)) == sequence && BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12)) == 0 &&
            BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8)) == b.Length - 24, "EXACT_NATIVE_RECEIPT_HEADER_REQUIRED");
        using var json = JsonDocument.Parse(b.AsMemory(24)); var r = json.RootElement;
        Need(r.GetProperty("nonce").GetString() == nonce && r.GetProperty("authorityId").GetString() == binding.AuthorityId.ToString("D") &&
            r.GetProperty("account").GetString() == binding.Account && r.GetProperty("accountEpoch").GetInt64() == binding.AccountEpoch &&
            r.GetProperty("application").GetString() == binding.ApplicationBinding && r.GetProperty("instanceId").GetString() == binding.InstanceId.ToString("D") &&
            r.GetProperty("instanceEpoch").GetInt64() == binding.InstanceEpoch && r.GetProperty("documentId").GetString() == binding.DocumentId.ToString("D") &&
            r.GetProperty("documentGeneration").GetInt64() == binding.DocumentGeneration && r.GetProperty("channelGeneration").GetInt64() == binding.ChannelGeneration, "EXACT_NATIVE_RECEIPT_BINDING_REQUIRED");
        Need(fence ? r.GetProperty("fenceId").GetInt64() == sequence && r.GetProperty("cutoff").GetInt64() == 2 && r.GetProperty("nextChannelGeneration").GetInt64() == binding.ChannelGeneration + 1
            : r.GetProperty("deliveryId").GetInt64() == 1 && r.GetProperty("sha256").GetString() == hash && r.GetProperty("result").GetString() == "posted", "EXACT_NATIVE_RECEIPT_RESULT_REQUIRED");
    }
    private static string Plain(string path) { string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); for (string? p = full; p is not null; p = Path.GetDirectoryName(p)) if (File.Exists(p) || Directory.Exists(p)) Need((File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0, "PLAIN_OWNED_TEST_PATH_REQUIRED"); return full; }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static string HashBytes(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string HashText(string text) => HashBytes(Encoding.UTF8.GetBytes(text));
    private static void Need(bool value, string code) { if (!value) throw new HarnessRejectException(code); }
    private static void Progress(string mode, string stage) => Console.WriteLine(mode + ": " + stage);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetProcessId(SafeFileHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint rights, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("KernelBase.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CompareObjectHandles(SafeFileHandle first, SafeProcessHandle second);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out SafeFileHandle duplicate, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeProcessHandle process, uint exit);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(SafeFileHandle job, uint exit);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, [Out] byte[] bytes, uint length, out uint returned);
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(SafeProcessHandle process);
    private sealed class HarnessRejectException(string safeCode) : Exception(safeCode) { internal string SafeCode { get; } = safeCode; }
}
