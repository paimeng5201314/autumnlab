/* Node built-in tests. Transport substitutes are explicit tests, never production fallback. */
"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs"), path = require("node:path"), vm = require("node:vm");
const source = fs.readFileSync(path.join(__dirname, "../sdk/autumn-sdk.js"), "utf8");
function fixture(available = true) {
  const sent = [], events = new Map(), pageEvents = new Map();
  let now = 0;
  const bridge = {
    postMessage(message) { sent.push(message); },
    addEventListener(name, callback) { events.set(name, callback); },
    removeEventListener(name) { events.delete(name); }
  };
  const window = {
    chrome: available ? { webview: bridge } : undefined,
    crypto: require("node:crypto").webcrypto, setTimeout, clearTimeout,
    performance: { now: () => now },
    addEventListener(name, callback) { pageEvents.set(name, callback); }
  };
  vm.runInNewContext(source, { window, TextEncoder, Date, console });
  return {
    sdk: window.autumn, sent, bridge,
    advance(milliseconds) { now += milliseconds; },
    deliver(data) { events.get("message")?.({ data }); },
    close() { pageEvents.get("pagehide")(); },
    ok(index, result) { this.deliver({ requestId: sent[index].requestId, ok: true, result }); }
  };
}
test("correlates out-of-order JSON and object responses with unique IDs", async () => {
  const f = fixture();
  const first = f.sdk.request("platform.getCapabilities"), second = f.sdk.request("lifecycle.getState");
  assert.equal(f.sent[0].protocolVersion, 1);
  assert.deepEqual(Object.keys(f.sent[0]).sort(), ["method", "params", "protocolVersion", "requestId"]);
  assert.match(f.sent[0].requestId, /^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/);
  assert.notEqual(f.sent[0].requestId, f.sent[1].requestId);
  f.ok(1, { state: "foreground" });
  f.deliver(JSON.stringify({ requestId: f.sent[0].requestId, ok: true, result: { protocolVersion: 1 } }));
  assert.equal((await first).protocolVersion, 1); assert.equal((await second).state, "foreground"); f.close();
});
test("propagates host denial and diagnostic correlation without fake success", async () => {
  const f = fixture(), p = f.sdk.request("saves.read", { slot: "game" });
  f.deliver({ requestId: f.sent[0].requestId, ok: false, error: { code: "PERMISSION_DENIED", message: "Denied", retryable: false, correlationId: "test-id" } });
  await assert.rejects(p, { code: "PERMISSION_DENIED", retryable: false, correlationId: "test-id" }); f.close();
});
test("missing host fails explicitly", async () => {
  await assert.rejects(fixture(false).sdk.request("saves.write", { slot: "game", value: 1 }), { code: "HOST_UNAVAILABLE" });
});
test("rejects non-JSON, cyclic, oversized UTF-8 and invalid options before transport", async () => {
  const f = fixture(), cycle = {}; cycle.self = cycle;
  for (const params of [{ value: NaN }, { value: undefined }, { value: 1n }, cycle, { value: new Date() }]) {
    await assert.rejects(f.sdk.request("saves.write", params), { code: "INVALID_ARGUMENT" });
  }
  await assert.rejects(f.sdk.request("saves.write", { value: "派".repeat(12000) }), { code: "MESSAGE_TOO_LARGE" });
  await assert.rejects(f.sdk.request("x", {}, { timeoutMs: 0 }), { code: "INVALID_ARGUMENT" });
  assert.equal(f.sent.length, 0); f.close();
});
test("enforces 16 pending requests and frees capacity on settlement", async () => {
  const f = fixture(), pending = Array.from({ length: 16 }, () => f.sdk.request("x"));
  await assert.rejects(f.sdk.request("x"), { code: "TOO_MANY_REQUESTS" });
  f.ok(0, true); await pending[0];
  const replacement = f.sdk.request("x");
  for (let i = 1; i < 17; i++) f.ok(i, true);
  await Promise.all([...pending, replacement]); f.close();
});
test("long session exceeds 4096 requests without ID reuse or exhausting capacity", async () => {
  const f = fixture(), ids = new Set();
  for (let index = 0; index < 12000; index++) {
    const pending = f.sdk.request("lifecycle.getState");
    const requestId = f.sent[index].requestId;
    assert.match(requestId, /^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/);
    assert.equal(ids.has(requestId), false); ids.add(requestId);
    // Replayed old responses cannot settle new requests, even beyond the old ceiling.
    if (index > 4096) f.ok(0, { state: "closed" });
    f.ok(index, { state: "foreground" });
    assert.equal((await pending).state, "foreground");
    f.advance(600);
  }
  assert.equal(ids.size, 12000); f.close();
});
test("rolling frequency limit is retryable and reopens at exactly 60 seconds", async () => {
  const f = fixture();
  for (let index = 0; index < 120; index++) {
    const pending = f.sdk.request("lifecycle.getState"); f.ok(index, true); await pending;
  }
  await assert.rejects(f.sdk.request("x"), { code: "RATE_LIMITED", retryable: true });
  f.advance(59999);
  await assert.rejects(f.sdk.request("x"), { code: "RATE_LIMITED", retryable: true });
  assert.equal(f.sent.length, 120);
  f.advance(1);
  const resumed = f.sdk.request("lifecycle.getState"); f.ok(120, true); await resumed;
  assert.notEqual(f.sent[120].requestId, f.sent[0].requestId); f.close();
});
test("timed-out and cancelled requests release all 16 pending slots", async () => {
  const f = fixture(), controller = new AbortController();
  const cancelled = Array.from({ length: 16 }, () => f.sdk.request("x", {}, { signal: controller.signal }));
  const rejections = cancelled.map(pending => assert.rejects(pending, { code: "USER_CANCELLED" }));
  controller.abort(); await Promise.all(rejections);
  const timed = Array.from({ length: 16 }, () => f.sdk.request("x", {}, { timeoutMs: 5 }));
  await Promise.all(timed.map(pending => assert.rejects(pending, { code: "TIMEOUT" })));
  const resumed = Array.from({ length: 16 }, () => f.sdk.request("x"));
  for (let index = 32; index < 48; index++) f.ok(index, true);
  await Promise.all(resumed); f.close();
});
test("timeout ignores late response and does not send a cancellation or retry", async () => {
  const f = fixture(), p = f.sdk.request("saves.write", { slot: "game", value: 1 }, { timeoutMs: 5 });
  await assert.rejects(p, (error) => error.code === "TIMEOUT" && error.message.includes("may still complete"));
  f.ok(0, { saved: true }); assert.equal(f.sent.length, 1); f.close();
});
test("AbortSignal before send sends nothing; after send only stops local waiting", async () => {
  const f = fixture(), before = new AbortController(); before.abort();
  await assert.rejects(f.sdk.request("x", {}, { signal: before.signal }), { code: "USER_CANCELLED" });
  assert.equal(f.sent.length, 0);
  const after = new AbortController(), p = f.sdk.request("x", {}, { signal: after.signal }); after.abort();
  await assert.rejects(p, (error) => error.code === "USER_CANCELLED" && error.message.includes("may still complete"));
  assert.equal(f.sent.length, 1); f.close();
});
test("lifecycle supports unsubscribe, isolates handlers and ends pending work", async () => {
  const f = fixture(), states = [];
  f.sdk.onLifecycle(() => { throw new Error("application handler"); });
  const unsubscribe = f.sdk.onLifecycle((state) => states.push(state));
  f.deliver({ event: "lifecycle.stateChanged", state: "background" }); unsubscribe();
  f.deliver({ event: "lifecycle.stateChanged", state: "foreground" });
  assert.deepEqual(states, ["background"]);
  const p = f.sdk.request("x"); f.deliver({ event: "lifecycle.stateChanged", state: "closed" });
  await assert.rejects(p, { code: "SESSION_EXPIRED" });
  await assert.rejects(f.sdk.request("x"), { code: "SESSION_EXPIRED" });
});
test("invalid matching responses fail; unrelated messages cannot settle requests", async () => {
  const f = fixture(), p = f.sdk.request("x");
  f.deliver("not JSON"); f.deliver({ requestId: "unknown", ok: true, result: true });
  f.deliver({ requestId: f.sent[0].requestId, ok: false, error: {} });
  await assert.rejects(p, { code: "INVALID_RESPONSE" }); f.close();
});
test("transport exceptions clean up and allow subsequent requests", async () => {
  const f = fixture(); f.bridge.postMessage = () => { throw new Error("closed bridge"); };
  await assert.rejects(f.sdk.request("x"), { code: "TRANSPORT_ERROR" }); f.close();
});
test("pagehide ends the session and rejects pending requests", async () => {
  const f = fixture(), p = f.sdk.request("x"); f.close();
  await assert.rejects(p, { code: "SESSION_EXPIRED" });
});
test("schema files parse and sample manifest follows frozen core constraints", () => {
  const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, "../samples/element-pairs/manifest.json"), "utf8"));
  const schema = JSON.parse(fs.readFileSync(path.join(__dirname, "../sdk/manifest.schema.json"), "utf8"));
  const messages = JSON.parse(fs.readFileSync(path.join(__dirname, "../sdk/message.schema.json"), "utf8"));
  for (const key of schema.required) assert.ok(Object.prototype.hasOwnProperty.call(manifest, key));
  for (const key of Object.keys(manifest)) assert.ok(Object.prototype.hasOwnProperty.call(schema.properties, key));
  for (const key of ["appId", "name", "version", "entry"]) assert.match(manifest[key], new RegExp(schema.properties[key].pattern));
  for (const invalid of ["con.app", "cn.BAD", "cn..game", "cn." + "a".repeat(64)]) assert.equal(new RegExp(schema.properties.appId.pattern).test(invalid), false);
  for (const invalid of ["1.0", "01.0.0", "1.0.0-01"]) assert.equal(new RegExp(schema.properties.version.pattern).test(invalid), false);
  assert.equal(messages.$defs.capabilities.properties.limits.properties.maximumPendingRequests.const, 16);
  assert.equal(messages.$defs.capabilities.properties.limits.properties.maximumRememberedRequestIds.const, 2048);
  assert.equal(messages.$defs.capabilities.properties.limits.properties.deduplicationWindowMs.const, 600000);
  assert.ok(messages.$defs.permissionState.properties.state.enum.includes("revoked"));
});

test("T03 typed events dispatch only known exact minimal shapes and unsubscribe", () => {
  const f = fixture(), events = [];
  f.sdk.onEvent("appearance.changed", () => { throw new Error("owned handler"); });
  const stop = f.sdk.onEvent("appearance.changed", value => events.push(value.theme));
  f.deliver({ event: "appearance.changed", data: { theme: "dark", language: "zh-CN", scale: 1, reduceMotion: false } });
  f.deliver({ event: "appearance.changed", data: { theme: "dark", language: "zh-CN", scale: 1, reduceMotion: false, accessToken: "forbidden" } });
  f.deliver({ event: "appearance.changed", data: { theme: "dark", language: "zh-CN", scale: 999, reduceMotion: false } });
  stop(); f.deliver({ event: "appearance.changed", data: { theme: "light", language: "en-US", scale: 1, reduceMotion: true } });
  assert.deepEqual(events, ["dark"]); f.close();
});
test("T03 refuses arbitrary events and bounds live subscriptions while allowing reuse", () => {
  const f = fixture();
  assert.throws(() => f.sdk.onEvent("host.execute", () => {}), { code: "CAPABILITY_UNAVAILABLE" });
  const stops = Array.from({ length: 64 }, () => f.sdk.onEvent("permissions.changed", () => {}));
  assert.throws(() => f.sdk.onLifecycle(() => {}), { code: "SUBSCRIPTION_LIMIT" });
  stops[0](); const stop = f.sdk.onLifecycle(() => {}); stop();
  for (const release of stops) release();
  const reused = f.sdk.onEvent("identity.changed", () => {}); reused(); f.close();
});
test("T03 permission and identity events reject token-bearing or invalid payloads", () => {
  const f = fixture(), permissions = [], identities = [];
  f.sdk.onEvent("permissions.changed", value => permissions.push(value.state));
  f.sdk.onEvent("identity.changed", value => identities.push(value.state));
  f.deliver({ event: "permissions.changed", data: { name: "identity.profile", state: "revoked" } });
  f.deliver({ event: "permissions.changed", data: { name: "host.execute", state: "granted" } });
  f.deliver({ event: "identity.changed", data: { state: "signed_in" } });
  f.deliver({ event: "identity.changed", data: { state: "signed_in", refreshToken: "never" } });
  f.deliver({ event: "identity.changed", data: { state: "fake_login_success" } });
  assert.deepEqual(permissions, ["revoked"]); assert.deepEqual(identities, ["signed_in"]); f.close();
});
test("T03 shortcuts and internal links deliver bounded declared-action data, not commands", () => {
  const f = fixture(), actions = [];
  f.sdk.onEvent("shortcuts.invoked", value => actions.push(value.action));
  f.sdk.onEvent("links.opened", value => actions.push(value.action));
  f.deliver({ event: "shortcuts.invoked", data: { id: "resume", action: "resume" } });
  f.deliver({ event: "links.opened", data: { action: "resume", arguments: { slot: "game" } } });
  f.deliver({ event: "links.opened", data: { action: "file:///private", arguments: {} } });
  f.deliver({ event: "links.opened", data: { action: "resume", arguments: { x: "a".repeat(257) } } });
  f.deliver({ event: "links.opened", data: { action: "resume", arguments: {}, appId: "victim.game" } });
  assert.deepEqual(actions, ["resume", "resume"]); f.close();
});
test("T03 terminal lifecycle event clears subscriptions and pending requests", async () => {
  const f = fixture(), events = [];
  f.sdk.onEvent("lifecycle.changed", value => events.push(value.state));
  const pending = f.sdk.request("identity.getProfile");
  f.deliver({ event: "lifecycle.changed", data: { state: "closed", blocksMaintenance: false } });
  await assert.rejects(pending, { code: "SESSION_EXPIRED" });
  f.deliver({ event: "lifecycle.changed", data: { state: "foreground", blocksMaintenance: true } });
  assert.deepEqual(events, ["closed"]);
});
test("T03 requests remain host-bound and unsupported server sessions never fake success", async () => {
  const f = fixture();
  const pending = f.sdk.request("identity.requestProfile");
  assert.deepEqual(Object.keys(f.sent[0]).sort(), ["method", "params", "protocolVersion", "requestId"]);
  f.ok(0, { appScopedUserId: "au1_test", displayName: "Explicit test profile", avatarUrl: null, isCached: true });
  assert.equal((await pending).isCached, true);
  const unsupported = f.sdk.request("identity.beginAppSession");
  f.deliver({ requestId: f.sent[1].requestId, ok: false, error: { code: "CAPABILITY_UNAVAILABLE", message: "Independent server OIDC is required.", retryable: false, correlationId: "test" } });
  await assert.rejects(unsupported, { code: "CAPABILITY_UNAVAILABLE" }); f.close();
});
test("T03 schema and TypeScript cover the same method and event names", () => {
  const messages = JSON.parse(fs.readFileSync(path.join(__dirname, "../sdk/message.schema.json"), "utf8"));
  const types = fs.readFileSync(path.join(__dirname, "../sdk/autumn-sdk.d.ts"), "utf8");
  assert.equal(new Set(messages.oneOf.map(item => item.$ref)).size, messages.oneOf.length);
  for (const conditional of messages.$defs.request.allOf) {
    const method = conditional.if.properties.method.const;
    assert.ok(types.includes('"' + method + '":'), method);
    assert.ok(messages.$defs[conditional.then.properties.params.$ref.split("/").at(-1)], method + " schema exists");
  }
  for (const event of messages.$defs.sdkEvent.oneOf) {
    const name = event.properties.event.const; assert.ok(types.includes('"' + name + '":'), name);
  }
  assert.equal(messages.$defs.permissionName.enum.length, 9);
  assert.equal(messages.$defs.profile.additionalProperties, false);
  assert.deepEqual(messages.$defs.profile.required, ["appScopedUserId", "displayName", "avatarUrl", "isCached"]);
});
