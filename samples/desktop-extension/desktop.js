(function () {
  "use strict";
  let previous = null;
  for (const name of ["notifications", "shortcuts", "widgets", "links"]) sample.permit("permit-" + name, name);
  async function showNotification(repeat) {
    if (!repeat) previous = "note-" + crypto.randomUUID();
    if (!previous) { sample.status("尚未发送通知；没有可重复的 ID。"); return; }
    const value = await sample.request("notifications.show", { id: previous, title: "桌面扩展样例", body: sample.byId("note").value, action: "focus" });
    sample.status(value.shown ? "通知已加入宿主通知中心。" : "通知未显示：" + value.reason);
  }
  sample.bind("notify", () => showNotification(false)); sample.bind("repeat-notify", () => showNotification(true));
  for (const [id, count] of [["badge", 1], ["clear-badge", 0]]) sample.bind(id, async () => { const value = await sample.request("notifications.setBadge", { count }); sample.status("宿主实际角标：" + value.count); });
  for (const [id, ids] of [["register", ["focus"]], ["unregister", []]]) sample.bind(id, async () => { const value = await sample.request("shortcuts.register", { ids }); sample.status("当前已登记操作：" + value.shortcuts.length); });
  sample.bind("widget", async () => { const value = await sample.request("widgets.update", { id: "note", lines: [sample.byId("note").value] }); sample.status(value.updated ? "纯文本小组件已更新。" : "未报告成功。"); });
  sample.bind("link", async () => { const value = await sample.request("links.openInternal", { action: "focus", arguments: { origin: "explicit-button" } }); sample.status("动作投递：" + value.delivered); });
  function focus(value) { sample.text("events", "收到真实动作：" + value.action); if (value.action === "focus") sample.byId("note").focus(); }
  sample.on("shortcuts.invoked", focus); sample.on("links.opened", focus);
  sample.on("permissions.changed", value => { sample.text("events", "权限变化：" + value.name + " / " + value.state); if (value.name === "notifications" && value.state !== "granted") previous = null; });
  sample.start();
})();
