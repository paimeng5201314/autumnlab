using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.OwnedBrowserParent;

// One CPWL launch, with exact original handle/token admission before execution.
internal sealed class NetOnlyLaunch
{
    internal SafeProcessHandle? Process;
    private SafeWaitHandle? thread;
    public int create_process_with_logon_calls { get; private set; }
    public bool create_succeeded { get; private set; }
    public int create_native_error { get; private set; }
    public bool no_child_created_proven { get; private set; } = true;
    public uint original_pid { get; private set; }
    public ulong original_creation_filetime { get; private set; }
    public SessionIdentity? parent_identity { get; private set; }
    public SessionIdentity? child_identity { get; private set; }
    public bool token_admission_passed { get; private set; }
    public bool original_image_verified { get; private set; }
    public bool resumed { get; private set; }
    public uint? resume_return { get; private set; }
    public int? resume_native_error { get; private set; }
    public bool original_process_signaled { get; private set; }
    public bool original_thread_signaled { get; private set; }
    public uint? exit_code { get; private set; }
    public string stage { get; private set; } = "not_run";
    public List<string> failures { get; } = [];
    public int synthetic_credentials_logged => 0;
    public uint logon_flags => 2;
    public string lsa_cleanup => "not_claimed";
    internal void Start(string executable, string arguments, string workingDirectory)
    {
        if (create_process_with_logon_calls != 0 || Process is not null) throw new InvalidOperationException("CPWL_EXACTLY_ONCE_NO_RETRY");
        using var current = SessionNative.Current(); parent_identity = SessionNative.Identity(current);
        if (parent_identity.elevated || parent_identity.appcontainer || parent_identity.integrity_rid != 8192) throw new InvalidDataException("ORDINARY_MEDIUM_CORE_PARENT_REQUIRED");
        byte[] random = RandomNumberGenerator.GetBytes(32); char[] secret = new char[64]; const string digits = "0123456789ABCDEF";
        for (int i = 0; i < random.Length; i++) { secret[i * 2] = digits[random[i] >> 4]; secret[i * 2 + 1] = digits[random[i] & 15]; }
        CryptographicOperations.ZeroMemory(random);
        IntPtr password = IntPtr.Zero, environment = IntPtr.Zero;
        try
        {
            password = Marshal.AllocHGlobal((secret.Length + 1) * 2); Marshal.Copy(secret, 0, password, secret.Length); Marshal.WriteInt16(password, secret.Length * 2, 0);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            environment = Marshal.StringToHGlobalUni("SystemRoot=" + windows + "\0WINDIR=" + windows + "\0PATH=" + Path.Combine(windows, "System32") + "\0\0");
            var command = new StringBuilder(Quote(executable) + " " + arguments);
            if (command.Length >= 1024) throw new InvalidDataException("CPWL_FIXED_COMMAND_LENGTH");
            var startup = new SessionNative.StartupInfo { cb = Marshal.SizeOf<SessionNative.StartupInfo>(), desktop = "WinSta0\\Default" };
            string username = "AOProbe_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
            stage = "CPWL_once_suspended"; create_process_with_logon_calls++; no_child_created_proven = false;
            create_succeeded = SessionNative.CreateProcessWithLogonW(username, ".", password, 2, executable, command, 0x08000404, environment, workingDirectory, ref startup, out var process);
            create_native_error = create_succeeded ? 0 : Marshal.GetLastWin32Error(); original_pid = process.processId;
            if (process.process != IntPtr.Zero) Process = new SafeProcessHandle(process.process, true);
            if (process.thread != IntPtr.Zero) thread = new SafeWaitHandle(process.thread, true);
            no_child_created_proven = !create_succeeded && process.process == IntPtr.Zero && process.thread == IntPtr.Zero && process.processId == 0 && process.threadId == 0;
            if (!create_succeeded) throw new Win32Exception(create_native_error, stage);
            if (Process is null || Process.IsInvalid || thread is null || thread.IsInvalid || original_pid == 0) throw new InvalidDataException("CPWL_ORIGINAL_HANDLES_REQUIRED");
            stage = "suspended_NETONLY_token_admission";
            child_identity = SessionNative.Identity(Process); token_admission_passed = SessionNative.TokenAdmission(parent_identity, child_identity);
            original_image_verified = SessionNative.GetProcessId(Process) == original_pid && string.Equals(SessionNative.Image(Process), executable, StringComparison.OrdinalIgnoreCase);
            Native.Check(Native.GetProcessTimes(Process.DangerousGetHandle(), out var created, out _, out _, out _), "original_NETONLY_host_creation_time");
            original_creation_filetime = created.Value;
            if (!token_admission_passed || !original_image_verified || SessionNative.Exited(Process)) throw new InvalidDataException("NETONLY_OS_TOKEN_IMAGE_ADMISSION");
            stage = "resume_original_NETONLY_thread"; Resume();
            if (!resumed || resume_return != 1) throw new InvalidDataException("EXACT_SINGLE_ORIGINAL_SUSPEND");
            stage = "running";
        }
        catch (Exception error) { failures.Add(Program.FailureCode(error)); throw; }
        finally
        {
            Array.Clear(secret);
            if (password != IntPtr.Zero) { for (int i = 0; i <= secret.Length; i++) Marshal.WriteInt16(password, i * 2, 0); Marshal.FreeHGlobal(password); }
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }
    private void Resume()
    {
        if (thread is null || thread.IsInvalid || resumed) return;
        if (SessionNative.WaitOriginalThread(thread) == 0) { original_thread_signaled = true; return; }
        resume_return = SessionNative.ResumeThread(thread); resume_native_error = resume_return == uint.MaxValue ? Marshal.GetLastWin32Error() : 0;
        if (resume_return is 0 or 1) resumed = true;
    }
    internal async Task ObserveNaturalExit(Action save)
    {
        while (thread is not null && !thread.IsInvalid && !resumed && !original_thread_signaled)
        { try { Resume(); } catch (Exception error) { Add(error); } save(); if (!resumed && !original_thread_signaled) await Task.Delay(100); }
        if (Process is not null && !Process.IsInvalid)
        {
            while (!original_process_signaled)
            { try { original_process_signaled = SessionNative.Exited(Process); } catch (Exception error) { Add(error); } if (!original_process_signaled) { save(); await Task.Delay(100); } }
            try { exit_code = SessionNative.ExitCode(Process); } catch (Exception error) { Add(error); }
        }
        if (thread is not null && !thread.IsInvalid) { original_thread_signaled = SessionNative.WaitOriginalThread(thread) == 0; if (original_thread_signaled) thread.Dispose(); }
    }
    internal bool Exited => Process is not null && SessionNative.Exited(Process);
    internal void CloseOriginalAfterExit()
    { if (Process is not null) { if (!original_process_signaled || !original_thread_signaled) throw new InvalidDataException("ORIGINAL_NETONLY_PROCESS_THREAD_EXIT_REQUIRED"); Process.Dispose(); } }
    private void Add(Exception error) { if (failures.Count < 64) failures.Add(Program.FailureCode(error)); }
    internal static string Quote(string text) { if (text.Contains('"') || text.Contains('\0') || text.EndsWith('\\')) throw new InvalidDataException("PLAIN_FIXED_ARGUMENT"); return '"' + text + '"'; }
}
