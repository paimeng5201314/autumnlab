/* Lab Chronicles AutumnOS SDK 0.3.0 · 制作人：派蒙. No external dependencies. */
(function (root) {
  "use strict";
  const MAX_PENDING = 16, MAX_BYTES = 32768, MAX_PER_MINUTE = 120, RATE_WINDOW_MS = 60000;
  const STATES = new Set(["starting", "foreground", "background", "suspended", "closing", "closed", "crashed"]);
  const PERMISSIONS = new Set(["saves", "identity.profile", "storage", "files.open", "files.save", "notifications", "shortcuts", "widgets", "links"]);
  const EVENT_NAMES = new Set(["appearance.changed", "permissions.changed", "identity.changed", "links.opened", "shortcuts.invoked", "lifecycle.changed"]);
  const MAX_LISTENERS = 64;
  const bridge = root.chrome && root.chrome.webview;
  const pending = new Map(), listeners = new Set(), eventListeners = new Map(), recentRequests = [];
  const prefix = "r" + (root.crypto && root.crypto.randomUUID ? root.crypto.randomUUID().replace(/-/g, "") : Date.now().toString(36));
  // BigInt avoids Number precision loss and ID reuse in long-lived documents.
  // Only the counter, 16 pending requests and 120 recent timestamps are retained.
  let sequence = 0n, ended = false, lastNow = 0;
  class AutumnSdkError extends Error {
    constructor(code, message, retryable = false, correlationId = null) {
      super(message); this.name = "AutumnSdkError"; this.code = code; this.retryable = retryable; this.correlationId = correlationId;
    }
  }
  function failure(code, message, retryable = false) { return new AutumnSdkError(code, message, retryable); }
  function monotonicNow() {
    const now = root.performance && typeof root.performance.now === "function" ? root.performance.now() : Date.now();
    lastNow = Math.max(lastNow, now); return lastNow;
  }
  function isObject(value) { return value !== null && typeof value === "object" && !Array.isArray(value); }
  function exact(value, names) { return isObject(value) && Object.keys(value).length === names.length && names.every(name => Object.prototype.hasOwnProperty.call(value, name)); }
  function actionId(value) { return typeof value === "string" && /^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$/.test(value); }
  function declaredId(value) { return typeof value === "string" && /^[a-z][a-z0-9_-]{0,39}$/.test(value); }
  function safeEvent(name, value) {
    if (name === "appearance.changed") return exact(value, ["theme", "language", "scale", "reduceMotion"])
      && ["light", "dark", "system"].includes(value.theme) && typeof value.language === "string"
      && value.language.length <= 32 && /^[a-zA-Z]{2,8}(?:-[a-zA-Z0-9]{1,8})*$/.test(value.language)
      && Number.isFinite(value.scale) && value.scale >= 0.5 && value.scale <= 8 && typeof value.reduceMotion === "boolean";
    if (name === "permissions.changed") return exact(value, ["name", "state"]) && PERMISSIONS.has(value.name) && ["prompt", "granted", "denied", "revoked"].includes(value.state);
    if (name === "identity.changed") return exact(value, ["state"]) && ["signed_in", "offline_cached"].includes(value.state);
    if (name === "lifecycle.changed") return exact(value, ["state", "blocksMaintenance"]) && STATES.has(value.state) && typeof value.blocksMaintenance === "boolean";
    if (name === "shortcuts.invoked") return exact(value, ["id", "action"]) && declaredId(value.id) && declaredId(value.action);
    if (name === "links.opened") return exact(value, ["action", "arguments"]) && declaredId(value.action) && isObject(value.arguments)
      && Object.keys(value.arguments).length <= 8 && Object.entries(value.arguments).every(([key, text]) => actionId(key)
        && typeof text === "string" && text.length <= 256 && !/[\u0000-\u001f\u007f-\u009f]/.test(text));
    return false;
  }
  function listenerCount() {
    let count = listeners.size; for (const set of eventListeners.values()) count += set.size; return count;
  }
  function subscribe(set, listener) {
    if (!set.has(listener) && listenerCount() >= MAX_LISTENERS) throw failure("SUBSCRIPTION_LIMIT", "At most 64 SDK event listeners may be active.");
    set.add(listener); return () => set.delete(listener);
  }
  // Reject values JSON.stringify silently changes; the host independently enforces limits.
  function assertJson(value, depth = 0, ancestors = new Set()) {
    if (value === null || typeof value === "string" || typeof value === "boolean") return;
    if (typeof value === "number" && Number.isFinite(value)) return;
    if (typeof value !== "object" || depth > 27 || ancestors.has(value)) throw failure("INVALID_ARGUMENT", "Parameters must contain finite, acyclic JSON values with at most 28 nested containers.");
    if (!Array.isArray(value) && Object.prototype.toString.call(value) !== "[object Object]") throw failure("INVALID_ARGUMENT", "Parameters must contain plain JSON objects.");
    ancestors.add(value);
    if (Array.isArray(value)) {
      for (let index = 0; index < value.length; index++) assertJson(value[index], depth + 1, ancestors);
    } else {
      if (typeof value.toJSON === "function" || Object.getOwnPropertySymbols(value).length) throw failure("INVALID_ARGUMENT", "Parameters cannot contain custom JSON serialization or symbol keys.");
      for (const key of Object.keys(value)) assertJson(value[key], depth + 1, ancestors);
    }
    ancestors.delete(value);
  }
  function settle(requestId, error, result) {
    const item = pending.get(requestId);
    if (!item) return;
    pending.delete(requestId); root.clearTimeout(item.timer);
    if (item.signal) item.signal.removeEventListener("abort", item.abort);
    if (error) item.reject(error); else item.resolve(result);
  }
  function dispose() {
    if (ended) return;
    ended = true;
    for (const requestId of pending.keys()) settle(requestId, failure("SESSION_EXPIRED", "The application session has ended."));
    if (bridge) bridge.removeEventListener("message", receive);
    listeners.clear(); eventListeners.clear(); recentRequests.length = 0;
  }
  function receive(event) {
    let message = event.data;
    if (typeof message === "string") { try { message = JSON.parse(message); } catch (_) { return; } }
    if (!isObject(message)) return;
    if (message.event === "lifecycle.stateChanged" && exact(message, ["event", "state"]) && STATES.has(message.state)) {
      for (const listener of Array.from(listeners)) {
        try { listener(message.state); } catch (_) { /* Application owns its handler errors. */ }
      }
      if (message.state === "closed" || message.state === "crashed") dispose();
      return;
    }
    if (typeof message.event === "string") {
      if (!exact(message, ["event", "data"]) || !EVENT_NAMES.has(message.event) || !safeEvent(message.event, message.data)) return;
      for (const listener of Array.from(eventListeners.get(message.event) || [])) {
        try { listener(message.data); } catch (_) { /* Application owns its handler errors. */ }
      }
      if (message.event === "lifecycle.changed" && ["closed", "crashed"].includes(message.data.state)) dispose();
      return;
    }
    if (typeof message.requestId !== "string" || !pending.has(message.requestId)) return;
    if (message.ok === true && Object.prototype.hasOwnProperty.call(message, "result")) settle(message.requestId, null, message.result);
    else if (message.ok === false && isObject(message.error)
      && typeof message.error.code === "string" && typeof message.error.message === "string"
      && typeof message.error.retryable === "boolean" && typeof message.error.correlationId === "string") {
      const error = message.error;
      settle(message.requestId, new AutumnSdkError(error.code, error.message, error.retryable, error.correlationId));
    } else settle(message.requestId, failure("INVALID_RESPONSE", "The host returned an invalid SDK response."));
  }
  function request(method, parameters = {}, options = {}) {
    return new Promise((resolve, reject) => {
      if (ended) { reject(failure("SESSION_EXPIRED", "The application session has ended.")); return; }
      if (!bridge || typeof bridge.postMessage !== "function") { reject(failure("HOST_UNAVAILABLE", "Open this application inside AutumnOS to use host capabilities.")); return; }
      if (typeof method !== "string" || method.length === 0 || method.length > 128 || !isObject(parameters) || !isObject(options)) {
        reject(failure("INVALID_ARGUMENT", "A method name, JSON object parameters and options object are required.")); return;
      }
      const timeoutMs = options.timeoutMs === undefined ? 30000 : options.timeoutMs, signal = options.signal;
      if (!Number.isInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 120000
        || (signal !== undefined && (!signal || typeof signal.aborted !== "boolean" || typeof signal.addEventListener !== "function" || typeof signal.removeEventListener !== "function"))) {
        reject(failure("INVALID_ARGUMENT", "timeoutMs must be an integer from 1 to 120000; signal must be an AbortSignal.")); return;
      }
      if (signal && signal.aborted) { reject(failure("USER_CANCELLED", "The caller stopped waiting before sending the request.")); return; }
      if (pending.size >= MAX_PENDING) { reject(failure("TOO_MANY_REQUESTS", "At most 16 SDK requests may be pending.", true)); return; }
      const now = monotonicNow();
      while (recentRequests.length && now - recentRequests[0] >= RATE_WINDOW_MS) recentRequests.shift();
      if (recentRequests.length >= MAX_PER_MINUTE) { reject(failure("RATE_LIMITED", "At most 120 SDK requests may be sent in a rolling 60 seconds.", true)); return; }
      const requestId = prefix + "_" + (++sequence).toString(36);
      let message;
      try {
        assertJson(parameters);
        const serialized = JSON.stringify({ protocolVersion: 1, requestId, method, params: parameters });
        if (new TextEncoder().encode(serialized).length > MAX_BYTES) throw failure("MESSAGE_TOO_LARGE", "The UTF-8 request exceeds 32768 bytes.");
        message = JSON.parse(serialized);
      } catch (error) { reject(error instanceof AutumnSdkError ? error : failure("INVALID_ARGUMENT", "Parameters could not be serialized.")); return; }
      const abort = () => settle(requestId, failure("USER_CANCELLED", "The caller stopped waiting. Submitted host work, including saves, may still complete."));
      const timer = root.setTimeout(() => settle(requestId, failure("TIMEOUT", "No host response arrived before the deadline. Submitted host work, including saves, may still complete.")), timeoutMs);
      pending.set(requestId, { resolve, reject, timer, signal, abort });
      if (signal) signal.addEventListener("abort", abort, { once: true });
      recentRequests.push(now);
      try { bridge.postMessage(message); }
      catch (_) { settle(requestId, failure("TRANSPORT_ERROR", "The request could not be delivered to AutumnOS.")); }
    });
  }
  if (bridge) bridge.addEventListener("message", receive);
  root.addEventListener("pagehide", dispose, { once: true });
  root.autumn = Object.freeze({
    version: "0.3.0", protocolVersion: 1, request,
    onLifecycle(listener) {
      if (typeof listener !== "function") throw new TypeError("A lifecycle listener is required.");
      if (ended) return () => {};
      return subscribe(listeners, listener);
    },
    onEvent(name, listener) {
      if (!EVENT_NAMES.has(name)) throw failure("CAPABILITY_UNAVAILABLE", "This event is not available in this SDK.");
      if (typeof listener !== "function") throw new TypeError("An event listener is required.");
      if (ended) return () => {};
      if (!eventListeners.has(name)) eventListeners.set(name, new Set());
      return subscribe(eventListeners.get(name), listener);
    }
  });
})(window);
