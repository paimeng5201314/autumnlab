(function () {
  "use strict";
  let active = null;
  function clear() { sample.text("profile", "资料已清空；需要时重新点击读取。"); }
  async function profile(method) {
    if (active) return;
    const controller = new AbortController(); active = controller;
    try {
      const value = await sample.request(method, {}, { timeoutMs: 120000, signal: controller.signal });
      if (!sample.alive) return;
      sample.text("profile", "昵称：" + value.displayName + "\n应用范围标识：" + value.appScopedUserId + "\n头像：" + (value.avatarUrl ? "存在（本样例不联网加载）" : "无") + "\n离线缓存：" + value.isCached);
      sample.status("已收到当前账号、当前应用授权的最小资料。");
    } finally { if (active === controller) active = null; }
  }
  sample.bind("query", async () => { const value = await sample.request("permissions.query", { name: "identity.profile" }); sample.text("permission-state", value.name + "：" + value.state); });
  sample.bind("request-profile", () => profile("identity.requestProfile"));
  sample.bind("read-profile", () => profile("identity.getProfile"));
  sample.bind("stop-waiting", async () => { active?.abort(); sample.status("仅停止本页等待。宿主已提交工作可能完成；不会报告授权失败或成功。"); });
  sample.on("permissions.changed", value => { if (value.name === "identity.profile") { sample.text("permission-state", value.state); if (value.state !== "granted") { active?.abort(); clear(); } } });
  sample.on("identity.changed", value => { active?.abort(); clear(); sample.status("身份状态发生变化：" + value.state + "。请自主重新读取。"); });
  sample.onLifecycle(state => { if (["closing", "closed", "crashed"].includes(state)) { active?.abort(); clear(); } });
  window.addEventListener("pagehide", () => { active?.abort(); clear(); }, { once: true });
  sample.start();
})();
