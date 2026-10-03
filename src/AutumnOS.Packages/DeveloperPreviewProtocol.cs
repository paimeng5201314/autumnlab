using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.Packages;

public sealed record DeveloperPreviewRequest(int ProtocolVersion, string Command, string? PackagePath = null,
    string? Sha256 = null, string? SessionId = null);
public sealed record DeveloperPreviewSimulationStatus(bool IsTestSimulation, string Account, bool PermissionsDenied, bool Offline,
    string NetworkScope = "preview_only_external_network_already_blocked");
public sealed record DeveloperPreviewResponse(bool Ok, string Code, string? SessionId = null,
    string? AppId = null, string? State = null, DeveloperSdkCall[]? Trace = null, DeveloperPreviewSimulationStatus? Simulation = null);

/// <summary>Dedicated current-user preview channel. Fixed simulation actions never mutate real identity; no general launch, filesystem or command messages.</summary>
public static class DeveloperPreviewProtocol
{
    public const int MaximumFrameBytes = 65536;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };
    public static string PipeName(string hostRoot)
    {
        if (!OperatingSystem.IsWindows()) throw new PackageException("DEVELOPER_WINDOWS_REQUIRED");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(hostRoot)).ToUpperInvariant();
        if (!Path.IsPathFullyQualified(hostRoot)) throw new PackageException("DEVELOPER_HOST_ROOT_INVALID");
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new PackageException("DEVELOPER_CURRENT_USER_REQUIRED");
        return "AutumnOS.Developer.v1." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sid + "\n" + root)))[..32];
    }
    public static void Validate(DeveloperPreviewRequest request)
    {
        if (request.ProtocolVersion != 1) throw new PackageException("DEVELOPER_PROTOCOL_UNSUPPORTED");
        if (request.Command == "preview")
        {
            if (request.SessionId is not null || request.PackagePath is not { Length: > 0 and <= 4096 } ||
                request.PackagePath.Any(char.IsControl) || !Path.IsPathFullyQualified(request.PackagePath) ||
                !Path.GetExtension(request.PackagePath).Equals(".autumn", StringComparison.OrdinalIgnoreCase) ||
                request.Sha256 is not { Length: 64 } || request.Sha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
                throw new PackageException("DEVELOPER_REQUEST_INVALID");
        }
        else if (request.Command is "status" or "trace" or "clear-trace" or "foreground" or "background" or "close"
            or "deny-permissions" or "restore-permissions" or "account-a" or "account-b" or "account-guest"
            or "offline" or "online" or "reset-simulation")
        {
            if (request.PackagePath is not null || request.Sha256 is not null || !Guid.TryParseExact(request.SessionId, "N", out _))
                throw new PackageException("DEVELOPER_REQUEST_INVALID");
        }
        else throw new PackageException("DEVELOPER_COMMAND_UNSUPPORTED");
    }
    public static async Task<DeveloperPreviewResponse> SendAsync(string hostRoot, DeveloperPreviewRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        using CancellationTokenSource work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        work.CancelAfter(TimeSpan.FromSeconds(125));
        using NamedPipeClientStream pipe = new(".", PipeName(hostRoot), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(3000, work.Token).ConfigureAwait(false); }
        catch (TimeoutException) { throw new PackageException("DEVELOPER_HOST_UNAVAILABLE"); }
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid)) throw new PackageException("DEVELOPER_HOST_UNVERIFIED");
        using Process server = Process.GetProcessById(checked((int)serverPid));
        string? actual = server.MainModule?.FileName;
        string expected = Path.Combine(Path.GetFullPath(hostRoot), "AutumnOS.Client.exe");
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new PackageException("DEVELOPER_HOST_UNVERIFIED");
        await WriteAsync(pipe, request, work.Token).ConfigureAwait(false);
        DeveloperPreviewResponse response = await ReadAsync<DeveloperPreviewResponse>(pipe, work.Token).ConfigureAwait(false);
        if (response.Code is not { Length: >= 1 and <= 80 } || response.Code.Any(c => c is not (>= 'A' and <= 'Z') and not (>= '0' and <= '9') and not '_') ||
            response.Trace is { Length: > 200 }) throw new PackageException("DEVELOPER_RESPONSE_INVALID");
        return response;
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int count = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (count is <= 0 or > MaximumFrameBytes) throw new PackageException("DEVELOPER_FRAME_INVALID");
        byte[] bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            void Unique(JsonElement value)
            {
                if (value.ValueKind == JsonValueKind.Object)
                {
                    HashSet<string> fields = new(StringComparer.Ordinal);
                    foreach (JsonProperty property in value.EnumerateObject())
                    { if (!fields.Add(property.Name)) throw new JsonException(); Unique(property.Value); }
                }
                else if (value.ValueKind == JsonValueKind.Array) foreach (JsonElement child in value.EnumerateArray()) Unique(child);
            }
            Unique(document.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new JsonException();
        }
        catch (JsonException) { throw new PackageException("DEVELOPER_FRAME_INVALID"); }
    }
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length is <= 0 or > MaximumFrameBytes) throw new PackageException("DEVELOPER_FRAME_INVALID");
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
    public static NamedPipeServerStream CreateServer(string hostRoot)
    {
        if (!OperatingSystem.IsWindows()) throw new PackageException("DEVELOPER_WINDOWS_REQUIRED");
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor($"O:{sid}G:{sid}D:P(A;;GA;;;{sid})", 1, out nint descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            // First instance, overlapped duplex, byte mode, remote clients forbidden, exactly one active client.
            SafePipeHandle handle = CreateNamedPipe(@"\\.\pipe\" + PipeName(hostRoot), 3 | 0x40000000u | 0x00080000u, 8, 1, 4096, 4096, 0, ref security);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public nint Descriptor; public int InheritHandle; }
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string value, uint revision, out nint descriptor, out uint size);
    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBufferSize, uint inBufferSize, uint timeout, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
