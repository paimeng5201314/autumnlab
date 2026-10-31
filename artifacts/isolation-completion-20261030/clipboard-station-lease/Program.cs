// SOURCE/COMPILE ONLY until the producer approves this exact new UAC trial.
// The elevated --hold branch receives no file paths, user SID, commands or SDDL.
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.OwnedClipboardStationLease;

internal static class Program
{
    private static string stage = "argument_validation";
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Policy.RequireNoRuntimeInjection();
            if (args.Length < 1) throw new InvalidOperationException("COMPILE_ONLY_EXPLICIT_MODE_REQUIRED");
            Dictionary<string, string> values = Parse(args);
            return args[0] switch
            {
                "--broker" => await Broker(values),
                "--hold" => await Hold(values),
                _ => throw new InvalidOperationException("COMPILE_ONLY_EXPLICIT_MODE_REQUIRED")
            };
        }
        catch (Exception error)
        {
            Emit("failed", new { stage, exception_type = error.GetType().Name, hresult = unchecked((uint)error.HResult),
                native_error = (error as Win32Exception)?.NativeErrorCode });
            return 1;
        }
    }
    private static void Emit(string status, object detail) => Console.WriteLine(JsonSerializer.Serialize(new {
        schema_version = 1, producer = "派蒙", kind = "owned_private_clipboard_station_lease", status, detail,
        clipboard_calls = "none_in_lease_code", system_policy_changes = "none", force_termination = "none" }));

    private static async Task<int> Broker(Dictionary<string, string> args)
    {
        RequireKeys(args, "--probe", "--manifest"); stage = "ordinary_broker_identity";
        Identity self = Native.CurrentIdentity();
        if (self.Elevated || self.AppContainer || self.Integrity != 0x2000) throw new InvalidOperationException("ORDINARY_MEDIUM_BROKER_REQUIRED");
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("COMPILED_APPHOST_REQUIRED");
        if (!Path.GetFileName(executable).Equals("AutumnOS.OwnedClipboardStationLease.DRAFT.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("FIXED_COMPILED_LEASE_APPHOST_REQUIRED");
        stage = "pre_uac_manifest_and_publish_pinning";
        using PinnedInputs pinned = PinnedInputs.Open(args["--manifest"], args["--probe"], executable);
        string nonce = Guid.NewGuid().ToString("N"), pipeName = Policy.PipePrefix + nonce, packageSid = Native.DerivePackageSid(nonce);
        var security = new PipeSecurity(); var owner = new SecurityIdentifier(self.Sid);
        security.SetAccessRuleProtection(true, false); security.SetOwner(owner);
        security.AddAccessRule(new(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier("S-1-5-18"), PipeAccessRights.FullControl, AccessControlType.Allow));
        // CurrentUserOnly checks elevation too and would reject the intended high
        // helper. Explicit same-user ACL + kernel peer identity replaces it.
        using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, security);
        using var handshake = new CancellationTokenSource(Policy.Lifetime);
        Emit("ready_for_single_uac", new { nonce, package_sid_sha256 = Policy.HashText(packageSid),
            package_station_access = Policy.StationPackageAccess, broker_file_count = pinned.BrokerFileCount, pinned.ManifestSha256 });
        stage = "one_new_station_uac_holder_launch";
        // The ONLY runas invocation. No routine build/test calls this mode.
        using HeldProcess holder = await LaunchHolder(executable, pipeName, nonce);
        try
        {
        stage = "authenticated_holder_pipe";
        await WithHolderExitObservation(holder, "holder_pipe_connection", handshake.Token, async cancellation =>
        { await pipe.WaitForConnectionAsync(cancellation); return true; });
        if (!Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint peerPid) || peerPid != holder.Id)
            throw new InvalidOperationException("PIPE_HOLDER_PID_MISMATCH");
        holder.RequireIdentity(self, elevated: true, executable);
        var create = new LeaseMessage(1, "create", nonce, Policy.StationPrefix + nonce, Policy.ProfilePrefix + nonce,
            holder.Id, checked((uint)Environment.ProcessId), self.Session, Policy.HashText(packageSid), Policy.StationPackageAccess);
        LeaseMessage ready = await WithHolderExitObservation(holder, "holder_ready_handshake", handshake.Token, async cancellation =>
        { await Protocol.Send(pipe, create, cancellation); return await Protocol.Receive(pipe, cancellation); });
        if (ready != create with { stage = "ready" }) throw new InvalidDataException("LEASE_READY_MISMATCH");
        holder.RequireLive();

        stage = "ordinary_controller_launch";
        var start = new ProcessStartInfo(pinned.ControllerPath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string value in new[] { "--execute", nonce, pinned.ManifestPath, holder.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(value);
        using Process controller = Process.Start(start) ?? throw new InvalidOperationException("ORDINARY_CONTROLLER_LAUNCH_FAILED");
        // SafeHandle is the original handle returned for this launched process.
        // It is borrowed; Process retains ownership for the entire wait.
        using var originalController = new HeldProcess(new SafeProcessHandle(controller.SafeHandle.DangerousGetHandle(), ownsHandle: false));
        try
        {
            originalController.RequireIdentity(self, elevated: false, pinned.ControllerPath);
            stage = "waiting_for_original_controller_exit_no_timeout_release";
            await originalController.WaitForExit();
        }
        finally { await originalController.WaitForExit(); }
        uint controllerCode = originalController.ExitCode;
        // The controller contract is to remain pending while any browser/child
        // cleanup is unproven. We never interpret a time budget as its exit.
        stage = "release_owned_station_lease";
        bool releaseProven = false;
        if (!holder.Exited)
        {
            using var releaseBudget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Protocol.Send(pipe, ready with { stage = "release" }, releaseBudget.Token);
            LeaseMessage released = await Protocol.Receive(pipe, releaseBudget.Token);
            if (released != ready with { stage = "released" }) throw new InvalidDataException("LEASE_RELEASE_MISMATCH");
            releaseProven = true;
        }
        await holder.WaitForExit();
        uint helperCode = holder.ExitCode;
        bool passed = controllerCode == 0 && helperCode == 0 && releaseProven;
        Emit(passed ? "passed_lease_only" : "failed", new { nonce, controller_pid = originalController.Id,
            controller_exit_code = controllerCode, helper_pid = holder.Id, helper_exit_code = helperCode,
            original_controller_handle_exit_observed = true, original_helper_handle_exit_observed = true,
            explicit_release_handshake = releaseProven, holder_close_does_not_prove_all_station_handles_closed = true,
            clipboard_acceptance = "separate_controller_report_only" });
        return passed ? 0 : 1;
        }
        finally
        {
            // The pipe disconnect asks the narrowly scoped holder to finish if
            // any handshake failed. Keep all pinned bytes and the original
            // process handle alive until its actual signaled exit.
            pipe.Dispose(); await holder.WaitForExit();
        }
    }

    private static async Task<HeldProcess> LaunchHolder(string executable, string pipeName, string nonce)
    {
        string command = string.Join(" ", new[] { "--hold", "--pipe", pipeName, "--nonce", nonce, "--broker-pid",
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) }.Select(Quote));
        var info = new Native.ShellExecuteInfo { Size = (uint)Marshal.SizeOf<Native.ShellExecuteInfo>(),
            Mask = 0x40 /* SEE_MASK_NOCLOSEPROCESS */ | 0x100 /* SEE_MASK_NOASYNC */, Verb = "runas", File = executable,
            Parameters = command, Directory = Path.GetDirectoryName(executable), Show = 0 /* SW_HIDE */ };
        bool launched = Native.ShellExecuteEx(ref info); int error = Marshal.GetLastWin32Error();
        if (!launched)
        {
            // An unexpected returned process is still retained and observed to exit;
            // no authenticated create request has been sent, so no station is authorized.
            if (info.Process != IntPtr.Zero)
            {
                using var unexpected = new HeldProcess(new SafeProcessHandle(info.Process, ownsHandle: true));
                Emit("partial_launch_waiting", new { original_process_id = unexpected.Id }); await unexpected.WaitForExit();
            }
            throw new Win32Exception(error, "SINGLE_UAC_HOLDER_LAUNCH_FAILED");
        }
        if (info.Process == IntPtr.Zero) throw new InvalidOperationException("UAC_ORIGINAL_PROCESS_HANDLE_REQUIRED");
        return new(new SafeProcessHandle(info.Process, ownsHandle: true));
    }

    private static async Task<T> WithHolderExitObservation<T>(HeldProcess holder, string phase, CancellationToken budget,
        Func<CancellationToken, Task<T>> operation)
    {
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(budget);
        using var exitObservationCancellation = new CancellationTokenSource();
        Task<T> pipeOperation = operation(operationCancellation.Token);
        Task originalHolderExit = holder.WaitForExit(exitObservationCancellation.Token);
        try
        {
            Task completed = await Task.WhenAny(pipeOperation, originalHolderExit);
            if (completed == originalHolderExit || holder.Exited)
            {
                stage = phase + "_original_holder_exited";
                Emit("failed_holder_exited_before_handshake", new { stage, helper_pid = holder.Id,
                    original_helper_handle_exit_observed = true, helper_exit_code = holder.ExitCode });
                operationCancellation.Cancel();
                try { await pipeOperation; } catch (Exception) { /* Drain only this now-cancelled pipe operation. */ }
                throw new InvalidOperationException("ORIGINAL_HOLDER_EXITED_BEFORE_HANDSHAKE");
            }
            return await pipeOperation;
        }
        finally
        {
            // Stop only this polling observer after the pipe wins. The original
            // handle remains held, and Broker.finally still waits without a
            // cancellation budget before releasing files or the handle.
            exitObservationCancellation.Cancel();
            try { await originalHolderExit; } catch (OperationCanceledException) { }
        }
    }

    private static async Task<int> Hold(Dictionary<string, string> args)
    {
        RequireKeys(args, "--pipe", "--nonce", "--broker-pid"); stage = "holder_identity_before_object_creation";
        string nonce = args["--nonce"];
        if (!Policy.Nonce(nonce) || args["--pipe"] != Policy.PipePrefix + nonce ||
            !uint.TryParse(args["--broker-pid"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uint brokerPid) ||
            brokerPid == 0 || brokerPid == Environment.ProcessId) throw new InvalidDataException("HOLDER_ARGUMENT_BINDING_INVALID");
        Identity self = Native.CurrentIdentity();
        if (!self.Elevated || self.AppContainer || self.Integrity != 0x3000) throw new InvalidOperationException("SAME_USER_HIGH_HOLDER_REQUIRED");
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("COMPILED_APPHOST_REQUIRED");
        using HeldProcess broker = HeldProcess.Open(brokerPid); broker.RequireIdentity(self, elevated: false, executable);
        using var lifetime = new CancellationTokenSource(Policy.Lifetime);
        using var pipe = new NamedPipeClientStream(".", args["--pipe"], PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
        await pipe.ConnectAsync(lifetime.Token);
        if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint actualPid) || actualPid != broker.Id)
            throw new InvalidOperationException("PIPE_BROKER_PID_MISMATCH");
        broker.RequireIdentity(self, elevated: false, executable);
        string packageSid = Native.DerivePackageSid(nonce);
        var expected = new LeaseMessage(1, "create", nonce, Policy.StationPrefix + nonce, Policy.ProfilePrefix + nonce,
            checked((uint)Environment.ProcessId), brokerPid, self.Session, Policy.HashText(packageSid), Policy.StationPackageAccess);
        LeaseMessage create = await Protocol.Receive(pipe, lifetime.Token);
        if (create != expected) throw new InvalidDataException("LEASE_CREATE_MISMATCH");
        broker.RequireLive(); stage = "create_exactly_one_new_noninteractive_station";
        string sddl = $"O:{self.Sid}G:{self.Sid}D:P(A;;0x{Policy.StationOwnerAccess:x8};;;{self.Sid})(A;;0x{Policy.StationOwnerAccess:x8};;;SY)(A;;0x{Policy.StationPackageAccess:x8};;;{packageSid})S:(ML;;NW;;;LW)";
        if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out IntPtr descriptor, out _)) throw Native.Error("NEW_STATION_SDDL_INVALID");
        IntPtr station = IntPtr.Zero;
        try
        {
            var attributes = new Native.SecurityAttributes { Length = Marshal.SizeOf<Native.SecurityAttributes>(), Descriptor = descriptor, Inherit = false };
            station = Native.CreateWindowStation(expected.station, 1 /* CWF_CREATE_ONLY */, Policy.StationOwnerAccess, ref attributes);
            if (station == IntPtr.Zero) throw Native.Error("CREATE_NEW_PRIVATE_STATION_FAILED");
            Native.RequireStation(station, nonce, self.Sid, packageSid); broker.RequireLive();
            await Protocol.Send(pipe, expected with { stage = "ready" }, lifetime.Token);
            stage = "holding_new_station_without_desktop_clipboard_or_children";
            LeaseMessage release = await Protocol.Receive(pipe, lifetime.Token);
            if (release != expected with { stage = "release" }) throw new InvalidDataException("LEASE_RELEASE_MISMATCH");
            broker.RequireLive(); Native.RequireStation(station, nonce, self.Sid, packageSid);
            if (!Native.CloseWindowStation(station)) throw Native.Error("CLOSE_OWN_STATION_HANDLE_FAILED");
            station = IntPtr.Zero;
            await Protocol.Send(pipe, expected with { stage = "released" }, lifetime.Token);
            return 0;
        }
        finally
        {
            // A disconnect/expiry closes only this holder's own handle. Other live
            // processes may still hold the object; this is never a cleanup proof.
            if (station != IntPtr.Zero && !Native.CloseWindowStation(station))
                Emit("holder_handle_close_failed", new { stage, native_error = Marshal.GetLastWin32Error() });
            Native.LocalFree(descriptor);
        }
    }
    private static Dictionary<string, string> Parse(string[] args)
    {
        if (args.Length % 2 != 1) throw new InvalidDataException("FIXED_KEY_VALUE_ARGUMENTS_REQUIRED");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i += 2) if (!values.TryAdd(args[i], args[i + 1])) throw new InvalidDataException("DUPLICATE_ARGUMENT");
        return values;
    }
    private static void RequireKeys(Dictionary<string, string> args, params string[] keys)
    {
        if (args.Count != keys.Length || keys.Any(key => !args.ContainsKey(key))) throw new InvalidDataException("FIXED_ARGUMENT_SET_REQUIRED");
    }
    private static string Quote(string value)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
