using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using AutumnOS.SessionBrowserProtocol;
using Microsoft.Web.WebView2.Core;
using FrozenPolicy = AutumnOS.ClipboardAppContainerProbe.FrozenPolicy;

namespace AutumnOS.OwnedBrowserParent;

internal static class BrowserHost
{
    internal static int Run(string nonce, string runtime, string udf, string expectedStation, string expectedDesktop,
        uint hostPid, ulong hostCreation, ulong inheritedWaitHandle)
    {
        using var scope = new BrowserNativeScope(expectedStation, expectedDesktop, hostPid, hostCreation, inheritedWaitHandle);
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        using var form = new BrowserWindow(nonce, runtime, udf, scope);
        // This HWND belongs to the authenticated noninteractive station. It
        // is never reparented to WinSta0; only bounded PNG bytes reach WinUI.
        Application.Run(form);
        return form.ExitCode;
    }

    private sealed class BrowserWindow : Form
    {
        private readonly string nonce, runtime, udf, origin, pageUri;
        private readonly BrowserNativeScope scope;
        private readonly Stream input = Console.OpenStandardInput(), output = Console.OpenStandardOutput();
        private readonly List<Stream> resources = [];
        private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly TaskCompletionSource<bool> pageReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim outputGate = new(1, 1);
        private CoreWebView2Environment? environment;
        private CoreWebView2Controller? controller;
        private CoreWebView2? core;
        private uint browserPid;
        private bool initializationReturned, initialized, navigationRequested, pageSucceeded, closing, disposedController;
        private bool failed, finished, parentClose, normalBrowserExit, compositionActive;
        private int processCount = -1, modifiers, mouseButtons, navigationStartCount;
        private long lastRequest, frameId, closeAt, lastInventory = -1000;
        private string stage = "browser_start", errorStage = "none";
        private string? firstFrameHash;
        private bool differentFrames, sentinelObserved;
        private JsonElement? evidence;
        private CompositionEffectObservation? compositionBaseline;
        internal int ExitCode { get; private set; } = 1;

        internal BrowserWindow(string nonce, string runtime, string udf, BrowserNativeScope scope)
        {
            this.nonce = nonce; this.scope = scope;
            if (!Path.IsPathFullyQualified(runtime) || !Path.IsPathFullyQualified(udf) ||
                !File.Exists(Path.Combine(runtime, "msedgewebview2.exe")) || !Directory.Exists(udf))
                throw new InvalidDataException("FROZEN_RUNTIME_AND_OWNED_UDF_REQUIRED");
            this.runtime = Path.GetFullPath(runtime); this.udf = Path.GetFullPath(udf);
            foreach (string key in Environment.GetEnvironmentVariables().Keys)
                if (key.StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("WEBVIEW2_OVERRIDE_REJECTED");
            origin = "https://owned-session-" + nonce + ".autumn.invalid"; pageUri = origin + "/index.html";
            AutoScaleMode = AutoScaleMode.None; ClientSize = new Size(800, 600);
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual; Location = Point.Empty;
            ShowInTaskbar = false; Text = "AutumnOS private owned-page producer";
            Shown += (_, _) => { timer.Start(); _ = InitializeAsync(); _ = Task.Run(ReadCommandsAsync); };
            timer.Tick += (_, _) => Tick();
            FormClosing += (_, args) => { if (!finished) { args.Cancel = true; RequestClose("private_window_close", true); } };
        }

        private async Task InitializeAsync()
        {
            try
            {
                scope.RequireBinding(); stage = "create_environment";
                var options = new CoreWebView2EnvironmentOptions(FrozenPolicy.BrowserArguments)
                { ExclusiveUserDataFolderAccess = true, IsCustomCrashReportingEnabled = true };
                environment = await CoreWebView2Environment.CreateAsync(runtime, udf, options);
                if (finished) return;
                environment.BrowserProcessExited += (_, args) =>
                {
                    if (args.BrowserProcessId == browserPid) normalBrowserExit = args.BrowserProcessExitKind == CoreWebView2BrowserProcessExitKind.Normal;
                };
                environment.ProcessInfosChanged += (_, _) => Inventory();
                Inventory();
                if (environment.BrowserVersionString != FrozenPolicy.RequiredBrowserVersion ||
                    !Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.UserDataFolder)).Equals(Path.TrimEndingDirectorySeparator(udf), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("FROZEN_RUNTIME_EXACT_UDF_REQUIRED");
                if (closing) return;
                stage = "create_controller";
                var optionsController = environment.CreateCoreWebView2ControllerOptions(); optionsController.IsInPrivateModeEnabled = true;
                controller = await environment.CreateCoreWebView2ControllerAsync(Handle, optionsController);
                if (finished) { controller.Close(); return; }
                scope.RequireBinding();
                controller.ShouldDetectMonitorScaleChanges = false;
                controller.RasterizationScale = 1;
                controller.BoundsMode = CoreWebView2BoundsMode.UseRawPixels;
                controller.Bounds = new Rectangle(0, 0, 800, 600); controller.ZoomFactor = 1; controller.IsVisible = true;
                if (controller.RasterizationScale != 1 || controller.Bounds != new Rectangle(0, 0, 800, 600) || controller.ZoomFactor != 1)
                    throw new InvalidDataException("EXACT_VIEWPORT_CONTROLLER_READBACK_REQUIRED");
                core = controller.CoreWebView2; browserPid = core.BrowserProcessId;
                if (!core.Profile.IsInPrivateModeEnabled) throw new InvalidDataException("INPRIVATE_REQUIRED");
                var settings = core.Settings;
                settings.AreHostObjectsAllowed = false; settings.AreDevToolsEnabled = false;
                settings.AreDefaultContextMenusEnabled = false; settings.AreDefaultScriptDialogsEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false; settings.IsPasswordAutosaveEnabled = false;
                settings.IsGeneralAutofillEnabled = false; settings.IsStatusBarEnabled = false; settings.IsZoomControlEnabled = false;
                core.PermissionRequested += (_, args) => { args.State = CoreWebView2PermissionState.Deny; args.Handled = true; };
                core.NewWindowRequested += (_, args) => args.Handled = true;
                core.DownloadStarting += (_, args) => args.Cancel = true;
                core.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;
                core.FrameNavigationStarting += (_, args) => args.Cancel = true;
                core.NavigationStarting += (_, args) =>
                {
                    navigationStartCount = checked(navigationStartCount + 1);
                    if (!navigationRequested || args.Uri != pageUri) { args.Cancel = true; RequestClose("unexpected_navigation", true); }
                };
                core.ProcessFailed += (_, _) => RequestClose("browser_process_failed", true);
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
                core.WebResourceRequested += Serve;
                core.WebMessageReceived += Message;
                initialized = true; stage = "controller_ready"; Inventory();
                await Send(new SessionHeader(1, nonce, 1, "controller_ready") { browser_pid = browserPid, initialization_succeeded = true, success = true });
            }
            catch (Exception) { RequestClose(stage, true); }
            finally { initializationReturned = true; }
        }

        private async Task ReadCommandsAsync()
        {
            try
            {
                while (!finished)
                {
                    SessionPacket? packet = await SessionProtocol.ReadAsync(input, nonce, 1, CancellationToken.None);
                    if (packet is null) continue;
                    var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    BeginInvoke(new Action(async () =>
                    {
                        try { await CommandAsync(packet.header); }
                        catch (Exception) { RequestClose(stage, true); }
                        finally { completed.TrySetResult(true); }
                    }));
                    await completed.Task;
                }
            }
            catch (Exception) { if (!finished && IsHandleCreated) try { BeginInvoke(new Action(() => RequestClose("input_pipe_failed", true))); } catch (InvalidOperationException) { } }
        }

        private async Task CommandAsync(SessionHeader request)
        {
            if (finished || closing) return;
            scope.RequireBinding();
            if (request.kind != "input" || request.request_id <= lastRequest) throw new InvalidDataException("STRICT_BROWSER_REQUEST_ORDER_REQUIRED");
            lastRequest = request.request_id;
            if (request.command == "close")
            {
                parentClose = true;
                if (pageSucceeded && !failed)
                {
                    stage = "close_final_evidence";
                    if (compositionActive)
                    {
                        await Cdp("Input.imeSetComposition", new { text = "", selectionStart = 0, selectionEnd = 0 });
                        compositionActive = false; failed = true; errorStage = "native_composition_aborted_on_close";
                    }
                    evidence = await ReadEvidenceAsync();
                }
                RequestClose("parent_close", false); return;
            }
            if (!initialized || core is null) throw new InvalidDataException("CONTROLLER_NOT_READY");
            stage = "owned_" + request.command;
            if (request.command == "load_owned_page")
            {
                if (navigationRequested) throw new InvalidDataException("OWNED_PAGE_LOAD_ONCE");
                navigationRequested = true; core.Navigate(pageUri);
                await pageReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
                evidence = await ReadEvidenceAsync(); pageSucceeded = true;
                await Send(WithEvidence(new SessionHeader(1, nonce, 1, "input_result") { request_id = request.request_id, success = true, page_succeeded = true }));
                return;
            }
            RequireDocument();
            if (request.command == "capture")
            {
                using var bytes = new BoundedCaptureStream();
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, bytes).WaitAsync(TimeSpan.FromSeconds(8));
                scope.RequireBinding(); RequireDocument();
                byte[] png = bytes.ToArray(); SessionProtocol.ValidatePng(png);
                using (var read = new MemoryStream(png, false))
                using (var bitmap = new Bitmap(read))
                {
                    Color pixel = bitmap.GetPixel(24, 24);
                    if (bitmap.Width != 800 || bitmap.Height != 600 || pixel.R != 235 || pixel.G != 151 || pixel.B != 52)
                        throw new InvalidDataException("CAPTURE_REAL_PIXEL_SENTINEL_REQUIRED");
                    sentinelObserved = true;
                }
                string hash = Convert.ToHexStringLower(SHA256.HashData(png));
                firstFrameHash ??= hash; differentFrames |= firstFrameHash != hash;
                evidence = await ReadEvidenceAsync();
                await Send(WithEvidence(new SessionHeader(1, nonce, 1, "frame")
                { request_id = request.request_id, frame_id = ++frameId, width = 800, height = 600, success = true }), png);
                return;
            }
            JsonElement? before = request.command is "ime_start" or "ime_update" or "ime_set_composition" or "ime_commit" or "ime_cancel"
                ? await ReadEvidenceAsync() : null;
            CompositionEffectObservation? beforeComposition = null;
            if (before is JsonElement nativeBefore)
            {
                beforeComposition = EffectObservation(nativeBefore); stage = "ime_target_admission";
                if (request.command == "ime_start")
                {
                    if (compositionActive) throw new InvalidDataException("IME_ALREADY_ACTIVE");
                    CompositionEffectGate.RequireAdmission(beforeComposition, null); compositionBaseline = beforeComposition;
                }
                else
                {
                    if (!compositionActive || compositionBaseline is null) throw new InvalidDataException("NATIVE_IME_START_REQUIRED");
                    if (request.command == "ime_cancel") CompositionEffectGate.RequireDocument(beforeComposition, compositionBaseline.document_instance_sha256);
                    else CompositionEffectGate.RequireAdmission(beforeComposition, compositionBaseline.document_instance_sha256);
                    if (request.command is "ime_update" or "ime_set_composition" && request.text!.Length == 0)
                        throw new InvalidDataException("COMPOSITION_EMPTY_UPDATE_REQUIRES_CANCEL");
                }
            }
            stage = "owned_" + request.command;
            await InputAsync(request);
            if (beforeComposition is not null)
            {
                stage = "ime_dom_effect";
                evidence = await WaitForCompositionEffectAsync(request, compositionBaseline!, beforeComposition);
                if (request.command is "ime_commit" or "ime_cancel") { compositionActive = false; compositionBaseline = null; }
            }
            else evidence = await ReadEvidenceAsync();
            await Send(WithBeforeEvidence(WithEvidence(new SessionHeader(1, nonce, 1, "input_result")
            { request_id = request.request_id, success = true, page_succeeded = true, operation_succeeded = true }), before));
        }

        private async Task InputAsync(SessionHeader h)
        {
            RequireDocument(); scope.RequireBinding();
            switch (h.command)
            {
                case "pointer_move": case "pointer_down": case "pointer_up": case "wheel":
                    modifiers = h.modifiers.GetValueOrDefault();
                    int bit = h.button switch { "left" => 1, "right" => 2, "middle" => 4, _ => 0 };
                    if (h.command == "pointer_down") mouseButtons |= bit;
                    if (h.command == "pointer_up") mouseButtons &= ~bit;
                    await Cdp("Input.dispatchMouseEvent", new { type = h.command switch { "pointer_down" => "mousePressed", "pointer_up" => "mouseReleased", "wheel" => "mouseWheel", _ => "mouseMoved" },
                        x = h.x!.Value, y = h.y!.Value, button = h.button ?? "none", buttons = mouseButtons, modifiers,
                        clickCount = h.command is "pointer_down" or "pointer_up" ? 1 : 0, deltaX = h.wheel_x ?? 0, deltaY = h.wheel_y ?? 0 });
                    break;
                case "key_down": case "key_up":
                    modifiers = h.modifiers.GetValueOrDefault();
                    bool down = h.command == "key_down";
                    (string key, string code, int vk) = Key(h.key!);
                    await Cdp("Input.dispatchKeyEvent", new { type = down ? "rawKeyDown" : "keyUp", key, code, windowsVirtualKeyCode = vk, nativeVirtualKeyCode = vk, modifiers });
                    break;
                case "private_paste":
                    await Cdp("Input.dispatchKeyEvent", new { type = "rawKeyDown", key = "Control", code = "ControlLeft", windowsVirtualKeyCode = 17, nativeVirtualKeyCode = 17, modifiers = 2 });
                    await Cdp("Input.dispatchKeyEvent", new { type = "rawKeyDown", key = "v", code = "KeyV", windowsVirtualKeyCode = 86, nativeVirtualKeyCode = 86, modifiers = 2 });
                    await Cdp("Input.dispatchKeyEvent", new { type = "keyUp", key = "v", code = "KeyV", windowsVirtualKeyCode = 86, nativeVirtualKeyCode = 86, modifiers = 2 });
                    await Cdp("Input.dispatchKeyEvent", new { type = "keyUp", key = "Control", code = "ControlLeft", windowsVirtualKeyCode = 17, nativeVirtualKeyCode = 17, modifiers = 0 });
                    modifiers = 0;
                    break;
                case "ime_start":
                    if (compositionActive) throw new InvalidDataException("IME_ALREADY_ACTIVE");
                    compositionActive = true;
                    // Empty imeSetComposition cancels; a native start with no
                    // preedit must wait for its first actual nonempty update.
                    if (h.text!.Length != 0) await SetComposition(h);
                    break;
                case "ime_update": case "ime_set_composition":
                    if (!compositionActive) throw new InvalidDataException("NATIVE_IME_START_REQUIRED");
                    await SetComposition(h); break;
                case "ime_commit":
                    if (!compositionActive) throw new InvalidDataException("NATIVE_IME_START_REQUIRED");
                    await Cdp("Input.insertText", new { text = h.text! }); break;
                case "ime_cancel":
                    if (!compositionActive) throw new InvalidDataException("NATIVE_IME_START_REQUIRED");
                    await Cdp("Input.imeSetComposition", new { text = "", selectionStart = 0, selectionEnd = 0 }); break;
                case "insert_text":
                    if (compositionActive) throw new InvalidDataException("COMPOSITION_MUST_COMMIT_OR_CANCEL");
                    await Cdp("Input.insertText", new { text = h.text! }); break;
                default: throw new InvalidDataException("FIXED_INPUT_ALLOWLIST_REQUIRED");
            }
        }
        private async Task<JsonElement> WaitForCompositionEffectAsync(SessionHeader request, CompositionEffectObservation baseline, CompositionEffectObservation before)
        {
            var effectBudget = Stopwatch.StartNew();
            while (true)
            {
                JsonElement observed = await ReadEvidenceAsync();
                if (CompositionEffectGate.HasRequiredEffect(request.command!, baseline, before, EffectObservation(observed), request.text)) return observed;
                if (effectBudget.ElapsedMilliseconds >= 1500) throw new InvalidDataException("COMPOSITION_DOM_EFFECT_NOT_OBSERVED");
                // Only bounded, fixed read-only snapshots are repeated. Time
                // passing never substitutes for an observed event delta.
                await Task.Delay(25);
            }
        }
        private static CompositionEffectObservation EffectObservation(JsonElement value) => new(
            value.GetProperty("document_instance_sha256").GetString()!, value.GetProperty("active_owned_text").GetBoolean(), value.GetProperty("document_has_focus").GetBoolean(),
            value.GetProperty("composition_start").GetInt32(), value.GetProperty("composition_update").GetInt32(), value.GetProperty("composition_end").GetInt32(), value.GetProperty("composing").GetBoolean(),
            OptionalLength(value, "composition_update_data_length"), OptionalHash(value, "composition_update_data_sha256"),
            OptionalLength(value, "composition_end_data_length"), OptionalHash(value, "composition_end_data_sha256"));
        private static int? OptionalLength(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : value.GetProperty(name).GetInt32();
        private static string? OptionalHash(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : value.GetProperty(name).GetString();
        private Task<string> SetComposition(SessionHeader h) => h.replacement_start.HasValue
            ? Cdp("Input.imeSetComposition", new { text = h.text!, selectionStart = h.selection_start!.Value, selectionEnd = h.selection_end!.Value,
                replacementStart = h.replacement_start!.Value, replacementEnd = h.replacement_end!.Value })
            : Cdp("Input.imeSetComposition", new { text = h.text!, selectionStart = h.selection_start!.Value, selectionEnd = h.selection_end!.Value });
        private Task<string> Cdp(string method, object value)
        { RequireDocument(); scope.RequireBinding(); return core!.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(value)); }
        private static (string key, string code, int vk) Key(string key)
        {
            if (key.Length == 1 && char.IsAsciiLetter(key[0])) return (key, "Key" + char.ToUpperInvariant(key[0]), char.ToUpperInvariant(key[0]));
            if (key.Length == 1 && char.IsAsciiDigit(key[0])) return (key, "Digit" + key, key[0]);
            return key switch
            {
                "Control" => (key, "ControlLeft", 17), "Shift" => (key, "ShiftLeft", 16), "Alt" => (key, "AltLeft", 18),
                "Enter" => (key, "Enter", 13), "Backspace" => (key, "Backspace", 8), "Tab" => (key, "Tab", 9), "Escape" => (key, "Escape", 27),
                "Delete" => (key, "Delete", 46), "Home" => (key, "Home", 36), "End" => (key, "End", 35), "PageUp" => (key, "PageUp", 33), "PageDown" => (key, "PageDown", 34),
                "ArrowLeft" => (key, "ArrowLeft", 37), "ArrowRight" => (key, "ArrowRight", 39), "ArrowUp" => (key, "ArrowUp", 38), "ArrowDown" => (key, "ArrowDown", 40), "Space" => (" ", "Space", 32),
                _ => throw new InvalidDataException("FIXED_DOM_KEY_REQUIRED")
            };
        }

        private async Task<JsonElement> ReadEvidenceAsync()
        {
            RequireDocument();
            // ExecuteScriptAsync does not await a JavaScript Promise. Use only
            // this fixed read-only expression and awaitPromise in the runtime.
            string json = await Cdp("Runtime.evaluate", new { expression = "window.collectPresentationEvidence()", awaitPromise = true, returnByValue = true }).WaitAsync(TimeSpan.FromSeconds(5));
            if (json.Length > 8192) throw new InvalidDataException("OWNED_EVIDENCE_LIMIT");
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("exceptionDetails", out _)) throw new InvalidDataException("OWNED_EVIDENCE_SCRIPT_FAILED");
            JsonElement value = doc.RootElement.GetProperty("result").GetProperty("value");
            if (value.GetProperty("nonce").GetString() != nonce || value.GetProperty("width").GetInt32() != 800 || value.GetProperty("height").GetInt32() != 600 ||
                value.GetProperty("device_pixel_ratio").GetDouble() != 1 || value.GetProperty("wasm").GetInt32() != 42 ||
                value.GetProperty("raf").GetInt32() < 60 || !value.GetProperty("ready").GetBoolean() || value.GetProperty("failed").GetBoolean())
                throw new InvalidDataException("OWNED_PAGE_REAL_WASM_VIEWPORT_RAF_REQUIRED");
            foreach (string counter in new[] { "composition_start", "composition_update", "composition_end", "input", "trusted_composition_start", "trusted_composition_update", "trusted_composition_end", "trusted_input" })
                if (value.GetProperty(counter).GetInt32() is < 0 or > 1024) throw new InvalidDataException("OWNED_DOM_COUNTER_LIMIT");
            foreach (string hash in new[] { "value_sha256", "phase_digest_sha256", "document_instance_sha256" })
            {
                string text = value.GetProperty(hash).GetString() ?? "";
                if (text.Length != 64 || text.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw new InvalidDataException("OWNED_DOM_HASH_REQUIRED");
            }
            if (value.GetProperty("value_length").GetInt32() is < 0 or > 1024) throw new InvalidDataException("OWNED_DOM_TEXT_LENGTH_LIMIT");
            int selectionStart = value.GetProperty("selection_start").GetInt32(), selectionEnd = value.GetProperty("selection_end").GetInt32();
            if (selectionStart < 0 || selectionEnd < selectionStart || selectionEnd > value.GetProperty("value_length").GetInt32())
                throw new InvalidDataException("OWNED_DOM_SELECTION_BOUNDS");
            foreach (string count in new[] { "pointer_down", "key_down" })
                if (value.GetProperty(count).GetInt32() is < 0 or > 1000000) throw new InvalidDataException("OWNED_DOM_INPUT_COUNTER_LIMIT");
            _ = value.GetProperty("active_owned_text").GetBoolean(); _ = value.GetProperty("document_has_focus").GetBoolean();
            CompositionEffectGate.RequireDocument(EffectObservation(value), null);
            return value.Clone();
        }
        private SessionHeader WithEvidence(SessionHeader header)
        {
            if (evidence is not JsonElement e) return header;
            return header with
            {
                wasm_result = e.GetProperty("wasm").GetInt32(), raf_count = e.GetProperty("raf").GetInt32(),
                dom_composition_start = e.GetProperty("composition_start").GetInt32(), dom_composition_update = e.GetProperty("composition_update").GetInt32(),
                dom_composition_end = e.GetProperty("composition_end").GetInt32(), dom_input_count = e.GetProperty("input").GetInt32(),
                dom_trusted_composition_start = e.GetProperty("trusted_composition_start").GetInt32(), dom_trusted_composition_update = e.GetProperty("trusted_composition_update").GetInt32(),
                dom_trusted_composition_end = e.GetProperty("trusted_composition_end").GetInt32(), dom_trusted_input_count = e.GetProperty("trusted_input").GetInt32(),
                dom_text_length = e.GetProperty("value_length").GetInt32(), dom_text_sha256 = e.GetProperty("value_sha256").GetString(),
                dom_phase_digest_sha256 = e.GetProperty("phase_digest_sha256").GetString(), dom_expected_hello = e.GetProperty("expected_hello").GetBoolean(),
                dom_composing = e.GetProperty("composing").GetBoolean(),
                dom_composition_update_data_length = OptionalLength(e, "composition_update_data_length"), dom_composition_update_data_sha256 = OptionalHash(e, "composition_update_data_sha256"),
                dom_composition_end_data_length = OptionalLength(e, "composition_end_data_length"), dom_composition_end_data_sha256 = OptionalHash(e, "composition_end_data_sha256"),
                dom_owned_text_focused = e.GetProperty("active_owned_text").GetBoolean(), dom_document_has_focus = e.GetProperty("document_has_focus").GetBoolean(),
                dom_selection_start = e.GetProperty("selection_start").GetInt32(), dom_selection_end = e.GetProperty("selection_end").GetInt32(),
                dom_pointer_down_count = e.GetProperty("pointer_down").GetInt32(), dom_key_down_count = e.GetProperty("key_down").GetInt32(),
                document_instance_sha256 = e.GetProperty("document_instance_sha256").GetString(), navigation_start_count = navigationStartCount,
                frame_pixel_sentinel = sentinelObserved, different_frame_observed = differentFrames, captured_frame_count = checked((int)frameId)
            };
        }
        private static SessionHeader WithBeforeEvidence(SessionHeader header, JsonElement? before)
        {
            if (header.kind != "input_result" || before is not JsonElement e) return header;
            return header with
            {
                before_dom_owned_text_focused = e.GetProperty("active_owned_text").GetBoolean(), before_dom_document_has_focus = e.GetProperty("document_has_focus").GetBoolean(),
                before_dom_selection_start = e.GetProperty("selection_start").GetInt32(), before_dom_selection_end = e.GetProperty("selection_end").GetInt32(),
                before_dom_text_length = e.GetProperty("value_length").GetInt32(), before_dom_text_sha256 = e.GetProperty("value_sha256").GetString(),
                before_dom_composition_start = e.GetProperty("composition_start").GetInt32(), before_dom_composition_update = e.GetProperty("composition_update").GetInt32(),
                before_dom_composition_end = e.GetProperty("composition_end").GetInt32()
            };
        }
        private void RequireDocument()
        { if (finished || closing || disposedController || core is null || core.Source != pageUri || !navigationRequested) throw new InvalidDataException("CURRENT_OWNED_DOCUMENT_REQUIRED"); }
        private void Message(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                if (args.Source != pageUri || args.WebMessageAsJson.Length > 1024) throw new InvalidDataException("OWNED_PAGE_MESSAGE_BOUNDING");
                using JsonDocument json = JsonDocument.Parse(args.WebMessageAsJson); var v = json.RootElement;
                if (v.GetProperty("nonce").GetString() != nonce || v.GetProperty("phase").GetString() != "page_ready" ||
                    v.GetProperty("wasm").GetInt32() != 42 || v.GetProperty("raf").GetInt32() < 60) throw new InvalidDataException("OWNED_PAGE_READY_PROOF_REQUIRED");
                pageReady.TrySetResult(true);
            }
            catch (Exception) { pageReady.TrySetException(new InvalidDataException("OWNED_PAGE_READY_FAILED")); RequestClose("owned_page_message_failed", true); }
        }
        private void Serve(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            try
            {
                byte[] bytes = []; string mime = "text/plain; charset=utf-8"; int status = 403;
                if (!closing && args.Request.Method == "GET" && args.Request.Uri == pageUri && args.ResourceContext == CoreWebView2WebResourceContext.Document)
                {
                    bytes = Encoding.UTF8.GetBytes("<!doctype html><html data-nonce=\"" + nonce + "\"><meta charset=utf-8><title>AutumnOS owned presentation</title><link rel=icon href=/owned-icon.svg><link rel=stylesheet href=/probe.css><canvas id=owned-canvas width=800 height=320></canvas><p id=instruction>仅输入合成测试文字：你好</p><textarea id=owned-text maxlength=1024 aria-label=Owned-test-input></textarea><script src=/probe.js defer></script></html>");
                    mime = "text/html; charset=utf-8"; status = 200;
                }
                else if (!closing && args.Request.Method == "GET" && args.Request.Uri == origin + "/probe.js" && args.ResourceContext == CoreWebView2WebResourceContext.Script)
                {
                    string resource = Assembly.GetExecutingAssembly().GetManifestResourceNames().Single(n => n.EndsWith(".OwnedPage.js", StringComparison.Ordinal));
                    using Stream script = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)!;
                    using MemoryStream copy = new(); script.CopyTo(copy); bytes = copy.ToArray(); mime = "application/javascript; charset=utf-8"; status = 200;
                }
                else if (!closing && args.Request.Method == "GET" && args.Request.Uri == origin + "/probe.css" && args.ResourceContext == CoreWebView2WebResourceContext.Stylesheet)
                {
                    bytes = Encoding.UTF8.GetBytes("html,body{margin:0;width:800px;height:600px;overflow:hidden;background:#152536;color:#f0eee7}#owned-canvas{position:absolute;left:0;top:0;width:800px;height:320px}#instruction{position:absolute;left:32px;top:315px;font:20px sans-serif}#owned-text{box-sizing:border-box;position:absolute;left:32px;top:360px;width:736px;height:120px;font:24px sans-serif;resize:none}");
                    mime = "text/css; charset=utf-8"; status = 200;
                }
                else if (!closing && args.Request.Method == "GET" && args.Request.Uri == origin + "/owned-icon.svg")
                { bytes = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 16 16\"><path fill=\"#eb9734\" d=\"M2 2h12v12H2z\"/></svg>"); mime = "image/svg+xml"; status = 200; }
                var data = new MemoryStream(bytes, false); resources.Add(data);
                args.Response = environment!.CreateWebResourceResponse(data, status, status == 200 ? "OK" : "Forbidden", FrozenPolicy.ResponseHeaders + "\r\nContent-Type: " + mime);
                if (status != 200) RequestClose("resource_outside_owned_package", true);
            }
            catch (Exception) { RequestClose("owned_resource_failed", true); }
        }

        private async Task Send(SessionHeader header, byte[]? data = null)
        {
            await outputGate.WaitAsync();
            try { await SessionProtocol.WriteAsync(output, new(header, data ?? []), CancellationToken.None); }
            finally { outputGate.Release(); }
        }
        private void Inventory()
        {
            if (finished || environment is null) return;
            try { processCount = environment.GetProcessInfos().Count; if (processCount > 128) RequestClose("sdk_process_limit", true); }
            catch (Exception) { processCount = -1; RequestClose("sdk_inventory_failed", true); }
        }
        private void RequestClose(string reason, bool failure)
        {
            failed |= failure; if (failure && errorStage == "none") errorStage = reason;
            if (closing || finished) return;
            closing = true; closeAt = clock.ElapsedMilliseconds;
        }
        private void Tick()
        {
            if (finished) return;
            try
            {
                if (!closing) scope.RequireBinding();
                if (clock.ElapsedMilliseconds - lastInventory >= 500) { lastInventory = clock.ElapsedMilliseconds; Inventory(); }
                if (!initializationReturned && clock.ElapsedMilliseconds >= 25000) RequestClose("initialization_budget_unproven", true);
                if (!closing) return;
                if (initializationReturned && !disposedController)
                {
                    try { controller?.Close(); controller = null; core = null; disposedController = true; }
                    catch (Exception) { failed = true; errorStage = "controller_close_failed"; }
                    foreach (Stream resource in resources) resource.Dispose(); resources.Clear();
                }
                Inventory();
                if (initializationReturned && disposedController && (environment is null || normalBrowserExit && processCount == 0)) { _ = CompleteAsync(); return; }
                if (clock.ElapsedMilliseconds - closeAt >= 12000) { failed = true; errorStage = "normal_cleanup_not_proven"; _ = CompleteAsync(); }
            }
            catch (Exception) { RequestClose("private_binding_or_cleanup_failed", true); }
        }
        private async Task CompleteAsync()
        {
            if (finished) return; finished = true; timer.Stop();
            bool clean = initializationReturned && disposedController && (environment is null || normalBrowserExit && processCount == 0);
            if (pageSucceeded && (frameId < 2 || !differentFrames || !sentinelObserved))
            { failed = true; if (errorStage == "none") errorStage = "presentation_capture_evidence_missing"; }
            bool success = initialized && pageSucceeded && parentClose && clean && !failed && frameId >= 2 && differentFrames && sentinelObserved;
            try
            {
                await Send(WithEvidence(new SessionHeader(1, nonce, 1, "host_complete")
                {
                    browser_pid = browserPid == 0 ? null : browserPid, success = success, cleanup_confirmed = clean,
                    initialization_succeeded = initialized, page_succeeded = pageSucceeded, controller_disposed = disposedController,
                    normal_browser_exit = normalBrowserExit, final_environment_process_count = processCount < 0 ? null : processCount,
                    error_stage = failed ? errorStage : null
                }));
                ExitCode = success ? 0 : 1;
            }
            catch (Exception) { ExitCode = 1; }
            finally { Close(); }
        }
        protected override void Dispose(bool disposing)
        { if (disposing) { timer.Dispose(); foreach (Stream resource in resources) resource.Dispose(); resources.Clear(); } base.Dispose(disposing); }
    }

    private sealed class BoundedCaptureStream : MemoryStream
    {
        private void RequireCapacity(int count) { if (count < 0 || Position + count > SessionProtocol.PngLimit) throw new InvalidDataException("CAPTURE_STREAM_LIMIT"); }
        public override void Write(byte[] buffer, int offset, int count) { RequireCapacity(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { RequireCapacity(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { RequireCapacity(1); base.WriteByte(value); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { RequireCapacity(count); return base.WriteAsync(buffer, offset, count, cancellationToken); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { RequireCapacity(buffer.Length); return base.WriteAsync(buffer, cancellationToken); }
        public override void SetLength(long value) { if (value is < 0 or > SessionProtocol.PngLimit) throw new InvalidDataException("CAPTURE_STREAM_LIMIT"); base.SetLength(value); }
    }

}
