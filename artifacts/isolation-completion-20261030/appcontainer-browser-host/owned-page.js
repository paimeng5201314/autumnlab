// 2026-11-05 reconstructed fixture, producer: 派蒙.
// Original machine bytes are unavailable. This is new test source, not historical evidence.
// Only an actual package-local WASM result and 60 actual RAF callbacks are reported.
(() => {
    "use strict";
    const nonce = document.documentElement.dataset.nonce;
    const canvas = document.getElementById("owned-canvas");
    if (!/^[a-f0-9]{32}$/.test(nonce) || !(canvas instanceof HTMLCanvasElement)) {
        throw new Error("OWNED_FIXTURE_BINDING_REQUIRED");
    }
    const context = canvas.getContext("2d");
    if (!context) throw new Error("OWNED_FIXTURE_CANVAS_REQUIRED");
    // (module (func (export "answer") (result i32) i32.const 42))
    const wasmBytes = new Uint8Array([
        0, 97, 115, 109, 1, 0, 0, 0,
        1, 5, 1, 96, 0, 1, 127, 3, 2, 1, 0,
        7, 10, 1, 6, 97, 110, 115, 119, 101, 114, 0, 0,
        10, 6, 1, 4, 0, 65, 42, 11
    ]);
    void (async () => {
        const { instance } = await WebAssembly.instantiate(wasmBytes);
        const wasm = instance.exports.answer();
        if (wasm !== 42) throw new Error("OWNED_FIXTURE_WASM_RESULT");
        let frames = 0;
        const paint = () => {
            frames += 1;
            context.fillStyle = "#152536";
            context.fillRect(0, 0, canvas.width, canvas.height);
            context.fillStyle = "#eb9734";
            context.fillRect((frames * 3) % (canvas.width - 20), 30, 20, 20);
            context.fillStyle = "#f0eee7";
            context.fillText("owned frame " + frames, 8, 18);
            if (frames === 60) {
                window.chrome.webview.postMessage({ nonce, kind: "owned_page_complete", wasm, frames });
            } else {
                requestAnimationFrame(paint);
            }
        };
        requestAnimationFrame(paint);
    })();
})();
