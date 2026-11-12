using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using AutumnOS.Runtime;

namespace AutumnOS.Tests;

// Synthetic dispatcher/copy/native-call fixtures. No WinUI, OS thread, process,
// native WebView Post, pipe authentication or process retirement is exercised.
internal static class RuntimeNativeDeliveryActorTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return Case("G2 native actor posts only on dispatcher callback and preserves decreasing IDs", OrderedPosts);
        yield return Case("G2 native actor copy placeholder prevents a fence from passing incomplete copy", CopyBeforeFence);
        yield return Case("G2 native actor canceled waiter retains held native Post and fence ownership", HeldPostCancellation);
        yield return Case("G2 native actor native throw after Post yields Unknown and blocks fence ACK", ThrowAfterPost);
        yield return Case("G2 native actor navigation and wrong dispatcher reject before native call", DocumentAndThread);
        yield return Case("G2 native actor closes admission but retains original queued callback until observed", ClosePendingCallback);
        yield return Case("G2 native actor ambiguous enqueue retains slots until original owner confirms stop", UnknownEnqueue);
        yield return Case("G2 native actor capacity rejection is explicitly before Post and not fence ACK", Capacity);
        yield return Case("G2 native actor full payload capacity preserves one ordered fence control slot", ReservedFenceControlSlot);
        yield return Case("G2 native actor rejects stale binding hash duplicates and incomplete cutoff", InvalidMessages);
        yield return Case("G2 native actor channel MaxValue never wraps or acknowledges zero", GenerationExhaustion);
        yield return Case("G2 native actor duplicate history is bounded and released only by actual fence", BoundedHistory);
        yield return Case("G2 native actor external dispatcher callbacks and notifications run outside lock", OutsideLocks);
    }
    private static (string Name, Action Run) Case(string name, Func<Task> body) =>
        (name, () => body().WaitAsync(TimeSpan.FromSeconds(12)).GetAwaiter().GetResult());
    private static void Check(bool value, string message = "Native delivery actor invariant failed.")
    { if (!value) throw new InvalidOperationException(message); }
    private static RuntimeDeliveryBinding Binding() => new(Guid.NewGuid(), "synthetic-account", 1, new string('a', 64), Guid.NewGuid(), 2, Guid.NewGuid(), 3, 4);
    private static RuntimeDeliveryWrite Delivery(RuntimeDeliveryBinding binding, long id = 1, string text = "{\"fixture\":true}")
    { byte[] bytes = Encoding.UTF8.GetBytes(text); return new(id, binding, new(bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), 7), bytes); }
    private static void Posted(RuntimeNativeDeliveryOutcome result)
    { Check(result.Kind == RuntimeNativeDeliveryOutcomeKind.PostReceipt && result.PostReceipt?.Result == RuntimeNativePostResult.Posted && result.FenceReceipt is null); }
    private static void NotPosted(RuntimeNativeDeliveryOutcome result)
    { Check(result.Kind == RuntimeNativeDeliveryOutcomeKind.PostReceipt && result.PostReceipt?.Result == RuntimeNativePostResult.RejectedBeforePost && result.FenceReceipt is null); }
    private static void NoAck(RuntimeNativeDeliveryOutcome result)
    { Check(result.FenceReceipt is null && result.Kind is RuntimeNativeDeliveryOutcomeKind.Unknown or RuntimeNativeDeliveryOutcomeKind.RejectedBeforeDispatch); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task OrderedPosts()
    {
        RuntimeDeliveryBinding binding = Binding(); var dispatcher = new Dispatcher(); var actor = new RuntimeNativeDeliveryActor(binding, dispatcher);
        Check(actor.TrySubmitDelivery(Delivery(binding, 90, "{\"order\":1}"), out var first));
        Check(actor.TrySubmitDelivery(Delivery(binding, 2, "{\"order\":2}"), out var second));
        Check(dispatcher.Posts.Count == 0 && !first.IsCompleted && actor.SnapshotCounts().Slots == 2 && !actor.WhenIdle.IsCompleted);
        dispatcher.RunNext(); Posted(await first); Posted(await second);
        Check(dispatcher.Posts.SequenceEqual(new[] { "{\"order\":1}", "{\"order\":2}" }) && actor.WhenIdle.IsCompleted);
        Check(actor.TrySubmitFence(new(1, binding, 90, 5), out var fence));
        Check(!fence.IsCompleted && actor.CurrentBinding == binding); dispatcher.RunNext();
        RuntimeNativeDeliveryOutcome ack = await fence;
        Check(ack.Kind == RuntimeNativeDeliveryOutcomeKind.FenceReceipt && ack.FenceReceipt == new RuntimeNativeFenceReceipt(1, binding, 90, 5));
        RuntimeDeliveryBinding next = binding with { ChannelGeneration = 5 };
        Check(actor.CurrentBinding == next); Check(actor.TrySubmitDelivery(Delivery(next, 3), out var current));
        dispatcher.RunNext(); Posted(await current); await actor.CloseAsync();
    }
    private static async Task CopyBeforeFence()
    {
        RuntimeDeliveryBinding binding = Binding(); var dispatcher = new Dispatcher(); var actor = new RuntimeNativeDeliveryActor(binding, dispatcher, 1);
        RuntimeDeliveryWrite plain = Delivery(binding, 9); using var memory = new BlockingMemory(plain.Payload.ToArray());
        RuntimeDeliveryWrite blocked = plain with { Payload = memory.Buffer };
        Task<Task<RuntimeNativeDeliveryOutcome>> copy = Task.Factory.StartNew(() =>
        { Check(actor.TrySubmitDelivery(blocked, out var result)); return result; }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        try
        {
            await memory.Entered.Task;
            Check(actor.TrySubmitFence(new(1, binding, 9, 5), out var fence));
            Check(dispatcher.Pending == 0 && !fence.IsCompleted && actor.SnapshotCounts().Slots == 2 && actor.SnapshotCounts().Copying == 1);
            memory.Release.TrySetResult(); Task<RuntimeNativeDeliveryOutcome> posted = await copy;
            Check(dispatcher.Pending == 1 && !posted.IsCompleted && !fence.IsCompleted);
            dispatcher.RunNext(); Posted(await posted); Check((await fence).FenceReceipt is not null); await actor.CloseAsync();
        }
        finally { memory.Release.TrySetResult(); await copy; }
    }
    private static async Task HeldPostCancellation()
    {
        RuntimeDeliveryBinding binding = Binding(); var dispatcher = new Dispatcher(); var actor = new RuntimeNativeDeliveryActor(binding, dispatcher, 1);
        var entered = Signal(); var release = Signal();
        dispatcher.Post = _ => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); };
        Check(actor.TrySubmitDelivery(Delivery(binding), out var post));
        Task callback = Task.Run(dispatcher.RunNext);
        try
        {
            await entered.Task; Check(actor.TrySubmitFence(new(1, binding, 1, 5), out var fence));
            using var cancel = new CancellationTokenSource(); Task<RuntimeNativeDeliveryOutcome> waiter = post.WaitAsync(cancel.Token); cancel.Cancel();
            try { await waiter; throw new InvalidOperationException("Canceled wait returned a receipt."); } catch (OperationCanceledException) { }
            Check(!post.IsCompleted && !fence.IsCompleted && actor.SnapshotCounts().Slots == 2 && !actor.WhenIdle.IsCompleted);
            try { actor.ConfirmDispatcherStopped(); throw new InvalidOperationException("Running native call was dismissed as dispatcher exit."); } catch (InvalidOperationException error) when (error.Message == "ACTOR_CALLBACK_STILL_RUNNING") { }
            release.TrySetResult(); await callback; Posted(await post); Check((await fence).FenceReceipt is not null); await actor.CloseAsync();
        }
        finally { release.TrySetResult(); await callback; }
    }
    private static async Task ThrowAfterPost()
    {
        RuntimeDeliveryBinding binding = Binding(); var dispatcher = new Dispatcher { Post = _ => throw new InvalidOperationException("synthetic throw after native side effect") };
        var actor = new RuntimeNativeDeliveryActor(binding, dispatcher);
        Check(actor.TrySubmitDelivery(Delivery(binding), out var post)); Check(actor.TrySubmitFence(new(1, binding, 1, 5), out var fence));
        dispatcher.RunNext(); RuntimeNativeDeliveryOutcome unknown = await post;
        Check(dispatcher.Posts.Count == 1 && unknown.Kind == RuntimeNativeDeliveryOutcomeKind.Unknown && unknown.PostReceipt is null);
        NoAck(await fence); Check(actor.CurrentBinding == binding && actor.SnapshotCounts().Quarantined); await actor.CloseAsync();
    }
    private static async Task DocumentAndThread()
    {
        foreach (bool wrongThread in new[] { false, true })
        {
            RuntimeDeliveryBinding binding = Binding(); var dispatcher = new Dispatcher { Current = _ => wrongThread, WrongThread = wrongThread };
            var actor = new RuntimeNativeDeliveryActor(binding, dispatcher);
            Check(actor.TrySubmitDelivery(Delivery(binding), out var post)); Check(actor.TrySubmitFence(new(1, binding, 1, 5), out var fence));
            dispatcher.RunNext(); NotPosted(await post); NoAck(await fence);
            Check(dispatcher.Posts.Count == 0 && actor.CurrentBinding == binding); await actor.CloseAsync();
        }
    }
    private static async Task ClosePendingCallback()
    {
        RuntimeDeliveryBinding binding = Binding(); var dispatcher = new Dispatcher(); var actor = new RuntimeNativeDeliveryActor(binding, dispatcher);
        Check(actor.TrySubmitDelivery(Delivery(binding), out var post)); Task close = actor.CloseAsync();
        Check(!close.IsCompleted && !post.IsCompleted && actor.SnapshotCounts().DispatcherPending && actor.SnapshotCounts().Slots == 1);
        dispatcher.RunNext(); NotPosted(await post); await close;
        Check(dispatcher.Posts.Count == 0 && actor.WhenIdle.IsCompleted && actor.Completion.IsCompletedSuccessfully);
    }
    private static async Task UnknownEnqueue()
    {
        RuntimeDeliveryBinding binding = Binding(); var dispatcher = new Dispatcher { ThrowAfterEnqueue = true }; var actor = new RuntimeNativeDeliveryActor(binding, dispatcher);
        Check(actor.TrySubmitDelivery(Delivery(binding), out var post));
        Check(!post.IsCompleted && actor.SnapshotCounts().DispatcherPending && actor.SnapshotCounts().Quarantined && actor.SnapshotCounts().Slots == 1);
        Task close = actor.CloseAsync(); Check(!close.IsCompleted);
        actor.ConfirmDispatcherStopped(); NotPosted(await post); await close;
        dispatcher.RunNext(); Check(dispatcher.Posts.Count == 0 && actor.SnapshotCounts().Slots == 0);
    }
    private static async Task Capacity()
    {
        RuntimeDeliveryBinding binding = Binding(); var dispatcher = new Dispatcher(); var actor = new RuntimeNativeDeliveryActor(binding, dispatcher, 1);
        Check(actor.TrySubmitDelivery(Delivery(binding, 7), out var first));
        Check(!actor.TrySubmitDelivery(Delivery(binding, 2), out var rejected)); NotPosted(await rejected);
        Check(actor.TrySubmitFence(new(1, binding, 7, 5), out var fence));
        Check(!fence.IsCompleted && dispatcher.Posts.Count == 0 && actor.SnapshotCounts().Slots == 2);
        dispatcher.RunNext(); Posted(await first); Check((await fence).FenceReceipt is not null);
        await actor.CloseAsync();
        var unavailable = new Dispatcher { Accept = false }; var other = new RuntimeNativeDeliveryActor(binding, unavailable);
        Check(other.TrySubmitDelivery(Delivery(binding), out var notQueued)); NotPosted(await notQueued);
        Check(unavailable.Posts.Count == 0 && other.SnapshotCounts().Slots == 0); await other.CloseAsync();
    }
    private static async Task ReservedFenceControlSlot()
    {
        RuntimeDeliveryBinding binding = Binding(); var dispatcher = new Dispatcher(); var actor = new RuntimeNativeDeliveryActor(binding, dispatcher, 2);
        var firstEntered = Signal(); var firstRelease = Signal(); var secondEntered = Signal(); var secondRelease = Signal(); int calls = 0;
        dispatcher.Post = _ =>
        {
            if (++calls == 1) { firstEntered.TrySetResult(); firstRelease.Task.GetAwaiter().GetResult(); }
            else { secondEntered.TrySetResult(); secondRelease.Task.GetAwaiter().GetResult(); }
        };
        Check(actor.TrySubmitDelivery(Delivery(binding, 90), out var first));
        Check(actor.TrySubmitDelivery(Delivery(binding, 2), out var second));
        Check(actor.TrySubmitFence(new(1, binding, 90, 5), out var fence));
        Check(actor.SnapshotCounts().Slots == 3 && !fence.IsCompleted);
        Task callback = Task.Run(dispatcher.RunNext);
        try
        {
            await firstEntered.Task;
            Check(!first.IsCompleted && !second.IsCompleted && !fence.IsCompleted && actor.SnapshotCounts().Slots == 3);
            firstRelease.TrySetResult(); await secondEntered.Task; Posted(await first);
            Check(!second.IsCompleted && !fence.IsCompleted && actor.SnapshotCounts().Slots == 2);
            secondRelease.TrySetResult(); await callback; Posted(await second);
            Check((await fence).FenceReceipt is not null && actor.SnapshotCounts().Slots == 0); await actor.CloseAsync();
        }
        finally { firstRelease.TrySetResult(); secondRelease.TrySetResult(); await callback; }
    }
    private static async Task InvalidMessages()
    {
        RuntimeDeliveryBinding binding = Binding();
        foreach (RuntimeDeliveryWrite invalid in new[] { Delivery(binding with { DocumentId = Guid.NewGuid() }),
            Delivery(binding) with { Descriptor = Delivery(binding).Descriptor with { Sha256 = new string('0', 64) } } })
        {
            var dispatcher = new Dispatcher(); var actor = new RuntimeNativeDeliveryActor(binding, dispatcher);
            bool accepted = actor.TrySubmitDelivery(invalid, out var result);
            if (accepted) NotPosted(await result); else NoAck(await result);
            Check(dispatcher.Posts.Count == 0 && actor.SnapshotCounts().Quarantined); await actor.CloseAsync();
        }
        {
            var dispatcher = new Dispatcher(); var actor = new RuntimeNativeDeliveryActor(binding, dispatcher);
            Check(actor.TrySubmitDelivery(Delivery(binding, 90), out var first)); dispatcher.RunNext(); Posted(await first);
            Check(!actor.TrySubmitDelivery(Delivery(binding, 90), out var duplicate)); Check((await duplicate).Kind == RuntimeNativeDeliveryOutcomeKind.Unknown);
            Check(dispatcher.Posts.Count == 1); await actor.CloseAsync();
        }
        {
            var dispatcher = new Dispatcher(); var actor = new RuntimeNativeDeliveryActor(binding, dispatcher);
            Check(actor.TrySubmitDelivery(Delivery(binding, 90), out var first));
            Check(!actor.TrySubmitFence(new(1, binding, 2, 5), out var fence)); NoAck(await fence);
            dispatcher.RunNext(); NotPosted(await first); Check(dispatcher.Posts.Count == 0); await actor.CloseAsync();
        }
    }
    private static async Task GenerationExhaustion()
    {
        RuntimeDeliveryBinding binding = Binding() with { ChannelGeneration = long.MaxValue - 1 };
        var dispatcher = new Dispatcher(); var actor = new RuntimeNativeDeliveryActor(binding, dispatcher);
        Check(actor.TrySubmitFence(new(1, binding, 0, long.MaxValue), out var fence)); dispatcher.RunNext(); Check((await fence).FenceReceipt is not null);
        RuntimeDeliveryBinding last = actor.CurrentBinding; Check(last.ChannelGeneration == long.MaxValue);
        Check(actor.TrySubmitDelivery(Delivery(last), out var post)); dispatcher.RunNext(); Posted(await post);
        Check(!actor.TrySubmitFence(new(2, last, 1, 0), out var invalid)); NoAck(await invalid);
        Check(actor.CurrentBinding.ChannelGeneration == long.MaxValue); await actor.CloseAsync();
    }
    private static async Task BoundedHistory()
    {
        RuntimeDeliveryBinding binding = Binding(); var dispatcher = new Dispatcher(); var actor = new RuntimeNativeDeliveryActor(binding, dispatcher, 1);
        for (int id = 1; id <= RuntimeNativeDeliveryActor.MaximumDeliveryHistory; id++)
        { Check(actor.TrySubmitDelivery(Delivery(binding, id), out var post)); dispatcher.RunNext(); Posted(await post); }
        Check(actor.SnapshotCounts().History == RuntimeNativeDeliveryActor.MaximumDeliveryHistory);
        Check(!actor.TrySubmitDelivery(Delivery(binding, 5000), out var full)); NotPosted(await full);
        Check(actor.TrySubmitFence(new(1, binding, RuntimeNativeDeliveryActor.MaximumDeliveryHistory, 5), out var fence));
        Check(actor.SnapshotCounts().History == RuntimeNativeDeliveryActor.MaximumDeliveryHistory && !fence.IsCompleted);
        dispatcher.RunNext(); Check((await fence).FenceReceipt is not null && actor.SnapshotCounts().History == 0);
        Check(actor.TrySubmitDelivery(Delivery(actor.CurrentBinding, 1), out var next)); dispatcher.RunNext(); Posted(await next); await actor.CloseAsync();
    }
    private static async Task OutsideLocks()
    {
        RuntimeDeliveryBinding binding = Binding(); var dispatcher = new Dispatcher(); var actor = new RuntimeNativeDeliveryActor(binding, dispatcher);
        bool unlocked = true;
        void CheckLock() => unlocked &= Task.Run(actor.SnapshotCounts).Wait(TimeSpan.FromSeconds(2));
        dispatcher.OnEnqueue = CheckLock; dispatcher.Current = _ => { CheckLock(); return true; };
        Task? close = null;
        dispatcher.Post = _ => { CheckLock(); close = actor.CloseAsync(); Check(!close.IsCompleted); };
        Check(actor.TrySubmitDelivery(Delivery(binding), out var post));
        bool continuationUnlocked = false;
        Task continuation = post.ContinueWith(_ => continuationUnlocked = Task.Run(actor.SnapshotCounts).Wait(TimeSpan.FromSeconds(2)), TaskScheduler.Default);
        dispatcher.RunNext(); Posted(await post); await continuation; await (close ?? throw new InvalidOperationException("Reentrant close absent."));
        Check(unlocked && continuationUnlocked && actor.WhenIdle.IsCompleted);
    }
    private sealed class Dispatcher : IRuntimeNativeDeliveryDispatcher
    {
        private readonly object gate = new(); private readonly Queue<Action> callbacks = [];
        [ThreadStatic] private static bool inCallback;
        internal readonly List<string> Posts = [];
        internal Action<string>? Post;
        internal Action? OnEnqueue;
        internal Func<RuntimeDeliveryBinding, bool> Current = _ => true;
        internal bool Accept = true, WrongThread, ThrowAfterEnqueue;
        internal int Pending { get { lock (gate) return callbacks.Count; } }
        public bool IsDispatcherThread => inCallback && !WrongThread;
        public bool TryEnqueue(Action callback)
        {
            OnEnqueue?.Invoke(); if (!Accept) return false;
            lock (gate) callbacks.Enqueue(callback);
            if (ThrowAfterEnqueue) throw new InvalidOperationException("synthetic ambiguous enqueue");
            return true;
        }
        public bool IsCurrentDocument(RuntimeDeliveryBinding binding) => Current(binding);
        public void PostJson(string json) { Posts.Add(json); Post?.Invoke(json); }
        internal void RunNext()
        {
            Action callback; lock (gate) callback = callbacks.Dequeue();
            inCallback = true; try { callback(); } finally { inCallback = false; }
        }
    }
    private sealed class BlockingMemory(byte[] bytes) : MemoryManager<byte>
    {
        internal readonly TaskCompletionSource Entered = Signal(), Release = Signal();
        internal Memory<byte> Buffer => CreateMemory(bytes.Length);
        public override Span<byte> GetSpan() { Entered.TrySetResult(); Release.Task.GetAwaiter().GetResult(); return bytes; }
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { if (disposing) Release.TrySetResult(); }
    }
}
