using System.Text;
using System.Text.Json;

namespace AutumnOS.NativeHost.Windows;

internal sealed partial class BrowserWindow
{
    // Commands contain only the finite UI operation and its data. Neither a
    // script nor a DevTools method name/parameter object is accepted from IPC.
    private async Task<bool> ForwardInputAsync(NativeViewKind kind, byte[] payload)
    {
        switch (kind)
        {
            case NativeViewKind.Pointer:
                {
                    var command = NativePointerInput.Parse(payload, buttonDown, viewport);
                    int x = command.X, y = command.Y;
                    string type = command.Action switch
                    {
                        "down" => "mousePressed", "up" => "mouseReleased", "move" => "mouseMoved", "wheel" => "mouseWheel",
                        _ => throw new InvalidDataException("BROWSER_POINTER_SEQUENCE")
                    };
                    if (type == "mousePressed") controller!.MoveFocus(Microsoft.Web.WebView2.Core.CoreWebView2MoveFocusReason.Programmatic);
                    buttonDown = command.NextButtonDown;
                    if (type == "mouseWheel")
                        await CdpAsync("Input.dispatchMouseEvent", new { type, x, y, button = "none", buttons = buttonDown ? 1 : 0,
                            clickCount = 0, deltaX = command.DeltaX, deltaY = command.DeltaY, modifiers = 0 });
                    else
                        await CdpAsync("Input.dispatchMouseEvent", new { type, x, y, button = type == "mouseMoved" ? "none" : "left",
                            buttons = buttonDown ? 1 : 0, clickCount = type == "mouseMoved" ? 0 : 1 });
                    lastPointerX = x; lastPointerY = y;
                    return true;
                }
            case NativeViewKind.Key:
                {
                    var command = NativeKeyboardInput.Parse(payload);
                    keyboard.Apply(command);
                    int vk = command.VirtualKey;
                    if (command.Action == "char")
                        await CdpAsync("Input.dispatchKeyEvent", new { type = "char", code = command.Key, key = command.Text,
                            text = command.Text, unmodifiedText = command.Text,
                            windowsVirtualKeyCode = vk, nativeVirtualKeyCode = vk, modifiers = 0 });
                    else
                        await CdpAsync("Input.dispatchKeyEvent", new { type = command.Action == "down" ? "rawKeyDown" : "keyUp",
                            code = command.Key, key = command.KeyValue, windowsVirtualKeyCode = vk, nativeVirtualKeyCode = vk, modifiers = 0 });
                    return true;
                }
            case NativeViewKind.Composition:
                using (var json = ParseObject(payload, "action", "text"))
                {
                    var value = json.RootElement;
                    string text = value.GetProperty("text").GetString()!;
                    if (text.Length > 256 || text.Any(char.IsControl) || Encoding.UTF8.GetByteCount(text) > 1024)
                        throw new InvalidDataException("BROWSER_COMPOSITION_LIMIT");
                    string? action = value.GetProperty("action").GetString();
                    if (action is "update" or "commit" or "cancel")
                    {
                        if (!compositionActive || !await HasCompositionTargetAsync())
                        {
                            compositionActive = false;
                            return false;
                        }
                    }
                    switch (action)
                    {
                        case "start" when !compositionActive:
                            if (!await BindCompositionTargetAsync()) return false;
                            compositionActive = true;
                            if (text.Length != 0) await ComposeAsync(text);
                            break;
                        case "update" when compositionActive && text.Length != 0:
                            await ComposeAsync(text); break;
                        case "commit" when compositionActive && text.Length != 0:
                            await CdpAsync("Input.insertText", new { text }); compositionActive = false; break;
                        case "cancel" when compositionActive && text.Length == 0:
                            await ComposeAsync(""); compositionActive = false; break;
                        default:
                            throw new InvalidDataException("BROWSER_COMPOSITION_SEQUENCE");
                    }
                    // Focus checks and input are separate browser calls. If
                    // the target changed during dispatch, its outcome is
                    // uncertain; never manufacture a before-input rejection.
                    if (!await HasCompositionTargetAsync()) throw new InvalidDataException("BROWSER_IME_DISPATCH_UNCERTAIN");
                    return true;
                }
        }
        throw new InvalidDataException("BROWSER_INPUT_UNSUPPORTED");
    }

    private Task ComposeAsync(string text) => CdpAsync("Input.imeSetComposition",
        new { text, selectionStart = text.Length, selectionEnd = text.Length });
    private int compositionRoot, compositionNode;
    private const string EditableFocusSelector = "textarea:focus:read-write,input:not([type]):focus:read-write," +
        "input[type=text i]:focus:read-write,input[type=search i]:focus:read-write,[contenteditable]:focus:read-write";

    private async Task<bool> BindCompositionTargetAsync()
    {
        RequireDocument(true);
        string response = await core!.CallDevToolsProtocolMethodAsync("DOM.getDocument", "{\"depth\":0,\"pierce\":false}");
        RequireDocument(true);
        using var result = ParseDomResult(response);
        var root = result.RootElement.GetProperty("root");
        if (root.GetProperty("nodeType").GetInt32() != 9 || root.GetProperty("documentURL").GetString() != challenge!.Isolation.PageUri)
            throw new InvalidDataException("BROWSER_COMPOSITION_DOCUMENT_LOST");
        compositionRoot = root.GetProperty("nodeId").GetInt32();
        compositionNode = await FocusedEditableNodeAsync();
        return compositionNode > 0;
    }

    // Numeric DOM IDs come from the native inspector on the original top-level
    // document. Never evaluate page functions or read text/clipboard contents.
    private async Task<int> FocusedEditableNodeAsync()
    {
        RequireDocument(true);
        string response = await core!.CallDevToolsProtocolMethodAsync("DOM.querySelector",
            JsonSerializer.Serialize(new { nodeId = compositionRoot, selector = EditableFocusSelector }));
        RequireDocument(true);
        using var result = ParseDomResult(response);
        return result.RootElement.GetProperty("nodeId").GetInt32();
    }
    private async Task<bool> HasCompositionTargetAsync() =>
        compositionNode > 0 && await FocusedEditableNodeAsync() == compositionNode;
    private static JsonDocument ParseDomResult(string json)
    {
        if (json.Length > 16 * 1024) throw new InvalidDataException("BROWSER_DOM_METADATA_LIMIT");
        return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
    }
    private async Task CdpAsync(string method, object parameters)
    {
        RequireDocument(true);
        await core!.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(parameters));
    }
}
