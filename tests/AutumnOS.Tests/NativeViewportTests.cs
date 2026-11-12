using System.Buffers.Binary;
using System.Text.Json;
using AutumnOS.NativeHost.Windows;

namespace AutumnOS.Tests;

internal static class NativeViewportTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("native.viewport.resize_keeps_pixel_edge_budget_and_does_not_enlarge", Budget);
        yield return ("native.viewport.wide_portrait_and_4k_aspects_follow_surface", Aspect);
        yield return ("native.viewport.rejects_invalid_or_overbudget_extents", Bounds);
        yield return ("native.viewport.wire_requires_exact_little_endian_extent", Wire);
        yield return ("native.viewport.maps_visible_frame_and_rejects_letterbox", Mapping);
        yield return ("native.viewport.release_clamp_preserves_finite_boundary", Clamp);
        yield return ("native.viewport.rejects_nonfinite_or_unlaidout_surfaces", InvalidSurface);
        yield return ("native.pointer.enforces_acknowledged_dynamic_viewport", DynamicPointer);
    }

    private static void Budget()
    {
        foreach (var (w, h) in new[] { (800d, 600d), (400d, 300d), (333.9, 222.9), (1d, 1d),
            (1920d, 1080d), (3840d, 2160d), (689d, 808d), (1080d, 1920d), (8192d, 1024d),
            (double.MaxValue, double.MaxValue), (double.MaxValue, 1d), (1d, double.MaxValue) })
        {
            var v = NativeViewport.FromSurface(w, h);
            Check(v.IsValid && (long)v.Width * v.Height <= 480000 && v.Width <= 2048 && v.Height <= 2048);
            Check(v.Width <= w && v.Height <= h);
        }
        Check(NativeViewport.FromSurface(800, 600) == NativeViewport.Default);
        Check(NativeViewport.FromSurface(400, 300) == new NativeViewport(400, 300));
        Check(NativeViewport.FromSurface(333.9, 222.9) == new NativeViewport(333, 222));
    }

    private static void Aspect()
    {
        foreach (var (w, h) in new[] { (1920d, 1080d), (3840d, 2160d), (689d, 808d), (1080d, 1920d) })
        {
            var v = NativeViewport.FromSurface(w, h);
            Check(Math.Abs((double)v.Width / v.Height - w / h) < .004);
            // Quantization can leave a few physical pixels, never the old fixed-4:3 bands.
            double scale = Math.Min(w / v.Width, h / v.Height);
            Check(w - v.Width * scale < 5 && h - v.Height * scale < 5);
        }
    }

    private static void Bounds()
    {
        foreach (var (w, h) in new[] { (0, 600), (800, 0), (-1, 1), (1, -1), (800, 601),
            (2049, 1), (1, 2049), (2048, 235), (int.MaxValue, int.MaxValue) })
            Reject(() => _ = new NativeViewport(w, h));
        Check(new NativeViewport(2048, 234).IsValid);
        Reject(() => default(NativeViewport).Validate());
        Reject(() => default(NativeViewport).Encode());
    }

    private static void Wire()
    {
        byte[] bytes = [0x20, 0x03, 0, 0, 0x58, 0x02, 0, 0];
        Check(NativeViewport.Parse(bytes) == NativeViewport.Default);
        Check(NativeViewport.Default.Encode().SequenceEqual(bytes));
        foreach (int length in new[] { 0, 1, 7, 9, 16 }) Reject(() => NativeViewport.Parse(new byte[length]));
        foreach (var v in new[] { new NativeViewport(1200, 400), new NativeViewport(320, 1000), new NativeViewport(1, 1) })
            Check(NativeViewport.Parse(v.Encode()) == v);
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 0); Reject(() => NativeViewport.Parse(bytes));
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 801); Reject(() => NativeViewport.Parse(bytes));
        BinaryPrimitives.WriteInt32BigEndian(bytes, 800); Reject(() => NativeViewport.Parse(bytes));
    }

    private static void Mapping()
    {
        var v = NativeViewport.Default;
        Check(v.TryMap(1600, 900, 200, 0, out int x, out int y) && x == 0 && y == 0);
        Check(v.TryMap(1600, 900, 800, 450, out x, out y) && x == 400 && y == 300);
        Check(v.TryMap(1600, 900, 1399.9, 899.9, out x, out y) && x == 799 && y == 599);
        foreach (var (px, py) in new[] { (199.9, 0d), (1400d, 0d), (200d, -1d), (200d, 900d) })
            Check(!v.TryMap(1600, 900, px, py, out _, out _));
        var portrait = new NativeViewport(320, 1000);
        Check(portrait.TryMap(640, 2000, 639.9, 1999.9, out x, out y) && x == 319 && y == 999);
    }

    private static void Clamp()
    {
        var v = NativeViewport.Default;
        Check(v.TryMap(1600, 900, -100, -100, out int x, out int y, clamp: true) && x == 0 && y == 0);
        Check(v.TryMap(1600, 900, 2000, 1000, out x, out y, clamp: true) && x == 799 && y == 599);
        Check(!v.TryMap(1600, 900, double.NaN, 0, out _, out _, clamp: true));
        Check(!v.TryMap(1600, 900, 0, double.PositiveInfinity, out _, out _, clamp: true));
    }

    private static void InvalidSurface()
    {
        foreach (double invalid in new[] { 0, -.1, .5, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            RejectArgument(() => NativeViewport.FromSurface(invalid, 600));
            RejectArgument(() => NativeViewport.FromSurface(800, invalid));
            Check(!NativeViewport.Default.TryMap(invalid, 600, 0, 0, out _, out _));
            Check(!NativeViewport.Default.TryMap(800, invalid, 0, 0, out _, out _));
        }
        Check(!default(NativeViewport).TryMap(800, 600, 0, 0, out _, out _));
    }

    private static void DynamicPointer()
    {
        static byte[] Move(int x, int y) => JsonSerializer.SerializeToUtf8Bytes(new { action = "move", x, y });
        var wide = new NativeViewport(1200, 400);
        var portrait = new NativeViewport(320, 1000);
        Check(NativePointerInput.Parse(Move(1199, 399), false, wide).X == 1199);
        Check(NativePointerInput.Parse(Move(319, 999), false, portrait).Y == 999);
        Reject(() => NativePointerInput.Parse(Move(1200, 399), false, wide));
        Reject(() => NativePointerInput.Parse(Move(319, 1000), false, portrait));
        Reject(() => NativePointerInput.Parse(Move(799, 599), false, portrait));
        Reject(() => NativePointerInput.Parse(Move(1199, 399), false));
        Reject(() => NativePointerInput.Parse(Move(0, 0), false, default));
    }

    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Invalid viewport was admitted."); }
    private static void RejectArgument(Action action)
    { try { action(); } catch (ArgumentOutOfRangeException) { return; } throw new InvalidOperationException("Invalid surface was admitted."); }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Viewport boundary mismatch."); }
}
