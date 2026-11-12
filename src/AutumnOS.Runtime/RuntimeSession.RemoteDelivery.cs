using System.Security.Cryptography;
using System.Text;

namespace AutumnOS.Runtime;

public sealed partial class RuntimeSession
{
    // A frame may spend time in IPC and in the image decoder. Publish the pixels only
    // if the exact authority frozen before the poll is still current. No I/O under the leases.
    internal bool WithCurrentView(RuntimeResponseStamp stamp, Action publish) =>
        WithEventAuthority("lifecycle.changed", stamp, publish);
    // The callback under all final permission, state and identity leases only reserves. No IPC, await
    // or arbitrary caller callback runs there. A writer intent between reserve and queue wins.
    internal async Task<(RuntimeDeliveryState State, string Response)> SendRemoteResponseAsync(RuntimeDeliveryCoordinator coordinator,
        RuntimeDeliveryCoordinator.EndpointKey endpoint, string method, RuntimePreparedResponse response)
    {
        var pending = new PendingRemote(coordinator, endpoint, response.Stamp.EventRevision);
        if (!MatchesEndpoint(coordinator, endpoint, response.Stamp)) return (RuntimeDeliveryState.NotSent, "");
        string validated = WithResponseAuthority(method, response.Stamp, response.Response, pending.Reserve);
        return (await pending.QueueAsync().ConfigureAwait(false), validated);
    }

    internal Task<RuntimeDeliveryState> SendRemoteEventAsync(RuntimeDeliveryCoordinator coordinator,
        RuntimeDeliveryCoordinator.EndpointKey endpoint, string name, RuntimeResponseStamp stamp, string json)
    {
        var pending = new PendingRemote(coordinator, endpoint, stamp.EventRevision);
        if (!MatchesEndpoint(coordinator, endpoint, stamp)) return Task.FromResult(RuntimeDeliveryState.NotSent);
        WithEventAuthority(name, stamp, () => pending.Reserve(json));
        return pending.QueueAsync();
    }
    private bool MatchesEndpoint(RuntimeDeliveryCoordinator kernel, RuntimeDeliveryCoordinator.EndpointKey endpoint, RuntimeResponseStamp stamp) =>
        ReferenceEquals(stamp.Owner, this) && services is not null &&
        kernel.MatchesInstance(endpoint, services.Account.AccountKey, services.Account.Epoch, services.Application.BindingKey,
            stamp.InstanceId, stamp.InstanceEpoch);

    private sealed class PendingRemote(RuntimeDeliveryCoordinator kernel, RuntimeDeliveryCoordinator.EndpointKey endpoint, long revision)
    {
        private byte[]? bytes;
        private RuntimeDeliveryCoordinator.DeliveryTicket? ticket;
        internal void Reserve(string json)
        {
            if (Encoding.UTF8.GetByteCount(json) > RuntimeDeliveryWireProtocol.MaximumPayloadBytes) return;
            bytes = Encoding.UTF8.GetBytes(json);
            ticket = kernel.TryReserve(endpoint, new(bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), revision)).Ticket;
        }
        internal Task<RuntimeDeliveryState> QueueAsync()
        {
            if (ticket is null) return Task.FromResult(RuntimeDeliveryState.NotSent);
            try { if (!kernel.QueueValidated(ticket, bytes!)) kernel.CancelBeforeSend(ticket); }
            catch { kernel.CancelBeforeSend(ticket); throw; }
            return kernel.WaitOutcomeAsync(ticket);
        }
    }
}
