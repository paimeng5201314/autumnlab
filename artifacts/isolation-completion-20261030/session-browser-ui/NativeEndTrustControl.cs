using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AutumnOS.OwnedBrowserParent;
using AutumnOS.SessionBrowserProtocol;

namespace AutumnOS.SessionBrowserUI;

// A file proof for a completed direct native A control. This class does not
// change the B acceptance gate or infer trust from a missing/default field.
internal sealed class NativeEndTrustControl : IDisposable
{
    private readonly List<FileStream> pins;
    private bool disposed;
    public string ControlReportSha256 { get; }
    public string BindingSha256 { get; }
    public string SourceSnapshotId { get; }
    public string ManifestSha256 { get; }
    public string RuntimeVersion { get; }
    public string RuntimeExecutableSha256 { get; }
    public string ControlExecutableSha256 { get; }
    public uint OriginalControlPid { get; }
    public ulong OriginalControlCreationFiletime { get; }
    public uint BrowserPid { get; }
    public int NativeCompositionEndCount { get; }
    public int NativeTrustedCompositionEndCount { get; }
    public bool ExpectedEndIsTrusted { get; }
    public string DocumentInstanceSha256 { get; }
    public bool ProtectedPreparedDirectoryVerified => true;
    public bool ProtectedReportAndBindingVerified => true;
    public bool OriginalControlExitBound => true;
    public bool ProofPinned => !disposed;
    public string ObservationScope => "native_A_reference_only_SDK_enumerated_and_held_process_samples_not_all_OS_isolation";

    private NativeEndTrustControl(List<FileStream> handles, SessionInputs input, string reportSha, string bindingSha,
        string runtimeSha, string controlSha, uint pid, ulong creation, uint browser, int totalEnd, int trustedEnd, string document)
    {
        pins = handles; SourceSnapshotId = input.source_snapshot_id; ManifestSha256 = input.manifest_sha256;
        RuntimeVersion = input.runtime_version; ControlReportSha256 = reportSha; BindingSha256 = bindingSha;
        RuntimeExecutableSha256 = runtimeSha; ControlExecutableSha256 = controlSha; OriginalControlPid = pid;
        OriginalControlCreationFiletime = creation; BrowserPid = browser; NativeCompositionEndCount = totalEnd;
        NativeTrustedCompositionEndCount = trustedEnd; ExpectedEndIsTrusted = NativeEndTrustComparison.Measure(totalEnd, trustedEnd);
        DocumentInstanceSha256 = document;
    }

    internal static NativeEndTrustControl Load(SessionInputs input, string manifestAbs, string controlReportAbs,
        string expectedReportSha, string expectedBindingSha)
    {
        Require(HashShape(expectedReportSha) && HashShape(expectedBindingSha), "NATIVE_CONTROL_EXPECTED_HASHES_REQUIRED");
        string manifest = Plain(manifestAbs), reportPath = Plain(controlReportAbs);
        string prepared = Path.GetDirectoryName(manifest)!;
        Require(Path.GetFileName(manifest) == "input-manifest.json" &&
            Path.GetFileName(prepared).StartsWith("prepared-", StringComparison.Ordinal) &&
            reportPath.Equals(Path.Combine(prepared, "native-ime-control.json"), StringComparison.OrdinalIgnoreCase), "EXACT_PREPARED_NATIVE_CONTROL_PATH_REQUIRED");
        string bindingPath = Plain(Path.Combine(prepared, "control-binding.json"));
        using var identity = WindowsIdentity.GetCurrent();
        string owner = identity.User?.Value ?? throw new InvalidDataException("NATIVE_CONTROL_CURRENT_OWNER_REQUIRED");
        RequireOwnedAcl(prepared, owner, true);
        var handles = new List<FileStream>(); bool transferred = false;
        try
        {
            _ = Pin(manifest, input.manifest_sha256, 4 * 1024 * 1024, handles);
            byte[] reportBytes = Pin(reportPath, expectedReportSha, 16 * 1024 * 1024, handles);
            byte[] bindingBytes = Pin(bindingPath, expectedBindingSha, 65536, handles);
            RequireOwnedAcl(manifest, owner, false); RequireOwnedAcl(reportPath, owner, false); RequireOwnedAcl(bindingPath, owner, false);
            using JsonDocument report = Parse(reportBytes), binding = Parse(bindingBytes);
            JsonElement a = report.RootElement, b = binding.RootElement;
            Require(Int(a, "schema_version") == 1 && Text(a, "status") == "completed_native_ime_control" && Flag(a, "native_control"), "COMPLETED_NATIVE_CONTROL_REQUIRED");
            Require(Int(b, "schema_version") == 1 && Text(b, "kind") == "native_ime_control_original_process_exit_binding", "NATIVE_CONTROL_ORIGINAL_PROCESS_BINDING_REQUIRED");
            foreach (JsonElement facts in new[] { a, b })
                Require(Text(facts, "source_snapshot_id") == input.source_snapshot_id && Text(facts, "manifest_sha256") == input.manifest_sha256,
                    "NATIVE_CONTROL_SOURCE_AND_MANIFEST_MISMATCH");
            Require(Text(b, "control_report_sha256") == expectedReportSha && Flag(b, "original_control_exit_observed") &&
                Int(b, "original_control_exit_code") == 0, "NATIVE_CONTROL_RUNNER_ORIGINAL_EXIT_ZERO_REQUIRED");
            uint pid = UInt(a, "original_control_pid"), browserPid = UInt(a, "browser_pid");
            ulong creation = ULong(a, "control_creation_filetime");
            Require(pid != 0 && browserPid != 0 && browserPid != pid && creation != 0 && UInt(b, "original_control_pid") == pid &&
                ULong(b, "control_creation_filetime") == creation, "NATIVE_CONTROL_ORIGINAL_PROCESS_IDENTITY_MISMATCH");
            string runtimeSha = input.runtime_files.Single(f => f.path == "msedgewebview2.exe").sha256;
            string controlSha = input.core_files.Single(f => f.path == input.core_executable).sha256;
            Require(Text(a, "runtime_version") == input.runtime_version && Text(a, "browser_version") == input.runtime_version &&
                Text(a, "runtime_executable_sha256") == runtimeSha && Text(a, "control_executable_sha256") == controlSha &&
                Text(b, "control_executable_sha256") == controlSha, "NATIVE_CONTROL_FIXED_RUNTIME_OR_IMAGE_MISMATCH");
            Require(Text(a, "station_name") == "WinSta0" && Text(a, "desktop_name") == "Default", "DIRECT_NATIVE_CONTROL_CONTEXT_REQUIRED");
            foreach (string zero in new[] { "cdp_input_calls", "netonly_logon_calls", "new_appcontainer_profiles", "new_window_stations", "new_desktops", "clipboard_api_calls" })
                Require(Int(a, zero) == 0, "NATIVE_CONTROL_FORBIDDEN_ACTION_COUNT");
            foreach (string positive in new[] { "initialization_succeeded", "page_succeeded", "final_dom_observed", "native_composition_observed", "normal_browser_exit",
                "original_browser_handle_signaled", "all_observed_process_handles_signaled", "controller_disposed", "window_closed_naturally", "cleanup_confirmed" })
                Require(Flag(a, positive), "NATIVE_CONTROL_POSITIVE_EVIDENCE_MISSING");
            Require(!Flag(a, "process_observation_gap") && !Flag(a, "report_write_failed") &&
                Int(a, "final_environment_process_count") == 0 && UInt(a, "normal_browser_exit_pid") == browserPid &&
                Int(a, "navigation_start_count") == 1 && Array(a, "errors").GetArrayLength() == 0 && Array(a, "process_failures").GetArrayLength() == 0 &&
                (!a.TryGetProperty("error_stage", out JsonElement error) || error.ValueKind == JsonValueKind.Null), "NATIVE_CONTROL_FAILURE_OR_CLEANUP_GAP");
            JsonElement samples = Array(a, "observed_processes");
            Require(samples.GetArrayLength() is >= 1 and <= 4096, "NATIVE_CONTROL_HELD_SAMPLES_REQUIRED");
            bool browserHeld = false;
            foreach (JsonElement held in samples.EnumerateArray())
            {
                uint heldPid = UInt(held, "pid");
                Require(held.GetProperty("exit_code").TryGetUInt32(out uint exit), "NATIVE_CONTROL_HELD_EXIT_CODE_REQUIRED");
                Require(heldPid != 0 && heldPid != pid && ULong(held, "creation_filetime") != 0 && Flag(held, "original_handle_signaled") &&
                    Flag(held, "image_in_new_copied_trees") && (!held.TryGetProperty("failure", out JsonElement failure) || failure.ValueKind == JsonValueKind.Null), "NATIVE_CONTROL_HELD_PROCESS_EXIT_UNPROVEN");
                Require(Flag(held.GetProperty("token"), "ordinary_parent_os_query"), "NATIVE_CONTROL_OS_TOKEN_SAMPLE_REQUIRED");
                if (heldPid == browserPid) { Require(exit == 0, "NATIVE_CONTROL_ORIGINAL_BROWSER_EXIT_MUST_BE_ZERO"); browserHeld = true; }
            }
            Require(browserHeld, "NATIVE_CONTROL_ORIGINAL_BROWSER_SAMPLE_MISSING");
            string nonce = Text(a, "nonce"), document = Text(a, "baseline_document_instance_sha256");
            Require(nonce.Length == 32 && nonce.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') && HashShape(document), "NATIVE_CONTROL_PAGE_BINDING_REQUIRED");
            JsonElement baseline = a.GetProperty("baseline_dom_evidence"), final = a.GetProperty("dom_evidence");
            VerifyPage(baseline, nonce, document); VerifyPage(final, nonce, document);
            Require(Int(baseline, "value_length") == 0 && Text(baseline, "value_sha256") == HashText("") && !Flag(baseline, "expected_hello") && !Flag(baseline, "composing"), "NATIVE_CONTROL_EMPTY_BASELINE_REQUIRED");
            foreach (string counter in new[] { "composition_start", "composition_update", "composition_end", "trusted_composition_start", "trusted_composition_update", "trusted_composition_end", "input", "trusted_input" })
                Require(Int(baseline, counter) == 0, "NATIVE_CONTROL_BASELINE_EVENTS_MUST_BE_EMPTY");
            int totalEnd = Counter(final, "composition_end"), trustedEnd = Counter(final, "trusted_composition_end");
            Require(Counter(final, "composition_start") > 0 && Counter(final, "composition_update") > 0 && totalEnd > 0 &&
                Counter(final, "trusted_composition_start") > 0 && Counter(final, "trusted_composition_update") > 0 && Counter(final, "trusted_input") > 0 &&
                Counter(final, "trusted_composition_start") <= Counter(final, "composition_start") && Counter(final, "trusted_composition_update") <= Counter(final, "composition_update") &&
                Counter(final, "trusted_input") <= Counter(final, "input") && trustedEnd <= totalEnd, "NATIVE_CONTROL_REAL_COMPOSITION_EVENTS_REQUIRED");
            Require(trustedEnd == 0 || trustedEnd == totalEnd, "NATIVE_CONTROL_MIXED_END_TRUST_REJECTED");
            string helloHash = HashText("你好");
            Require(!Flag(final, "composing") && Flag(final, "expected_hello") && Int(final, "value_length") == 2 && Text(final, "value_sha256") == helloHash &&
                Int(final, "composition_end_data_length") == 2 && Text(final, "composition_end_data_sha256") == helloHash,
                "NATIVE_CONTROL_FINAL_NATIVE_TEXT_AND_END_DATA_MISMATCH");
            var proof = new NativeEndTrustControl(handles, input, expectedReportSha, expectedBindingSha, runtimeSha, controlSha, pid, creation, browserPid, totalEnd, trustedEnd, document);
            transferred = true; return proof;
        }
        finally { if (!transferred) foreach (FileStream file in handles) file.Dispose(); }
    }
    private static void VerifyPage(JsonElement page, string nonce, string document)
    {
        Require(Text(page, "nonce") == nonce && Text(page, "document_instance_sha256") == document && Int(page, "wasm") == 42 && Int(page, "raf") >= 60 &&
            Int(page, "width") == 800 && Int(page, "height") == 600 && page.GetProperty("device_pixel_ratio").GetDouble() == 1 && Flag(page, "ready") && !Flag(page, "failed") &&
            HashShape(Text(page, "value_sha256")) && HashShape(Text(page, "phase_digest_sha256")), "NATIVE_CONTROL_SAME_REAL_OWNED_PAGE_REQUIRED");
    }
    private static byte[] Pin(string path, string expected, int limit, List<FileStream> handles)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); handles.Add(file);
        Require(file.Length is >= 2 && file.Length <= limit, "NATIVE_CONTROL_PROOF_FILE_SIZE");
        byte[] bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes); file.Position = 0;
        Require(Convert.ToHexStringLower(SHA256.HashData(bytes)) == expected, "NATIVE_CONTROL_PROOF_HASH_MISMATCH"); return bytes;
    }
    private static string Plain(string path)
    {
        Require(Path.IsPathFullyQualified(path), "ABSOLUTE_NATIVE_CONTROL_PROOF_PATH_REQUIRED");
        string full = Path.GetFullPath(path); OwnedFiles.FixedLocal(full); OwnedFiles.PlainAncestors(full);
        Require(File.Exists(full), "EXISTING_NATIVE_CONTROL_PROOF_FILE_REQUIRED"); return full;
    }
    private static void RequireOwnedAcl(string path, string owner, bool directory)
    {
        byte[] bytes = directory ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access).GetSecurityDescriptorBinaryForm() :
            new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
        var sd = new RawSecurityDescriptor(bytes, 0);
        Require(sd.Owner?.Value == owner && sd.DiscretionaryAcl is { Count: 2 } &&
            (!directory || (sd.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0), "PROTECTED_OWNER_SYSTEM_NATIVE_CONTROL_PROOF_REQUIRED");
        var expected = new HashSet<string>(StringComparer.Ordinal) { owner, "S-1-5-18" };
        foreach (GenericAce generic in sd.DiscretionaryAcl!)
        {
            Require(generic is CommonAce, "NATIVE_CONTROL_PLAIN_ACES_REQUIRED"); var ace = (CommonAce)generic;
            Require(ace.AceQualifier == AceQualifier.AccessAllowed && !ace.IsCallback && (ace.AceFlags & AceFlags.InheritOnly) == 0 &&
                unchecked((uint)ace.AccessMask) == 0x1F01FF && expected.Remove(ace.SecurityIdentifier.Value), "NATIVE_CONTROL_OWNER_SYSTEM_ONLY_EFFECTIVE_FULL_ACCESS");
            if (directory) Require((ace.AceFlags & (AceFlags.ContainerInherit | AceFlags.ObjectInherit)) == (AceFlags.ContainerInherit | AceFlags.ObjectInherit), "NATIVE_CONTROL_PARENT_INHERITABLE_ACES_REQUIRED");
        }
        Require(expected.Count == 0, "NATIVE_CONTROL_OWNER_SYSTEM_ACES_REQUIRED");
    }
    private static JsonDocument Parse(byte[] bytes)
    {
        JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        try { RejectDuplicates(document.RootElement); return document; } catch { document.Dispose(); throw; }
    }
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject()) { Require(names.Add(property.Name), "NATIVE_CONTROL_DUPLICATE_JSON_FIELD"); RejectDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (JsonElement child in value.EnumerateArray()) RejectDuplicates(child);
    }
    private static int Counter(JsonElement value, string name) { int count = Int(value, name); Require(count is >= 0 and <= 1024, "NATIVE_CONTROL_EVENT_COUNT_BOUND"); return count; }
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString() ?? throw new InvalidDataException("NATIVE_CONTROL_REQUIRED_STRING");
    private static int Int(JsonElement value, string name) => value.GetProperty(name).GetInt32();
    private static uint UInt(JsonElement value, string name) => value.GetProperty(name).GetUInt32();
    private static ulong ULong(JsonElement value, string name) => value.GetProperty(name).GetUInt64();
    private static bool Flag(JsonElement value, string name) => value.GetProperty(name).GetBoolean();
    private static JsonElement Array(JsonElement value, string name) { JsonElement array = value.GetProperty(name); Require(array.ValueKind == JsonValueKind.Array, "NATIVE_CONTROL_REQUIRED_ARRAY"); return array; }
    private static bool HashShape(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string HashText(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Require(bool condition, string code) { if (!condition) throw new InvalidDataException(code); }
    public void Dispose()
    {
        if (disposed) return;
        Exception? failure = null;
        foreach (FileStream file in pins) try { file.Dispose(); } catch (Exception error) { failure ??= error; }
        disposed = true; if (failure is not null) throw failure;
    }
}
