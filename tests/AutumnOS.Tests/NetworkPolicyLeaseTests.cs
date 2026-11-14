using System.Security.Cryptography;
using AutumnOS.Contracts;
using AutumnOS.Runtime;
using AutumnOS.Shell;

namespace AutumnOS.Tests;

// Real ordering kernel/router and preview policy, synthetic transport and lifetime.
// A synthetic Post receipt here proves ordering only, never a native STA Post.
internal static class NetworkPolicyLeaseTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return Case("live_offline_cancels_reserved_response_before_queue_and_waits_fence", Reserved);
        yield return Case("live_offline_waits_for_inflight_post_then_same_channel_fence", Sending);
        yield return Case("live_offline_fences_only_bound_preview_instance", Unrelated);
        yield return Case("synthetic_account_offline_fences_simulation_endpoint_only", Synthetic);
    }
    private static (string, Action) Case(string name, Func<Task> run) =>
        ("network.preview.synthetic_remote_" + name, () => run().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
    private static Task Reserved() => ReservedCore(false);
    private static Task Synthetic() => ReservedCore(true);
    private static async Task ReservedCore(bool synthetic)
    {
        await using var fixture = await Fixture.Create(synthetic);
        var target = fixture.Target;
        var ticket = target.Reserve();
        var before = fixture.Policy.Snapshot; var token = fixture.Policy.NetworkPolicyInvalidated;
        Task offline = fixture.Policy.SetOfflineAsync(true);
        RuntimeFenceWrite fence = await FirstFenceOrUnexpectedCommit(target, offline);
        Check(!offline.IsCompleted && !fixture.Policy.Offline && !token.IsCancellationRequested);
        Check(!target.Core.QueueValidated(ticket, Payload));
        Check(await target.Core.WaitOutcomeAsync(ticket) == RuntimeDeliveryState.NotSent && target.Driver.PostCount == 0);
        Check((synthetic ? fixture.Live : fixture.Test).Deliveries.SnapshotCounts().Fences == 0);
        target.Ack(fence); await offline;
        Check(fixture.Policy.Offline && token.IsCancellationRequested && target.Driver.PostCount == 0);
        Check(fixture.Policy.Snapshot.SessionEpoch == before.SessionEpoch && fixture.Policy.Snapshot.AccountNamespace == before.AccountNamespace);
    }
    private static async Task Sending()
    {
        await using var fixture = await Fixture.Create(false, holdPost: true);
        var target = fixture.Target; var before = fixture.Policy.Snapshot;
        target.Driver.BeforePost = () => Check(!fixture.Policy.Offline);
        var ticket = target.Reserve(); Check(target.Core.QueueValidated(ticket, Payload));
        await target.Driver.DeliveryStarted.Task;
        Task offline = fixture.Policy.SetOfflineAsync(true);
        // BeginAsync installs intent synchronously before returning its permit.
        // The driver is held before its fake Post; no sleeps/time-based race.
        Check(!offline.IsCompleted && target.Core.SnapshotCounts().Fences == 1 && !fixture.Policy.Offline);
        Check(!target.Driver.FenceReceived.Task.IsCompleted);
        target.Driver.ReleasePost.TrySetResult(true);
        Check(await target.Core.WaitOutcomeAsync(ticket) == RuntimeDeliveryState.Posted);
        RuntimeFenceWrite fence = await FirstFenceOrUnexpectedCommit(target, offline);
        Check(!offline.IsCompleted && target.Driver.PostCount == 1 && !fixture.Policy.Offline);
        target.Ack(fence); await offline;
        Check(fixture.Policy.Offline && fixture.Policy.Snapshot.SessionEpoch == before.SessionEpoch && fixture.Policy.Snapshot.AccountNamespace == before.AccountNamespace);
    }
    private static async Task Unrelated()
    {
        await using var fixture = await Fixture.Create(false);
        var bound = fixture.Target.Binding;
        var others = new[]
        {
            fixture.Add(fixture.Live, bound.AccountKey, bound.ApplicationBinding), // same app, another instance
            fixture.Add(fixture.Live, bound.AccountKey, new string('b', 64)),
            fixture.Add(fixture.Live, "other-real-account", bound.ApplicationBinding)
        };
        Task offline = fixture.Policy.SetOfflineAsync(true);
        RuntimeFenceWrite fence = await FirstFenceOrUnexpectedCommit(fixture.Target, offline);
        foreach (var endpoint in others)
        {
            var ticket = endpoint.Reserve(); Check(endpoint.Core.QueueValidated(ticket, Payload));
            Check(await endpoint.Core.WaitOutcomeAsync(ticket) == RuntimeDeliveryState.Posted);
            Check(endpoint.Driver.PostCount == 1 && !endpoint.Driver.FenceReceived.Task.IsCompleted);
        }
        Check(!offline.IsCompleted && !fixture.Policy.Offline);
        fixture.Target.Ack(fence); await offline;
    }
    private static async Task<RuntimeFenceWrite> FirstFenceOrUnexpectedCommit(Endpoint endpoint, Task commit)
    {
        Task first = await Task.WhenAny(endpoint.Driver.FenceReceived.Task, commit);
        Check(ReferenceEquals(first, endpoint.Driver.FenceReceived.Task));
        return await endpoint.Driver.FenceReceived.Task;
    }
    private static readonly byte[] Payload = [0, 1, 255];
    private sealed class Fixture : IAsyncDisposable
    {
        internal RuntimeAuthorityMutationCoordinator Live { get; } = new(Guid.NewGuid(), new());
        internal RuntimeAuthorityMutationCoordinator Test { get; } = new(Guid.NewGuid(), new());
        internal PreviewAuthorityRouter Router { get; }
        internal DeveloperPreviewSimulation Policy { get; }
        internal Endpoint Target { get; private set; } = null!;
        private readonly List<Endpoint> endpoints = [];
        private Fixture() { Router = new(Live, Test); Policy = new(Router); }
        internal static async Task<Fixture> Create(bool synthetic, bool holdPost = false)
        {
            var fixture = new Fixture();
            if (synthetic) await fixture.Policy.SwitchAccountAsync("account-a");
            fixture.Target = fixture.Add(synthetic ? fixture.Test : fixture.Live,
                synthetic ? fixture.Policy.Snapshot.AccountNamespace : "real-account-fixture", new string('a', 64), holdPost);
            var binding = fixture.Target.Binding;
            fixture.Router.BindInstanceScope(RuntimeMutationScope.ForInstance(binding.AccountKey, binding.ApplicationBinding, binding.InstanceId));
            return fixture;
        }
        internal Endpoint Add(RuntimeAuthorityMutationCoordinator authority, string account, string app, bool holdPost = false)
        { var endpoint = new Endpoint(authority, account, app, holdPost); endpoints.Add(endpoint); return endpoint; }
        public async ValueTask DisposeAsync()
        {
            foreach (var endpoint in endpoints) endpoint.Driver.ReleasePost.TrySetResult(true);
            foreach (var endpoint in endpoints) await endpoint.Retire();
            await Policy.DisposeAsync();
        }
    }
    private sealed class Endpoint
    {
        internal RuntimeDeliveryCoordinator Core { get; }
        internal RuntimeDeliveryBinding Binding { get; }
        internal RuntimeDeliveryCoordinator.HostRegistration Registration { get; }
        internal Driver Driver { get; }
        private readonly Lifetime lifetime;
        internal Endpoint(RuntimeAuthorityMutationCoordinator authority, string account, string app, bool holdPost)
        {
            Core = authority.Deliveries; Binding = new(authority.IdentityAuthorityId, account, 1, app, Guid.NewGuid(), 1, Guid.NewGuid(), 1, 1);
            Driver = new(holdPost); lifetime = new(Binding);
            Registration = authority.RegisterHostEndpoint(Binding, Driver, lifetime);
            Driver.Post = write => Registration.Evidence.AcceptNativePost(new(write.DeliveryId, write.Binding, write.Descriptor.Sha256, RuntimeNativePostResult.Posted));
        }
        internal RuntimeDeliveryCoordinator.DeliveryTicket Reserve() => Core.TryReserve(Registration.Endpoint,
            new(Payload.Length, Convert.ToHexStringLower(SHA256.HashData(Payload)), 1)).Ticket ?? throw new InvalidOperationException("RESERVATION_REJECTED");
        internal void Ack(RuntimeFenceWrite fence) => Check(Registration.Evidence.AcceptNativeFence(new(fence.FenceId, fence.Binding, fence.CutoffDeliveryId, fence.NextChannelGeneration)));
        internal async Task Retire()
        { lifetime.Completion.TrySetResult(true); Check(await Registration.Evidence.RetirementObserved); await Registration.Evidence.WhenIdle; }
    }
    private sealed class Driver : IOrderedRuntimeDeliveryDriver
    {
        internal TaskCompletionSource<bool> DeliveryStarted { get; } = Signal();
        internal TaskCompletionSource<bool> ReleasePost { get; } = Signal();
        internal TaskCompletionSource<RuntimeFenceWrite> FenceReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Func<RuntimeDeliveryWrite, bool> Post = null!;
        internal Action? BeforePost;
        internal int PostCount;
        internal Driver(bool holdPost) { if (!holdPost) ReleasePost.TrySetResult(true); }
        public async ValueTask WriteDeliveryAsync(RuntimeDeliveryWrite delivery)
        { DeliveryStarted.TrySetResult(true); await ReleasePost.Task; BeforePost?.Invoke(); Interlocked.Increment(ref PostCount); Check(Post(delivery)); }
        public ValueTask WriteFenceAsync(RuntimeFenceWrite fence) { Check(FenceReceived.TrySetResult(fence)); return ValueTask.CompletedTask; }
    }
    private sealed class Lifetime(RuntimeDeliveryBinding binding) : IOriginalRuntimeEndpointLifetime
    {
        internal TaskCompletionSource<bool> Completion { get; } = Signal();
        private RuntimeEndpointLifetimeRegistration? registration;
        public RuntimeEndpointLifetimeRegistration OriginalLifetime => registration ??= RuntimeEndpointLifetimeRegistration.BindForHost(binding, Completion.Task);
    }
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("PREVIEW_REMOTE_POLICY_ASSERTION"); }
}

