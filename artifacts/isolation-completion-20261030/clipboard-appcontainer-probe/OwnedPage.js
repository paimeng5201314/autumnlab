// 2026-11-05 reconstructed fixture, producer: 派蒙.
// Original machine bytes are unavailable. This is new test source, not historical evidence.
// Uses the real execCommand/default paste paths; no Clipboard API, baseline, side or success oracle.
(() => {
    "use strict";
    const nonce = document.documentElement.dataset.nonce;
    const operation = document.documentElement.dataset.operation;
    if (!/^[a-f0-9]{32}$/.test(nonce) || !["copy", "cut", "paste"].includes(operation)) {
        throw new Error("OWNED_FIXTURE_BINDING_REQUIRED");
    }
    const selected = document.getElementById("selected");
    const paste = document.getElementById("paste");
    const copy = document.getElementById("copy");
    const cut = document.getElementById("cut");
    const prepare = document.getElementById("prepare");
    if (!(selected instanceof HTMLTextAreaElement) || !(paste instanceof HTMLTextAreaElement) ||
        !(copy instanceof HTMLButtonElement) || !(cut instanceof HTMLButtonElement) || !(prepare instanceof HTMLButtonElement)) {
        throw new Error("OWNED_FIXTURE_DOM_REQUIRED");
    }
    const marker = "owned-page-" + nonce + "-" + operation;
    // This is the owned copy/cut source, never the native-only paste baseline.
    selected.value = marker;
    const pasteEvents = [];
    let captureReady = false;
    let failed = false;
    const post = (phase, facts) => window.chrome.webview.postMessage({ nonce, operation, phase, ...facts });
    const activeElement = () => document.activeElement ? document.activeElement.id : "";
    const userActivation = () => !!navigator.userActivation && navigator.userActivation.isActive === true;
    const fail = code => {
        failed = true;
        // An unknown phase is a permanent native failure, never an empty successful result.
        post("fixture_error", { code });
    };
    const requireCapture = () => {
        if (!captureReady || failed || paste.value.length > 4096 || pasteEvents.length > 64) {
            throw new Error("OWNED_FIXTURE_PASTE_CAPTURE_FAILED");
        }
    };
    window.addEventListener("error", () => { failed = true; });
    window.addEventListener("unhandledrejection", () => { failed = true; });
    const edit = (command, event) => {
        const trustedClick = event.isTrusted === true;
        const activation = userActivation();
        selected.focus();
        selected.select();
        const selectionBeforeMatches = selected.value.slice(selected.selectionStart, selected.selectionEnd) === marker;
        const documentFocused = document.hasFocus();
        let attempted = false;
        let execCommandResult = false;
        try {
            attempted = true;
            execCommandResult = document.execCommand(command) === true;
        } catch {
            // A denied/throwing real attempt is not rewritten into an execution success.
        }
        post(command, { attempted, trustedClick, userActivation: activation, documentFocused,
            selectionBeforeMatches, execCommandResult, selectionAfterEmpty: selected.value.length === 0 });
    };
    copy.addEventListener("click", event => edit("copy", event));
    cut.addEventListener("click", event => edit("cut", event));
    prepare.addEventListener("click", event => {
        const activation = userActivation();
        paste.focus();
        // Never clear the target or accumulated events when preparing/sampling/closing.
        post("paste_ready", { trustedClick: event.isTrusted === true, userActivation: activation,
            documentFocused: document.hasFocus(), activeElement: activeElement() });
    });
    paste.addEventListener("keydown", event => {
        if (event.ctrlKey && event.key.toLowerCase() === "v") {
            post("paste_keydown", { trusted: event.isTrusted === true, control: event.ctrlKey === true,
                userActivation: userActivation(), documentFocused: document.hasFocus(), activeElement: activeElement() });
        }
        // Let the engine execute its normal paste action; do not preventDefault or set value.
    });
    document.addEventListener("paste", event => {
        if (failed) return;
        try {
            if (!event.clipboardData) { fail("PASTE_EVENT_DATA_UNAVAILABLE"); return; }
            const text = event.clipboardData.getData("text/plain");
            if (typeof text !== "string" || text.length > 4096 || pasteEvents.length >= 64) {
                fail("PASTE_EVENT_BOUNDS"); return;
            }
            const entry = { event_index: pasteEvents.length + 1, trusted: event.isTrusted === true, text };
            pasteEvents.push(entry);
            post("paste_event", entry);
        } catch {
            fail("PASTE_EVENT_READ_FAILED");
        }
    }, true);
    Object.defineProperty(window, "reportPasteResult", { value: index => {
        requireCapture();
        if (!Number.isInteger(index) || index < 0 || index > 10) throw new Error("PASTE_SAMPLE_INDEX_BOUNDS");
        post("paste_result", { sample_index: index, complete: captureReady && !failed, value: paste.value,
            pasteEvents: pasteEvents.map(entry => ({ trusted: entry.trusted, text: entry.text })),
            documentFocused: document.hasFocus(), activeElement: activeElement() });
    } });
    Object.defineProperty(window, "collectPasteEvidence", { value: () => {
        requireCapture();
        return { capture_version: 1, nonce, operation, event_count: pasteEvents.length,
            paste_events: pasteEvents.map(entry => ({ ...entry })), value: paste.value };
    } });
    captureReady = selected.isConnected && paste.isConnected && copy.isConnected && cut.isConnected && prepare.isConnected;
    if (!captureReady) throw new Error("OWNED_FIXTURE_CONNECTED_DOM_REQUIRED");
    post("ready", { loaded: captureReady });
})();
