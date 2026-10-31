using System.Security.Cryptography;

namespace AutumnOS.Runtime;

// G2-B1 ordering kernel only. No authority hooks, native process observations,
// IPC decoder, or production remote-delivery admission are installed here.
internal sealed record RuntimeDeliveryLimits(int MaximumEndpoints = 32, int MaximumTicketsPerEndpoint = 16,
    int MaximumPayloadBytes = 32768, int MaximumFences = 32);

internal sealed record RuntimeDeliveryBinding(Guid IdentityAuthorityId, string AccountKey, long AccountEpoch,
    string ApplicationBinding, Guid InstanceId, long InstanceEpoch, Guid DocumentId,
    long DocumentGeneration, long ChannelGeneration);

internal sealed record RuntimeDeliveryDescriptor(int Bytes, string Sha256, long FrozenRevision);
internal enum RuntimeDeliveryState { Reserved, NotSent, Sending, Posted, Rejected, Unknown, FencedUnknown, RetiredUnknown }
internal enum RuntimeNativePostResult { Posted, RejectedBeforePost }
internal enum RuntimeDeliveryRejection { None, Capacity, Fenced, EndpointUnavailable }
internal enum RuntimeAuthorityScopeKind { Identity, Application, Instance }
internal sealed record RuntimeAuthorityScope(RuntimeAuthorityScopeKind Kind, Guid IdentityAuthorityId,
    string? AccountKey = null, string? ApplicationBinding = null, Guid? InstanceId = null);

internal sealed record RuntimeDeliveryWrite(long DeliveryId, RuntimeDeliveryBinding Binding,
    RuntimeDeliveryDescriptor Descriptor, ReadOnlyMemory<byte> Payload);
// DeliveryId is unique correlation, not wire sequence: independent copies may
// become ready out of reservation order. The driver is called in queue order.
internal sealed record RuntimeFenceWrite(long FenceId, RuntimeDeliveryBinding Binding,
    long CutoffDeliveryId, long NextChannelGeneration);
internal sealed record RuntimeNativePostReceipt(long DeliveryId, RuntimeDeliveryBinding Binding,
    string PayloadSha256, RuntimeNativePostResult Result);
internal sealed record RuntimeNativeFenceReceipt(long FenceId, RuntimeDeliveryBinding Binding,
    long CutoffDeliveryId, long NextChannelGeneration);

// Completion means bytes were written, never that WebView2 posted the message.
// The coordinator alone calls this ordered driver, outside its state lock.
internal interface IOrderedRuntimeDeliveryDriver
{
    ValueTask WriteDeliveryAsync(RuntimeDeliveryWrite delivery);
    ValueTask WriteFenceAsync(RuntimeFenceWrite fence);
}

// The future OS owner creates one registration from its original handle group.
// No OS witness is implemented in B1; tests bind explicitly synthetic Tasks.
internal sealed class RuntimeEndpointLifetimeRegistration
{
    private int claimed;
    internal RuntimeDeliveryBinding OriginalBinding { get; }
    internal Task OriginalGroupRetired { get; }
    private RuntimeEndpointLifetimeRegistration(RuntimeDeliveryBinding binding, Task retirement)
    { OriginalBinding = binding; OriginalGroupRetired = retirement; }
    internal static RuntimeEndpointLifetimeRegistration BindForHost(RuntimeDeliveryBinding binding, Task retirement)
    {
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(retirement);
        return new(binding, retirement);
    }
    internal bool TryClaimOnce() => Interlocked.CompareExchange(ref claimed, 1, 0) == 0;
}

// Host object capability, never a wire bool. Read once outside the state lock.
// The immutable token binds the initial generation and is not reusable, even
// after retirement or by another coordinator. A failed Task is not retirement.
internal interface IOriginalRuntimeEndpointLifetime { RuntimeEndpointLifetimeRegistration OriginalLifetime { get; } }

internal sealed class RuntimeDeliveryCoordinator
{
    private readonly object sync = new(), issuer = new();
    private readonly RuntimeDeliveryLimits limits;
    private readonly List<EndpointState> endpoints = [];
    private readonly List<FenceState> fences = [];
    private long nextFence, nextDelivery;

    internal RuntimeDeliveryCoordinator(RuntimeDeliveryLimits? limits = null)
    {
        this.limits = limits ?? new();
        if (this.limits.MaximumEndpoints is < 1 or > 1024 || this.limits.MaximumTicketsPerEndpoint is < 1 or > 1024 ||
            this.limits.MaximumPayloadBytes is < 1 or > 16777216 || this.limits.MaximumFences is < 1 or > 1024 ||
            (long)this.limits.MaximumEndpoints * this.limits.MaximumTicketsPerEndpoint * this.limits.MaximumPayloadBytes > 67108864)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    // Only the trusted host composition root receives both capabilities. The
    // endpoint key may be used by delivery code; the evidence port stays with
    // the original native peer decoder. Neither is an OS proof by itself.
    internal HostRegistration RegisterHostEndpoint(RuntimeDeliveryBinding binding,
        IOrderedRuntimeDeliveryDriver driver, IOriginalRuntimeEndpointLifetime witness)
        => RegisterCore(binding, driver, witness);

    internal ReservationResult TryReserve(EndpointKey endpoint, RuntimeDeliveryDescriptor descriptor)
        => ReserveCore(endpoint, descriptor);
    // Validated here means length/hash only, never application permission.
    internal bool QueueValidated(DeliveryTicket ticket, ReadOnlyMemory<byte> payload) => QueueCore(ticket, payload);
    internal bool CancelBeforeSend(DeliveryTicket ticket) => CancelCore(ticket);
    internal Task<RuntimeDeliveryState> WaitOutcomeAsync(DeliveryTicket ticket, CancellationToken waitCancellation = default)
        => TicketFor(ticket).Completion.Task.WaitAsync(waitCancellation);
    internal RuntimeDeliveryState StateOf(DeliveryTicket ticket) { lock (sync) return TicketFor(ticket).State; }
    internal int OutstandingCount(EndpointKey endpoint) { lock (sync) return EndpointFor(endpoint).Tickets.Count; }
    internal (int Endpoints, int Tickets, int ResourceSlots, int Fences, int QueuedWork) SnapshotCounts()
    { lock (sync) return (endpoints.Count, endpoints.Sum(e => e.Tickets.Count), endpoints.Sum(e => e.OwnedSlots.Count), fences.Count, endpoints.Sum(e => e.Outbound.Count)); }
    internal ValueTask<FenceOperation> BeginFenceAsync(RuntimeAuthorityScope scope, CancellationToken beforeIntentCancellation = default)
        => BeginFenceCore(scope, beforeIntentCancellation);

    internal sealed record ReservationResult(DeliveryTicket? Ticket, RuntimeDeliveryRejection Rejection);

    // Private constructors plus an issuer object check prevent capabilities
    // minted with another coordinator, copied DTOs or guessed numeric IDs from
    // acquiring authority. This is an in-process host boundary, not protection
    // against arbitrary trusted-process reflection/unsafe code execution.
    internal abstract class Capability
    {
        private readonly object brand, state;
        protected Capability(object brand, object state) { this.brand = brand; this.state = state; }
        internal object Unwrap(object expected) => ReferenceEquals(brand, expected) ? state : throw new InvalidOperationException("DELIVERY_CAPABILITY_ISSUER_MISMATCH");
    }
    internal sealed class EndpointKey : Capability
    {
        private EndpointKey(object brand, object state) : base(brand, state) { }
        internal static EndpointKey Mint(object brand, object state) => new(brand, state);
    }
    internal sealed class DeliveryTicket : Capability
    {
        private DeliveryTicket(object brand, object state) : base(brand, state) { }
        internal static DeliveryTicket Mint(object brand, object state) => new(brand, state);
    }
    internal sealed class HostRegistration : Capability
    {
        internal EndpointKey Endpoint { get; }
        internal HostEvidencePort Evidence { get; }
        private HostRegistration(object brand, object state, EndpointKey endpoint, HostEvidencePort evidence) : base(brand, state)
        { Endpoint = endpoint; Evidence = evidence; }
        internal static HostRegistration Mint(object brand, object state, EndpointKey endpoint, HostEvidencePort evidence) => new(brand, state, endpoint, evidence);
    }
    internal sealed class HostEvidencePort : Capability
    {
        private readonly RuntimeDeliveryCoordinator owner;
        private HostEvidencePort(object brand, object state, RuntimeDeliveryCoordinator owner) : base(brand, state) { this.owner = owner; }
        internal static HostEvidencePort Mint(object brand, object state, RuntimeDeliveryCoordinator owner) => new(brand, state, owner);
        internal bool AcceptNativePost(RuntimeNativePostReceipt receipt) => owner.AcceptPost(this, receipt);
        internal bool AcceptNativeFence(RuntimeNativeFenceReceipt receipt) => owner.AcceptFence(this, receipt);
        internal bool MarkReceiptUnknown(long deliveryId) => owner.ReceiptUnknown(this, deliveryId);
        internal void MarkTransportUnknown() => owner.TransportUnknown(this);
        internal Task<bool> RetirementObserved => owner.EndpointFor(this).RetirementObservation.Task;
        internal Task WhenUnavailable => owner.EndpointFor(this).UnavailableObservation.Task;
        internal Task WhenIdle { get { lock (owner.sync) return owner.EndpointFor(this).Idle.Task; } }
    }
    internal sealed class FenceOperation : Capability
    {
        private readonly RuntimeDeliveryCoordinator owner;
        private FenceOperation(object brand, object state, RuntimeDeliveryCoordinator owner) : base(brand, state) { this.owner = owner; }
        internal static FenceOperation Mint(object brand, object state, RuntimeDeliveryCoordinator owner) => new(brand, state, owner);
        // Cancellation stops only this wait; registered writer intent persists.
        internal Task<MutationPermit> WaitCommitPermitAsync(CancellationToken waitCancellation = default)
            => owner.FenceFor(this).Ready.Task.WaitAsync(waitCancellation);
    }
    internal sealed class MutationPermit : Capability
    {
        private readonly RuntimeDeliveryCoordinator owner;
        private MutationPermit(object brand, object state, RuntimeDeliveryCoordinator owner) : base(brand, state) { this.owner = owner; }
        internal static MutationPermit Mint(object brand, object state, RuntimeDeliveryCoordinator owner) => new(brand, state, owner);
        // A caller publishes Changed/Cancel/Publish only after this returns (or
        // throws), when the fence has been released. No authority is wired B1.
        internal void Commit(Action shortAuthorityCommit) => owner.CommitCore(this, shortAuthorityCommit);
    }

    private sealed class EndpointState(RuntimeDeliveryBinding binding, IOrderedRuntimeDeliveryDriver driver)
    {
        internal RuntimeDeliveryBinding Binding = binding;
        internal readonly IOrderedRuntimeDeliveryDriver Driver = driver;
        internal readonly Dictionary<long, TicketState> Tickets = [];
        // Includes terminal tickets while a copy or driver still owns a buffer.
        internal readonly HashSet<TicketState> OwnedSlots = [];
        internal readonly Queue<Work> Outbound = [];
        internal readonly TaskCompletionSource<bool> RetirementObservation = NewCompletion<bool>();
        internal readonly TaskCompletionSource<bool> UnavailableObservation = NewCompletion<bool>();
        internal TaskCompletionSource<bool> Idle = NewCompletion<bool>();
        internal long NextDelivery;
        internal bool PumpRunning, Unavailable, DriverBroken, Retired;
    }
    private sealed class TicketState(EndpointState endpoint, long id, RuntimeDeliveryDescriptor descriptor)
    {
        internal readonly EndpointState Endpoint = endpoint;
        internal readonly long Id = id;
        internal readonly RuntimeDeliveryBinding Binding = endpoint.Binding;
        internal readonly RuntimeDeliveryDescriptor Descriptor = descriptor;
        internal readonly TaskCompletionSource<RuntimeDeliveryState> Completion = NewCompletion<RuntimeDeliveryState>();
        internal RuntimeDeliveryState State = RuntimeDeliveryState.Reserved;
        internal byte[]? Payload;
        internal bool Queued, CopyInProgress, WriteInProgress;
    }
    private sealed class FenceState(long id, RuntimeAuthorityScope scope)
    {
        internal readonly long Id = id;
        internal readonly RuntimeAuthorityScope Scope = scope;
        internal readonly List<FenceLeg> Legs = [];
        internal readonly TaskCompletionSource<MutationPermit> Ready = NewCompletion<MutationPermit>();
        internal bool Active, PermitIssued, Committing, Finished;
    }
    private sealed class FenceLeg(FenceState fence, EndpointState endpoint)
    {
        internal readonly FenceState Fence = fence;
        internal readonly EndpointState Endpoint = endpoint;
        internal readonly RuntimeDeliveryBinding Binding = endpoint.Binding;
        internal readonly long Cutoff = endpoint.NextDelivery;
        // Zero is never emitted; an exhausted live endpoint must retire.
        internal readonly long NextGeneration = endpoint.Binding.ChannelGeneration == long.MaxValue ? 0 : endpoint.Binding.ChannelGeneration + 1;
        internal bool WriteStarted, Acknowledged;
    }
    private sealed record Work(TicketState? Ticket, FenceLeg? Fence);
    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private EndpointState EndpointFor(Capability capability) => capability.Unwrap(issuer) as EndpointState
        ?? throw new InvalidOperationException("DELIVERY_ENDPOINT_CAPABILITY_REQUIRED");
    private TicketState TicketFor(Capability capability) => capability.Unwrap(issuer) as TicketState
        ?? throw new InvalidOperationException("DELIVERY_TICKET_CAPABILITY_REQUIRED");
    private FenceState FenceFor(Capability capability) => capability.Unwrap(issuer) as FenceState
        ?? throw new InvalidOperationException("DELIVERY_FENCE_CAPABILITY_REQUIRED");

    private HostRegistration RegisterCore(RuntimeDeliveryBinding binding, IOrderedRuntimeDeliveryDriver driver,
        IOriginalRuntimeEndpointLifetime witness)
    {
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(driver); ArgumentNullException.ThrowIfNull(witness);
        ValidateBinding(binding);
        // The potentially external getter is evaluated once, outside sync.
        RuntimeEndpointLifetimeRegistration lifetime = witness.OriginalLifetime
            ?? throw new ArgumentException("ORIGINAL_LIFETIME_TOKEN_REQUIRED", nameof(witness));
        if (lifetime.OriginalBinding != binding) throw new ArgumentException("ORIGINAL_LIFETIME_BINDING_MISMATCH", nameof(witness));
        Task retirement = lifetime.OriginalGroupRetired;
        EndpointState endpoint;
        HostRegistration registration;
        lock (sync)
        {
            if (endpoints.Count >= limits.MaximumEndpoints) throw new InvalidOperationException("DELIVERY_ENDPOINT_CAPACITY");
            if (fences.Any(f => Matches(f.Scope, binding))) throw new InvalidOperationException("DELIVERY_REGISTRATION_FENCED");
            if (endpoints.Any(e => !e.Retired && e.Binding.IdentityAuthorityId == binding.IdentityAuthorityId && e.Binding.InstanceId == binding.InstanceId))
                throw new InvalidOperationException("DELIVERY_LIVE_INSTANCE_ALREADY_REGISTERED");
            if (!lifetime.TryClaimOnce()) throw new InvalidOperationException("ORIGINAL_LIFETIME_ALREADY_REGISTERED");
            endpoint = new(binding, driver); endpoint.Idle.TrySetResult(true); endpoints.Add(endpoint);
            registration = HostRegistration.Mint(issuer, endpoint, EndpointKey.Mint(issuer, endpoint), HostEvidencePort.Mint(issuer, endpoint, this));
        }
        _ = ObserveRetirementAsync(endpoint, retirement);
        return registration;
    }

    private ReservationResult ReserveCore(EndpointKey endpointKey, RuntimeDeliveryDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Bytes < 0 || descriptor.Bytes > limits.MaximumPayloadBytes || descriptor.FrozenRevision < 0 || !HashShape(descriptor.Sha256))
            throw new ArgumentException("DELIVERY_DESCRIPTOR_INVALID", nameof(descriptor));
        lock (sync)
        {
            EndpointState endpoint = EndpointFor(endpointKey);
            if (endpoint.Unavailable || endpoint.Retired) return new(null, RuntimeDeliveryRejection.EndpointUnavailable);
            if (fences.Any(f => Matches(f.Scope, endpoint.Binding))) return new(null, RuntimeDeliveryRejection.Fenced);
            if (endpoint.OwnedSlots.Count >= limits.MaximumTicketsPerEndpoint) return new(null, RuntimeDeliveryRejection.Capacity);
            long id = checked(++nextDelivery); endpoint.NextDelivery = id;
            var ticket = new TicketState(endpoint, id, descriptor); endpoint.Tickets.Add(id, ticket); endpoint.OwnedSlots.Add(ticket);
            if (endpoint.Idle.Task.IsCompleted) endpoint.Idle = NewCompletion<bool>();
            return new(DeliveryTicket.Mint(issuer, ticket), RuntimeDeliveryRejection.None);
        }
    }

    private bool QueueCore(DeliveryTicket capability, ReadOnlyMemory<byte> payload)
    {
        TicketState ticket = TicketFor(capability);
        lock (sync)
        {
            if (ticket.State != RuntimeDeliveryState.Reserved || ticket.Queued || ticket.Endpoint.Unavailable ||
                ticket.Endpoint.Retired || fences.Any(f => Matches(f.Scope, ticket.Binding))) return false;
            if (payload.Length != ticket.Descriptor.Bytes || payload.Length > limits.MaximumPayloadBytes)
                throw new ArgumentException("DELIVERY_PAYLOAD_LENGTH_MISMATCH", nameof(payload));
            // One slot, one copy attempt. No custom MemoryManager.GetSpan is
            // called before this atomic claim, or while holding sync.
            ticket.Queued = true; ticket.CopyInProgress = true;
        }
        List<EndpointState> starts = [];
        byte[]? copy = null;
        bool queued = false;
        try
        {
            copy = payload.ToArray();
            if (Convert.ToHexStringLower(SHA256.HashData(copy)) != ticket.Descriptor.Sha256)
                throw new ArgumentException("DELIVERY_PAYLOAD_HASH_MISMATCH", nameof(payload));
            lock (sync)
            {
                queued = ticket.State == RuntimeDeliveryState.Reserved && !ticket.Endpoint.Unavailable &&
                    !ticket.Endpoint.Retired && !fences.Any(f => Matches(f.Scope, ticket.Binding));
                if (queued)
                {
                    ticket.Payload = copy; copy = null; ticket.Endpoint.Outbound.Enqueue(new(ticket, null));
                    ScheduleLocked(ticket.Endpoint, starts);
                }
            }
            return queued;
        }
        finally
        {
            if (copy is not null) { CryptographicOperations.ZeroMemory(copy); copy = null; }
            lock (sync)
            {
                ticket.CopyInProgress = false;
                if (!queued && ticket.State == RuntimeDeliveryState.Reserved) FinishLocked(ticket, RuntimeDeliveryState.NotSent);
                ReleaseSlotIfPossibleLocked(ticket); RemoveRetiredLocked(ticket.Endpoint);
            }
            StartPumps(starts);
        }
    }

    private bool CancelCore(DeliveryTicket capability)
    {
        lock (sync)
        {
            TicketState ticket = TicketFor(capability);
            if (ticket.State != RuntimeDeliveryState.Reserved) return false;
            FinishLocked(ticket, RuntimeDeliveryState.NotSent); return true;
        }
    }

    private ValueTask<FenceOperation> BeginFenceCore(RuntimeAuthorityScope scope, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(scope); ValidateScope(scope); cancellation.ThrowIfCancellationRequested();
        List<EndpointState> starts = [];
        FenceOperation operation;
        lock (sync)
        {
            cancellation.ThrowIfCancellationRequested();
            if (fences.Count >= limits.MaximumFences) throw new InvalidOperationException("DELIVERY_FENCE_CAPACITY");
            var fence = new FenceState(checked(++nextFence), scope); fences.Add(fence);
            operation = FenceOperation.Mint(issuer, fence, this);
            // Writer intent takes effect even while an earlier overlapping
            // writer owns its permit. Unsent work can no longer begin sending.
            foreach (EndpointState endpoint in endpoints.Where(e => Matches(scope, e.Binding)))
                CancelReservedLocked(endpoint);
            AdvanceFencesLocked(starts);
        }
        StartPumps(starts);
        // Cancellation after this point only cancels a wait. The coordinator
        // retains this writer intent until its one-shot permit is consumed.
        return ValueTask.FromResult(operation);
    }

    private void AdvanceFencesLocked(List<EndpointState> starts)
    {
        for (int index = 0; index < fences.Count; index++)
        {
            FenceState fence = fences[index];
            if (fence.Active || fences.Take(index).Any(earlier => Overlaps(earlier.Scope, fence.Scope))) continue;
            fence.Active = true;
            foreach (EndpointState endpoint in endpoints.Where(e => Matches(fence.Scope, e.Binding)))
            {
                var leg = new FenceLeg(fence, endpoint); fence.Legs.Add(leg);
                if (!endpoint.Retired && leg.NextGeneration == 0)
                { UnavailableLocked(endpoint); CancelReservedLocked(endpoint); }
                else if (!endpoint.Retired)
                { endpoint.Outbound.Enqueue(new(null, leg)); ScheduleLocked(endpoint, starts); }
            }
        }
        foreach (FenceState fence in fences.Where(f => f.Active && !f.PermitIssued))
        {
            bool ready = fence.Legs.All(leg => leg.Endpoint.Retired || leg.Acknowledged &&
                !leg.Endpoint.Tickets.Values.Any(t => t.Id <= leg.Cutoff));
            if (!ready) continue;
            fence.PermitIssued = true; fence.Ready.TrySetResult(MutationPermit.Mint(issuer, fence, this));
        }
    }

    private void CommitCore(MutationPermit capability, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        FenceState fence;
        lock (sync)
        {
            fence = FenceFor(capability);
            if (!fence.PermitIssued || fence.Finished || fence.Committing || !fences.Contains(fence))
                throw new InvalidOperationException("DELIVERY_MUTATION_PERMIT_ALREADY_USED");
            fence.Committing = true;
        }
        try { action(); } // No state lock, driver, Changed, or Cancel callback.
        finally
        {
            List<EndpointState> starts = [];
            lock (sync)
            {
                fence.Finished = true; fences.Remove(fence); AdvanceFencesLocked(starts);
            }
            StartPumps(starts);
        }
    }

    private bool AcceptPost(HostEvidencePort port, RuntimeNativePostReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        lock (sync)
        {
            EndpointState endpoint = EndpointFor(port);
            if (endpoint.Retired || !endpoint.Tickets.TryGetValue(receipt.DeliveryId, out TicketState? ticket) ||
                ticket.State is not (RuntimeDeliveryState.Sending or RuntimeDeliveryState.Unknown) || ticket.Binding != receipt.Binding ||
                ticket.Descriptor.Sha256 != receipt.PayloadSha256 || !Enum.IsDefined(receipt.Result)) return false;
            FinishLocked(ticket, receipt.Result == RuntimeNativePostResult.Posted ? RuntimeDeliveryState.Posted : RuntimeDeliveryState.Rejected);
            return true;
        }
    }

    private bool AcceptFence(HostEvidencePort port, RuntimeNativeFenceReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        List<EndpointState> starts = [];
        lock (sync)
        {
            EndpointState endpoint = EndpointFor(port);
            FenceLeg? leg = fences.Where(f => f.Active && f.Id == receipt.FenceId).SelectMany(f => f.Legs)
                .SingleOrDefault(l => ReferenceEquals(l.Endpoint, endpoint));
            if (endpoint.Retired || leg is null || !leg.WriteStarted || leg.Acknowledged || leg.Binding != receipt.Binding ||
                leg.Cutoff != receipt.CutoffDeliveryId || leg.NextGeneration != receipt.NextChannelGeneration) return false;
            leg.Acknowledged = true;
            endpoint.Binding = endpoint.Binding with { ChannelGeneration = leg.NextGeneration };
            if (leg.NextGeneration == long.MaxValue) UnavailableLocked(endpoint);
            foreach (TicketState ticket in endpoint.Tickets.Values.Where(t => t.Id <= leg.Cutoff).ToArray())
            {
                if (ticket.State is RuntimeDeliveryState.Sending or RuntimeDeliveryState.Unknown)
                    FinishLocked(ticket, RuntimeDeliveryState.FencedUnknown);
            }
            AdvanceFencesLocked(starts);
        }
        StartPumps(starts); return true;
    }

    private bool ReceiptUnknown(HostEvidencePort port, long id)
    {
        lock (sync)
        {
            EndpointState endpoint = EndpointFor(port);
            if (!endpoint.Tickets.TryGetValue(id, out TicketState? ticket) || ticket.State != RuntimeDeliveryState.Sending) return false;
            ticket.State = RuntimeDeliveryState.Unknown; UnavailableLocked(endpoint); CancelReservedLocked(endpoint); return true;
        }
    }

    private void TransportUnknown(HostEvidencePort port)
    { lock (sync) BreakTransportLocked(EndpointFor(port)); }

    private static void BreakTransportLocked(EndpointState endpoint)
    {
        endpoint.DriverBroken = true; UnavailableLocked(endpoint); CancelReservedLocked(endpoint);
        foreach (TicketState ticket in endpoint.Tickets.Values)
            if (ticket.State == RuntimeDeliveryState.Sending) ticket.State = RuntimeDeliveryState.Unknown;
    }

    private async Task ObserveRetirementAsync(EndpointState endpoint, Task retirement)
    {
        try { await retirement.ConfigureAwait(false); }
        catch (Exception)
        {
            lock (sync) { BreakTransportLocked(endpoint); endpoint.RetirementObservation.TrySetResult(false); }
            return; // A faulted/cancelled lifetime task is never retirement.
        }
        List<EndpointState> starts = [];
        lock (sync)
        {
            endpoint.Retired = true; UnavailableLocked(endpoint);
            foreach (TicketState ticket in endpoint.Tickets.Values.ToArray())
                FinishLocked(ticket, ticket.State == RuntimeDeliveryState.Reserved ? RuntimeDeliveryState.NotSent : RuntimeDeliveryState.RetiredUnknown);
            endpoint.Outbound.Clear(); AdvanceFencesLocked(starts); RemoveRetiredLocked(endpoint);
            endpoint.RetirementObservation.TrySetResult(true);
        }
        StartPumps(starts);
    }

    private static void FinishLocked(TicketState ticket, RuntimeDeliveryState outcome)
    {
        bool removeQueued = ticket.State == RuntimeDeliveryState.Reserved && ticket.Queued;
        ticket.State = outcome; ticket.Endpoint.Tickets.Remove(ticket.Id);
        if (ticket.Payload is byte[] bytes) { CryptographicOperations.ZeroMemory(bytes); ticket.Payload = null; }
        if (removeQueued)
        {
            // Freeing a ticket slot must also free its queued work reference;
            // repeated queue/cancel behind a held writer cannot grow a history.
            Work[] retained = ticket.Endpoint.Outbound.Where(w => !ReferenceEquals(w.Ticket, ticket)).ToArray();
            ticket.Endpoint.Outbound.Clear(); foreach (Work work in retained) ticket.Endpoint.Outbound.Enqueue(work);
        }
        ticket.Completion.TrySetResult(outcome);
        ReleaseSlotIfPossibleLocked(ticket);
    }
    private static void ReleaseSlotIfPossibleLocked(TicketState ticket)
    {
        if (ticket.State is RuntimeDeliveryState.Reserved or RuntimeDeliveryState.Sending or RuntimeDeliveryState.Unknown ||
            ticket.CopyInProgress || ticket.WriteInProgress || ticket.Payload is not null) return;
        ticket.Endpoint.OwnedSlots.Remove(ticket); CheckIdleLocked(ticket.Endpoint);
    }
    private static void CheckIdleLocked(EndpointState endpoint)
    { if (!endpoint.PumpRunning && endpoint.OwnedSlots.Count == 0) endpoint.Idle.TrySetResult(true); }
    private static void UnavailableLocked(EndpointState endpoint)
    { endpoint.Unavailable = true; endpoint.UnavailableObservation.TrySetResult(true); }
    private static void CancelReservedLocked(EndpointState endpoint)
    {
        foreach (TicketState ticket in endpoint.Tickets.Values.Where(t => t.State == RuntimeDeliveryState.Reserved).ToArray())
            FinishLocked(ticket, RuntimeDeliveryState.NotSent);
    }
    private static void ScheduleLocked(EndpointState endpoint, List<EndpointState> starts)
    {
        if (endpoint.PumpRunning) return;
        if (endpoint.Idle.Task.IsCompleted) endpoint.Idle = NewCompletion<bool>();
        endpoint.PumpRunning = true; starts.Add(endpoint);
    }
    private void StartPumps(List<EndpointState> starts)
    { foreach (EndpointState endpoint in starts) _ = PumpAsync(endpoint); }

    private async Task PumpAsync(EndpointState endpoint)
    {
        while (true)
        {
            Work work; byte[]? payload = null;
            lock (sync)
            {
                if (endpoint.Outbound.Count == 0 || endpoint.Retired)
                { endpoint.PumpRunning = false; CheckIdleLocked(endpoint); RemoveRetiredLocked(endpoint); return; }
                work = endpoint.Outbound.Dequeue();
                if (work.Ticket is TicketState ticket)
                {
                    if (ticket.State != RuntimeDeliveryState.Reserved) continue;
                    if (endpoint.Unavailable || fences.Any(f => Matches(f.Scope, ticket.Binding)))
                    { FinishLocked(ticket, RuntimeDeliveryState.NotSent); continue; }
                    ticket.State = RuntimeDeliveryState.Sending; ticket.WriteInProgress = true;
                    payload = ticket.Payload; ticket.Payload = null;
                }
                else if (work.Fence is FenceLeg leg)
                {
                    if (endpoint.DriverBroken || leg.Fence.Finished || leg.Acknowledged) continue;
                    leg.WriteStarted = true;
                }
            }
            try
            {
                if (work.Ticket is TicketState ticket)
                    await endpoint.Driver.WriteDeliveryAsync(new(ticket.Id, ticket.Binding, ticket.Descriptor,
                        payload ?? throw new InvalidOperationException("DELIVERY_QUEUED_PAYLOAD_MISSING"))).ConfigureAwait(false);
                else if (work.Fence is FenceLeg leg)
                    await endpoint.Driver.WriteFenceAsync(new(leg.Fence.Id, leg.Binding, leg.Cutoff, leg.NextGeneration)).ConfigureAwait(false);
            }
            catch (Exception)
            { lock (sync) BreakTransportLocked(endpoint); }
            finally
            {
                if (payload is not null) { CryptographicOperations.ZeroMemory(payload); payload = null; }
                if (work.Ticket is TicketState completedWrite)
                    lock (sync) { completedWrite.WriteInProgress = false; ReleaseSlotIfPossibleLocked(completedWrite); }
            }
        }
    }

    private void RemoveRetiredLocked(EndpointState endpoint)
    {
        if (endpoint.Retired && !endpoint.PumpRunning && endpoint.OwnedSlots.Count == 0) endpoints.Remove(endpoint);
    }
    private static bool Matches(RuntimeAuthorityScope scope, RuntimeDeliveryBinding binding) =>
        scope.IdentityAuthorityId == binding.IdentityAuthorityId && (scope.Kind == RuntimeAuthorityScopeKind.Identity ||
        scope.AccountKey == binding.AccountKey && scope.ApplicationBinding == binding.ApplicationBinding &&
        (scope.Kind == RuntimeAuthorityScopeKind.Application || scope.InstanceId == binding.InstanceId));
    private static bool Overlaps(RuntimeAuthorityScope a, RuntimeAuthorityScope b) =>
        a.IdentityAuthorityId == b.IdentityAuthorityId && (a.Kind == RuntimeAuthorityScopeKind.Identity || b.Kind == RuntimeAuthorityScopeKind.Identity ||
        a.AccountKey == b.AccountKey && a.ApplicationBinding == b.ApplicationBinding &&
        (a.Kind == RuntimeAuthorityScopeKind.Application || b.Kind == RuntimeAuthorityScopeKind.Application || a.InstanceId == b.InstanceId));
    private static bool HashShape(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool PlainKey(string? value) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl);
    private static void ValidateBinding(RuntimeDeliveryBinding binding)
    {
        if (binding.IdentityAuthorityId == Guid.Empty || binding.InstanceId == Guid.Empty || binding.DocumentId == Guid.Empty ||
            !PlainKey(binding.AccountKey) || !HashShape(binding.ApplicationBinding) || binding.AccountEpoch < 0 || binding.InstanceEpoch < 0 ||
            binding.DocumentGeneration < 1 || binding.ChannelGeneration is < 1 or long.MaxValue)
            throw new ArgumentException("DELIVERY_BINDING_INVALID", nameof(binding));
    }
    private static void ValidateScope(RuntimeAuthorityScope scope)
    {
        if (!Enum.IsDefined(scope.Kind) || scope.IdentityAuthorityId == Guid.Empty ||
            (scope.Kind == RuntimeAuthorityScopeKind.Identity
                ? scope.AccountKey is not null || scope.ApplicationBinding is not null || scope.InstanceId is not null
                : !PlainKey(scope.AccountKey) || !HashShape(scope.ApplicationBinding) ||
                  (scope.Kind == RuntimeAuthorityScopeKind.Instance ? scope.InstanceId is null || scope.InstanceId == Guid.Empty : scope.InstanceId is not null)))
            throw new ArgumentException("DELIVERY_AUTHORITY_SCOPE_INVALID", nameof(scope));
    }
}
