/* Shared sample shell. Actual SDK only; no mock account, persistence or network fallback. 制作人：派蒙 */
(function () {
  "use strict";
  const sdk = window.autumn, subscriptions = [], end = new AbortController(), running = new Set();
  const lifecycleObservers = new Set();
  let alive = true, state = "starting", revision = 0, capabilities = null;
  const byId = id => document.getElementById(id);
  function text(id, value) { const element = byId(id); if (element) element.textContent = String(value); }
  function status(value, error = false) { text("status", value); if (byId("status")) byId("status").className = error ? "error" : ""; }
  function dispose() {
    if (!alive) return; alive = false; end.abort();
    for (const stop of subscriptions.splice(0)) stop();
    lifecycleObservers.clear();
  }
  function lifecycle(next) {
    state = next; revision++; text("lifecycle", "真实实例状态：" + next);
    for (const button of document.querySelectorAll("button[data-host]")) button.disabled = next !== "foreground";
    for (const listener of [...lifecycleObservers]) listener(next);
    if (["closing", "closed", "crashed"].includes(next)) dispose();
  }
  function request(method, params = {}, options = {}) {
    if (!alive) return Promise.reject(Object.assign(new Error(), { code: "SESSION_EXPIRED" }));
    if (!sdk) return Promise.reject(Object.assign(new Error(), { code: "HOST_UNAVAILABLE" }));
    return sdk.request(method, params, { signal: end.signal, ...options });
  }
  function on(name, listener) { const stop = sdk.onEvent(name, listener); subscriptions.push(stop); return stop; }
  function bind(id, action) {
    byId(id).addEventListener("click", async () => {
      if (!alive || running.has(id)) return;
      running.add(id);
      try { await action(); }
      catch (error) { if (alive) status("操作未报告成功：" + (error.code || "SAMPLE_ERROR") + (["TIMEOUT", "USER_CANCELLED"].includes(error.code) ? "。仅停止等待，已提交工作可能完成，请读回核对。" : ""), true); }
      finally { running.delete(id); }
    });
  }
  function permit(id, name) { bind(id, async () => { const result = await request("permissions.request", { name }, { timeoutMs: 120000 }); status(name + "：" + result.state); }); }
  const media = window.matchMedia("(prefers-color-scheme: dark)");
  let appearance = null, appearanceRevision = 0;
  function paint() {
    if (!appearance) return;
    document.documentElement.dataset.theme = appearance.theme === "system" ? (media.matches ? "dark" : "light") : appearance.theme;
    document.documentElement.dataset.reduceMotion = String(appearance.reduceMotion);
    document.documentElement.lang = appearance.language;
    text("appearance", "外观：" + appearance.theme + " · " + appearance.language + " · 缩放 " + appearance.scale + " · 减少动态 " + appearance.reduceMotion);
  }
  media.addEventListener("change", paint); subscriptions.push(() => media.removeEventListener("change", paint));
  window.addEventListener("pagehide", dispose, { once: true });
  window.sample = Object.freeze({ byId, text, status, request, on, bind, permit,
    onLifecycle(listener) { lifecycleObservers.add(listener); return () => lifecycleObservers.delete(listener); },
    get state() { return state; }, get capabilities() { return capabilities; }, get alive() { return alive; },
    async start() {
      if (!sdk) { status("HOST_UNAVAILABLE：请在 AutumnOS 内部打开已校验的 .autumn。", true); return; }
      subscriptions.push(sdk.onLifecycle(lifecycle));
      on("appearance.changed", value => { appearanceRevision++; appearance = value; paint(); });
      try {
        capabilities = await request("platform.getCapabilities");
        text("capabilities", "SDK " + sdk.version + " / 协议 " + capabilities.protocolVersion + " / " + capabilities.accountMode + " / " + capabilities.capabilities.join(", "));
        const before = revision, current = await request("lifecycle.getState");
        if (before === revision) lifecycle(current.state);
        const beforeAppearance = appearanceRevision;
        const value = await request("appearance.get");
        if (beforeAppearance === appearanceRevision) { appearance = value; paint(); }
        if (alive) status("已连接真实宿主。权限仅在你点击申请时请求。");
      } catch (error) { status("连接结果：" + (error.code || "SAMPLE_ERROR"), true); }
    }
  });
})();
