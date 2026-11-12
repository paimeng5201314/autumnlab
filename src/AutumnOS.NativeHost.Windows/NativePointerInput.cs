using System.Text.Json;

namespace AutumnOS.NativeHost.Windows;

public readonly record struct NativePointerCommand(string Action, int X, int Y, int DeltaX, int DeltaY, bool NextButtonDown);

// One finite native-pointer command, never a CDP method or parameter bag.
public static class NativePointerInput
{
    public const int MaximumWheelDelta = 1200;

    public static (int DeltaX, int DeltaY) NormalizeWheel(int nativeDelta, bool horizontal)
    {
        int delta = Math.Clamp(nativeDelta, -MaximumWheelDelta, MaximumWheelDelta);
        // Windows: positive vertical = away/up, positive horizontal = right.
        // The remote viewport uses positive Y down and positive X right.
        return horizontal ? (delta, 0) : (0, -delta);
    }

    public static NativePointerCommand Parse(byte[] payload, bool buttonDown)
        => Parse(payload, buttonDown, NativeViewport.Default);

    public static NativePointerCommand Parse(byte[] payload, bool buttonDown, NativeViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(payload);
        viewport.Validate();
        if (payload.Length is < 1 or > 256) throw Invalid();
        try
        {
            using var json = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 2 });
            JsonElement value = json.RootElement;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("action", out var actionValue) ||
                actionValue.ValueKind != JsonValueKind.String) throw Invalid();
            string? action = actionValue.GetString();
            var remaining = new HashSet<string>(action == "wheel" ? ["action", "x", "y", "deltaX", "deltaY"] : ["action", "x", "y"], StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) if (!remaining.Remove(property.Name)) throw Invalid();
            if (remaining.Count != 0 || !Integer(value.GetProperty("x"), out int x) || !Integer(value.GetProperty("y"), out int y) ||
                x < 0 || x >= viewport.Width || y < 0 || y >= viewport.Height) throw Invalid();
            int deltaX = 0, deltaY = 0;
            bool next = buttonDown;
            switch (action)
            {
                case "down" when !buttonDown: next = true; break;
                case "up" when buttonDown: next = false; break;
                case "move": break;
                case "wheel":
                    if (!Integer(value.GetProperty("deltaX"), out deltaX) || !Integer(value.GetProperty("deltaY"), out deltaY) ||
                        deltaX is < -MaximumWheelDelta or > MaximumWheelDelta || deltaY is < -MaximumWheelDelta or > MaximumWheelDelta ||
                        (deltaX == 0) == (deltaY == 0)) throw Invalid();
                    break;
                default: throw Invalid();
            }
            return new(action, x, y, deltaX, deltaY, next);
        }
        catch (JsonException) { throw Invalid(); }
    }
    private static bool Integer(JsonElement value, out int number)
    { number = 0; return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number); }
    private static InvalidDataException Invalid() => new("BROWSER_POINTER_COMMAND_INVALID");
}
