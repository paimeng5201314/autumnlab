using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.Launcher;

public readonly record struct LauncherStart(LauncherCoordinator? Primary, int ExitCode, RecallOutcome? Outcome);
public readonly record struct LauncherEvent(string EventName, int RequestPid, RecallResult Result);

/// <summary>Owns the mutex on the entry thread until the WinUI message loop has finished.</summary>
public sealed class LauncherCoordinator : IDisposable
{
    public const string ProductKey = "LabChronicles.AutumnOS.Launcher.v1";
    public static readonly TimeSpan ForwardBudget = TimeSpan.FromSeconds(15);
    private readonly Mutex mutex;
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource<Func<int, CancellationToken, Task<RecallResult>>> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<NamedPipeServerStream> servers = [];
    private readonly List<Task> workers = [];
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private int isStopping;
    public int ProcessId { get; } = Environment.ProcessId;
    public int SessionId { get; } = Process.GetCurrentProcess().SessionId;
    public string PipeName { get; }
    public bool IsStopping => Volatile.Read(ref isStopping) != 0;
    public event Action<LauncherEvent>? Observed;

    public static string ScopeName(string productKey = ProductKey)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (productKey.Length is < 1 or > 100 || productKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-')) throw new ArgumentException("Invalid product key.");
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("No Windows user SID.");
        string user = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid))).ToLowerInvariant();
        return $"{productKey}.{user}.s{Process.GetCurrentProcess().SessionId}";
    }

    public static LauncherStart Enter(string productKey = ProductKey, TimeSpan? budget = null)
    {
        string scope = ScopeName(productKey);
        var gate = new Mutex(false, scope, new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = true });
        bool transferred = false;
        try
        {
            var clock = Stopwatch.StartNew();
            TimeSpan limit = budget ?? ForwardBudget;
            if (limit <= TimeSpan.Zero || limit > ForwardBudget) throw new ArgumentOutOfRangeException(nameof(budget));
            while (clock.Elapsed < limit)
            {
                bool acquired;
                try { acquired = gate.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (acquired)
                {
                    try
                    {
                        var primary = new LauncherCoordinator(gate, scope);
                        transferred = true;
                        return new(primary, 0, null);
                    }
                    catch { gate.ReleaseMutex(); throw; }
                }
                // No XAML/COM dispatcher exists yet. Pipe I/O runs on the pool; the mutex
                // acquisition always stays on this entry thread, including crash recovery.
                var reply = Task.Run(() => ForwardAsync(scope, limit - clock.Elapsed)).GetAwaiter().GetResult();
                if (reply is { } outcome)
                {
                    if (outcome is RecallOutcome.Foreground or RecallOutcome.AttentionRequested) return new(null, 0, outcome);
                    if (outcome == RecallOutcome.Rejected) return new(null, 21, outcome);
                    if (outcome == RecallOutcome.Timeout) return new(null, 20, outcome);
                    // An owner stopping is not yet dead. Retry only within the same budget;
                    // a new primary still requires actual mutex acquisition.
                }
                Thread.Sleep(50);
            }
            return new(null, 20, RecallOutcome.Timeout);
        }
        finally { if (!transferred) gate.Dispose(); }
    }

    private LauncherCoordinator(Mutex gate, string scope)
    {
        mutex = gate; PipeName = scope;
        try
        {
            // Keep every endpoint allocated for the complete owner lifetime. Four bounded
            // listeners permit cold-start bursts without an unbounded request/task queue.
            for (int i = 0; i < 4; i++)
                servers.Add(LocalPipeServer.Create(scope, i == 0));
            foreach (var server in servers) workers.Add(Task.Run(() => ServeAsync(server)));
        }
        catch { foreach (var server in servers) server.Dispose(); stopping.Dispose(); throw; }
    }

    public void SetReady(Func<int, CancellationToken, Task<RecallResult>> recall) => ready.TrySetResult(recall);

    private async Task ServeAsync(NamedPipeServerStream server)
    {
        while (!IsStopping)
        {
            try
            {
                await server.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
                using var requestTime = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                requestTime.CancelAfter(TimeSpan.FromSeconds(12));
                var token = requestTime.Token;
                if (!Native.GetNamedPipeClientProcessId(server.SafePipeHandle, out uint clientPid) ||
                    !Native.ProcessIdToSessionId(clientPid, out uint session) || session != SessionId) continue;
                byte[] bytes = new byte[LauncherProtocol.RequestSize];
                await server.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                if (!server.IsMessageComplete || !LauncherProtocol.TryReadRequest(bytes, out var nonce))
                { Observe("request_rejected", (int)clientPid, new(RecallOutcome.Rejected, 0)); continue; }
                RecallResult result;
                try
                {
                    var handler = await ready.Task.WaitAsync(token).ConfigureAwait(false);
                    result = IsStopping ? new(RecallOutcome.Closing, 0) : await handler((int)clientPid, token).WaitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { result = new(IsStopping ? RecallOutcome.Closing : RecallOutcome.Timeout, 0); }
                catch (Exception error) when (error is not OutOfMemoryException) { result = new(RecallOutcome.Rejected, 0); }
                Observe("recall_handled", (int)clientPid, result);
                if (!stopping.IsCancellationRequested)
                {
                    using var responseTime = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await server.WriteAsync(LauncherProtocol.Response(nonce, ProcessId, SessionId, result), responseTime.Token).ConfigureAwait(false);
                    // DisconnectNamedPipe discards unread buffered replies. The client
                    // reads the complete ACK and then closes; wait for that EOF within
                    // the same finite budget instead of an unbounded WaitForPipeDrain.
                    byte[] endOfExchange = new byte[1];
                    if (await server.ReadAsync(endOfExchange, responseTime.Token).ConfigureAwait(false) != 0)
                        Observe("request_rejected", (int)clientPid, new(RecallOutcome.Rejected, 0));
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException)
            {
                if (!IsStopping)
                {
                    Observe("transport_unavailable", 0, new(RecallOutcome.Rejected, 0));
                    // An unavailable transport must not create a CPU/logging hot loop.
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }
            finally
            {
                // A failed write can put PipeStream in Broken: IsConnected then becomes
                // false, but Disconnect is still required before reusing this listener.
                try { server.Disconnect(); }
                catch (Exception error) when (error is IOException or InvalidOperationException) { }
            }
        }
    }

    private static async Task<RecallOutcome?> ForwardAsync(string scope, TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return RecallOutcome.Timeout;
        using var total = new CancellationTokenSource(remaining);
        using var pipe = new NamedPipeClientStream(".", scope, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        bool requestSent = false;
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(total.Token))
            { connect.CancelAfter(TimeSpan.FromMilliseconds(500)); await pipe.ConnectAsync(connect.Token).ConfigureAwait(false); }
            pipe.ReadMode = PipeTransmissionMode.Message;
            if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid) ||
                !Native.ProcessIdToSessionId(serverPid, out uint session) || session != Process.GetCurrentProcess().SessionId) return RecallOutcome.Rejected;
            // Grant only this validated pipe server, never ASFW_ANY. Windows may deny it.
            Native.AllowSetForegroundWindow(serverPid);
            var nonce = Guid.NewGuid();
            requestSent = true;
            await pipe.WriteAsync(LauncherProtocol.Request(nonce), total.Token).ConfigureAwait(false);
            byte[] response = new byte[LauncherProtocol.ResponseSize];
            await pipe.ReadExactlyAsync(response, total.Token).ConfigureAwait(false);
            if (!pipe.IsMessageComplete || !LauncherProtocol.TryReadResponse(response, nonce, (int)serverPid, (int)session, out var result)) return RecallOutcome.Rejected;
            return result.Outcome;
        }
        catch (OperationCanceledException) { return total.IsCancellationRequested ? RecallOutcome.Timeout : null; }
        // Once a recall may have been sent, a lost ACK is not permission to keep
        // recalling the window. Report the uncertain outcome without repeated focus.
        catch (IOException) { return requestSent ? RecallOutcome.Timeout : null; }
        catch (UnauthorizedAccessException) { return RecallOutcome.Rejected; }
    }

    public void Observe(string name, int requestPid, RecallResult result)
    { try { Observed?.Invoke(new(name, requestPid, result)); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }

    public void BeginStop()
    {
        if (Interlocked.Exchange(ref isStopping, 1) != 0) return;
        Observe("stopping", 0, new(RecallOutcome.Closing, 0));
        stopping.Cancel();
        foreach (var server in servers) server.Dispose();
    }

    public void Dispose()
    {
        if (Environment.CurrentManagedThreadId != ownerThread) throw new InvalidOperationException("Release on the owner entry thread.");
        BeginStop();
        // No wait on UI dispatchers. All waits/I/O have cancellation; releasing the gate is
        // the final action after the main window and WinUI application have shut down.
        mutex.ReleaseMutex(); mutex.Dispose();
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ProcessIdToSessionId(uint pid, out uint session);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AllowSetForegroundWindow(uint pid);
    }
}
