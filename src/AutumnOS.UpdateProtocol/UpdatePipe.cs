using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using AutumnOS.Launcher;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.UpdateProtocol;

internal sealed record UpdateMessage(string Kind, string TransactionId, string BuildId = "", string SourceId = "", string Digest = "");
internal static class UpdatePipe
{
    internal static string Name(string id, string phase) => LauncherCoordinator.ScopeName("LabChronicles.AutumnOS.Update.v1") + "." + id + "." + phase;
    internal static NamedPipeServerStream Server(string id, string phase) => LocalPipeServer.Create(Name(id, phase), true);
    internal static async Task<NamedPipeClientStream> ConnectAsync(string id, string phase, int expectedPid, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", Name(id, phase), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(ct).ConfigureAwait(false); pipe.ReadMode = PipeTransmissionMode.Message;
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) || pid != expectedPid || Process.GetProcessById((int)pid).SessionId != Process.GetCurrentProcess().SessionId)
                throw new IOException("UPDATE_PIPE_IDENTITY_INVALID");
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }
    internal static void VerifyClient(NamedPipeServerStream pipe, int expectedPid)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid != expectedPid || Process.GetProcessById((int)pid).SessionId != Process.GetCurrentProcess().SessionId)
            throw new IOException("UPDATE_PIPE_IDENTITY_INVALID");
    }
    internal static async Task SendAsync(PipeStream pipe, UpdateMessage message, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, UpdateFiles.Json);
        if (bytes.Length > 4096) throw new IOException("UPDATE_PIPE_MESSAGE_INVALID");
        byte[] packet = new byte[bytes.Length + 4]; BitConverter.TryWriteBytes(packet.AsSpan(0, 4), bytes.Length); bytes.CopyTo(packet, 4);
        await pipe.WriteAsync(packet, ct).ConfigureAwait(false);
    }
    internal static async Task<UpdateMessage> ReceiveAsync(PipeStream pipe, string id, string kind, CancellationToken ct)
    {
        byte[] length = new byte[4]; await pipe.ReadExactlyAsync(length, ct).ConfigureAwait(false);
        int count = BitConverter.ToInt32(length); if (count is < 2 or > 4096) throw new IOException("UPDATE_PIPE_MESSAGE_INVALID");
        byte[] bytes = new byte[count]; await pipe.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        if (!pipe.IsMessageComplete) throw new IOException("UPDATE_PIPE_MESSAGE_INVALID");
        var message = JsonSerializer.Deserialize<UpdateMessage>(bytes, UpdateFiles.Json);
        if (message is null || message.TransactionId != id || message.Kind != kind) throw new IOException("UPDATE_PIPE_MESSAGE_INVALID");
        return message;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle handle, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle handle, out uint pid);
}
