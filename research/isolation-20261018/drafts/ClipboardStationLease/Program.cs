// REVIEW DRAFT. Do not run --broker / --hold before explicit user authorization for UAC.
// The elevated --hold path creates exactly one new noninteractive window station, then
// only holds its handle. It never opens a clipboard, starts a child or writes any file.
// The normal --broker path owns evidence files and launches the normal-user probe.
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.ClipboardStationLease;

internal static class Program
{
    private const string StationPrefix = "AutumnOS_ClipboardProbe_";
    private const string PipePrefix = "AutumnOS_ClipboardLease_";
    private const uint StationAccess = 0x000F037F;
    private static readonly TimeSpan LeaseLifetime = TimeSpan.FromMinutes(10);

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length < 1) throw new ArgumentException("MODE_REQUIRED");
            var options = Parse(args);
            return args[0] switch
            {
                "--broker" => await RunBrokerAsync(options),
                "--hold" => await RunHolderAsync(options),
                _ => throw new ArgumentException("UNKNOWN_MODE")
            };
        }
        catch (Exception error)
        {
            // No supplied file path is ever opened here, particularly by elevated mode.
            Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "failed", error = error.Message,
                native_error = error is Win32Exception native ? native.NativeErrorCode : (int?)null }));
            return 1;
        }
    }

    private static async Task<int> RunBrokerAsync(Dictionary<string, string> args)
    {
        RequireKeys(args, "--probe", "--runtime", "--report");
        var self = CurrentIdentity();
        if (self.elevated || self.integrity != 0x2000) throw new InvalidOperationException("BROKER_MUST_BE_NORMAL_MEDIUM_USER");
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("APPHOST_REQUIRED");
        if (Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("RUN_COMPILED_APPHOST_NOT_DOTNET_EXEC");
        string probe = Path.GetFullPath(args["--probe"]), runtime = Path.GetFullPath(args["--runtime"]), report = Path.GetFullPath(args["--report"]);
        if (!File.Exists(probe) || !File.Exists(Path.Combine(runtime, "msedgewebview2.exe")) || File.Exists(report))
            throw new ArgumentException("PROBE_RUNTIME_OR_NEW_REPORT_REQUIRED");
        string leaseReport = report + ".lease.json";
        if (File.Exists(leaseReport)) throw new ArgumentException("LEASE_REPORT_ALREADY_EXISTS");

        string nonce = Guid.NewGuid().ToString("N"), pipeName = PipePrefix + nonce;
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        var owner = new SecurityIdentifier(self.sid);
        security.SetOwner(owner);
        security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // CurrentUserOnly also compares elevation level and would reject the intended
        // elevated client. Explicit ACL plus both kernel peer PID checks replaces it.
        using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, security);
        using var lifetime = new CancellationTokenSource(LeaseLifetime);
        var launch = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "--hold", "--pipe", pipeName, "--nonce", nonce, "--broker-pid", Environment.ProcessId.ToString(), "--owner-sid", self.sid })
            launch.ArgumentList.Add(argument);
        // THIS IS THE ONLY UAC REQUEST, and is not executed during preparation/build.
        using var helper = Process.Start(launch) ?? throw new InvalidOperationException("UAC_HELPER_NOT_STARTED");
        await pipe.WaitForConnectionAsync(lifetime.Token);
        if (!Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientPid)) throw Native.Error("PIPE_CLIENT_PID_FAILED");
        if (clientPid != helper.Id || helper.HasExited) throw new InvalidOperationException("PIPE_HELPER_PID_MISMATCH");
        using var helperIdentity = RequirePeer(helper, self.sid, self.session, executable, requireElevated: true);
        await WriteAsync(pipe, new Message("create", nonce, StationPrefix + nonce, helper.Id, self.sid, self.session), lifetime.Token);
        Message ready = await ReadAsync(pipe, lifetime.Token);
        if (ready != new Message("ready", nonce, StationPrefix + nonce, helper.Id, self.sid, self.session))
            throw new InvalidOperationException("LEASE_READY_MISMATCH");

        // The elevated helper receives no probe, runtime, report or other file path.
        var test = new ProcessStartInfo(probe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "--runtime", runtime, "--report", report, "--leased-station", ready.station,
            "--lease-nonce", nonce, "--lease-helper-pid", helper.Id.ToString() }) test.ArgumentList.Add(argument);
        using var probeProcess = Process.Start(test) ?? throw new InvalidOperationException("NORMAL_USER_PROBE_NOT_STARTED");
        await probeProcess.WaitForExitAsync(lifetime.Token);
        await WriteAsync(pipe, ready with { stage = "release" }, lifetime.Token);
        Message released = await ReadAsync(pipe, lifetime.Token);
        if (released != ready with { stage = "released" }) throw new InvalidOperationException("LEASE_RELEASE_MISMATCH");
        await helper.WaitForExitAsync(lifetime.Token);
        Directory.CreateDirectory(Path.GetDirectoryName(leaseReport)!);
        await File.WriteAllTextAsync(leaseReport, JsonSerializer.Serialize(new { schema_version = 1,
            status = helper.ExitCode == 0 ? "passed" : "failed", lease = ready, broker_pid = Environment.ProcessId,
            probe_pid = probeProcess.Id, probe_exit_code = probeProcess.ExitCode, helper_exit_code = helper.ExitCode,
            scope = "Lease lifecycle only. Clipboard acceptance is exclusively in the separate probe report.",
            real_clipboard_touched_by_lease_code = false, system_policy_changes = false,
            helper_starts_children = false, helper_writes_files = false }, new JsonSerializerOptions { WriteIndented = true }));
        return probeProcess.ExitCode == 0 && helper.ExitCode == 0 ? 0 : 1;
        // On timeout/error disposing the pipe lets the helper leave normally. No process
        // is killed. A still-running probe owns its handles until it exits naturally.
    }

    private static async Task<int> RunHolderAsync(Dictionary<string, string> args)
    {
        RequireKeys(args, "--pipe", "--nonce", "--broker-pid", "--owner-sid");
        string nonce = args["--nonce"];
        if (!Regex.IsMatch(nonce, "\\A[0-9a-f]{32}\\z") || args["--pipe"] != PipePrefix + nonce)
            throw new ArgumentException("INVALID_LEASE_IDENTIFIERS");
        var self = CurrentIdentity();
        if (!self.elevated || self.integrity != 0x3000 || self.sid != args["--owner-sid"])
            throw new InvalidOperationException("SAME_USER_HIGH_INTEGRITY_UAC_REQUIRED");
        if (!int.TryParse(args["--broker-pid"], out int brokerPid) || brokerPid <= 0 || brokerPid == Environment.ProcessId)
            throw new ArgumentException("INVALID_BROKER_PID");
        using var broker = Process.GetProcessById(brokerPid);
        using var brokerIdentity = RequirePeer(broker, self.sid, self.session, Environment.ProcessPath!, requireElevated: false);
        using var lifetime = new CancellationTokenSource(LeaseLifetime);
        using var pipe = new NamedPipeClientStream(".", args["--pipe"], PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
        await pipe.ConnectAsync(lifetime.Token);
        if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid)) throw Native.Error("PIPE_SERVER_PID_FAILED");
        if (serverPid != brokerPid || broker.HasExited) throw new InvalidOperationException("PIPE_BROKER_PID_MISMATCH");
        Message request = await ReadAsync(pipe, lifetime.Token);
        var expected = new Message("create", nonce, StationPrefix + nonce, Environment.ProcessId, self.sid, self.session);
        if (request != expected) throw new InvalidOperationException("LEASE_CREATE_MISMATCH");
        string sddl = $"O:{self.sid}G:{self.sid}D:P(A;;0x000F037F;;;{self.sid})(A;;0x000F037F;;;SY)S:(ML;;NW;;;ME)";
        if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out IntPtr descriptor, out _))
            throw Native.Error("STATION_SECURITY_DESCRIPTOR_FAILED");
        IntPtr station = IntPtr.Zero;
        try
        {
            var attributes = new Native.SecurityAttributes { Length = Marshal.SizeOf<Native.SecurityAttributes>(), Descriptor = descriptor, Inherit = false };
            station = Native.CreateWindowStation(expected.station, 1 /* CWF_CREATE_ONLY */, StationAccess, ref attributes);
            if (station == IntPtr.Zero) throw Native.Error("CREATE_NEW_PRIVATE_STATION_FAILED");
            // A successful handle is the API result; a stale LastError is not failure.
            if (Native.Name(station) != expected.station || Native.Visible(station) || Native.Owner(station) != self.sid)
                throw new InvalidOperationException("NEW_PRIVATE_STATION_METADATA_MISMATCH");
            Native.RequireStationAclAndLabel(station, self.sid);
            Message ready = expected with { stage = "ready" };
            await WriteAsync(pipe, ready, lifetime.Token);
            Message release = await ReadAsync(pipe, lifetime.Token);
            if (release != ready with { stage = "release" }) throw new InvalidOperationException("LEASE_RELEASE_MISMATCH");
            // This helper never associates itself with the station or any desktop.
            if (!Native.CloseWindowStation(station)) throw Native.Error("CLOSE_PRIVATE_STATION_HANDLE_FAILED");
            station = IntPtr.Zero;
            await WriteAsync(pipe, ready with { stage = "released" }, lifetime.Token);
            return 0;
        }
        finally
        {
            if (station != IntPtr.Zero) Native.CloseWindowStation(station);
            Native.LocalFree(descriptor);
        }
    }

    private static SafeProcessHandle RequirePeer(Process peer, string sid, int session, string executable, bool requireElevated)
    {
        if (peer.HasExited || peer.SessionId != session) throw new InvalidOperationException("PEER_SESSION_MISMATCH");
        // Do not request Process.Handle / PROCESS_ALL_ACCESS across Medium -> High IL.
        // The pinned query-only handle establishes identity without write/kill rights.
        var process = Native.OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, checked((uint)peer.Id));
        if (process.IsInvalid) { process.Dispose(); throw Native.Error("PEER_QUERY_HANDLE_FAILED"); }
        try
        {
            if (!Native.OpenProcessToken(process, 8 /* TOKEN_QUERY */, out var token)) throw Native.Error("PEER_TOKEN_QUERY_FAILED");
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            {
                if (identity.User?.Value != sid || Native.Elevated(token) != requireElevated ||
                    Native.Integrity(token) != (requireElevated ? 0x3000 : 0x2000))
                    throw new InvalidOperationException("PEER_TOKEN_MISMATCH");
            }
            var image = new StringBuilder(32768); int size = image.Capacity;
            if (!Native.QueryFullProcessImageName(process, 0, image, ref size)) throw Native.Error("PEER_IMAGE_QUERY_FAILED");
            if (!Path.GetFullPath(image.ToString()).Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("PEER_EXECUTABLE_MISMATCH");
            return process;
        }
        catch { process.Dispose(); throw; }
    }

    private static (string sid, int session, bool elevated, int integrity) CurrentIdentity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return (identity.User?.Value ?? throw new InvalidOperationException("USER_SID_REQUIRED"),
            Process.GetCurrentProcess().SessionId, Native.Elevated(identity.AccessToken), Native.Integrity(identity.AccessToken));
    }
    private static Dictionary<string, string> Parse(string[] args)
    {
        if (args.Length % 2 != 1) throw new ArgumentException("KEY_VALUE_ARGUMENTS_REQUIRED");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i += 2) if (!result.TryAdd(args[i], args[i + 1])) throw new ArgumentException("DUPLICATE_ARGUMENT");
        return result;
    }
    private static void RequireKeys(Dictionary<string, string> args, params string[] keys)
    { if (args.Count != keys.Length || keys.Any(key => !args.ContainsKey(key))) throw new ArgumentException("INVALID_ARGUMENTS"); }
    private sealed record Message(string stage, string nonce, string station, int helper_pid, string owner_sid, int session);
    private static async Task WriteAsync(Stream stream, Message message, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message), length = new byte[4];
        if (payload.Length is < 1 or > 4096) throw new InvalidDataException("LEASE_MESSAGE_TOO_LARGE");
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await stream.WriteAsync(length, token); await stream.WriteAsync(payload, token); await stream.FlushAsync(token);
    }
    private static async Task<Message> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] length = new byte[4]; await stream.ReadExactlyAsync(length, token);
        int count = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (count is < 1 or > 4096) throw new InvalidDataException("LEASE_MESSAGE_LENGTH_INVALID");
        byte[] payload = new byte[count]; await stream.ReadExactlyAsync(payload, token);
        return JsonSerializer.Deserialize<Message>(payload) ?? throw new InvalidDataException("LEASE_MESSAGE_INVALID");
    }
}

internal static class Native
{
    internal static Win32Exception Error(string stage) => new(Marshal.GetLastWin32Error(), stage);
    internal static bool Elevated(SafeAccessTokenHandle token)
    {
        if (!GetTokenInformation(token, 20, out int value, sizeof(int), out _)) throw Error("TOKEN_ELEVATION_QUERY_FAILED");
        return value != 0;
    }
    internal static int Integrity(SafeAccessTokenHandle token)
    {
        _ = GetTokenInformationBuffer(token, 25, IntPtr.Zero, 0, out int size);
        if (size <= IntPtr.Size || size > 4096) throw Error("TOKEN_INTEGRITY_SIZE_INVALID");
        IntPtr data = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformationBuffer(token, 25, data, size, out _)) throw Error("TOKEN_INTEGRITY_QUERY_FAILED");
            string sid = new SecurityIdentifier(Marshal.ReadIntPtr(data)).Value;
            return int.Parse(sid[(sid.LastIndexOf('-') + 1)..]);
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    internal static string Name(IntPtr station)
    {
        var text = new StringBuilder(512);
        if (!GetUserObjectInformation(station, 2, text, text.Capacity * 2, out _)) throw Error("STATION_NAME_QUERY_FAILED");
        return text.ToString();
    }
    internal static bool Visible(IntPtr station)
    {
        if (!GetUserObjectFlags(station, 1, out var flags, Marshal.SizeOf<UserObjectFlags>(), out _)) throw Error("STATION_FLAGS_QUERY_FAILED");
        return (flags.Flags & 1) != 0;
    }
    internal static string Owner(IntPtr station)
    {
        uint result = GetSecurityInfo(station, 7 /* SE_WINDOW_OBJECT */, 1 /* OWNER_SECURITY_INFORMATION */,
            out IntPtr owner, out _, out _, out _, out IntPtr descriptor);
        if (result != 0) throw new Win32Exception(checked((int)result), "STATION_OWNER_QUERY_FAILED");
        try { return new SecurityIdentifier(owner).Value; } finally { LocalFree(descriptor); }
    }
    internal static void RequireStationAclAndLabel(IntPtr station, string expectedOwner)
    {
        uint result = GetSecurityInfo(station, 7, 0x14 /* DACL | LABEL_SECURITY_INFORMATION */,
            out _, out _, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        if (result != 0) throw new Win32Exception(checked((int)result), "STATION_ACL_LABEL_QUERY_FAILED");
        try
        {
            if (dacl == IntPtr.Zero || Marshal.ReadInt16(dacl, 4) != 2 || sacl == IntPtr.Zero || Marshal.ReadInt16(sacl, 4) != 1)
                throw new InvalidOperationException("STATION_ACL_LABEL_COUNT_MISMATCH");
            var owners = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < 2; i++)
            {
                if (!GetAce(dacl, i, out IntPtr ace)) throw Error("STATION_DACL_ACE_QUERY_FAILED");
                if (Marshal.ReadByte(ace) != 0 /* ACCESS_ALLOWED */ || Marshal.ReadByte(ace, 1) != 0 || Marshal.ReadInt32(ace, 4) != 0x000F037F)
                    throw new InvalidOperationException("STATION_DACL_ACE_MISMATCH");
                owners.Add(new SecurityIdentifier(IntPtr.Add(ace, 8)).Value);
            }
            if (!owners.SetEquals(new[] { expectedOwner, "S-1-5-18" })) throw new InvalidOperationException("STATION_DACL_SIDS_MISMATCH");
            if (!GetAce(sacl, 0, out IntPtr label)) throw Error("STATION_LABEL_ACE_QUERY_FAILED");
            if (Marshal.ReadByte(label) != 0x11 /* SYSTEM_MANDATORY_LABEL */ || Marshal.ReadByte(label, 1) != 0 ||
                Marshal.ReadInt32(label, 4) != 1 /* NO_WRITE_UP */ || new SecurityIdentifier(IntPtr.Add(label, 8)).Value != "S-1-16-8192")
                throw new InvalidOperationException("STATION_MEDIUM_LABEL_MISMATCH");
        }
        finally { LocalFree(descriptor); }
    }
    [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct UserObjectFlags { [MarshalAs(UnmanagedType.Bool)] public bool Inherit; [MarshalAs(UnmanagedType.Bool)] public bool Reserved; public uint Flags; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int type, out int data, int size, out int required);
    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformationBuffer(SafeAccessTokenHandle token, int type, IntPtr data, int size, out int required);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr CreateWindowStation(string name, uint flags, uint access, ref SecurityAttributes attributes);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseWindowStation(IntPtr station);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformation(IntPtr station, int index, StringBuilder data, int size, out int required);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectFlags(IntPtr station, int index, out UserObjectFlags data, int size, out int required);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(IntPtr handle, int type, uint info, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetAce(IntPtr acl, int index, out IntPtr ace);
    [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr memory);
}
