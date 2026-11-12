// 2026-11-05 reconstructed fixture, producer: 派蒙.
// Original machine bytes are unavailable. This is new test source, not historical evidence.
// Only observes actual DOM input/composition; never sets text, selection, focus, trust or event counts.
(() => {
    "use strict";
    const nonce = document.documentElement.dataset.nonce;
    const canvas = document.getElementById("owned-canvas");
    const text = document.getElementById("owned-text");
    if (!/^[a-f0-9]{32}$/.test(nonce) || !(canvas instanceof HTMLCanvasElement) || !(text instanceof HTMLTextAreaElement)) {
        throw new Error("OWNED_FIXTURE_BINDING_REQUIRED");
    }
    const context = canvas.getContext("2d");
    if (!context) throw new Error("OWNED_FIXTURE_CANVAS_REQUIRED");
    const encoder = new TextEncoder();
    const hex = bytes => Array.from(bytes, value => value.toString(16).padStart(2, "0")).join("");
    const hash = async value => hex(new Uint8Array(await crypto.subtle.digest("SHA-256", encoder.encode(value))));
    const counters = { composition_start: 0, composition_update: 0, composition_end: 0, input: 0,
        trusted_composition_start: 0, trusted_composition_update: 0, trusted_composition_end: 0, trusted_input: 0,
        pointer_down: 0, key_down: 0 };
    const phases = [];
    let updateData = null;
    let endData = null;
    let composing = false;
    let ready = false;
    let failed = false;
    let wasm = null;
    let raf = 0;
    let documentHash = null;
    const validText = value => {
        if (typeof value !== "string" || value.length > 1024 || value.includes("\0")) return false;
        // TextEncoder replaces unmatched surrogates; reject instead of hashing different text.
        for (let index = 0; index < value.length; index += 1) {
            const unit = value.charCodeAt(index);
            if (unit >= 0xd800 && unit <= 0xdbff) {
                const next = value.charCodeAt(++index);
                if (!(next >= 0xdc00 && next <= 0xdfff)) return false;
            } else if (unit >= 0xdc00 && unit <= 0xdfff) return false;
        }
        return true;
    };
    const count = (name, limit) => {
        counters[name] += 1;
        if (!Number.isSafeInteger(counters[name]) || counters[name] > limit) failed = true;
    };
    const phase = (name, event) => {
        count(name, 1024);
        const trusted = event.isTrusted === true;
        if (trusted) count("trusted_" + name, 1024);
        const data = event.data;
        if ((name !== "input" || data !== null) && !validText(data)) { failed = true; return; }
        // Ledger is bounded. Exceeding a bound latches failure; no saturated successful counter.
        if (phases.length >= 4096) { failed = true; return; }
        phases.push({ sequence: phases.length + 1, name, trusted, data });
        if (name === "composition_start") composing = true;
        if (name === "composition_update") updateData = data;
        if (name === "composition_end") { endData = data; composing = false; }
    };
    for (const [domName, name] of [["compositionstart", "composition_start"], ["compositionupdate", "composition_update"],
        ["compositionend", "composition_end"], ["input", "input"]]) {
        text.addEventListener(domName, event => phase(name, event));
    }
    document.addEventListener("pointerdown", () => count("pointer_down", 1000000), true);
    document.addEventListener("keydown", () => count("key_down", 1000000), true);
    window.addEventListener("error", () => { failed = true; });
    window.addEventListener("unhandledrejection", () => { failed = true; });
    canvas.addEventListener("contextlost", () => { failed = true; });
    Object.defineProperty(window, "collectPresentationEvidence", { value: async () => {
        // Freeze a single DOM/event generation before any hashing await. Later input is not spliced into this snapshot.
        const value = text.value;
        const start = text.selectionStart;
        const end = text.selectionEnd;
        const snapshot = { ...counters };
        const events = phases.map(entry => ({ ...entry }));
        const update = updateData;
        const ended = endData;
        const facts = { nonce, width: window.innerWidth, height: window.innerHeight, device_pixel_ratio: window.devicePixelRatio,
            wasm, raf, ready, failed, ...snapshot, composing, value_length: value.length,
            expected_hello: value === "你好", active_owned_text: document.activeElement === text,
            document_has_focus: document.hasFocus(), selection_start: start, selection_end: end,
            document_instance_sha256: documentHash };
        if (!validText(value) || start < 0 || end < start || end > value.length) failed = true;
        const records = await Promise.all(events.map(async entry => {
            const length = entry.data === null ? "-" : String(entry.data.length);
            const digest = entry.data === null ? "-" : await hash(entry.data);
            return entry.sequence + "|" + entry.name + "|" + (entry.trusted ? "1" : "0") + "|" + length + "|" + digest + "\n";
        }));
        const [valueHash, phaseHash, updateHash, endHash] = await Promise.all([
            hash(value), hash(records.join("")), update === null ? null : hash(update), ended === null ? null : hash(ended)
        ]);
        return { ...facts, failed: facts.failed || failed, value_sha256: valueHash, phase_digest_sha256: phaseHash,
            composition_update_data_length: update === null ? null : update.length, composition_update_data_sha256: updateHash,
            composition_end_data_length: ended === null ? null : ended.length, composition_end_data_sha256: endHash };
    } });
    // (module (func (export "answer") (result i32) i32.const 42))
    const wasmBytes = new Uint8Array([
        0, 97, 115, 109, 1, 0, 0, 0,
        1, 5, 1, 96, 0, 1, 127, 3, 2, 1, 0,
        7, 10, 1, 6, 97, 110, 115, 119, 101, 114, 0, 0,
        10, 6, 1, 4, 0, 65, 42, 11
    ]);
    void (async () => {
        const random = crypto.getRandomValues(new Uint8Array(32));
        documentHash = await hash("autumnos-reconstructed-document-v1|" + nonce + "|" + hex(random));
        const result = await WebAssembly.instantiate(wasmBytes);
        wasm = result.instance.exports.answer();
        if (wasm !== 42) throw new Error("OWNED_FIXTURE_WASM_RESULT");
        const paint = () => {
            try {
                raf += 1;
                if (!Number.isSafeInteger(raf)) throw new Error("OWNED_FIXTURE_RAF_BOUNDS");
                context.fillStyle = "#152536";
                context.fillRect(0, 0, canvas.width, canvas.height);
                context.fillStyle = "#639cc2";
                context.fillRect(64 + (raf * 3) % 672, 130, 32, 40);
                context.fillStyle = "#f0eee7";
                context.font = "24px sans-serif";
                context.fillText("Owned WASM " + wasm + " · frame " + raf, 64, 80);
                // Stable exact interior pixel checked by native CapturePreview at (24,24).
                context.fillStyle = "#eb9734";
                context.fillRect(16, 16, 32, 32);
                if (!ready && !failed && raf >= 60 && window.innerWidth === 800 && window.innerHeight === 600 &&
                    window.devicePixelRatio === 1 && canvas.isConnected && text.isConnected) {
                    ready = true;
                    window.chrome.webview.postMessage({ nonce, phase: "page_ready", wasm, raf });
                }
                if (!failed) requestAnimationFrame(paint);
            } catch {
                failed = true;
            }
        };
        requestAnimationFrame(paint);
    })();
})();
