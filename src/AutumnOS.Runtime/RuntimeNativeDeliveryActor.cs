using System.Text;

namespace AutumnOS.Runtime;

internal interface IRuntimeNativeDeliveryDispatcher
{
    bool IsDispatcherThread { get; }
    bool TryEnqueue(Action callback);
    bool IsCurrentDocument(RuntimeDeliveryBinding binding);
    void PostJson(string json);
}
internal enum RuntimeNativeDeliveryOutcomeKind { PostReceipt, FenceReceipt, RejectedBeforeDispatch, Unknown }
internal sealed record RuntimeNativeDeliveryOutcome(RuntimeNativeDeliveryOutcomeKind Kind,
    RuntimeNativePostReceipt? PostReceipt = null, RuntimeNativeFenceReceipt? FenceReceipt = null, string? ErrorCode = null);

/// <summary>Bounded single-endpoint native STA queue. Actor drain is never process-group retirement.</summary>
internal sealed class RuntimeNativeDeliveryActor
{
    internal const int MaximumDeliveryHistory = 4096;
    private readonly object sync = new();
    private readonly IRuntimeNativeDeliveryDispatcher dispatcher;
    private readonly int maximumSlots;
    private readonly Queue<Entry> queue = [];
    private readonly HashSet<long> deliveryHistory = [];
    private RuntimeDeliveryBinding binding;
    private bool scheduled, running, quarantined, closing, dispatcherStopped, fenceQueued;
    private int slots, deliverySlots, copying;
    private long maximumDeliveryId;
    private TaskCompletionSource idle = NewCompletion();
    private readonly TaskCompletionSource completion = NewCompletion();
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private sealed class Entry(RuntimeDeliveryWrite? delivery, RuntimeFenceWrite? fence)
    {
        internal readonly RuntimeDeliveryWrite? Delivery = delivery;
        internal readonly RuntimeFenceWrite? Fence = fence;
        internal readonly TaskCompletionSource<RuntimeNativeDeliveryOutcome> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal byte[] Payload = [];
        internal bool Ready;
        internal string? CopyError;
    }
    internal RuntimeNativeDeliveryActor(RuntimeDeliveryBinding binding, IRuntimeNativeDeliveryDispatcher dispatcher, int maximumSlots = 16)
    {
        RuntimeDeliveryWireProtocol.ValidateBinding(binding); ArgumentNullException.ThrowIfNull(dispatcher);
        // At most 48 MiB for simultaneous 32 KiB copies plus temporary UTF16 text,
        // independent of untrusted message sizes or a coordinator's larger limits.
        if (maximumSlots is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(maximumSlots));
        this.binding = binding; this.dispatcher = dispatcher; this.maximumSlots = maximumSlots;
        idle.TrySetResult();
    }
    internal RuntimeDeliveryBinding CurrentBinding { get { lock (sync) return binding; } }
    internal Task WhenIdle { get { lock (sync) return idle.Task; } }
    internal Task Completion => completion.Task;
    internal (int Slots, int Queued, int Copying, bool DispatcherPending, bool Running, bool Quarantined, bool Closing, int History) SnapshotCounts()
    { lock (sync) return (slots, queue.Count, copying, scheduled, running, quarantined, closing, deliveryHistory.Count); }

    internal bool TrySubmitDelivery(RuntimeDeliveryWrite delivery, out Task<RuntimeNativeDeliveryOutcome> outcome)
    {
        Entry? entry = null; RuntimeNativeDeliveryOutcome? rejected = null;
        try
        {
            ArgumentNullException.ThrowIfNull(delivery); RuntimeDeliveryWireProtocol.ValidateBinding(delivery.Binding);
            if (delivery.Descriptor is null || delivery.DeliveryId < 1 || delivery.Descriptor.Bytes != delivery.Payload.Length ||
                delivery.Payload.Length > RuntimeDeliveryWireProtocol.MaximumPayloadBytes || delivery.Descriptor.FrozenRevision < 0 ||
                delivery.Descriptor.Sha256 is not { Length: 64 } || !delivery.Descriptor.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw RuntimeDeliveryWireProtocol.Invalid("ACTOR_DELIVERY_INVALID");
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException)
        {
            lock (sync) quarantined = true;
            outcome = Task.FromResult(Rejected("ACTOR_DELIVERY_INVALID")); Drive(); return false;
        }
        lock (sync)
        {
            if (closing || quarantined || dispatcherStopped) rejected = Rejected("ACTOR_UNAVAILABLE");
            else if (delivery.Binding != binding || fenceQueued) { quarantined = true; rejected = Rejected("ACTOR_BINDING_STALE"); }
            else if (deliveryHistory.Contains(delivery.DeliveryId))
            { quarantined = true; rejected = Unknown("ACTOR_DUPLICATE_DELIVERY"); }
            else if (deliverySlots >= maximumSlots || deliveryHistory.Count == MaximumDeliveryHistory)
                rejected = NotPosted(delivery, "ACTOR_CAPACITY");
            else
            {
                if (slots++ == 0) idle = NewCompletion();
                deliverySlots++; copying++; deliveryHistory.Add(delivery.DeliveryId); maximumDeliveryId = Math.Max(maximumDeliveryId, delivery.DeliveryId);
                // Do not retain a caller's possibly enormous backing memory after copying.
                entry = new(delivery with { Payload = ReadOnlyMemory<byte>.Empty }, null); queue.Enqueue(entry);
            }
        }
        if (entry is null) { outcome = Task.FromResult(rejected!); Drive(); return false; }
        outcome = entry.Completion.Task;
        byte[] payload = []; string? failure = null;
        try
        {
            // Slot and FIFO placeholder are owned before external MemoryManager.GetSpan.
            payload = delivery.Payload.ToArray(); RuntimeDeliveryWireProtocol.ValidatePayload(payload, delivery.Descriptor.Sha256);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = "ACTOR_COPY_INVALID"; }
        lock (sync)
        {
            copying--; entry.Payload = payload; entry.CopyError = failure; entry.Ready = true;
            if (failure is not null) quarantined = true;
        }
        Drive(); return true;
    }
    internal bool TrySubmitFence(RuntimeFenceWrite fence, out Task<RuntimeNativeDeliveryOutcome> outcome)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(fence); RuntimeDeliveryWireProtocol.ValidateBinding(fence.Binding);
            if (fence.FenceId < 1 || fence.CutoffDeliveryId < 0 || fence.Binding.ChannelGeneration == long.MaxValue ||
                fence.NextChannelGeneration != fence.Binding.ChannelGeneration + 1) throw RuntimeDeliveryWireProtocol.Invalid("ACTOR_FENCE_INVALID");
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException)
        {
            lock (sync) quarantined = true;
            outcome = Task.FromResult(Rejected("ACTOR_FENCE_INVALID")); Drive(); return false;
        }
        RuntimeNativeDeliveryOutcome? rejection = null; Entry? entry = null;
        lock (sync)
        {
            if (closing || quarantined || dispatcherStopped) rejection = Unknown("ACTOR_UNAVAILABLE");
            else if (fence.Binding != binding || fenceQueued || fence.CutoffDeliveryId < maximumDeliveryId)
            { quarantined = true; rejection = Unknown("ACTOR_FENCE_STALE"); }
            else
            {
                // One separate control slot remains available when every payload
                // slot is occupied. fenceQueued owns it until callback settlement.
                if (slots++ == 0) idle = NewCompletion();
                fenceQueued = true; entry = new(null, fence) { Ready = true }; queue.Enqueue(entry);
            }
        }
        if (entry is null) { outcome = Task.FromResult(rejection!); Drive(); return false; }
        outcome = entry.Completion.Task; Drive(); return true;
    }
    // Cancellation belongs only to a caller's outcome.WaitAsync(token). The
    // original outcome, slot and native invocation remain owned by this actor.
    internal Task CloseAsync()
    {
        lock (sync) { closing = true; quarantined = true; }
        Drive(); SignalDrain(); return completion.Task;
    }
    /// <summary>Trusted original STA owner only: queued callbacks can no longer run. Not an OS retirement proof.</summary>
    internal void ConfirmDispatcherStopped()
    {
        lock (sync)
        {
            if (running) throw new InvalidOperationException("ACTOR_CALLBACK_STILL_RUNNING");
            dispatcherStopped = true; scheduled = false; closing = true; quarantined = true;
        }
        Drive(); SignalDrain();
    }
    private void Drive()
    {
        List<(Entry Entry, RuntimeNativeDeliveryOutcome Outcome)> drained = [];
        bool enqueue = false;
        lock (sync)
        {
            if (!scheduled && !running)
            {
                if (closing || quarantined || dispatcherStopped)
                {
                    while (queue.TryPeek(out Entry? head) && head.Ready)
                    {
                        queue.Dequeue(); ReleaseEntryLocked(head);
                        drained.Add((head, head.Delivery is { } delivery ? NotPosted(delivery, head.CopyError ?? "ACTOR_UNAVAILABLE") : Unknown("ACTOR_FENCE_UNACKNOWLEDGED")));
                    }
                }
                else if (queue.TryPeek(out Entry? head) && head.Ready) { scheduled = true; enqueue = true; }
            }
        }
        foreach (var item in drained) item.Entry.Completion.TrySetResult(item.Outcome);
        if (enqueue)
        {
            int invoked = 0;
            try
            {
                if (!dispatcher.TryEnqueue(() => { if (Interlocked.Exchange(ref invoked, 1) == 0) ProcessQueue(); })) DispatchFailed(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException) { DispatchFailed(true); }
        }
        SignalDrain();
    }
    private void DispatchFailed(bool ownershipUnknown)
    {
        // False explicitly rejected ownership. A throw might occur after enqueue:
        // retain that callback/slot until it returns or its original STA owner
        // confirms destruction. Never turn unknown enqueue state into a receipt.
        lock (sync) { quarantined = true; if (!running && !ownershipUnknown) scheduled = false; }
        Drive();
    }
    private void ProcessQueue()
    {
        lock (sync)
        {
            if (dispatcherStopped) return;
            scheduled = false;
            if (running) return;
            running = true;
        }
        try
        {
            bool actualSta;
            try { actualSta = dispatcher.IsDispatcherThread; }
            catch (Exception error) when (error is not OutOfMemoryException) { actualSta = false; }
            if (!actualSta) { lock (sync) quarantined = true; }
            while (true)
            {
                Entry entry;
                lock (sync)
                {
                    if (!queue.TryPeek(out Entry? head) || !head.Ready) break;
                    entry = queue.Dequeue();
                }
                RuntimeNativeDeliveryOutcome result = ProcessEntry(entry);
                lock (sync) ReleaseEntryLocked(entry);
                entry.Completion.TrySetResult(result);
            }
        }
        finally { lock (sync) running = false; Drive(); }
    }
    private RuntimeNativeDeliveryOutcome ProcessEntry(Entry entry)
    {
        lock (sync)
        {
            if (quarantined || closing || dispatcherStopped)
                return entry.Delivery is { } rejected ? NotPosted(rejected, entry.CopyError ?? "ACTOR_UNAVAILABLE") : Unknown("ACTOR_FENCE_UNACKNOWLEDGED");
        }
        RuntimeDeliveryBinding original = entry.Delivery?.Binding ?? entry.Fence!.Binding;
        bool nativeStarted = false;
        try
        {
            if (!dispatcher.IsCurrentDocument(original))
            {
                lock (sync) quarantined = true;
                return entry.Delivery is { } stale ? NotPosted(stale, "ACTOR_DOCUMENT_STALE") : Unknown("ACTOR_DOCUMENT_STALE");
            }
            if (entry.Delivery is { } delivery)
            {
                string json = Utf8.GetString(entry.Payload);
                lock (sync)
                {
                    if (quarantined || closing || dispatcherStopped) return NotPosted(delivery, "ACTOR_UNAVAILABLE");
                    nativeStarted = true;
                }
                // This exact synchronous STA call returning is the only Posted fact.
                dispatcher.PostJson(json);
                return new(RuntimeNativeDeliveryOutcomeKind.PostReceipt, new(delivery.DeliveryId, original, delivery.Descriptor.Sha256, RuntimeNativePostResult.Posted));
            }
            RuntimeFenceWrite fence = entry.Fence!;
            lock (sync)
            {
                if (quarantined || closing || dispatcherStopped || binding != original) return Unknown("ACTOR_FENCE_UNACKNOWLEDGED");
                binding = original with { ChannelGeneration = fence.NextChannelGeneration };
                deliveryHistory.Clear();
            }
            // All earlier copies/posts returned; admission now rejects every old
            // channel. No queue write, pipe flush or timeout can execute this leg.
            return new(RuntimeNativeDeliveryOutcomeKind.FenceReceipt, FenceReceipt: new(fence.FenceId, original, fence.CutoffDeliveryId, fence.NextChannelGeneration));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            lock (sync) quarantined = true;
            return nativeStarted ? Unknown("ACTOR_NATIVE_POST_UNKNOWN") : entry.Delivery is { } rejected
                ? NotPosted(rejected, "ACTOR_BEFORE_POST_FAILED") : Unknown("ACTOR_FENCE_UNACKNOWLEDGED");
        }
    }
    private void SignalDrain()
    {
        TaskCompletionSource? signalIdle = null, signalClosed = null;
        lock (sync)
        {
            if (slots == 0 && !scheduled && !running)
            { signalIdle = idle; if (closing) signalClosed = completion; }
        }
        signalIdle?.TrySetResult(); signalClosed?.TrySetResult();
    }
    private void ReleaseEntryLocked(Entry entry)
    {
        slots--; entry.Payload = [];
        if (entry.Delivery is not null) deliverySlots--;
        else fenceQueued = false;
    }
    private static RuntimeNativeDeliveryOutcome NotPosted(RuntimeDeliveryWrite value, string code) =>
        new(RuntimeNativeDeliveryOutcomeKind.PostReceipt, new(value.DeliveryId, value.Binding, value.Descriptor.Sha256, RuntimeNativePostResult.RejectedBeforePost), ErrorCode: code);
    private static RuntimeNativeDeliveryOutcome Unknown(string code) => new(RuntimeNativeDeliveryOutcomeKind.Unknown, ErrorCode: code);
    private static RuntimeNativeDeliveryOutcome Rejected(string code) => new(RuntimeNativeDeliveryOutcomeKind.RejectedBeforeDispatch, ErrorCode: code);
    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
