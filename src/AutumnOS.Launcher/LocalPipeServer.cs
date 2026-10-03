using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.Launcher;

internal static class LocalPipeServer
{
    internal static NamedPipeServerStream Create(string name, bool first)
    {
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        // Explicit protected DACL, creator is the sole allowed account. Native creation
        // also rejects remote SMB clients; managed PipeOptions cannot express that flag.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor($"O:{sid}G:{sid}D:P(A;;GA;;;{sid})", 1, out nint descriptor, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            uint open = 3 | 0x40000000u | (first ? 0x00080000u : 0u); // duplex, overlapped, first instance
            var handle = CreateNamedPipe(@"\\.\pipe\" + name, open, 4 | 2 | 8, 4, 512, 512, 0, ref security); // message/read-message/reject-remote
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle) { ReadMode = PipeTransmissionMode.Message }; }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public nint Descriptor; public int InheritHandle; }
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string value, uint revision, out nint descriptor, out uint size);
    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBufferSize, uint inBufferSize, uint timeout, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
