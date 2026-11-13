"use strict";
(() => {
  const total = 1024;
  const perLoadTimeoutMs = 15000;
  const runTimeoutMs = 600000;
  const start = document.getElementById("start");
  const status = document.getElementById("status");
  const count = document.getElementById("count");
  const progress = document.getElementById("progress");
  const detail = document.getElementById("detail");
  const result = document.getElementById("result");
  const state = {
    status: "not_run", executions: 0, loadEvents: 0, next: 0,
    activeScript: null, cancelPending: null, failure: null,
    startedAt: null, finishedAt: null
  };
  Object.defineProperty(window, "__autumnResourceRepeat", { value: state });
  let runTimer = null;

  function render() {
    count.textContent = String(state.executions);
    progress.value = state.executions;
    status.dataset.outcome = state.status;
    result.textContent = JSON.stringify({
      fixture: "native-resource-repeat-v1", status: state.status,
      target: total, script_executions: state.executions,
      script_load_events: state.loadEvents, same_package_url: "repeat.js",
      started_at: state.startedAt, finished_at: state.finishedAt,
      failure: state.failure, native_serve_count: "requires_host_evidence",
      full_isolation: false
    }, null, 2);
  }

  function fail(code) {
    if (state.status !== "running") return;
    state.status = "failed";
    state.failure = code;
    state.finishedAt = new Date().toISOString();
    clearTimeout(runTimer);
    state.cancelPending?.();
    status.textContent = "失败 · " + code;
    detail.textContent = "本轮失败已保留。请结束应用后使用新实例重试，不能将超时或未执行计为通过。";
    render();
  }

  function loadOne(expected) {
    return new Promise((resolve, reject) => {
      const script = document.createElement("script");
      script.async = true;
      script.src = "repeat.js";
      script.dataset.expected = String(expected);
      state.next = expected;
      state.activeScript = script;
      let settled = false;
      let timer = null;
      function finish(error) {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        script.onload = null;
        script.onerror = null;
        script.remove();
        if (state.activeScript === script) state.activeScript = null;
        state.cancelPending = null;
        if (error) reject(new Error(error)); else resolve();
      }
      state.cancelPending = () => finish("RUN_ALREADY_FAILED");
      script.onload = () => {
        if (state.status !== "running") { finish("LOAD_AFTER_FAILURE"); return; }
        state.loadEvents++;
        if (state.executions !== expected || state.loadEvents !== expected ||
            script.dataset.executed !== "native-resource-repeat-v1") {
          finish("EXECUTION_OR_LOAD_COUNT_MISMATCH_" + expected); return;
        }
        render();
        finish(null);
      };
      script.onerror = () => finish("SCRIPT_LOAD_ERROR_" + expected);
      timer = setTimeout(() => finish("SCRIPT_TIMEOUT_" + expected), perLoadTimeoutMs);
      try { document.head.appendChild(script); }
      catch { finish("SCRIPT_APPEND_ERROR_" + expected); }
    });
  }

  window.addEventListener("error", () => fail("SCRIPT_RUNTIME_ERROR"));
  window.addEventListener("unhandledrejection", () => fail("UNHANDLED_REJECTION"));
  start.addEventListener("click", async () => {
    if (state.status !== "not_run") return;
    state.status = "running";
    state.startedAt = new Date().toISOString();
    start.disabled = true;
    start.textContent = "本轮已开始";
    status.textContent = "执行中 · 请保持应用前台";
    runTimer = setTimeout(() => fail("RUN_TIMEOUT"), runTimeoutMs);
    render();
    try {
      for (let expected = 1; expected <= total; expected++) {
        if (state.status !== "running") return;
        await loadOne(expected);
      }
      if (state.status !== "running") return;
      if (state.executions !== total || state.loadEvents !== total) { fail("FINAL_COUNT_MISMATCH"); return; }
      clearTimeout(runTimer);
      state.status = "passed";
      state.finishedAt = new Date().toISOString();
      status.textContent = "完成 · 1024 次脚本执行与加载逐次一致";
      detail.textContent = "脚本回归完成。仍需记录当前构建身份、宿主资源处理基线和正常关闭证据。";
      render();
    } catch (error) { fail(error instanceof Error ? error.message : "UNKNOWN_LOAD_FAILURE"); }
  });
  start.disabled = false;
  start.textContent = "开始 1024 次回归";
  status.textContent = "已就绪 · 点击开始";
  render();
})();
