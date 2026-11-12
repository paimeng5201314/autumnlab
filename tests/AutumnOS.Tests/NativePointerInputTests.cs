using System.Text;
using System.Text.Json;
using AutumnOS.NativeHost.Windows;

namespace AutumnOS.Tests;

internal static class NativePointerInputTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("native.pointer.wheel_directions_and_device_extremes_are_bounded", Normalize);
        yield return ("native.pointer.wheel_preserves_button_sequence", Sequence);
        yield return ("native.pointer.wheel_requires_exact_single_axis_schema", Schema);
        yield return ("native.pointer.wheel_rejects_outside_viewport_and_delta_limits", Bounds);
    }
    private static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value);
    private static NativePointerCommand Wheel(int x, int y, int dx, int dy, bool down = false) =>
        NativePointerInput.Parse(Json(new { action = "wheel", x, y, deltaX = dx, deltaY = dy }), down);
    private static void Normalize()
    {
        Check(NativePointerInput.NormalizeWheel(120, false) == (0, -120));
        Check(NativePointerInput.NormalizeWheel(-120, false) == (0, 120));
        Check(NativePointerInput.NormalizeWheel(120, true) == (120, 0));
        Check(NativePointerInput.NormalizeWheel(-120, true) == (-120, 0));
        Check(NativePointerInput.NormalizeWheel(int.MinValue, false) == (0, 1200));
        Check(NativePointerInput.NormalizeWheel(int.MaxValue, true) == (1200, 0));
        Check(NativePointerInput.NormalizeWheel(1, false) == (0, -1));
    }
    private static void Sequence()
    {
        var down = NativePointerInput.Parse(Json(new { action = "down", x = 1, y = 1 }), false);
        Check(down.NextButtonDown);
        var wheel = Wheel(1, 1, 0, 120, down.NextButtonDown);
        Check(wheel.NextButtonDown && wheel.Action == "wheel" && wheel.DeltaY == 120);
        var up = NativePointerInput.Parse(Json(new { action = "up", x = 1, y = 1 }), wheel.NextButtonDown);
        Check(!up.NextButtonDown && !Wheel(1, 1, -1, 0).NextButtonDown);
        Reject(() => NativePointerInput.Parse(Json(new { action = "up", x = 1, y = 1 }), false));
        Reject(() => NativePointerInput.Parse(Json(new { action = "down", x = 1, y = 1 }), true));
    }
    private static void Schema()
    {
        foreach (string payload in new[] {
            "{\"action\":\"wheel\",\"x\":1,\"y\":1,\"deltaY\":1}",
            "{\"action\":\"wheel\",\"x\":1,\"y\":1,\"deltaX\":0,\"deltaY\":1,\"deltaY\":2}",
            "{\"action\":\"wheel\",\"x\":1,\"y\":1,\"deltaX\":0,\"deltaY\":1,\"method\":\"Runtime.evaluate\"}",
            "{\"action\":\"wheel\",\"x\":1,\"y\":1,\"deltaX\":0,\"deltaY\":1.5}",
            "{\"action\":\"wheel\",\"x\":1,\"y\":1,\"deltaX\":0,\"deltaY\":\"1\"}",
            "{\"action\":\"down\",\"x\":1,\"y\":1,\"deltaX\":0,\"deltaY\":1}",
            "{\"action\":\"unknown\",\"x\":1,\"y\":1}", "null", new string(' ', 257) })
            Reject(() => NativePointerInput.Parse(Encoding.UTF8.GetBytes(payload), false));
        Reject(() => Wheel(1, 1, 0, 0)); Reject(() => Wheel(1, 1, 1, 1));
    }
    private static void Bounds()
    {
        Check(Wheel(0, 0, 0, -1200).DeltaY == -1200);
        Check(Wheel(799, 599, 1200, 0).DeltaX == 1200);
        foreach (var p in new[] { (-1, 0), (800, 0), (0, -1), (0, 600) }) Reject(() => Wheel(p.Item1, p.Item2, 0, 1));
        foreach (int value in new[] { -1201, 1201, int.MinValue, int.MaxValue })
        { Reject(() => Wheel(1, 1, value, 0)); Reject(() => Wheel(1, 1, 0, value)); }
    }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Invalid pointer input was admitted."); }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Pointer boundary mismatch."); }
}
