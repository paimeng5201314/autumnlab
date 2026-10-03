/* Real SDK examples for the controlled element-pairs sample · 制作人：派蒙. */
(function () {
  "use strict";
  const byId = (id) => document.getElementById(id), sdk = window.autumn;
  const controls = Array.from(document.querySelectorAll("#t03-features button"));
  const subscriptions = [], pending = new Set();
  const knownCapabilities = new Set();
  const systemTheme = window.matchMedia("(prefers-color-scheme: dark)");
  let disposed = false, lifecycle = "starting", appearance = null, readFile = null, saveFile = null, notificationId = null;
  let snapshotRevision = 0;
  const permissionOutput = {
    "identity.profile": "t03-profile-result", notifications: "t03-notifications-result", widgets: "t03-desktop-result",
    shortcuts: "t03-desktop-result", links: "t03-desktop-result", storage: "t03-storage-result",
    "files.open": "t03-files-result", "files.save": "t03-files-result"
  };
  function output(id, text, isError = false) {
    if (disposed) return;
    byId(id).textContent = text; byId(id).dataset.error = String(isError);
  }
  function failure(code) { return Object.assign(new Error(code), { code }); }
  function safeError(error) {
    const code = typeof error?.code === "string" && /^[A-Z0-9_]{1,80}$/.test(error.code) ? error.code : "INVALID_RESPONSE";
    const explanations = {
      AUTH_REQUIRED: "尚未登录，请在 AutumnOS 账号应用中自主选择登录；配对游戏仍可继续。",
      PERMISSION_DENIED: "权限被拒绝；不会再次自动弹出授权。",
      PERMISSION_REVOKED: "权限已撤销；需在系统设置中决定是否重新允许。",
      PERMISSION_NOT_DECLARED: "当前包未声明此权限，宿主已拒绝。",
      USER_GESTURE_REQUIRED: "请重新点击当前操作按钮；授权和打开选择器必须分别点击。",
      USER_CANCELLED: "你已取消；不报告操作成功。",
      FILE_TOO_LARGE: "文件超出此样例的 4 KiB 文本读取限制或宿主限制。",
      SESSION_EXPIRED: "此游戏会话已失效，请从桌面重新打开。",
      HOST_UNAVAILABLE: "请在 AutumnOS 内部打开应用。",
      CAPABILITY_UNAVAILABLE: "当前宿主未提供此能力。",
      FILE_CHANGED_SINCE_PICK: "所选文件已经改变，请重新选择导出位置。",
      TIMEOUT: "等待超时；已提交的写入可能已经完成，请先读取核对。"
    };
    return code + " · " + (explanations[code] || "操作没有报告成功；现有游戏进度保留。");
  }
  function refreshControls() {
    for (const button of controls) {
      button.disabled = disposed || lifecycle !== "foreground" || button.dataset.busy === "true"
        || !knownCapabilities.has(button.dataset.capability);
    }
  }
  async function request(method, params = {}) {
    if (disposed || !sdk) throw failure("HOST_UNAVAILABLE");
    const cancellation = new AbortController(); pending.add(cancellation);
    try { return await sdk.request(method, params, { signal: cancellation.signal, timeoutMs: 120000 }); }
    finally { pending.delete(cancellation); }
  }
  function action(id, resultId, run) {
    byId(id).addEventListener("click", async () => {
      if (disposed || lifecycle !== "foreground" || byId(id).disabled) return;
      byId(id).dataset.busy = "true"; refreshControls();
      try { await run(); }
      catch (error) { output(resultId, safeError(error), true); }
      finally { byId(id).dataset.busy = "false"; refreshControls(); }
    });
  }
  function pairs() {
    const value = Number.parseInt(byId("pairs-count").textContent, 10);
    return Number.isInteger(value) && value >= 0 && value <= 6 ? value : 0;
  }
  function note() { return byId("t03-note").value.slice(0, 240); }
  function encode(text) {
    const bytes = new TextEncoder().encode(text);
    if (bytes.length > 4096) throw failure("FILE_TOO_LARGE");
    return btoa(String.fromCharCode(...bytes));
  }
  function decode(value) {
    if (typeof value !== "string" || value.length > 5464 || !/^[A-Za-z0-9+/]*={0,2}$/.test(value)) throw failure("FILE_TOO_LARGE");
    const binary = atob(value);
    if (binary.length > 4096) throw failure("FILE_TOO_LARGE");
    return new TextDecoder("utf-8", { fatal: true }).decode(Uint8Array.from(binary, c => c.charCodeAt(0)));
  }
  function validateCapability(value) {
    if (!value || typeof value.handle !== "string" || !/^[a-f0-9]{64}$/.test(value.handle)
      || typeof value.name !== "string" || !Number.isSafeInteger(value.bytes) || value.bytes < 0) throw failure("INVALID_RESPONSE");
    return value;
  }
  function showProfile(profile) {
    if (!profile || typeof profile.appScopedUserId !== "string" || typeof profile.displayName !== "string" || typeof profile.isCached !== "boolean") throw failure("INVALID_RESPONSE");
    output("t03-profile-result", "昵称：" + profile.displayName + "\n本应用范围标识：" + profile.appScopedUserId
      + "\n来源：" + (profile.isCached ? "离线缓存资料" : "已验证账号的授权资料")
      + "\n头像：" + (profile.avatarUrl ? "宿主提供了地址；此离线样例不请求远程图片。" : "未提供。"));
  }
  for (const button of controls.filter(item => item.dataset.permission)) {
    action(button.id, permissionOutput[button.dataset.permission], async () => {
      const result = await request("permissions.request", { name: button.dataset.permission });
      if (!result || result.name !== button.dataset.permission || !["prompt", "granted", "denied", "revoked"].includes(result.state)) throw failure("INVALID_RESPONSE");
      output(permissionOutput[result.name], result.name + "：" + result.state + "。请再点击相应操作按钮。", result.state !== "granted");
    });
  }
  action("t03-profile-request", "t03-profile-result", async () => showProfile(await request("identity.requestProfile")));
  action("t03-profile-read", "t03-profile-result", async () => showProfile(await request("identity.getProfile")));
  async function notify(repeat) {
    if (!repeat) notificationId = "pairs-" + crypto.randomUUID();
    if (!notificationId) throw failure("INVALID_PARAMS");
    const result = await request("notifications.show", { id: notificationId, title: "元素配对", body: "当前已配对 " + pairs() + " / 6 组。", action: "resume" });
    if (result.shown === true) output("t03-notifications-result", "通知已交给桌面通知中心。点击通知只触发本应用声明的回到配对动作。");
    else if (result.shown === false && ["muted", "duplicate"].includes(result.reason)) output("t03-notifications-result", result.reason === "muted" ? "应用已静音，本次通知未显示。" : "重复通知已被去重，本次未再次显示。");
    else throw failure("INVALID_RESPONSE");
  }
  action("t03-notify", "t03-notifications-result", () => notify(false));
  action("t03-notify-duplicate", "t03-notifications-result", () => notify(true));
  async function badge(count) {
    const result = await request("notifications.setBadge", { count });
    if (!Number.isInteger(result.count)) throw failure("INVALID_RESPONSE");
    output("t03-notifications-result", "宿主当前角标：" + result.count + (result.count !== count ? "（静音等宿主规则已生效）" : ""));
  }
  action("t03-badge", "t03-notifications-result", () => badge(pairs()));
  action("t03-badge-clear", "t03-notifications-result", () => badge(0));
  action("t03-widget", "t03-desktop-result", async () => {
    const result = await request("widgets.update", { id: "progress", lines: ["已配对 " + pairs() + " / 6 组", "翻牌次数 " + byId("moves-count").textContent] });
    if (result.updated !== true || result.id !== "progress") throw failure("INVALID_RESPONSE");
    output("t03-desktop-result", "已更新真实桌面小组件。返回桌面查看；不会结束游戏。");
  });
  async function shortcuts(ids) {
    const result = await request("shortcuts.register", { ids });
    if (!Array.isArray(result.shortcuts) || result.shortcuts.length !== ids.length) throw failure("INVALID_RESPONSE");
    output("t03-desktop-result", ids.length ? "已登记「回到配对」。返回桌面后长按或右键图标查看；原继续／结束操作保留。" : "已撤下本应用快捷操作。");
  }
  action("t03-shortcut", "t03-desktop-result", () => shortcuts(["resume"]));
  action("t03-shortcut-clear", "t03-desktop-result", () => shortcuts([]));
  action("t03-link", "t03-desktop-result", async () => {
    const result = await request("links.openInternal", { action: "resume", arguments: { origin: "sample-button" } });
    if (result.delivered !== true || result.action !== "resume") throw failure("INVALID_RESPONSE");
    output("t03-desktop-result", "宿主已投递内部 resume 动作；未调用 Windows 外部协议或新建游戏。");
  });
  action("t03-preference-save", "t03-storage-result", async () => {
    const result = await request("preferences.set", { key: "field_note", value: { formatVersion: 1, text: note() } });
    if (result.saved !== true) throw failure("INVALID_RESPONSE");
    output("t03-storage-result", "笔记设置已保存到当前账号和应用来源的私有空间。");
  });
  action("t03-preference-read", "t03-storage-result", async () => {
    const result = await request("preferences.get", { key: "field_note" });
    if (result.exists === false) { output("t03-storage-result", "当前账号没有笔记设置；输入保持不变。"); return; }
    if (result.exists !== true || !result.value || result.value.formatVersion !== 1 || typeof result.value.text !== "string" || result.value.text.length > 240) throw failure("INVALID_RESPONSE");
    byId("t03-note").value = result.value.text; output("t03-storage-result", "已读取本账号笔记设置。");
  });
  action("t03-private-save", "t03-storage-result", async () => {
    const result = await request("storage.write", { key: "field_note", data: encode(note()) });
    if (result.written !== true) throw failure("INVALID_RESPONSE");
    output("t03-storage-result", "已原子写入本应用私有文件；游戏存档未修改。");
  });
  action("t03-private-read", "t03-storage-result", async () => {
    const result = await request("storage.read", { key: "field_note" });
    if (result.exists === false) { output("t03-storage-result", "当前账号没有此私有文件；输入保持不变。"); return; }
    if (result.exists !== true || result.encoding !== "base64") throw failure("INVALID_RESPONSE");
    const text = decode(result.data); if (text.length > 240) throw failure("INVALID_RESPONSE");
    byId("t03-note").value = text; output("t03-storage-result", "已读取本账号的私有文件。");
  });
  action("t03-file-pick", "t03-files-result", async () => {
    // User gesture is consumed by this direct request, not by a permission dialog followed by a picker.
    const selected = validateCapability(await request("files.pickOpen"));
    if (readFile) { try { await request("files.close", { handle: readFile.handle }); } catch (_) { /* host also expires old handles */ } }
    readFile = selected;
    output("t03-files-result", "已选：" + selected.name + "（" + selected.bytes + " 字节）。另点「读取已选文件」确认内容。");
  });
  action("t03-file-read", "t03-files-result", async () => {
    if (!readFile) throw failure("FILE_HANDLE_INVALID");
    if (readFile.bytes > 4096) throw failure("FILE_TOO_LARGE");
    const result = await request("files.read", { handle: readFile.handle });
    if (result.encoding !== "base64") throw failure("INVALID_RESPONSE");
    byId("t03-file-preview").textContent = decode(result.data);
    output("t03-files-result", "已读取用户选择的文本；未自动写入笔记或游戏存档。");
  });
  action("t03-file-close", "t03-files-result", async () => {
    if (!readFile) throw failure("FILE_HANDLE_INVALID");
    const result = await request("files.close", { handle: readFile.handle });
    if (result.closed !== true) throw failure("INVALID_RESPONSE");
    readFile = null; byId("t03-file-preview").textContent = ""; output("t03-files-result", "已释放读取句柄。");
  });
  action("t03-file-save-pick", "t03-files-result", async () => {
    const selected = validateCapability(await request("files.pickSave"));
    if (saveFile) { try { await request("files.close", { handle: saveFile.handle }); } catch (_) { } }
    saveFile = selected;
    output("t03-files-result", "已选导出名称：" + selected.name + "。另点「导出到已选位置」提交；已有文件将保留恢复备份。");
  });
  action("t03-file-export", "t03-files-result", async () => {
    if (!saveFile) throw failure("FILE_HANDLE_INVALID");
    const result = await request("files.write", { handle: saveFile.handle, data: encode(note()) });
    if (!Number.isSafeInteger(result.bytes) || typeof result.backupRetained !== "boolean") throw failure("INVALID_RESPONSE");
    saveFile = null;
    output("t03-files-result", "已导出 " + result.bytes + " 字节。" + (result.backupRetained ? "原文件在同目录保留 .AutumnOS-backup-随机值 备份。" : "创建了所选新文件。") + "再次导出须重新选择。");
  });
  function applyAppearance(value) {
    if (!value || !["light", "dark", "system"].includes(value.theme) || !Number.isFinite(value.scale) || value.scale < .5 || value.scale > 8
      || typeof value.language !== "string" || !/^[A-Za-z]{2,8}(?:-[A-Za-z0-9]{1,8})*$/.test(value.language) || typeof value.reduceMotion !== "boolean") throw failure("INVALID_RESPONSE");
    appearance = value; snapshotRevision++;
    const root = document.documentElement;
    root.dataset.autumnTheme = value.theme === "system" ? (systemTheme.matches ? "dark" : "light") : value.theme;
    root.dataset.autumnMotion = value.reduceMotion ? "reduced" : "normal";
    root.lang = value.language;
    // Windows/WebView already applies device DPI. Scale only any remaining host-to-WebView ratio.
    const scaleRatio = Math.max(.5, Math.min(2, value.scale / (window.devicePixelRatio || 1)));
    document.querySelector(".app").style.zoom = String(scaleRatio);
    output("t03-appearance", "外观：" + value.theme + "（生效 " + root.dataset.autumnTheme + "） · 语言 " + value.language
      + " · 宿主缩放 " + value.scale + " · 减少动态效果 " + (value.reduceMotion ? "开启" : "关闭"));
  }
  function systemChanged() { if (appearance && appearance.theme === "system" && !disposed) applyAppearance(appearance); }
  function resume(actionName) {
    if (actionName !== "resume" || disposed) return;
    output("t03-events", "收到宿主声明的 resume 动作；保留当前牌局与输入。");
    const card = document.querySelector(".card:not(:disabled)"); if (card) card.focus({ preventScroll: false });
  }
  function dispose() {
    if (disposed) return;
    disposed = true;
    for (const abort of pending) abort.abort(); pending.clear();
    for (const unsubscribe of subscriptions.splice(0)) unsubscribe();
    systemTheme.removeEventListener("change", systemChanged);
    readFile = null; saveFile = null; refreshControls();
    // Closing destroys the host instance and revokes its handles. No post-close SDK call is sent.
  }
  if (!sdk?.onEvent) { output("t03-events", "当前 SDK 不支持此扩展；原游戏仍可使用。", true); return; }
  subscriptions.push(sdk.onLifecycle(state => {
    lifecycle = state; refreshControls();
    if (["closing", "closed", "crashed"].includes(state)) dispose();
  }));
  subscriptions.push(sdk.onEvent("appearance.changed", value => { if (!disposed) applyAppearance(value); }));
  subscriptions.push(sdk.onEvent("permissions.changed", value => {
    if (disposed) return;
    output("t03-events", "权限变化：" + value.name + " → " + value.state);
    if (value.name === "identity.profile" && value.state !== "granted") output("t03-profile-result", "资料授权已撤销，样例已清空显示。已交给其他应用的资料不能远程收回。");
    if (value.name === "files.open" && value.state !== "granted") { readFile = null; byId("t03-file-preview").textContent = ""; }
    if (value.name === "files.save" && value.state !== "granted") saveFile = null;
  }));
  subscriptions.push(sdk.onEvent("identity.changed", () => output("t03-profile-result", "账号状态已变化，请自主点击读取资料；不会自动获取新资料。")));
  subscriptions.push(sdk.onEvent("links.opened", value => resume(value.action)));
  subscriptions.push(sdk.onEvent("shortcuts.invoked", value => resume(value.action)));
  systemTheme.addEventListener("change", systemChanged);
  window.addEventListener("pagehide", dispose, { once: true });
  (async () => {
    try {
      const capabilities = await request("platform.getCapabilities");
      for (const name of capabilities.capabilities || []) knownCapabilities.add(name);
      const state = await request("lifecycle.getState"); lifecycle = state.state;
      if (knownCapabilities.has("appearance")) {
        const revision = snapshotRevision, value = await request("appearance.get");
        if (!disposed && revision === snapshotRevision) applyAppearance(value);
      } else output("t03-appearance", "当前宿主未提供外观快照。", true);
      output("t03-events", "当前数据空间：" + (capabilities.accountMode === "account" ? "登录账号" : "游客") + "。尚未自动申请任何权限。");
    } catch (error) { output("t03-events", safeError(error), true); }
    finally { refreshControls(); }
  })();
})();
