using AutumnOS.Runtime;

namespace AutumnOS.NativeHost.Windows;

// Each outer request contains exactly one inner ordered delivery/fence frame.
// Keep the inner sequence across requests, independent from frame/input traffic.
internal sealed class BrowserDeliveryChannel : IDisposable
{
    private readonly MemoryStream input = new(), output = new();
    private readonly RuntimeDeliveryWireReader reader;
    private readonly RuntimeDeliveryWireWriter writer;
    private readonly RuntimeNativeDeliveryActor actor;
    private int active, unavailable;
    internal BrowserDeliveryChannel(RuntimeDeliveryBinding binding, IRuntimeNativeDeliveryDispatcher dispatcher, string nonce)
    {
        reader = new(input, nonce); writer = new(output, nonce);
        actor = new(binding, dispatcher);
    }
    internal async Task<byte[]> DispatchAsync(byte[] bytes, bool packageAdmitted, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0) throw new InvalidOperationException("BROWSER_OVERLAPPING_DELIVERY");
        try
        {
            if (Volatile.Read(ref unavailable) != 0 || bytes.Length > 32768 + 4096 + 24)
                throw new InvalidDataException("BROWSER_DELIVERY_UNAVAILABLE");
            input.SetLength(0); input.Write(bytes); input.Position = 0;
            var message = await reader.ReadAsync(actor.CurrentBinding, token);
            if (input.Position != input.Length) throw new InvalidDataException("BROWSER_DELIVERY_TRAILING_BYTES");
            Task<RuntimeNativeDeliveryOutcome> outcome;
            switch (message)
            {
                case RuntimeDeliveryWireDelivery d when packageAdmitted:
                    actor.TrySubmitDelivery(d.Delivery, out outcome); break;
                case RuntimeDeliveryWireFence f:
                    actor.TrySubmitFence(f.Fence, out outcome); break;
                default: throw new InvalidDataException("BROWSER_DELIVERY_DIRECTION_OR_ADMISSION");
            }
            var result = await outcome;
            output.SetLength(0);
            if (result.PostReceipt is { } post) await writer.WritePostReceiptAsync(post);
            else if (result.FenceReceipt is { } fence) await writer.WriteFenceReceiptAsync(fence);
            else throw new InvalidDataException("BROWSER_DELIVERY_UNCONFIRMED");
            return output.ToArray();
        }
        catch { Volatile.Write(ref unavailable, 1); throw; }
        finally { Volatile.Write(ref active, 0); }
    }

    internal Task CloseAsync() { Volatile.Write(ref unavailable, 1); return actor.CloseAsync(); }
    public void Dispose() { input.Dispose(); output.Dispose(); }
}
