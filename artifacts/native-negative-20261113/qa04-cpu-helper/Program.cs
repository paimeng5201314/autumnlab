using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

// This helper never creates a process or Job. The parent starts only this new
// image with its original handles, an atomic private Job and BelowNormal class.
// There is no PID, program, output-path or user-data argument.
if (args.Length == 0) return 0;
if (args.Length != 3 || args[0] != "--owned-cpu" || args[1] is not ("control" or "load") ||
    args[2].Length != 32 || args[2].Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
    return 64;
if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return 78;
try { return BoundedCpu.Run(args[1], args[2]); }
catch (Exception error) when (error is not OutOfMemoryException)
{
    // Exception messages and paths are never emitted.
    Console.Error.WriteLine("CPU_HELPER_FAILED|" + error.GetType().Name + "|" + unchecked((uint)error.HResult).ToString("x8", CultureInfo.InvariantCulture));
    return 70;
}

internal static class BoundedCpu
{
    private const int BufferBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(20);

    internal static int Run(string mode, string id)
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "cpu-worker-runs"));
        RequirePlainAncestors(root);
        Directory.CreateDirectory(root);
        string directory = Path.Combine(root, id);
        RequirePlainAncestors(directory);
        Directory.CreateDirectory(directory);
        string readyPath = Path.Combine(directory, "ready.json"), resultPath = Path.Combine(directory, "result.json");
        if (File.Exists(readyPath) || File.Exists(resultPath)) return 73;

        // Identical initialization and read-only shared data in both modes.
        byte[] buffer = new byte[BufferBytes];
        Array.Fill(buffer, (byte)0x5a);
        int workers = Math.Clamp(Environment.ProcessorCount, 1, 2);
        using var ready = new CountdownEvent(workers);
        using var start = new ManualResetEventSlim();
        using var current = Process.GetCurrentProcess();
        Exception? workerError = null;
        long iterations = 0, startTimestamp = 0;
        var threads = new Thread[workers];
        for (int i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(() =>
            {
                try
                {
                    ready.Signal();
                    start.Wait();
                    Span<byte> digest = stackalloc byte[32];
                    while (Stopwatch.GetElapsedTime(startTimestamp) < Duration)
                    {
                        if (mode == "control") Thread.Sleep(20);
                        else
                        {
                            // Recheck the monotonic cutoff before every fixed
                            // 32MiB operation; no mutable/shared application data.
                            SHA256.HashData(buffer, digest);
                            Interlocked.Increment(ref iterations);
                        }
                    }
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                { Interlocked.CompareExchange(ref workerError, error, null); }
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "owned-cpu-" + i.ToString(CultureInfo.InvariantCulture)
            };
            threads[i].Start();
        }
        if (!ready.Wait(TimeSpan.FromSeconds(3))) return 71;

        TimeSpan cpuBefore = current.TotalProcessorTime;
        string startUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        startTimestamp = Stopwatch.GetTimestamp();
        WriteNew(readyPath, new { status = "ready", mode, id, bufferBytes = BufferBytes, workers, startUtc });
        start.Set();
        // Foreground lifetime is bounded as well. Background worker threads do
        // not keep the helper alive if a known finite hashing operation stalls.
        foreach (Thread thread in threads)
        {
            TimeSpan remaining = TimeSpan.FromSeconds(24) - Stopwatch.GetElapsedTime(startTimestamp);
            if (remaining <= TimeSpan.Zero || !thread.Join(remaining)) return 72;
        }
        if (workerError is not null) throw new InvalidOperationException("CPU_HELPER_WORKER_FAILED", workerError);
        string endUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        double wallSeconds = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
        current.Refresh();
        double cpuSeconds = (current.TotalProcessorTime - cpuBefore).TotalSeconds;
        WriteNew(resultPath, new
        {
            status = "completed", mode, id, bufferBytes = BufferBytes, workers,
            iterations = Interlocked.Read(ref iterations), wallSeconds, cpuSeconds,
            startUtc, endUtc, peakWorkingSetBytes = current.PeakWorkingSet64
        });
        GC.KeepAlive(buffer);
        return 0;
    }

    private static void WriteNew(string path, object value)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(output, value, new JsonSerializerOptions { WriteIndented = true });
        output.Flush(true);
    }

    private static void RequirePlainAncestors(string path)
    {
        for (string? p = Path.GetFullPath(path); p is not null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("CPU_HELPER_PLAIN_ARTIFACT_PATH_REQUIRED");
    }
}
