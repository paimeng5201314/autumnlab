namespace AutumnOS.Runtime;

// Implemented only by the trusted original OS owner. A decoded PID, nonce or
// wire field cannot implement the kernel-handle checks behind this interface.
internal interface IOriginalRuntimeDeliveryPeer : IOriginalRuntimeEndpointLifetime
{
    RuntimeDeliveryBinding InitialBinding { get; }
    Stream DuplexStream { get; }
    void RequireOriginalPeer();
}

/// <summary>
/// No outbound queue and no retirement inference. The coordinator owns all
/// payload slots; the original OS owner owns the stream and handle group.
/// Completing a write proves only that the writer no longer uses its memory.
/// </summary>
internal sealed class RuntimeDeliveryParentTransport : IOrderedRuntimeDeliveryDriver, IOriginalRuntimeEndpointLifetime
{
    private readonly object sync = new();
    private readonly IOriginalRuntimeDeliveryPeer peer;
    private readonly RuntimeDeliveryWireReader reader;
    private readonly RuntimeDeliveryWireWriter writer;
    private RuntimeDeliveryBinding expectedBinding;
    private RuntimeDeliveryCoordinator.HostEvidencePort? evidence;
    private Task? readTask;
    private bool unavailable;

    internal RuntimeDeliveryParentTransport(IOriginalRuntimeDeliveryPeer peer, string nonce)
    {
        ArgumentNullException.ThrowIfNull(peer);
        RuntimeDeliveryWireProtocol.ValidateNonce(nonce);
        RuntimeDeliveryBinding initialBinding = peer.InitialBinding;
        RuntimeDeliveryWireProtocol.ValidateBinding(initialBinding);
        peer.RequireOriginalPeer();
        OriginalLifetime = peer.OriginalLifetime;
        if (OriginalLifetime.OriginalBinding != initialBinding)
            throw new ArgumentException("TRANSPORT_ORIGINAL_BINDING_MISMATCH", nameof(peer));
        Stream stream = peer.DuplexStream;
        if (!stream.CanRead || !stream.CanWrite) throw new ArgumentException("TRANSPORT_DUPLEX_REQUIRED", nameof(peer));
        this.peer = peer; expectedBinding = initialBinding;
        reader = new(stream, nonce); writer = new(stream, nonce);
    }

    public RuntimeEndpointLifetimeRegistration OriginalLifetime { get; }
    internal bool IsUnavailable { get { lock (sync) return unavailable; } }
    internal Task WhenReaderStopped { get { lock (sync) return readTask ?? throw new InvalidOperationException("TRANSPORT_NOT_BOUND"); } }

    // Register first, then bind once, then allow reservations. The evidence
    // port stays here, never in an application or an IPC deserializer callback.
    internal void BindEvidenceAndStart(RuntimeDeliveryCoordinator.HostEvidencePort port)
    {
        ArgumentNullException.ThrowIfNull(port);
        lock (sync)
        {
            if (evidence is not null) throw new InvalidOperationException("TRANSPORT_ALREADY_BOUND");
            evidence = port;
            // Yield before reading: no peer/stream/native call under this lock.
            readTask = ReadOwnedAsync(port);
        }
    }

    public async ValueTask WriteDeliveryAsync(RuntimeDeliveryWrite delivery)
    {
        RequireWritable();
        try
        {
            peer.RequireOriginalPeer();
            await writer.WriteDeliveryAsync(delivery).ConfigureAwait(false);
        }
        catch { MarkUnknown(); throw; }
    }

    public async ValueTask WriteFenceAsync(RuntimeFenceWrite fence)
    {
        RequireWritable();
        try
        {
            peer.RequireOriginalPeer();
            await writer.WriteFenceAsync(fence).ConfigureAwait(false);
        }
        catch { MarkUnknown(); throw; }
    }

    private void RequireWritable()
    {
        lock (sync)
        {
            if (evidence is null) throw new InvalidOperationException("TRANSPORT_NOT_BOUND");
            if (unavailable) throw new IOException("TRANSPORT_UNAVAILABLE");
        }
    }

    private async Task ReadOwnedAsync(RuntimeDeliveryCoordinator.HostEvidencePort port)
    {
        await Task.Yield();
        try
        {
            while (true)
            {
                peer.RequireOriginalPeer();
                RuntimeDeliveryWireMessage message = await reader.ReadAsync(expectedBinding).ConfigureAwait(false);
                // Verify again after a blocked read, against the same original
                // handles. A new process reusing the numeric PID is irrelevant.
                peer.RequireOriginalPeer();
                bool accepted;
                switch (message)
                {
                    case RuntimeDeliveryWirePostReceipt post:
                        accepted = port.AcceptNativePost(post.Receipt);
                        break;
                    case RuntimeDeliveryWireFenceReceipt fence:
                        accepted = port.AcceptNativeFence(fence.Receipt);
                        if (accepted) expectedBinding = expectedBinding with { ChannelGeneration = fence.Receipt.NextChannelGeneration };
                        break;
                    default:
                        throw new InvalidDataException("TRANSPORT_RECEIPT_DIRECTION_REQUIRED");
                }
                if (!accepted) throw new InvalidDataException("TRANSPORT_UNEXPECTED_RECEIPT");
            }
        }
        catch { MarkUnknown(); throw; }
    }

    private void MarkUnknown()
    {
        RuntimeDeliveryCoordinator.HostEvidencePort? port;
        lock (sync) { unavailable = true; port = evidence; }
        port?.MarkTransportUnknown();
        // Do not close/dispose another owner's stream or synthesize retirement.
        // A separately live reader can still deliver an exact late post receipt.
    }
}
