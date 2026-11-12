using System.Buffers.Binary;
using System.Text;
using AutumnOS.NativeHost.Windows;
using AutumnOS.Runtime;

namespace AutumnOS.Tests;

internal static class NativeViewBoundaryTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("native.view.rejects_oversize_before_allocating_or_reading_payload", () => RejectSizes().GetAwaiter().GetResult());
        yield return ("native.view.host_bootstrap_reconstructs_exact_same_document_from_parent_nonce", Recreate);
        yield return ("native.view.truncated_frames_do_not_complete_or_consume_another_packet", () => Truncated().GetAwaiter().GetResult());
        yield return ("native.view.resize_requires_exact_eight_bytes_before_reading_payload", () => ResizeSizes().GetAwaiter().GetResult());
        yield return ("native.view.resize_roundtrip_and_truncation_preserve_frame_boundary", () => ResizeRoundtrip().GetAwaiter().GetResult());
    }
    private static async Task RejectSizes()
    {
        foreach (var item in new[] { (NativeViewKind.Frame, 1048577), (NativeViewKind.Poll, 1),
            (NativeViewKind.Receipt, int.MaxValue), ((NativeViewKind)127, 0), (NativeViewKind.Pointer, -1) })
        {
            byte[] header = new byte[20]; "AVP1"u8.CopyTo(header); header[4] = (byte)item.Item1;
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), 1);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), item.Item2);
            try { _ = await NativeViewProtocol.ReadAsync(new MemoryStream(header), default); }
            catch (InvalidDataException) { continue; }
            throw new Exception("Packet size was accepted.");
        }
    }
    private static void Recreate()
    {
        var id = Guid.NewGuid(); string origin = "https://" + Guid.NewGuid().ToString("N") + ".autumn.invalid/";
        var parent = RuntimeIsolationPolicy.CreateParserGateChallenge(id, origin, "a/index.html");
        var child = RuntimeIsolationPolicy.CreateParserGateChallenge(id, origin, "a/index.html", parent.Isolation.Nonce);
        if (parent.BootstrapHtml != child.BootstrapHtml || parent.BootstrapScript != child.BootstrapScript ||
            parent.BindingResultExpression != child.BindingResultExpression || child.Isolation.PageUri != origin + "a/index.html")
            throw new Exception("Two ends constructed different documents.");
        foreach (string bad in new[] { "", new string('a',63), new string('z',64), new string('A',64) })
        {
            try { _ = RuntimeIsolationPolicy.CreateParserGateChallenge(id, origin, "a/index.html", bad); }
            catch (ArgumentException) { continue; }
            throw new Exception("Invalid parent nonce.");
        }
    }
    private static async Task Truncated()
    {
        using var wire = new MemoryStream();
        await NativeViewProtocol.WriteAsync(wire, new(1, NativeViewKind.SdkRequest, Encoding.UTF8.GetBytes("{}")), default);
        byte[] data = wire.ToArray();
        for (int i = 0; i < data.Length; i++)
        {
            try { _ = await NativeViewProtocol.ReadAsync(new MemoryStream(data, 0, i, false), default); }
            catch (EndOfStreamException) { continue; }
            throw new Exception("Truncated packet accepted.");
        }
    }

    private static async Task ResizeSizes()
    {
        foreach (int length in new[] { -1, 0, 1, 2, 3, 4, 5, 6, 7, 9, 4096, int.MaxValue })
        {
            byte[] header = new byte[20]; "AVP1"u8.CopyTo(header); header[4] = (byte)NativeViewKind.Resize;
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), 1);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), length);
            // No payload exists: EndOfStreamException would reveal that the malformed
            // length was admitted and a payload read was attempted.
            try { _ = await NativeViewProtocol.ReadAsync(new MemoryStream(header), default); }
            catch (InvalidDataException) { continue; }
            throw new Exception("Resize payload length was accepted.");
        }
        using var output = new MemoryStream();
        foreach (int length in new[] { 0, 1, 7, 9 })
        {
            try { await NativeViewProtocol.WriteAsync(output, new(1, NativeViewKind.Resize, new byte[length]), default); }
            catch (InvalidDataException) { if (output.Length != 0) throw new Exception("Malformed resize partially written."); continue; }
            throw new Exception("Malformed resize was written.");
        }
    }

    private static async Task ResizeRoundtrip()
    {
        using var output = new MemoryStream();
        var viewport = new NativeViewport(1200, 400);
        await NativeViewProtocol.WriteAsync(output, new(3, NativeViewKind.Resize, viewport.Encode()), default);
        byte[] bytes = output.ToArray();
        if (bytes.Length != 28 || bytes[4] != 10) throw new Exception("Resize changed the wire format.");
        output.Position = 0;
        var packet = await NativeViewProtocol.ReadAsync(output, default);
        if (packet.Sequence != 3 || packet.Kind != NativeViewKind.Resize || NativeViewport.Parse(packet.Payload) != viewport ||
            output.Position != output.Length) throw new Exception("Resize frame did not roundtrip.");
        for (int length = 20; length < bytes.Length; length++)
        {
            try { _ = await NativeViewProtocol.ReadAsync(new MemoryStream(bytes, 0, length, false), default); }
            catch (EndOfStreamException) { continue; }
            throw new Exception("Truncated resize frame completed.");
        }
    }
}
