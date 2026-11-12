using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace AutumnOS.NativeHost.Windows;

public sealed class NativeInputItem
{
    private readonly byte[] payload;
    internal NativeInputQueue Owner { get; }
    public NativeViewKind Kind { get; }
    public long Epoch { get; }
    public bool Gesture { get; }
    public CancellationToken BeforeWriteCancellation { get; }
    internal bool CoalesciblePointerMove { get; }
    internal NativeInputItem(NativeInputQueue owner, NativeViewKind kind, long epoch, bool gesture, byte[] payload, CancellationToken cancellation,
        bool coalesciblePointerMove = false)
    { Owner = owner; Kind = kind; Epoch = epoch; Gesture = gesture; this.payload = payload; BeforeWriteCancellation = cancellation;
        CoalesciblePointerMove = coalesciblePointerMove; }
    public byte[] CopyPayload() => payload.ToArray();
}

public readonly record struct NativeInputQueueSnapshot(int PendingCount, int PendingPointerMoves, bool IsClosed);

// Owns both queued bytes and cancellation of an already-dequeued input that
// is still waiting for the original transport's before-write admission.
public sealed class NativeInputQueue : IDisposable
{
    public const int Capacity = 32;
    public const int MaximumPayloadBytes = 4096;
    private readonly object sync = new();
    private readonly LinkedList<NativeInputItem> queue = new();
    private CancellationTokenSource? current = new();
    private long epoch;
    private bool closed;
    public NativeInputQueue(long initialEpoch = 0)
    { if (initialEpoch < 0) throw new ArgumentOutOfRangeException(nameof(initialEpoch)); epoch = initialEpoch; }
    public long Epoch { get { lock (sync) return epoch; } }
    public bool IsClosed { get { lock (sync) return closed; } }
    public bool HasPending { get { lock (sync) return queue.Count != 0; } }
    public NativeInputQueueSnapshot Snapshot
    {
        get { lock (sync) return new(queue.Count, queue.Count(item => item.CoalesciblePointerMove), closed); }
    }

    public bool TryEnqueue(NativeViewKind kind, ReadOnlySpan<byte> payload, bool gesture = false)
    {
        if (kind is not (NativeViewKind.Pointer or NativeViewKind.Key or NativeViewKind.Composition) ||
            payload.Length is < 1 or > MaximumPayloadBytes) return false;
        lock (sync)
        {
            if (closed || queue.Count >= Capacity) return false;
            queue.AddLast(new NativeInputItem(this, kind, epoch, gesture, payload.ToArray(), current!.Token));
            return true;
        }
    }
    public bool TryEnqueuePointerMove(int x, int y, NativeViewport viewport)
    {
        if (!viewport.IsValid || x < 0 || x >= viewport.Width || y < 0 || y >= viewport.Height) return false;
        // Only this finite operation can be coalesced. Ordinary enqueue calls,
        // including down/up, key, composition and wheel, remain ordering barriers.
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { action = "move", x, y });
        lock (sync)
        {
            if (closed) return false;
            var item = new NativeInputItem(this, NativeViewKind.Pointer, epoch, false, payload, current!.Token, true);
            if (queue.Last is { Value: { CoalesciblePointerMove: true } } last && last.Value.Epoch == epoch)
            {
                last.Value = item;
                return true;
            }
            if (queue.Count >= Capacity) return false;
            queue.AddLast(item);
            return true;
        }
    }
    public bool TryDequeue([NotNullWhen(true)] out NativeInputItem? item)
    {
        lock (sync)
        {
            if (!closed && queue.First is { } first)
            {
                item = first.Value; queue.RemoveFirst(); return true;
            }
            item = null; return false;
        }
    }
    public bool IsCurrent(NativeInputItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (sync) return !closed && ReferenceEquals(item.Owner, this) && item.Epoch == epoch;
    }
    public bool Invalidate()
    {
        CancellationTokenSource? retired;
        bool advanced;
        lock (sync)
        {
            if (closed) return false;
            retired = current; queue.Clear();
            advanced = epoch != long.MaxValue;
            if (advanced) { epoch++; current = new(); }
            else { closed = true; current = null; }
        }
        CancelAndDispose(retired);
        return advanced;
    }
    public void Dispose()
    {
        CancellationTokenSource? retired;
        lock (sync)
        {
            if (closed) return;
            closed = true; queue.Clear(); retired = current; current = null;
        }
        CancelAndDispose(retired);
    }
    private static void CancelAndDispose(CancellationTokenSource? source)
    {
        if (source is null) return;
        // Cancellation may run transport callbacks synchronously. Never call it
        // under the queue monitor, nor dispose a live uncanceled generation.
        try { source.Cancel(); } finally { source.Dispose(); }
    }
}
