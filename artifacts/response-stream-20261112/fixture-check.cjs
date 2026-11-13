"use strict";
// Explicit DOM/loader control-flow fixtures, not actual WebView2/Serve evidence.
const fs = require("node:fs"), path = require("node:path"), vm = require("node:vm"), assert = require("node:assert/strict");
const root = path.resolve(__dirname, "../../tests/fixtures/native-resource-repeat");
const loader = fs.readFileSync(path.join(root, "loader.js"), "utf8");
const repeat = fs.readFileSync(path.join(root, "repeat.js"), "utf8");
async function simulate(fault) {
  const nodes = new Map(), handlers = new Map(), timers = new Map();
  let timerId = 0, appends = 0, context;
  function node() { return { dataset: {}, textContent: "", disabled: true, value: 0,
    addEventListener(name, fn) { this[name] = fn; }, remove() {} }; }
  const document = { currentScript: null, getElementById(id) { if (!nodes.has(id)) nodes.set(id, node()); return nodes.get(id); },
    createElement(name) { assert.equal(name, "script"); return node(); },
    head: { appendChild(script) {
      appends++; assert.equal(script.src, "repeat.js");
      queueMicrotask(() => {
        const current = Number(script.dataset.expected);
        if (current === 513 && fault === "load-error") { script.onerror(); return; }
        if (current === 513 && (fault === "timeout" || fault === "run-timeout")) {
          const delay = fault === "timeout" ? 15000 : 600000;
          [...timers.values()].find(timer => timer.ms === delay).fn(); return;
        }
        document.currentScript = script;
        try {
          if (!(current === 513 && fault === "missing-execution")) vm.runInContext(repeat, context);
          if (current === 513 && fault === "duplicate-execution") vm.runInContext(repeat, context);
        } catch { handlers.get("error")?.(); }
        document.currentScript = null;
        script.onload?.();
      });
    } } };
  const window = { addEventListener(name, fn) { handlers.set(name, fn); } };
  context = vm.createContext({ window, document, Date, Promise, Error, Object, String, JSON,
    setTimeout(fn, ms) { const id = ++timerId; timers.set(id, { fn, ms }); return id; }, clearTimeout(id) { timers.delete(id); } });
  vm.runInContext(loader, context);
  assert.equal(nodes.get("status").dataset.outcome, "not_run");
  await nodes.get("start").click();
  const report = JSON.parse(nodes.get("result").textContent);
  assert.equal(report.full_isolation, false);
  assert.equal(report.native_serve_count, "requires_host_evidence");
  if (!fault) {
    assert.equal(report.status, "passed"); assert.equal(report.script_executions, 1024);
    assert.equal(report.script_load_events, 1024); assert.equal(appends, 1024);
  } else { assert.equal(report.status, "failed"); assert.ok(report.failure); assert.ok(report.script_executions < 1024); }
  const before = appends;
  await nodes.get("start").click();
  assert.equal(appends, before, "completed/failed instance must not reset its evidence");
  return { case: fault || "success", status: "passed", observed_status: report.status, executions: report.script_executions, load_events: report.script_load_events, failure: report.failure };
}
(async () => {
  const results = [];
  for (const fault of [null, "missing-execution", "duplicate-execution", "load-error", "timeout", "run-timeout"]) results.push(await simulate(fault));
  const report = { kind: "explicit_VM_and_DOM_fixture_control_flow_not_native_browser", status: "passed", full_isolation: false, total: results.length, results };
  fs.writeFileSync(path.join(__dirname, "fixture-check.json"), JSON.stringify(report, null, 2));
  console.log(JSON.stringify(report, null, 2));
})().catch(error => { console.error(error); process.exitCode = 1; });
