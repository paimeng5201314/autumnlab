using System.Runtime.CompilerServices;
using AutumnOS.NativeHost.Windows;

namespace AutumnOS.Tests;

internal static class BrowserResponseStreamTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("native.response_stream.repeated_exact_length_reads_do_not_exhaust_lifetime_budget", Repeated);
        yield return ("native.response_stream.unconsumed_and_partial_responses_keep_concurrent_limit", PendingLimit);
        yield return ("native.response_stream.removing_tracking_does_not_dispose_async_reader", DelayedReader);
        yield return ("native.response_stream.seek_and_empty_read_do_not_fake_consumption", SeekAndEmptyRead);
        yield return ("native.response_stream.empty_body_and_denial_need_no_pending_slot", EmptyBody);
        yield return ("native.response_stream.body_remains_readonly_and_rejects_invalid_reads", ReadOnly);
        yield return ("native.response_stream.async_copy_and_byte_reads_complete_exact_content", ReadForms);
        yield return ("native.response_stream.background_reads_keep_cursor_and_completion_consistent", ConcurrentReads);
        yield return ("native.response_stream.canceled_read_keeps_response_pending_and_readable", CanceledRead);
        yield return ("native.response_stream.pending_streams_close_without_mutating_collection", ClosePending);
        yield return ("native.response_stream.completed_stream_is_not_rooted_by_host_list", Collection);
    }

    private static void Repeated()
    {
        var pending = new List<Stream>();
        byte[] body = Enumerable.Range(0, 257).Select(i => (byte)i).ToArray();
        byte[] result = new byte[body.Length];
        for (int i = 0; i < 4096; i++)
        {
            Stream stream = BrowserResponseStream.Open(pending, body);
            Check(pending.Count == 1);
            Check(stream.Read(result, 0, result.Length) == result.Length && result.SequenceEqual(body));
            // Deliberately no EOF read, Dispose or GC between requests.
        }
    }

    private static void PendingLimit()
    {
        var pending = new List<Stream>();
        for (int i = 0; i < BrowserResponseStream.MaximumPending; i++) BrowserResponseStream.Open(pending, [1, 2]);
        ExpectLimit(() => BrowserResponseStream.Open(pending, [3]));
        Stream first = pending[0];
        Check(first.ReadByte() == 1);
        ExpectLimit(() => BrowserResponseStream.Open(pending, [3]));
        Check(first.ReadByte() == 2);
        Stream admitted = BrowserResponseStream.Open(pending, [3]);
        Check(pending.Count == BrowserResponseStream.MaximumPending && admitted.ReadByte() == 3);
        Check(!pending.Contains(first));
    }

    private static void DelayedReader()
    {
        var pending = new List<Stream>();
        byte[] body = [7, 9, 11];
        Stream retainedByConsumer = BrowserResponseStream.Open(pending, body);
        Check(retainedByConsumer.ReadByte() == 7);
        BrowserResponseStream.Open(pending, [1]);
        Check(pending.Count == 2 && retainedByConsumer.ReadByte() == 9 && retainedByConsumer.ReadByte() == 11);
        BrowserResponseStream.Open(pending, [2]);
        Check(!pending.Contains(retainedByConsumer));
        GC.Collect(); GC.WaitForPendingFinalizers();
        Check(retainedByConsumer.CanRead && retainedByConsumer.ReadByte() == -1);
        retainedByConsumer.Position = 0;
        byte[] copy = new byte[body.Length];
        Check(retainedByConsumer.Read(copy.AsSpan()) == body.Length && copy.SequenceEqual(body));
        Check(retainedByConsumer.Length == body.Length);
    }

    private static void SeekAndEmptyRead()
    {
        var pending = new List<Stream>();
        Stream stream = BrowserResponseStream.Open(pending, [1, 2, 3]);
        Check(stream.Read([], 0, 0) == 0 && stream.Read(Span<byte>.Empty) == 0);
        stream.Seek(0, SeekOrigin.End);
        Check(stream.ReadByte() == -1);
        BrowserResponseStream.Open(pending, [4]);
        Check(pending.Contains(stream));
        stream.Position = 2;
        Check(stream.ReadByte() == 3);
        BrowserResponseStream.Open(pending, [5]);
        Check(pending.Contains(stream));
        stream.Position = 0;
        Check(stream.ReadByte() == 1 && stream.ReadByte() == 2);
        BrowserResponseStream.Open(pending, [6]);
        Check(pending.Contains(stream));
        Check(stream.ReadByte() == 3);
        BrowserResponseStream.Open(pending, [7]);
        Check(!pending.Contains(stream));
    }

    private static void EmptyBody()
    {
        var pending = new List<Stream>();
        for (int i = 0; i < 1024; i++) Check(ReferenceEquals(BrowserResponseStream.Open(pending, []), Stream.Null));
        Check(pending.Count == 0);
        for (int i = 0; i < BrowserResponseStream.MaximumPending; i++) BrowserResponseStream.Open(pending, [1]);
        Check(ReferenceEquals(BrowserResponseStream.Open(pending, []), Stream.Null));
        Check(pending.Count == BrowserResponseStream.MaximumPending);
    }

    private static void ReadOnly()
    {
        var pending = new List<Stream>();
        byte[] body = [2, 4];
        Stream stream = BrowserResponseStream.Open(pending, body);
        Check(!stream.CanWrite);
        Expect<NotSupportedException>(() => stream.Write([8], 0, 1));
        Expect<NotSupportedException>(() => stream.WriteByte(8));
        Expect<NotSupportedException>(() => stream.SetLength(1));
        Expect<ArgumentException>(() => Check(stream.Read(new byte[1], 0, 2) == 2));
        Check(stream.Position == 0 && stream.Length == 2 && body.SequenceEqual(new byte[] { 2, 4 }));
        BrowserResponseStream.Open(pending, [1]);
        Check(pending.Contains(stream));
    }

    private static void ReadForms()
    {
        var pending = new List<Stream>();
        byte[] body = Enumerable.Range(0, 100003).Select(i => (byte)(i * 7)).ToArray();
        Stream stream = BrowserResponseStream.Open(pending, body);
        using var copy = new MemoryStream();
        stream.CopyToAsync(copy, 137).GetAwaiter().GetResult();
        Check(copy.ToArray().SequenceEqual(body));
        Stream bytes = BrowserResponseStream.Open(pending, [3, 5]);
        Check(!pending.Contains(stream));
        Check(bytes.ReadByte() == 3 && bytes.ReadByte() == 5 && bytes.ReadByte() == -1);
        BrowserResponseStream.Open(pending, [8]);
        Check(!pending.Contains(bytes));
    }

    private static void ClosePending()
    {
        var pending = new List<Stream>();
        Stream unread = BrowserResponseStream.Open(pending, [1, 2]);
        Stream partial = BrowserResponseStream.Open(pending, [3, 4]);
        Check(partial.ReadByte() == 3);
        foreach (Stream stream in pending) stream.Dispose();
        pending.Clear();
        Check(!unread.CanRead && !partial.CanRead);
        Expect<ObjectDisposedException>(() => unread.ReadByte());
        Expect<ObjectDisposedException>(() => partial.ReadByte());
    }

    private static void ConcurrentReads()
    {
        var pending = new List<Stream>();
        byte[] body = Enumerable.Repeat((byte)43, 200003).ToArray();
        Stream stream = BrowserResponseStream.Open(pending, body);
        Task<int>[] readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            byte[] buffer = new byte[127];
            int total = 0, read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                Check(buffer.Take(read).All(value => value == 43));
                total += read;
            }
            return total;
        })).ToArray();
        Task.WaitAll(readers);
        Check(readers.Sum(task => task.Result) == body.Length && stream.Position == body.Length);
        BrowserResponseStream.Open(pending, [1]);
        Check(!pending.Contains(stream) && pending.Count == 1);
    }

    private static void CanceledRead()
    {
        var pending = new List<Stream>();
        Stream stream = BrowserResponseStream.Open(pending, [5, 7]);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Expect<OperationCanceledException>(() => Check(stream.ReadAsync(new byte[2].AsMemory(), canceled.Token)
            .GetAwaiter().GetResult() == 2));
        Check(stream.Position == 0);
        BrowserResponseStream.Open(pending, [1]);
        Check(pending.Contains(stream) && stream.ReadByte() == 5 && stream.ReadByte() == 7);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ReleaseCompleted(List<Stream> pending)
    {
        Stream stream = BrowserResponseStream.Open(pending, [1]);
        var weak = new WeakReference(stream);
        Check(stream.ReadByte() == 1);
        BrowserResponseStream.Open(pending, [2]);
        return weak;
    }

    private static void Collection()
    {
        var pending = new List<Stream>();
        WeakReference old = ReleaseCompleted(pending);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Check(!old.IsAlive && pending.Count == 1 && pending[0].ReadByte() == 2);
        GC.KeepAlive(pending);
    }

    private static void ExpectLimit(Action action)
    {
        try { action(); }
        catch (InvalidDataException error) when (error.Message == "BROWSER_RESPONSE_STREAM_LIMIT") { return; }
        throw new InvalidOperationException("Pending response quota was bypassed.");
    }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool condition)
    { if (!condition) throw new InvalidOperationException("Response stream lifetime mismatch."); }
}
