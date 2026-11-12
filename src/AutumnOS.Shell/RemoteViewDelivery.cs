using AutumnOS.NativeHost.Windows;
using AutumnOS.Runtime;

namespace AutumnOS.Shell;

// Registration is tied to the original OS group. Only an actual matching ALD1 receipt may
// finish a sending ticket. EOF, timeout and peer death never synthesize a post or fence ACK.
internal sealed class RemoteViewDelivery : IOrderedRuntimeDeliveryDriver, IOriginalRuntimeEndpointLifetime
{
    private readonly NativeProcessOwner owner;
    private readonly MemoryStream outbound = new(), inbound = new();
    private readonly RuntimeDeliveryWireWriter writer;
    private readonly RuntimeDeliveryWireReader reader;
    private readonly TaskCompletionSource<RuntimeDeliveryCoordinator.HostRegistration> attached =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public RuntimeEndpointLifetimeRegistration OriginalLifetime { get; }
    internal RuntimeDeliveryCoordinator.HostRegistration Registration { get; }

    internal RemoteViewDelivery(NativeProcessOwner owner, RuntimeDeliveryBinding binding, RuntimeAuthorityMutationCoordinator authority)
    {
        this.owner = owner;
        OriginalLifetime = RuntimeEndpointLifetimeRegistration.BindForHost(binding, owner.OriginalGroupRetired);
        writer = new(outbound, owner.Nonce); reader = new(inbound, owner.Nonce);
        Registration = authority.RegisterHostEndpoint(binding, this, this);
        attached.SetResult(Registration);
    }

    public ValueTask WriteDeliveryAsync(RuntimeDeliveryWrite delivery) =>
        ExchangeAsync(delivery.Binding, delivery, null);
    public ValueTask WriteFenceAsync(RuntimeFenceWrite fence) => ExchangeAsync(fence.Binding, null, fence);

    private async ValueTask ExchangeAsync(RuntimeDeliveryBinding binding, RuntimeDeliveryWrite? delivery, RuntimeFenceWrite? fence)
    {
        var evidence = (await attached.Task.ConfigureAwait(false)).Evidence;
        try
        {
            owner.RequireOriginalPeer();
            outbound.SetLength(0);
            if (delivery is not null) await writer.WriteDeliveryAsync(delivery).ConfigureAwait(false);
            else await writer.WriteFenceAsync(fence!).ConfigureAwait(false);
            byte[] frame = outbound.ToArray();
            NativeViewPacket packet;
            try { packet = await owner.ExchangeAsync(NativeViewKind.Delivery, frame, CancellationToken.None).ConfigureAwait(false); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(frame); }
            if (packet.Kind != NativeViewKind.Receipt) throw new InvalidDataException("NATIVE_DELIVERY_ACK_MISSING");
            inbound.SetLength(0); inbound.Write(packet.Payload); inbound.Position = 0;
            RuntimeDeliveryWireMessage ack = await reader.ReadAsync(binding).ConfigureAwait(false);
            owner.RequireOriginalPeer();
            if (inbound.Position != inbound.Length || !(ack switch {
                RuntimeDeliveryWirePostReceipt post when delivery?.DeliveryId == post.Receipt.DeliveryId => evidence.AcceptNativePost(post.Receipt),
                RuntimeDeliveryWireFenceReceipt receipt when fence?.FenceId == receipt.Receipt.FenceId => evidence.AcceptNativeFence(receipt.Receipt),
                _ => false }))
                throw new InvalidDataException("NATIVE_DELIVERY_ACK_INVALID");
        }
        catch { evidence.MarkTransportUnknown(); await owner.RequestCloseAsync().ConfigureAwait(false); throw; }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(outbound.GetBuffer().AsSpan(0, (int)outbound.Length));
            inbound.SetLength(0); outbound.SetLength(0);
        }
    }
}
