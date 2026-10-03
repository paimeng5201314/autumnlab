using System.Diagnostics;
using AutumnOS.Launcher;
using AutumnOS.Update;

namespace AutumnOS.UpdateProtocol;

/// <summary>Named mutex ownership stays on its dedicated thread; no cross-process/thread transfer.</summary>
public sealed class UpdateMutexLease : IDisposable
{
    private readonly ManualResetEventSlim release = new();
    private readonly Thread owner;
    private int disposed;
    private UpdateMutexLease(string name, TimeSpan timeout)
    {
        var acquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        owner = new Thread(() =>
        {
            try
            {
                using var mutex = new Mutex(false, name, new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = true });
                bool held;
                try { held = mutex.WaitOne(timeout); } catch (AbandonedMutexException) { held = true; }
                acquired.TrySetResult(held);
                if (!held) return;
                try { release.Wait(); } finally { mutex.ReleaseMutex(); }
            }
            catch (Exception e) { acquired.TrySetException(e); }
        }) { IsBackground = true, Name = "AutumnOS maintenance ownership" };
        owner.Start();
        if (!acquired.Task.GetAwaiter().GetResult()) { Dispose(); throw new IOException("UPDATE_COORDINATION_BUSY"); }
    }
    public static UpdateMutexLease AcquireMaintenance(TimeSpan? timeout = null) => new(LauncherCoordinator.ScopeName("LabChronicles.AutumnOS.Maintenance.v1"), timeout ?? TimeSpan.Zero);
    public static UpdateMutexLease AcquireBusiness(TimeSpan? timeout = null) => new(LauncherCoordinator.ScopeName(), timeout ?? TimeSpan.Zero);
    public static bool MaintenanceActive()
    { try { using var lease = AcquireMaintenance(); return false; } catch (IOException) { return true; } }
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) != 0) return; release.Set(); owner.Join(); release.Dispose(); }
}

public static class UpdateRootLease
{
    public static FileStream AcquireShared(string root) => Open(root, FileShare.Read);
    public static FileStream AcquireExclusive(string root) => Open(root, FileShare.None);
    private static FileStream Open(string root, FileShare share)
    {
        string path = Path.Combine(UpdateFiles.Root(root), ".autumnos-runtime.lock");
        UpdatePaths.EnsureNoReparsePoints(path);
        if (!File.Exists(path))
        {
            try { using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); created.Flush(true); }
            catch (IOException) when (File.Exists(path)) { }
        }
        return new FileStream(path, FileMode.Open, FileAccess.Read, share);
    }
}
