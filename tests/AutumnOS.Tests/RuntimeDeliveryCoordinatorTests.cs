using System.Security.Cryptography;
using System.Buffers;
using System.Text;
using System.Threading.Channels;
using AutumnOS.Runtime;
using Coordinator = AutumnOS.Runtime.RuntimeDeliveryCoordinator;

namespace AutumnOS.Tests;

// Pure ordering fakes only: no process handle, IPC, WebView2, permission,
// account mutation, desktop, clipboard, filesystem or OS retirement proof.
internal static class RuntimeDeliveryCoordinatorTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return Case("bounded_tickets_cancelled_queue_does_not_accumulate", CapacityAndCancellation);
        yield return Case("transport_write_is_not_native_post_or_receipt", WriteIsNotPost);
        yield return Case("wait_cancellation_unknown_retains_slot", CancellationAndUnknown);
        yield return Case("fence_cannot_overtake_held_send_and_settles_unknown_honestly", OrderedFence);
        yield return Case("posted_receipt_still_needs_exact_fence_ack", ReceiptAndFence);
        yield return Case("wrong_endpoint_binding_generation_hash_and_duplicate_receipts_rejected", WrongReceipts);
        yield return Case("cross_coordinator_and_forged_capability_brands_rejected", CapabilityBrands);
        yield return Case("only_original_registered_lifetime_can_retire_unknown", RegisteredLifetime);
        yield return Case("faulted_or_cancelled_lifetime_is_not_retirement", FailedLifetime);
        yield return Case("identity_application_instance_scope_and_fifo_writer_intents", ScopeOrder);
        yield return Case("fence_wait_cancellation_keeps_intent_and_capacity", FenceCancellation);
        yield return Case("commit_is_outside_state_lock_one_shot_and_exception_safe", CommitReentry);
        yield return Case("concurrent_use_of_same_permit_runs_only_one_commit", ConcurrentCommit);
        yield return Case("driver_callbacks_can_reenter_from_another_thread", DriverReentry);
        yield return Case("registered_witness_getter_is_outside_state_lock", WitnessReentry);
        yield return Case("wrong_binding_or_reused_lifetime_registration_rejected", LifetimeBinding);
        yield return Case("driver_fault_stays_unknown_until_original_retirement", DriverFailure);
        yield return Case("held_parent_write_keeps_retired_endpoint_memory_bounded", HeldWriteRetirement);
        yield return Case("late_ack_cannot_relabel_fenced_unknown_or_replay_in_new_registration", LateReceipt);
        yield return Case("payload_copy_hash_and_explicit_host_limit", PayloadOwnership);
        yield return Case("copy_claim_is_once_and_cancelled_copy_retains_resource_slot", CopyOwnership);
        yield return Case("posted_ack_before_driver_return_does_not_release_buffer_slot", EarlyReceiptSlot);
        yield return Case("native_rejection_is_terminal_without_posted_success", NativeRejection);
        yield return Case("pre_cancelled_fence_registers_no_writer_intent", PreCancelledFence);
    }

    private static (string, Action) Case(string name, Func<Task> run) =>
        ("g2b1.delivery.pure_fake_" + name, () => run().WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult());

    private static async Task CapacityAndCancellation()
    {
        var c = new Coordinator(new(1, 2, 128, 2)); var e = new Endpoint(c);
        var hold = Signal(); e.Driver.DeliveryGate = hold.Task;
        Coordinator.DeliveryTicket sent = Queue(c, e, "first"); _ = await e.Driver.Delivery();
        for (int i = 0; i < 40; i++)
        {
            Coordinator.DeliveryTicket queued = Queue(c, e, "queued");
            Check(c.CancelBeforeSend(queued)); Check(await c.WaitOutcomeAsync(queued) == RuntimeDeliveryState.NotSent);
            Check(c.SnapshotCounts().QueuedWork == 0);
        }
        Coordinator.DeliveryTicket second = Reserve(c, e, "second");
        Check(c.TryReserve(e.Registration.Endpoint, Descriptor("third")).Rejection == RuntimeDeliveryRejection.Capacity);
        Check(c.CancelBeforeSend(second));
        Throws<InvalidOperationException>(() => _ = new Endpoint(c));
        hold.SetResult(true); e.Registration.Evidence.MarkTransportUnknown(); e.Lifetime.Retire();
        Check(await c.WaitOutcomeAsync(sent) == RuntimeDeliveryState.RetiredUnknown);
        Check(await e.Registration.Evidence.RetirementObserved);
        await e.Driver.DeliveryReturned.Task;
    }

    private static async Task WriteIsNotPost()
    {
        var c = new Coordinator(); var e = new Endpoint(c);
        Coordinator.DeliveryTicket ticket = Queue(c, e, "one"); RuntimeDeliveryWrite write = await e.Driver.Delivery();
        await e.Driver.DeliveryReturned.Task;
        Check(c.StateOf(ticket) == RuntimeDeliveryState.Sending && !c.WaitOutcomeAsync(ticket).IsCompleted);
        Check(e.Post(write)); Check(await c.WaitOutcomeAsync(ticket) == RuntimeDeliveryState.Posted);
        Check(!e.Post(write) && c.OutstandingCount(e.Registration.Endpoint) == 0);
        await e.Retire();
    }

    private static async Task CancellationAndUnknown()
    {
        var c = new Coordinator(new(2, 1, 128, 2)); var e = new Endpoint(c);
        var hold = Signal(); e.Driver.DeliveryGate = hold.Task;
        Coordinator.DeliveryTicket ticket = Queue(c, e, "private"); RuntimeDeliveryWrite write = await e.Driver.Delivery();
        using var cancel = new CancellationTokenSource(); Task<RuntimeDeliveryState> wait = c.WaitOutcomeAsync(ticket, cancel.Token);
        cancel.Cancel(); await Cancelled(wait);
        Check(c.StateOf(ticket) == RuntimeDeliveryState.Sending && !c.CancelBeforeSend(ticket));
        Check(e.Registration.Evidence.MarkReceiptUnknown(write.DeliveryId));
        Check(c.StateOf(ticket) == RuntimeDeliveryState.Unknown && c.OutstandingCount(e.Registration.Endpoint) == 1);
        Check(c.TryReserve(e.Registration.Endpoint, Descriptor("later")).Rejection == RuntimeDeliveryRejection.EndpointUnavailable);
        e.Lifetime.Retire(); Check(await c.WaitOutcomeAsync(ticket) == RuntimeDeliveryState.RetiredUnknown);
        hold.SetResult(true); await e.Driver.DeliveryReturned.Task; await e.Retire();
    }

    private static async Task OrderedFence()
    {
        var c = new Coordinator(); var e = new Endpoint(c); var hold = Signal(); e.Driver.DeliveryGate = hold.Task;
        Coordinator.DeliveryTicket first = Queue(c, e, "first"); RuntimeDeliveryWrite write = await e.Driver.Delivery();
        Coordinator.DeliveryTicket unsent = Queue(c, e, "unsent");
        Coordinator.FenceOperation fence = await c.BeginFenceAsync(Instance(e.Binding));
        Check(await c.WaitOutcomeAsync(unsent) == RuntimeDeliveryState.NotSent && e.Driver.FenceCalls == 0);
        Check(c.TryReserve(e.Registration.Endpoint, Descriptor("new")).Rejection == RuntimeDeliveryRejection.Fenced);
        Check(!e.Registration.Evidence.AcceptNativeFence(new(1, e.Binding, write.DeliveryId, 2)));
        Check(e.Registration.Evidence.MarkReceiptUnknown(write.DeliveryId));
        Check(!fence.WaitCommitPermitAsync().IsCompleted);
        hold.SetResult(true); RuntimeFenceWrite barrier = await e.Driver.Fence();
        Check(e.Driver.DeliveryCalls == 1 && barrier.CutoffDeliveryId == write.DeliveryId);
        Check(e.Fence(barrier)); Check(await c.WaitOutcomeAsync(first) == RuntimeDeliveryState.FencedUnknown);
        Coordinator.MutationPermit permit = await fence.WaitCommitPermitAsync(); permit.Commit(() => { });
        Check(c.StateOf(first) != RuntimeDeliveryState.Posted); await e.Retire();
    }

    private static async Task ReceiptAndFence()
    {
        var c = new Coordinator(); var e = new Endpoint(c);
        Coordinator.DeliveryTicket ticket = Queue(c, e, "private"); RuntimeDeliveryWrite write = await e.Driver.Delivery();
        Coordinator.FenceOperation fence = await c.BeginFenceAsync(Application(e.Binding)); RuntimeFenceWrite barrier = await e.Driver.Fence();
        Task<Coordinator.MutationPermit> ready = fence.WaitCommitPermitAsync();
        Check(!ready.IsCompleted); Check(e.Post(write)); Check(await c.WaitOutcomeAsync(ticket) == RuntimeDeliveryState.Posted);
        Check(!ready.IsCompleted);
        Check(!e.Registration.Evidence.AcceptNativeFence(Receipt(barrier) with { CutoffDeliveryId = barrier.CutoffDeliveryId + 1 }));
        Check(!e.Registration.Evidence.AcceptNativeFence(Receipt(barrier) with { FenceId = barrier.FenceId + 1 }));
        Check(!e.Registration.Evidence.AcceptNativeFence(Receipt(barrier) with { NextChannelGeneration = barrier.NextChannelGeneration + 1 }));
        Check(!e.Registration.Evidence.AcceptNativeFence(Receipt(barrier) with { Binding = barrier.Binding with { DocumentGeneration = 2 } }));
        Check(e.Fence(barrier)); Check(!e.Fence(barrier)); (await ready).Commit(() => { });
        Coordinator.DeliveryTicket current = Queue(c, e, "current"); RuntimeDeliveryWrite currentWrite = await e.Driver.Delivery();
        Check(currentWrite.Binding.ChannelGeneration == 2); Check(e.Post(currentWrite)); await c.WaitOutcomeAsync(current); await e.Retire();
    }

    private static async Task WrongReceipts()
    {
        var c = new Coordinator(); var a = new Endpoint(c); var b = new Endpoint(c, a.Binding with { InstanceId = Guid.NewGuid(), DocumentId = Guid.NewGuid() });
        Coordinator.DeliveryTicket ticket = Queue(c, a, "private"); RuntimeDeliveryWrite write = await a.Driver.Delivery();
        RuntimeNativePostReceipt good = Receipt(write);
        Check(!b.Registration.Evidence.AcceptNativePost(good));
        Check(!a.Registration.Evidence.AcceptNativePost(good with { PayloadSha256 = new string('0', 64) }));
        foreach (RuntimeDeliveryBinding wrong in new[]
        {
            write.Binding with { AccountEpoch = 2 }, write.Binding with { InstanceEpoch = 2 },
            write.Binding with { ChannelGeneration = 2 }, write.Binding with { DocumentId = Guid.NewGuid() },
            write.Binding with { ApplicationBinding = new string('b', 64) }
        }) Check(!a.Registration.Evidence.AcceptNativePost(good with { Binding = wrong }));
        Check(!a.Registration.Evidence.AcceptNativePost(good with { Result = (RuntimeNativePostResult)99 }));
        Check(a.Post(write)); Check(await c.WaitOutcomeAsync(ticket) == RuntimeDeliveryState.Posted);
        Check(!a.Post(write)); await a.Retire(); await b.Retire();
    }

    private static async Task CapabilityBrands()
    {
        var a = new Coordinator(); var b = new Coordinator(); var e = new Endpoint(a);
        Coordinator.DeliveryTicket ticket = Reserve(a, e, "private");
        Throws<InvalidOperationException>(() => b.TryReserve(e.Registration.Endpoint, Descriptor("other")));
        Throws<InvalidOperationException>(() => b.CancelBeforeSend(ticket));
        Throws<InvalidOperationException>(() => a.TryReserve(Coordinator.EndpointKey.Mint(new object(), new object()), Descriptor("fake")));
        Throws<InvalidOperationException>(() => Coordinator.MutationPermit.Mint(new object(), new object(), a).Commit(() => throw new Exception("MUST_NOT_RUN")));
        Throws<InvalidOperationException>(() => Coordinator.HostEvidencePort.Mint(new object(), new object(), a).MarkTransportUnknown());
        Check(a.CancelBeforeSend(ticket)); await e.Retire();
    }

    private static async Task RegisteredLifetime()
    {
        var c = new Coordinator(); var a = new Endpoint(c); var b = new Endpoint(c, a.Binding with { InstanceId = Guid.NewGuid(), DocumentId = Guid.NewGuid() });
        Coordinator.DeliveryTicket ticket = Queue(c, a, "private"); RuntimeDeliveryWrite write = await a.Driver.Delivery();
        a.Registration.Evidence.MarkReceiptUnknown(write.DeliveryId);
        Coordinator.FenceOperation fence = await c.BeginFenceAsync(Instance(a.Binding)); _ = await a.Driver.Fence();
        await b.Retire(); Check(c.StateOf(ticket) == RuntimeDeliveryState.Unknown && !fence.WaitCommitPermitAsync().IsCompleted);
        await a.Retire(); Check(await c.WaitOutcomeAsync(ticket) == RuntimeDeliveryState.RetiredUnknown);
        (await fence.WaitCommitPermitAsync()).Commit(() => { });
    }

    private static async Task FailedLifetime()
    {
        foreach (bool cancelled in new[] { false, true })
        {
            var c = new Coordinator(); var e = new Endpoint(c);
            Coordinator.DeliveryTicket ticket = Queue(c, e, "private"); _ = await e.Driver.Delivery();
            if (cancelled) e.Lifetime.Completion.SetCanceled(); else e.Lifetime.Completion.SetException(new InvalidOperationException("FAKE_WITNESS_FAILURE"));
            Check(!await e.Registration.Evidence.RetirementObserved);
            Check(c.StateOf(ticket) == RuntimeDeliveryState.Unknown && !c.WaitOutcomeAsync(ticket).IsCompleted);
            Coordinator.FenceOperation fence = await c.BeginFenceAsync(Instance(e.Binding));
            Check(!fence.WaitCommitPermitAsync().IsCompleted);
        }
    }

    private static async Task ScopeOrder()
    {
        var c = new Coordinator(); var a = new Endpoint(c);
        var b = new Endpoint(c, a.Binding with { InstanceId = Guid.NewGuid(), DocumentId = Guid.NewGuid() });
        var otherApp = new Endpoint(c, a.Binding with { ApplicationBinding = new string('b', 64), InstanceId = Guid.NewGuid(), DocumentId = Guid.NewGuid() });
        var otherIdentity = new Endpoint(c);
        Coordinator.FenceOperation first = await c.BeginFenceAsync(Instance(a.Binding)); RuntimeFenceWrite firstWrite = await a.Driver.Fence();
        Check(a.Fence(firstWrite)); Coordinator.MutationPermit firstPermit = await first.WaitCommitPermitAsync();
        Coordinator.FenceOperation second = await c.BeginFenceAsync(Application(a.Binding));
        Coordinator.FenceOperation third = await c.BeginFenceAsync(new(RuntimeAuthorityScopeKind.Identity, a.Binding.IdentityAuthorityId));
        Check(a.Driver.FenceCalls == 1 && b.Driver.FenceCalls == 0 && otherApp.Driver.FenceCalls == 0);
        Check(c.TryReserve(b.Registration.Endpoint, Descriptor("blocked")).Rejection == RuntimeDeliveryRejection.Fenced);
        Check(c.TryReserve(otherApp.Registration.Endpoint, Descriptor("blocked")).Rejection == RuntimeDeliveryRejection.Fenced);
        Coordinator.DeliveryTicket free = Reserve(c, otherIdentity, "free"); Check(c.CancelBeforeSend(free));
        Throws<InvalidOperationException>(() => _ = new Endpoint(c, a.Binding with { InstanceId = Guid.NewGuid(), DocumentId = Guid.NewGuid() }));
        Coordinator.FenceOperation parallel = await c.BeginFenceAsync(Instance(otherIdentity.Binding));
        Check(otherIdentity.Fence(await otherIdentity.Driver.Fence()));
        (await parallel.WaitCommitPermitAsync()).Commit(() => { }); // Does not wait on the other identity's writer.
        firstPermit.Commit(() => { });
        foreach (Endpoint endpoint in new[] { a, b }) Check(endpoint.Fence(await endpoint.Driver.Fence()));
        Check(otherApp.Driver.FenceCalls == 0); (await second.WaitCommitPermitAsync()).Commit(() => { });
        foreach (Endpoint endpoint in new[] { a, b, otherApp }) Check(endpoint.Fence(await endpoint.Driver.Fence()));
        (await third.WaitCommitPermitAsync()).Commit(() => { }); Check(c.SnapshotCounts().Fences == 0);
        foreach (Endpoint endpoint in new[] { a, b, otherApp, otherIdentity }) await endpoint.Retire();
    }

    private static async Task FenceCancellation()
    {
        var c = new Coordinator(new(2, 2, 128, 1)); var e = new Endpoint(c);
        Coordinator.FenceOperation fence = await c.BeginFenceAsync(Instance(e.Binding)); RuntimeFenceWrite barrier = await e.Driver.Fence();
        using var cancel = new CancellationTokenSource(); Task<Coordinator.MutationPermit> wait = fence.WaitCommitPermitAsync(cancel.Token);
        cancel.Cancel(); await Cancelled(wait);
        Check(c.TryReserve(e.Registration.Endpoint, Descriptor("blocked")).Rejection == RuntimeDeliveryRejection.Fenced);
        Throws<InvalidOperationException>(() => c.BeginFenceAsync(Instance(e.Binding)).GetAwaiter().GetResult());
        Check(e.Fence(barrier)); (await fence.WaitCommitPermitAsync()).Commit(() => { });
        Coordinator.DeliveryTicket ticket = Reserve(c, e, "new"); Check(c.CancelBeforeSend(ticket)); await e.Retire();
    }

    private static async Task CommitReentry()
    {
        var c = new Coordinator(); var e = new Endpoint(c);
        Coordinator.FenceOperation fence = await c.BeginFenceAsync(Instance(e.Binding)); Check(e.Fence(await e.Driver.Fence()));
        Coordinator.MutationPermit permit = await fence.WaitCommitPermitAsync(); var failure = new InvalidOperationException("FAKE_COMMIT_FAILURE");
        bool caught = false;
        try
        {
            permit.Commit(() =>
            {
                // A different thread must enter the state lock while this
                // action runs; same-thread Monitor recursion would prove less.
                RuntimeDeliveryRejection rejected = Task.Run(() => c.TryReserve(e.Registration.Endpoint, Descriptor("blocked")).Rejection)
                    .WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                Check(rejected == RuntimeDeliveryRejection.Fenced); throw failure;
            });
        }
        catch (InvalidOperationException actual) { Check(ReferenceEquals(actual, failure)); caught = true; }
        Check(caught);
        Throws<InvalidOperationException>(() => permit.Commit(() => { }));
        Check(c.SnapshotCounts().Fences == 0);
        Coordinator.DeliveryTicket ticket = Reserve(c, e, "after_failed_commit"); Check(c.CancelBeforeSend(ticket)); await e.Retire();
    }

    private static async Task DriverReentry()
    {
        var c = new Coordinator(); var e = new Endpoint(c); bool called = false;
        e.Driver.OnDelivery = _ =>
        {
            Task.Run(() => { Coordinator.DeliveryTicket nested = Reserve(c, e, "nested"); Check(c.CancelBeforeSend(nested)); })
                .WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); called = true;
        };
        Coordinator.DeliveryTicket ticket = Queue(c, e, "outer"); RuntimeDeliveryWrite write = await e.Driver.Delivery();
        Check(called && e.Post(write)); Check(await c.WaitOutcomeAsync(ticket) == RuntimeDeliveryState.Posted); await e.Retire();
    }

    private static async Task ConcurrentCommit()
    {
        var c = new Coordinator(); var e = new Endpoint(c);
        Coordinator.FenceOperation fence = await c.BeginFenceAsync(Instance(e.Binding)); Check(e.Fence(await e.Driver.Fence()));
        Coordinator.MutationPermit permit = await fence.WaitCommitPermitAsync();
        var entered = Signal(); using var release = new ManualResetEventSlim(); int commits = 0;
        Task first = Task.Run(() => permit.Commit(() =>
        {
            Interlocked.Increment(ref commits); entered.TrySetResult(true);
            if (!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("FAKE_COMMIT_RELEASE_REQUIRED");
        }));
        try
        {
            await entered.Task;
            Throws<InvalidOperationException>(() => permit.Commit(() => Interlocked.Increment(ref commits)));
            Check(c.TryReserve(e.Registration.Endpoint, Descriptor("blocked")).Rejection == RuntimeDeliveryRejection.Fenced);
        }
        finally { release.Set(); }
        await first; Check(commits == 1 && c.SnapshotCounts().Fences == 0); await e.Retire();
    }

    private static async Task DriverFailure()
    {
        var c = new Coordinator(); var e = new Endpoint(c); e.Driver.DeliveryFailure = new IOException("FAKE_PARTIAL_WRITE");
        Coordinator.DeliveryTicket ticket = Queue(c, e, "private"); _ = await e.Driver.Delivery();
        await e.Registration.Evidence.WhenUnavailable;
        Check(c.StateOf(ticket) == RuntimeDeliveryState.Unknown && !c.WaitOutcomeAsync(ticket).IsCompleted);
        Coordinator.FenceOperation fence = await c.BeginFenceAsync(Instance(e.Binding));
        Check(e.Driver.FenceCalls == 0 && !fence.WaitCommitPermitAsync().IsCompleted);
        await e.Retire(); Check(await c.WaitOutcomeAsync(ticket) == RuntimeDeliveryState.RetiredUnknown);
        (await fence.WaitCommitPermitAsync()).Commit(() => { });
    }

    private static async Task WitnessReentry()
    {
        var c = new Coordinator(); var driver = new FakeDriver(); int calls = 0;
        RuntimeDeliveryBinding binding = new(Guid.NewGuid(), "controlled-account", 1, new string('a', 64), Guid.NewGuid(), 1, Guid.NewGuid(), 1, 1);
        var lifetime = new FakeLifetime(binding);
        lifetime.OnRead = () =>
        {
            int count = Task.Run(() => c.SnapshotCounts().Endpoints).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            Check(count == 0); calls++;
        };
        Coordinator.HostRegistration registration = c.RegisterHostEndpoint(binding, driver, lifetime);
        Check(calls == 1); lifetime.Retire(); Check(await registration.Evidence.RetirementObserved);
    }

    private static async Task LifetimeBinding()
    {
        var c = new Coordinator(); var e = new Endpoint(c);
        RuntimeDeliveryBinding other = e.Binding with { InstanceId = Guid.NewGuid(), DocumentId = Guid.NewGuid() };
        Throws<ArgumentException>(() => c.RegisterHostEndpoint(other, new FakeDriver(), e.Lifetime));
        Throws<InvalidOperationException>(() => new Coordinator().RegisterHostEndpoint(e.Binding, new FakeDriver(), e.Lifetime));
        await e.Retire();
        Throws<InvalidOperationException>(() => c.RegisterHostEndpoint(e.Binding, new FakeDriver(), e.Lifetime));
    }

    private static async Task HeldWriteRetirement()
    {
        var c = new Coordinator(new(1, 1, 128, 2)); var e = new Endpoint(c); var hold = Signal(); e.Driver.DeliveryGate = hold.Task;
        Coordinator.DeliveryTicket ticket = Queue(c, e, "private"); _ = await e.Driver.Delivery();
        await e.Retire(); Check(await c.WaitOutcomeAsync(ticket) == RuntimeDeliveryState.RetiredUnknown);
        Check(c.SnapshotCounts().Endpoints == 1); Throws<InvalidOperationException>(() => _ = new Endpoint(c));
        hold.SetResult(true); await e.Driver.DeliveryReturned.Task;
        await e.Registration.Evidence.WhenIdle;
        Check(c.SnapshotCounts().Endpoints == 0 && c.SnapshotCounts().ResourceSlots == 0);
    }

    private static async Task LateReceipt()
    {
        var c = new Coordinator(); var e = new Endpoint(c);
        Coordinator.DeliveryTicket old = Queue(c, e, "same"); RuntimeDeliveryWrite oldWrite = await e.Driver.Delivery();
        e.Registration.Evidence.MarkReceiptUnknown(oldWrite.DeliveryId);
        Coordinator.FenceOperation fence = await c.BeginFenceAsync(Instance(e.Binding)); Check(e.Fence(await e.Driver.Fence()));
        Check(await c.WaitOutcomeAsync(old) == RuntimeDeliveryState.FencedUnknown); (await fence.WaitCommitPermitAsync()).Commit(() => { });
        Check(!e.Post(oldWrite) && c.StateOf(old) == RuntimeDeliveryState.FencedUnknown); await e.Retire();
        var next = new Endpoint(c, e.Binding); Coordinator.DeliveryTicket current = Queue(c, next, "same");
        RuntimeDeliveryWrite currentWrite = await next.Driver.Delivery(); Check(currentWrite.DeliveryId > oldWrite.DeliveryId);
        Check(!next.Post(oldWrite)); Check(next.Post(currentWrite)); await c.WaitOutcomeAsync(current); await next.Retire();
    }

    private static async Task PayloadOwnership()
    {
        var c = new Coordinator(new(2, 2, 65536, 2)); var e = new Endpoint(c); var hold = Signal(); e.Driver.DeliveryGate = hold.Task;
        byte[] bytes = Encoding.UTF8.GetBytes(new string('p', 40000)); string expected = Hash(bytes);
        Coordinator.DeliveryTicket ticket = c.TryReserve(e.Registration.Endpoint, new(bytes.Length, expected, 4)).Ticket!;
        Check(c.QueueValidated(ticket, bytes)); RuntimeDeliveryWrite write = await e.Driver.Delivery(); bytes[0] = 0;
        Check(write.Descriptor.Bytes == 40000 && Hash(write.Payload.Span) == expected);
        Coordinator.DeliveryTicket invalid = Reserve(c, e, "right");
        Throws<ArgumentException>(() => c.QueueValidated(invalid, Encoding.UTF8.GetBytes("wrong")));
        Check(await c.WaitOutcomeAsync(invalid) == RuntimeDeliveryState.NotSent);
        Check(e.Post(write)); await c.WaitOutcomeAsync(ticket); hold.SetResult(true); await e.Driver.DeliveryReturned.Task; await e.Retire();
    }

    private static async Task CopyOwnership()
    {
        var c = new Coordinator(new(1, 1, 128, 2)); var e = new Endpoint(c);
        byte[] bytes = Encoding.UTF8.GetBytes("blocking-copy");
        Coordinator.DeliveryTicket ticket = c.TryReserve(e.Registration.Endpoint, new(bytes.Length, Hash(bytes), 1)).Ticket!;
        using var manager = new BlockingMemory(bytes);
        Task<bool> copying = Task.Run(() => c.QueueValidated(ticket, manager.Buffer));
        try
        {
            await manager.Entered.Task;
            Check(!c.QueueValidated(ticket, manager.Buffer) && manager.SpanCalls == 1);
            Check(c.CancelBeforeSend(ticket)); Check(await c.WaitOutcomeAsync(ticket) == RuntimeDeliveryState.NotSent);
            Check(!c.QueueValidated(ticket, manager.Buffer) && manager.SpanCalls == 1);
            Check(c.SnapshotCounts().Tickets == 0 && c.SnapshotCounts().ResourceSlots == 1);
            Check(c.TryReserve(e.Registration.Endpoint, Descriptor("later")).Rejection == RuntimeDeliveryRejection.Capacity);
            await e.Retire();
            Check(c.SnapshotCounts().Endpoints == 1); Throws<InvalidOperationException>(() => _ = new Endpoint(c));
        }
        finally { manager.Release.Set(); }
        Check(!await copying); await e.Registration.Evidence.WhenIdle;
        Check(c.SnapshotCounts().ResourceSlots == 0 && c.SnapshotCounts().Endpoints == 0);
        var next = new Endpoint(c); await next.Retire();
    }

    private static async Task EarlyReceiptSlot()
    {
        var c = new Coordinator(new(1, 1, 128, 2)); var e = new Endpoint(c); var hold = Signal(); e.Driver.DeliveryGate = hold.Task;
        Coordinator.DeliveryTicket ticket = Queue(c, e, "private"); RuntimeDeliveryWrite write = await e.Driver.Delivery();
        Check(e.Post(write)); Check(await c.WaitOutcomeAsync(ticket) == RuntimeDeliveryState.Posted);
        Check(c.SnapshotCounts().Tickets == 0 && c.SnapshotCounts().ResourceSlots == 1);
        Check(c.TryReserve(e.Registration.Endpoint, Descriptor("later")).Rejection == RuntimeDeliveryRejection.Capacity);
        await e.Retire(); Check(c.SnapshotCounts().Endpoints == 1);
        hold.SetResult(true); await e.Registration.Evidence.WhenIdle;
        Check(c.SnapshotCounts().ResourceSlots == 0 && c.SnapshotCounts().Endpoints == 0);
    }

    private static async Task NativeRejection()
    {
        var c = new Coordinator(); var e = new Endpoint(c);
        Coordinator.DeliveryTicket ticket = Queue(c, e, "private"); RuntimeDeliveryWrite write = await e.Driver.Delivery();
        Check(e.Registration.Evidence.AcceptNativePost(Receipt(write) with { Result = RuntimeNativePostResult.RejectedBeforePost }));
        Check(await c.WaitOutcomeAsync(ticket) == RuntimeDeliveryState.Rejected && !e.Post(write)); await e.Retire();
    }

    private static async Task PreCancelledFence()
    {
        var c = new Coordinator(); var e = new Endpoint(c); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Throws<OperationCanceledException>(() => c.BeginFenceAsync(Instance(e.Binding), cancel.Token).GetAwaiter().GetResult());
        Check(c.SnapshotCounts().Fences == 0); Coordinator.DeliveryTicket ticket = Reserve(c, e, "new"); Check(c.CancelBeforeSend(ticket)); await e.Retire();
    }

    private sealed class FakeLifetime : IOriginalRuntimeEndpointLifetime
    {
        private readonly RuntimeEndpointLifetimeRegistration registration;
        internal TaskCompletionSource<bool> Completion { get; } = Signal();
        internal Action? OnRead;
        internal FakeLifetime(RuntimeDeliveryBinding binding) { registration = RuntimeEndpointLifetimeRegistration.BindForHost(binding, Completion.Task); }
        public RuntimeEndpointLifetimeRegistration OriginalLifetime { get { OnRead?.Invoke(); return registration; } }
        internal void Retire() => Completion.TrySetResult(true);
    }
    private sealed class FakeDriver : IOrderedRuntimeDeliveryDriver
    {
        private readonly Channel<RuntimeDeliveryWrite> deliveries = Channel.CreateUnbounded<RuntimeDeliveryWrite>();
        private readonly Channel<RuntimeFenceWrite> barriers = Channel.CreateUnbounded<RuntimeFenceWrite>();
        internal Task DeliveryGate = Task.CompletedTask;
        internal Exception? DeliveryFailure;
        internal Action<RuntimeDeliveryWrite>? OnDelivery;
        internal TaskCompletionSource<bool> DeliveryReturned { get; } = Signal();
        internal int DeliveryCalls, FenceCalls;
        public async ValueTask WriteDeliveryAsync(RuntimeDeliveryWrite delivery)
        {
            Interlocked.Increment(ref DeliveryCalls); OnDelivery?.Invoke(delivery); Check(deliveries.Writer.TryWrite(delivery));
            try { await DeliveryGate; if (DeliveryFailure is Exception error) throw error; }
            finally { DeliveryReturned.TrySetResult(true); }
        }
        public ValueTask WriteFenceAsync(RuntimeFenceWrite fence)
        { Interlocked.Increment(ref FenceCalls); Check(barriers.Writer.TryWrite(fence)); return ValueTask.CompletedTask; }
        internal Task<RuntimeDeliveryWrite> Delivery() => deliveries.Reader.ReadAsync().AsTask();
        internal Task<RuntimeFenceWrite> Fence() => barriers.Reader.ReadAsync().AsTask();
    }
    private sealed class BlockingMemory(byte[] bytes) : MemoryManager<byte>
    {
        internal TaskCompletionSource<bool> Entered { get; } = Signal();
        internal ManualResetEventSlim Release { get; } = new();
        internal int SpanCalls;
        internal Memory<byte> Buffer => CreateMemory(bytes.Length);
        public override Span<byte> GetSpan()
        {
            Interlocked.Increment(ref SpanCalls); Entered.TrySetResult(true);
            if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("FAKE_COPY_RELEASE_REQUIRED");
            return bytes;
        }
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException("FAKE_COPY_ONLY");
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { if (disposing) { Release.Set(); Release.Dispose(); } }
    }
    private sealed class Endpoint
    {
        internal RuntimeDeliveryBinding Binding { get; }
        internal FakeLifetime Lifetime { get; }
        internal FakeDriver Driver { get; } = new();
        internal Coordinator.HostRegistration Registration { get; }
        internal Endpoint(Coordinator c, RuntimeDeliveryBinding? binding = null)
        {
            Binding = binding ?? new(Guid.NewGuid(), "controlled-account", 1, new string('a', 64), Guid.NewGuid(), 1, Guid.NewGuid(), 1, 1);
            Lifetime = new(Binding);
            Registration = c.RegisterHostEndpoint(Binding, Driver, Lifetime);
        }
        internal bool Post(RuntimeDeliveryWrite write) => Registration.Evidence.AcceptNativePost(Receipt(write));
        internal bool Fence(RuntimeFenceWrite write) => Registration.Evidence.AcceptNativeFence(Receipt(write));
        internal async Task Retire() { Lifetime.Retire(); Check(await Registration.Evidence.RetirementObserved); }
    }
    private static Coordinator.DeliveryTicket Reserve(Coordinator c, Endpoint e, string body)
    { var result = c.TryReserve(e.Registration.Endpoint, Descriptor(body)); Check(result.Rejection == RuntimeDeliveryRejection.None && result.Ticket is not null); return result.Ticket!; }
    private static Coordinator.DeliveryTicket Queue(Coordinator c, Endpoint e, string body)
    { Coordinator.DeliveryTicket ticket = Reserve(c, e, body); Check(c.QueueValidated(ticket, Encoding.UTF8.GetBytes(body))); return ticket; }
    private static RuntimeDeliveryDescriptor Descriptor(string body) { byte[] bytes = Encoding.UTF8.GetBytes(body); return new(bytes.Length, Hash(bytes), 3); }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static RuntimeNativePostReceipt Receipt(RuntimeDeliveryWrite write) => new(write.DeliveryId, write.Binding, write.Descriptor.Sha256, RuntimeNativePostResult.Posted);
    private static RuntimeNativeFenceReceipt Receipt(RuntimeFenceWrite write) => new(write.FenceId, write.Binding, write.CutoffDeliveryId, write.NextChannelGeneration);
    private static RuntimeAuthorityScope Instance(RuntimeDeliveryBinding b) => new(RuntimeAuthorityScopeKind.Instance, b.IdentityAuthorityId, b.AccountKey, b.ApplicationBinding, b.InstanceId);
    private static RuntimeAuthorityScope Application(RuntimeDeliveryBinding b) => new(RuntimeAuthorityScopeKind.Application, b.IdentityAuthorityId, b.AccountKey, b.ApplicationBinding);
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Cancelled(Task task) { try { await task; } catch (OperationCanceledException) { return; } throw new InvalidOperationException("EXPECTED_CANCELLED_WAITER"); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("EXPECTED_" + typeof(T).Name); }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("DELIVERY_COORDINATOR_FAKE_ASSERTION"); }
}
