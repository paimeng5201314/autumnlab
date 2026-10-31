"use strict";

// Executes the shipped controller and SDK with an in-memory DOM/renderer and
// host transport. This verifies application behavior, not native isolation.
const test = require("node:test"), assert = require("node:assert/strict");
const fs = require("node:fs"), path = require("node:path"), vm = require("node:vm");
const { TextEncoder } = require("node:util");
const project = path.resolve(__dirname, "../samples/chemleap");

class Events {
  constructor() { this.listeners = new Map(); }
  addEventListener(name, callback) {
    if (!this.listeners.has(name)) this.listeners.set(name, new Set());
    this.listeners.get(name).add(callback);
  }
  removeEventListener(name, callback) { this.listeners.get(name)?.delete(callback); }
  emit(name, event = {}) { return Promise.all([...(this.listeners.get(name) || [])].map(callback => callback(event))); }
}
class Element extends Events {
  constructor(name) {
    super(); this.id = name; this.children = []; this.dataset = {}; this.style = {};
    this.attributes = new Map(); this.disabled = false; this.hidden = false;
    this.textContent = ""; this.classList = { toggle() {} };
  }
  setAttribute(name, value) { this.attributes.set(name, String(value)); }
  getAttribute(name) { return this.attributes.get(name) ?? null; }
  append(...nodes) { this.children.push(...nodes); }
  replaceChildren(...nodes) { this.children = nodes; }
  scrollIntoView() {}
  closest() { return null; }
  setPointerCapture() {}
}
function fixture({ host = true } = {}) {
  const window = new Events(), document = new Events(), elements = new Map();
  document.getElementById = id => {
    if (!elements.has(id)) elements.set(id, new Element(id));
    return elements.get(id);
  };
  document.createElement = name => new Element(name);
  document.createTextNode = value => ({ textContent: value }); document.hidden = false;
  const bridge = new Events(), requests = [], frames = new Map(), writes = [];
  bridge.postMessage = message => requests.push(message);
  window.chrome = host ? { webview: bridge } : undefined;
  window.matchMedia = () => ({ matches: false });
  let now = 100, frameId = 0, scene, disposed = 0;
  const context = vm.createContext({ window, document, AbortController, TextEncoder, setTimeout, clearTimeout,
    performance: { now: () => now }, crypto: { randomUUID: () => "5ca02587-0443-4054-af73-763ab0872cc4", getRandomValues: array => array.fill(7) },
    localStorage: { getItem: () => null, setItem: (...args) => writes.push(args) },
    requestAnimationFrame: callback => { frames.set(++frameId, callback); return frameId; }, cancelAnimationFrame: id => frames.delete(id) });
  window.setTimeout = setTimeout; window.clearTimeout = clearTimeout;
  window.crypto = context.crypto; window.performance = context.performance;
  for (const file of ["chemistry.js", "model.js", "autumn-sdk.js"]) {
    vm.runInContext(fs.readFileSync(path.join(project, file), "utf8"), context, { filename: file });
  }
  // The production model's UMD globals target the VM global; the renderer and
  // controller use window exactly as in the packaged document.
  window.ChemLeapChemistry = context.ChemLeapChemistry;
  window.ChemLeapModel = context.ChemLeapModel;
  window.ChemLeapRenderer = { create: () => ({ setScene: value => { scene = value; }, render() {}, resize() {},
    projectPlatforms: () => [], dispose: () => { disposed++; } }) };
  vm.runInContext(fs.readFileSync(path.join(project, "game.js"), "utf8"), context, { filename: "game.js" });
  const flush = () => new Promise(resolve => setImmediate(resolve));
  const reply = async (method, result) => {
    const request = requests.find(item => item.method === method && !item.replied);
    assert.ok(request, "Expected actual SDK request: " + method); request.replied = true;
    await bridge.emit("message", { data: { requestId: request.requestId, ok: true, result } }); await flush();
  };
  const lifecycle = state => bridge.emit("message", { data: { event: "lifecycle.changed", data: { state,
    blocksMaintenance: !["closed", "crashed"].includes(state) } } });
  return { get: document.getElementById, requests, writes, frames, window, bridge, reply, lifecycle, flush,
    get scene() { return scene; }, get disposed() { return disposed; },
    async click(id) { const element = document.getElementById(id); if (!element.disabled) await element.emit("click", { detail: 1 }); },
    tick(milliseconds) { now += milliseconds; const callbacks = [...frames.values()]; frames.clear(); callbacks.forEach(callback => callback(now)); },
    async close() { await window.emit("pagehide"); await flush(); }
  };
}
async function withFixture(body, options) { const app = fixture(options); try { await body(app); } finally { await app.close(); } }
async function foreground(app) { await app.reply("lifecycle.getState", { state: "foreground", blocksMaintenance: true }); }
async function start(app) { await foreground(app); await app.click("start-button"); }

test("host waits for confirmed foreground before accepting gameplay or save actions", () => withFixture(async app => {
  assert.equal(app.get("start-button").disabled, true);
  assert.equal(app.get("load-button").disabled, true);
  assert.equal(app.frames.size, 0);
  await app.click("start-button"); await app.click("load-button");
  assert.deepEqual(app.requests.map(request => request.method), ["lifecycle.getState"]);
  await foreground(app);
  assert.equal(app.get("start-button").disabled, false); await app.click("start-button");
  assert.equal(app.get("platform-options").children.filter(button => !button.disabled).length, 3);
}));

test("current SDK lifecycle events suspend and resume one unfinished jump", () => withFixture(async app => {
  await start(app);
  const index = app.scene.state.platforms.findIndex(platform =>
    app.window.ChemLeapChemistry.findReaction(app.scene.state.current, platform.substance));
  await app.get("platform-options").children[index].emit("click"); app.tick(16); app.tick(100);
  assert.equal(app.scene.state.phase, "jumping");
  await app.lifecycle("suspended");
  assert.equal(app.get("pause-overlay").hidden, false); assert.equal(app.frames.size, 0);
  app.tick(5000); assert.equal(app.get("score").textContent, 0);
  await app.lifecycle("foreground");
  for (let index = 0; index < 22; index++) app.tick(50);
  assert.equal(app.get("score").textContent, 10); assert.equal(app.scene.state.round, 2);
}));

test("late initial lifecycle snapshot cannot overwrite a newer host event", () => withFixture(async app => {
  await app.lifecycle("foreground");
  await app.reply("lifecycle.getState", { state: "background", blocksMaintenance: true });
  await app.click("start-button");
  assert.equal(app.get("platform-options").children.filter(button => !button.disabled).length, 3);
  assert.ok(app.frames.size > 0);
}));

test("closing is irreversible across both lifecycle channels and disables actions", () => withFixture(async app => {
  await start(app); await app.lifecycle("closing");
  assert.equal(app.disposed, 1); assert.equal(app.frames.size, 0);
  await app.lifecycle("foreground");
  await app.bridge.emit("message", { data: { event: "lifecycle.stateChanged", state: "foreground" } });
  for (const name of ["start-button", "jump-button", "restart-button", "again-button", "save-button", "load-button"])
    assert.equal(app.get(name).disabled, true, name);
  assert.equal(app.get("platform-options").children.filter(button => !button.disabled).length, 0);
  const count = app.requests.length; await app.click("load-button");
  assert.equal(app.requests.length, count); assert.equal(app.frames.size, 0);
}));

test("closing while permission reply is pending cannot submit a new save", () => withFixture(async app => {
  await start(app);
  const save = app.click("save-button"); await app.flush();
  assert.ok(app.requests.some(request => request.method === "permissions.query"));
  await app.lifecycle("closing");
  await app.reply("permissions.query", { state: "granted" });
  assert.equal(app.requests.some(request => request.method === "saves.write"), false);
  await save;
  assert.equal(app.writes.length, 0);
}));

test("background transition during permission prompt does not submit a new save", () => withFixture(async app => {
  await start(app);
  const save = app.click("save-button"); await app.flush();
  await app.lifecycle("background");
  await app.reply("permissions.query", { state: "granted" });
  assert.equal(app.requests.some(request => request.method === "saves.write"), false);
  await save;
  assert.equal(app.writes.length, 0);
  await app.lifecycle("foreground"); assert.equal(app.get("save-button").disabled, false);
}));

test("a denied host save never falls back to browser storage and keeps gameplay", () => withFixture(async app => {
  await start(app);
  const save = app.click("save-button"); await app.flush();
  await app.reply("permissions.query", { state: "denied" }); await save;
  assert.equal(app.requests.some(request => request.method === "saves.write"), false);
  assert.equal(app.writes.length, 0); assert.match(app.get("save-status").textContent, /未允许/);
  assert.equal(app.get("platform-options").children.filter(button => !button.disabled).length, 3);
}));

test("ordinary browser play remains available without a host lifecycle", () => withFixture(async app => {
  assert.equal(app.get("start-button").disabled, false); await app.click("start-button");
  assert.equal(app.get("platform-options").children.filter(button => !button.disabled).length, 3);
  assert.deepEqual(app.requests, []); await app.click("save-button"); assert.equal(app.writes.length, 1);
}, { host: false }));
