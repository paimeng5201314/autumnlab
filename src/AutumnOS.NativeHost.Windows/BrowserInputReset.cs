using System.Text.Json;

namespace AutumnOS.NativeHost.Windows;

internal sealed partial class BrowserWindow
{
    private readonly NativeKeyboardState keyboard = new();
    private int lastPointerX, lastPointerY;

    // Ordered with all prior input. A reset has no text payload and only
    // releases inputs actually held by this private controller/document.
    private async Task<NativeViewPacket> ResetInputAsync(NativeViewPacket request)
    {
        RequireDocument(true);
        if (request.Payload.Length != 0) throw new InvalidDataException("BROWSER_RESET_PAYLOAD");
        if (compositionActive)
        {
            if (!await HasCompositionTargetAsync()) throw new InvalidDataException("BROWSER_RESET_ORIGINAL_IME_NODE_LOST");
            await ComposeAsync("");
            if (!await HasCompositionTargetAsync()) throw new InvalidDataException("BROWSER_RESET_IME_UNCERTAIN");
            compositionActive = false; compositionNode = 0; compositionRoot = 0;
        }
        foreach (string code in keyboard.HeldKeys)
        {
            RequireDocument(true);
            await ForwardInputAsync(NativeViewKind.Key, JsonSerializer.SerializeToUtf8Bytes(new { action = "up", key = code }));
        }
        keyboard.Reset();
        if (buttonDown)
        {
            RequireDocument(true);
            await CdpAsync("Input.dispatchMouseEvent", new { type = "mouseReleased", x = lastPointerX, y = lastPointerY,
                button = "left", buttons = 0, clickCount = 0 });
            buttonDown = false;
        }
        RequireDocument(true);
        return new(request.Sequence, NativeViewKind.Accepted, []);
    }
}
