using AutumnOS.NativeHost.Windows;

namespace AutumnOS.Tests;

// The semaphore is a transport-contract fixture. These exercise the actual
// production queue/token; they do not claim a native pipe or browser run.
internal static class NativeInputQueueTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("native.input.invalidate_cancels_a_dequeued_input_waiting_before_write", () => BeforeWrite().GetAwaiter().GetResult());
        yield return ("native.input.old_queue_is_discarded_and_next_epoch_remains_usable", Epochs);
        yield return ("native.input.capacity_kind_and_bytes_are_bounded", Capacity);
        yield return ("native.input.owns_payload_and_rejects_foreign_epoch_item", Ownership);
        yield return ("native.input.terminal_epoch_and_dispose_cancel_without_overflow", Terminal);
        yield return ("native.input.cancellation_callbacks_run_outside_monitor", Callback);
        yield return ("native.input.pending_composition_is_not_discarded_or_mistaken_for_delivery", PendingComposition);
        yield return ("native.input.pointer_move_burst_keeps_last_position_and_click_order", MoveBurst);
        yield return ("native.input.pointer_coalescing_does_not_cross_keys_composition_or_pointer_edges", MoveBarriers);
        yield return ("native.input.pointer_move_rejects_invalid_viewport_and_retains_capacity", MoveBounds);
        yield return ("native.input.pointer_move_does_not_mutate_dequeued_or_retired_items", MoveOwnership);
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("NATIVE_INPUT_QUEUE_BOUNDARY_ASSERTION"); }
    private static NativeInputItem Take(NativeInputQueue queue)
    { Check(queue.TryDequeue(out var item)); return item!; }
    private static async Task BeforeWrite()
    {
        using var queue = new NativeInputQueue();
        using var transport = new SemaphoreSlim(0, 1);
        Check(queue.TryEnqueue(NativeViewKind.Key, "old"u8));
        var old = Take(queue);
        int written = 0;
        async Task Send(NativeInputItem item)
        {
            await transport.WaitAsync(item.BeforeWriteCancellation);
            try { item.BeforeWriteCancellation.ThrowIfCancellationRequested(); written++; }
            finally { transport.Release(); }
        }
        Task blocked = Send(old); Check(!blocked.IsCompleted);
        Check(queue.Invalidate());
        try { await blocked.WaitAsync(TimeSpan.FromSeconds(2)); throw new InvalidOperationException("Canceled old input reached write."); }
        catch (OperationCanceledException) { }
        Check(written == 0 && !queue.IsCurrent(old));
        Check(queue.TryEnqueue(NativeViewKind.Key, "new"u8));
        var next = Take(queue); transport.Release();
        await Send(next).WaitAsync(TimeSpan.FromSeconds(2));
        Check(written == 1 && queue.IsCurrent(next));
    }
    private static void Epochs()
    {
        using var queue = new NativeInputQueue();
        Check(queue.TryEnqueue(NativeViewKind.Key, "first"u8));
        var taken = Take(queue);
        Check(queue.TryEnqueue(NativeViewKind.Pointer, "queued"u8));
        Check(queue.Invalidate() && queue.Epoch == 1 && taken.BeforeWriteCancellation.IsCancellationRequested);
        Check(!queue.TryDequeue(out _) && queue.TryEnqueue(NativeViewKind.Composition, "next"u8));
        var next = Take(queue); Check(next.Epoch == 1 && !next.BeforeWriteCancellation.IsCancellationRequested && queue.IsCurrent(next));
    }
    private static void Capacity()
    {
        using var queue = new NativeInputQueue();
        Check(!queue.TryEnqueue(NativeViewKind.Key, []));
        Check(!queue.TryEnqueue(NativeViewKind.Key, new byte[NativeInputQueue.MaximumPayloadBytes + 1]));
        foreach (NativeViewKind kind in Enum.GetValues<NativeViewKind>())
            if (kind is not (NativeViewKind.Key or NativeViewKind.Pointer or NativeViewKind.Composition))
                Check(!queue.TryEnqueue(kind, "x"u8));
        for (int index = 0; index < NativeInputQueue.Capacity; index++)
            Check(queue.TryEnqueue(NativeViewKind.Key, new byte[NativeInputQueue.MaximumPayloadBytes]));
        Check(!queue.TryEnqueue(NativeViewKind.Key, "x"u8));
        _ = Take(queue); Check(queue.TryEnqueue(NativeViewKind.Pointer, "x"u8));
    }
    private static void Ownership()
    {
        using var queue = new NativeInputQueue(); using var other = new NativeInputQueue();
        byte[] caller = [1, 2, 3];
        Check(queue.TryEnqueue(NativeViewKind.Key, caller, true)); caller[0] = 9;
        var item = Take(queue); byte[] exported = item.CopyPayload(); exported[1] = 9;
        Check(item.CopyPayload().AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }) && item.Gesture);
        Check(queue.IsCurrent(item) && !other.IsCurrent(item));
    }
    private static void Terminal()
    {
        using var queue = new NativeInputQueue(long.MaxValue - 1);
        Check(queue.Invalidate() && queue.Epoch == long.MaxValue);
        Check(queue.TryEnqueue(NativeViewKind.Key, "terminal"u8)); var item = Take(queue);
        Check(!queue.Invalidate() && queue.IsClosed && queue.Epoch == long.MaxValue && item.BeforeWriteCancellation.IsCancellationRequested);
        Check(!queue.TryEnqueue(NativeViewKind.Key, "blocked"u8) && !queue.TryDequeue(out _) && !queue.Invalidate());
        queue.Dispose(); queue.Dispose();
        using var disposed = new NativeInputQueue(); Check(disposed.TryEnqueue(NativeViewKind.Pointer, "close"u8));
        var original = Take(disposed); disposed.Dispose();
        Check(original.BeforeWriteCancellation.IsCancellationRequested && !disposed.IsCurrent(original) && !disposed.TryEnqueue(NativeViewKind.Key, "blocked"u8));
    }
    private static void Callback()
    {
        using var queue = new NativeInputQueue(); Check(queue.TryEnqueue(NativeViewKind.Key, "old"u8));
        var old = Take(queue);
        bool callbackRan = false;
        using var registration = old.BeforeWriteCancellation.Register(() =>
        {
            Task read = Task.Run(() => Check(queue.Epoch == 1 && queue.TryEnqueue(NativeViewKind.Key, "new"u8)));
            Check(read.Wait(TimeSpan.FromSeconds(2))); callbackRan = true;
        });
        Check(queue.Invalidate() && callbackRan && queue.IsCurrent(Take(queue)));
    }
    private static void PendingComposition()
    {
        using var queue = new NativeInputQueue();
        Check(!queue.HasPending);
        Check(queue.TryEnqueue(NativeViewKind.Composition, "{\"action\":\"start\",\"text\":\"\"}"u8));
        Check(queue.TryEnqueue(NativeViewKind.Composition, "{\"action\":\"commit\",\"text\":\"test\"}"u8));
        Check(queue.HasPending);
        var start = Take(queue);
        Check(queue.HasPending && queue.IsCurrent(start));
        var commit = Take(queue);
        // Empty means no queued input, not an ACK: the separately owned item still
        // has its live before-write cancellation and must be awaited by the pump.
        Check(!queue.HasPending && queue.IsCurrent(commit) && !commit.BeforeWriteCancellation.IsCancellationRequested);
        Check(commit.CopyPayload().AsSpan().SequenceEqual("{\"action\":\"commit\",\"text\":\"test\"}"u8));
        Check(queue.TryEnqueue(NativeViewKind.Composition, "new"u8) && queue.HasPending);
        Check(queue.Invalidate() && !queue.HasPending && !queue.IsCurrent(commit) && commit.BeforeWriteCancellation.IsCancellationRequested);
        Check(queue.TryEnqueue(NativeViewKind.Composition, "queued"u8) && queue.HasPending);
        queue.Dispose(); Check(!queue.HasPending);
    }
    private static NativePointerCommand Pointer(NativeInputItem item, bool buttonDown = false) =>
        NativePointerInput.Parse(item.CopyPayload(), buttonDown);
    private static void MoveBurst()
    {
        using var queue = new NativeInputQueue();
        Check(queue.TryEnqueue(NativeViewKind.Pointer, "{\"action\":\"down\",\"x\":1,\"y\":2}"u8, true));
        for (int i = 0; i < 1000; i++) Check(queue.TryEnqueuePointerMove(i % 800, i % 600, NativeViewport.Default));
        Check(queue.TryEnqueue(NativeViewKind.Pointer, "{\"action\":\"up\",\"x\":199,\"y\":399}"u8));
        Check(queue.Snapshot == new NativeInputQueueSnapshot(3, 1, false));
        var down = Take(queue); Check(down.Gesture && Pointer(down).NextButtonDown);
        var move = Take(queue); var command = Pointer(move, true);
        Check(!move.Gesture && command.Action == "move" && command.X == 199 && command.Y == 399 && command.NextButtonDown);
        Check(Pointer(Take(queue), true).Action == "up" && !queue.HasPending);
    }
    private static void MoveBarriers()
    {
        foreach (NativeViewKind barrier in new[] { NativeViewKind.Key, NativeViewKind.Composition, NativeViewKind.Pointer })
        {
            using var queue = new NativeInputQueue();
            Check(queue.TryEnqueuePointerMove(10, 20, NativeViewport.Default));
            Check(queue.TryEnqueue(barrier, "barrier"u8));
            Check(queue.TryEnqueuePointerMove(30, 40, NativeViewport.Default));
            Check(queue.TryEnqueuePointerMove(50, 60, NativeViewport.Default));
            Check(queue.Snapshot == new NativeInputQueueSnapshot(3, 2, false));
            Check(Pointer(Take(queue)).X == 10);
            var between = Take(queue); Check(between.Kind == barrier && between.CopyPayload().AsSpan().SequenceEqual("barrier"u8));
            Check(Pointer(Take(queue)).X == 50);
        }
    }
    private static void MoveBounds()
    {
        using var queue = new NativeInputQueue();
        foreach (var (x, y) in new[] { (-1, 0), (800, 0), (0, -1), (0, 600), (int.MaxValue, int.MaxValue) })
            Check(!queue.TryEnqueuePointerMove(x, y, NativeViewport.Default));
        Check(!queue.TryEnqueuePointerMove(0, 0, default) && !queue.HasPending);
        var portrait = new NativeViewport(320, 1000);
        Check(!queue.TryEnqueuePointerMove(320, 600, portrait));
        Check(queue.TryEnqueuePointerMove(319, 999, portrait));
        Check(NativePointerInput.Parse(Take(queue).CopyPayload(), false, portrait).Y == 999);
        for (int i = 0; i < NativeInputQueue.Capacity - 1; i++) Check(queue.TryEnqueue(NativeViewKind.Key, "key"u8));
        Check(queue.TryEnqueuePointerMove(1, 2, NativeViewport.Default));
        Check(queue.TryEnqueuePointerMove(3, 4, NativeViewport.Default));
        Check(queue.Snapshot == new NativeInputQueueSnapshot(32, 1, false));
        Check(!queue.TryEnqueue(NativeViewKind.Key, "key"u8));
        _ = Take(queue);
        Check(queue.TryEnqueue(NativeViewKind.Pointer, "up"u8));
        Check(!queue.TryEnqueuePointerMove(5, 6, NativeViewport.Default) && queue.Snapshot.PendingCount == 32);
    }
    private static void MoveOwnership()
    {
        using var queue = new NativeInputQueue();
        Check(queue.TryEnqueuePointerMove(1, 2, NativeViewport.Default));
        var original = Take(queue);
        Check(queue.TryEnqueuePointerMove(3, 4, NativeViewport.Default));
        Check(queue.TryEnqueuePointerMove(5, 6, NativeViewport.Default));
        Check(Pointer(original).X == 1 && Pointer(Take(queue)).X == 5 && queue.IsCurrent(original));
        Check(queue.TryEnqueuePointerMove(7, 8, NativeViewport.Default));
        Check(queue.Invalidate() && !queue.HasPending && original.BeforeWriteCancellation.IsCancellationRequested && !queue.IsCurrent(original));
        Check(queue.TryEnqueuePointerMove(9, 10, NativeViewport.Default));
        var next = Take(queue); Check(Pointer(next).X == 9 && next.Epoch == 1 && !next.BeforeWriteCancellation.IsCancellationRequested);
        queue.Dispose(); Check(queue.Snapshot == new NativeInputQueueSnapshot(0, 0, true));
        Check(!queue.TryEnqueuePointerMove(1, 2, NativeViewport.Default));
    }
}
