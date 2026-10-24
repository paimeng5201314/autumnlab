/* Original abstract element matching game · 制作人：派蒙. No network or browser storage. */
(function () {
  "use strict";
  const elements = { H: "氢", He: "氦", C: "碳", N: "氮", O: "氧", Ne: "氖" };
  const symbols = Object.keys(elements), byId = (id) => document.getElementById(id);
  const board = byId("board"), nickname = byId("nickname");
  const saveButton = byId("save-button"), loadButton = byId("load-button"), restartButton = byId("restart-button");
  let deck = [], matched = new Set(), selected = [], moves = 0;
  let flipTimer = null, busy = false, hostReady = false, paused = false, lifecycleRevision = 0;
  const buttons = [];
  function showSave(message, tone = "") {
    byId("save-status").textContent = message;
    byId("save-status").className = "save-status" + (tone ? " " + tone : "");
  }
  function clearSelection() {
    if (flipTimer !== null) clearTimeout(flipTimer);
    flipTimer = null; selected = [];
  }
  function render() {
    buttons.forEach((button, index) => {
      const isMatched = matched.has(index), visible = isMatched || selected.includes(index);
      button.className = "card " + (isMatched ? "matched" : visible ? "revealed" : "hidden-card");
      button.querySelector(".symbol").textContent = visible ? deck[index] : "✦";
      button.querySelector(".element-name").textContent = visible ? elements[deck[index]] : "DISCOVER";
      button.setAttribute("aria-label", "第 " + (index + 1) + " 张卡片，" + (visible ? elements[deck[index]] + " " + deck[index] + (isMatched ? "，已配对" : "，已翻开") : "未翻开"));
      button.setAttribute("aria-pressed", String(visible));
      button.disabled = busy || paused || isMatched;
    });
    byId("pairs-count").replaceChildren(document.createTextNode(String(matched.size / 2)), Object.assign(document.createElement("span"), { textContent: "/6" }));
    byId("moves-count").textContent = String(moves);
    saveButton.disabled = !hostReady || busy || paused;
    loadButton.disabled = !hostReady || busy || paused;
    restartButton.disabled = busy || paused; nickname.disabled = busy || paused;
    document.querySelector(".app").classList.toggle("paused", paused);
  }
  function newRound() {
    clearSelection(); deck = [...symbols, ...symbols];
    for (let index = deck.length - 1; index > 0; index--) {
      const random = new Uint32Array(1); crypto.getRandomValues(random);
      const target = random[0] % (index + 1);
      [deck[index], deck[target]] = [deck[target], deck[index]];
    }
    matched = new Set(); moves = 0;
    byId("round-status").textContent = "慢慢来，记住每一次相遇。";
    byId("board-title").textContent = "让相同的元素相遇"; render();
  }
  function flip(index) {
    if (busy || paused || flipTimer !== null || matched.has(index) || selected.includes(index)) return;
    selected.push(index);
    if (selected.length === 2) {
      moves++;
      if (deck[selected[0]] === deck[selected[1]]) {
        const symbol = deck[index]; selected.forEach((value) => matched.add(value)); selected = [];
        byId("round-status").textContent = matched.size === 12 ? "全部相遇！用了 " + moves + " 次翻牌。" : "找到了 " + elements[symbol] + "（" + symbol + "）！";
        if (matched.size === 12) byId("board-title").textContent = "每一次相遇，都算数 ✦";
      } else {
        byId("round-status").textContent = "记住这两个位置，再试一次。";
        flipTimer = setTimeout(() => { flipTimer = null; selected = []; render(); }, 850);
      }
    }
    render();
  }
  for (let index = 0; index < 12; index++) {
    const button = document.createElement("button");
    button.type = "button"; button.id = "card-" + index; button.dataset.index = String(index);
    button.append(Object.assign(document.createElement("span"), { className: "symbol" }), Object.assign(document.createElement("span"), { className: "element-name" }));
    button.addEventListener("click", () => flip(index));
    button.addEventListener("keydown", (event) => {
      const offset = { ArrowLeft: -1, ArrowRight: 1, ArrowUp: -4, ArrowDown: 4 }[event.key];
      if (offset === undefined || event.isComposing) return;
      event.preventDefault(); let next = index;
      for (let count = 0; count < buttons.length; count++) {
        next = (next + offset + buttons.length) % buttons.length;
        if (!buttons[next].disabled) { buttons[next].focus(); break; }
      }
    });
    buttons.push(button); board.append(button);
  }
  function applyLifecycle(state) {
    lifecycleRevision++; paused = state !== "foreground";
    const labels = { starting: "正在启动", foreground: "AutumnOS · 运行中", background: "后台 · 游戏仍存活", suspended: "游戏已挂起", closing: "正在关闭", closed: "游戏已关闭", crashed: "运行已中断" };
    byId("runtime-status").textContent = labels[state] || state;
    if (paused) clearSelection();
    if (state === "closed" || state === "crashed") hostReady = false;
    render();
  }
  async function refreshLifecycle() {
    const revision = lifecycleRevision;
    try {
      const result = await window.autumn.request("lifecycle.getState");
      if (revision === lifecycleRevision) applyLifecycle(result.state);
    } catch (error) { if (error.code === "SESSION_EXPIRED") applyLifecycle("closed"); }
  }
  async function requireSavePermission() {
    let permission = await window.autumn.request("permissions.query", { name: "saves" });
    if (permission.state === "revoked") throw Object.assign(new Error("Permission was revoked."), { code: "PERMISSION_REVOKED" });
    if (permission.state === "prompt") {
      showSave("请在 AutumnOS 提示中选择是否允许本地存档。关闭提示或拒绝都不会丢失当前游戏。");
      permission = await window.autumn.request("permissions.request", { name: "saves" }, { timeoutMs: 120000 });
    }
    byId("permission-status").textContent = permission.state === "granted" ? "当前账号已允许此应用存档" : "你未授予存档权限";
    if (permission.state !== "granted") throw Object.assign(new Error("Save permission was denied."), { code: "PERMISSION_DENIED" });
  }
  function validatedSave(value) {
    if (!value || typeof value !== "object" || Array.isArray(value)
      || Object.keys(value).sort().join(",") !== "deck,formatVersion,matched,moves,nickname"
      || value.formatVersion !== 1 || typeof value.nickname !== "string" || value.nickname.length > 24
      || !Number.isSafeInteger(value.moves) || value.moves < 0 || !Array.isArray(value.deck) || value.deck.length !== 12
      || symbols.some((symbol) => value.deck.filter((card) => card === symbol).length !== 2)
      || !Array.isArray(value.matched) || value.matched.length % 2 !== 0
      || new Set(value.matched).size !== value.matched.length
      || value.matched.some((index) => !Number.isInteger(index) || index < 0 || index > 11)
      || value.moves < value.matched.length / 2
      || symbols.some((symbol) => { const count = value.matched.filter((index) => value.deck[index] === symbol).length; return count !== 0 && count !== 2; })) {
      throw Object.assign(new Error("Unsupported or invalid save format."), { code: "SAVE_FORMAT_UNSUPPORTED" });
    }
    return value;
  }
  function errorMessage(error) {
    const messages = {
      PERMISSION_DENIED: "你未授予存档权限；当前游戏仍可继续。",
      PERMISSION_NOT_DECLARED: "应用未声明存档权限，无法访问存档。",
      PERMISSION_REVOKED: "本次游戏的存档权限已撤销，当前进度仍可保留在画面中。",
      SESSION_SUSPENDED: "游戏已挂起，请恢复游戏后再操作。",
      REQUEST_TIMEOUT: "宿主处理已超时。已提交的保存可能仍会完成，请先读取核对。",
      REQUEST_BUSY: "宿主正在处理较多请求，请稍后重试。",
      RATE_LIMITED: "请求过于频繁，请稍候再试。",
      TIMEOUT: "等待已超时。已提交的保存可能仍会完成，请先读取核对，不要立即重试写入。",
      USER_CANCELLED: "等待已取消；已提交的保存可能仍会完成。",
      SESSION_EXPIRED: "游戏会话已结束，请重新打开应用。",
      SAVE_CORRUPT: "现有存档损坏；原文件已保留，未覆盖。",
      SAVE_FORMAT_UNSUPPORTED: "存档格式不受支持或内容损坏；保留原文件和当前游戏。",
      SAVE_BUSY: "存档正在被其他操作使用，请稍后重试。",
      HOST_UNAVAILABLE: "未连接 AutumnOS，无法访问本地存档。",
      STORAGE_IO_ERROR: "本地存档读写失败；请检查设备空间和数据目录权限。"
    };
    return messages[error.code] || "操作失败（" + (error.code || "UNKNOWN_ERROR") + "），未报告成功。";
  }
  async function saveOrLoad(mode) {
    if (!hostReady || busy || paused) return;
    busy = true; render();
    try {
      await requireSavePermission();
      if (mode === "save") {
        const previous = await window.autumn.request("saves.read", { slot: "game" });
        if (previous.exists) validatedSave(previous.value);
        const value = { formatVersion: 1, nickname: nickname.value, deck: [...deck], matched: [...matched].sort((a, b) => a - b), moves };
        const result = await window.autumn.request("saves.write", { slot: "game", value });
        if (result.saved !== true) throw Object.assign(new Error(), { code: "INVALID_RESPONSE" });
        showSave("已保存到本机。下次打开后选择「读取存档」即可继续。", "success");
      } else {
        const result = await window.autumn.request("saves.read", { slot: "game" });
        if (!result.exists) { showSave("本机还没有这款游戏的存档。当前游戏未改变。"); return; }
        const value = validatedSave(result.value);
        clearSelection(); deck = [...value.deck]; matched = new Set(value.matched); moves = value.moves; nickname.value = value.nickname;
        byId("board-title").textContent = matched.size === 12 ? "每一次相遇，都算数 ✦" : "让相同的元素相遇";
        byId("round-status").textContent = "已恢复 " + matched.size / 2 + " 组配对，继续这段发现。";
        showSave("已读取本机存档。欢迎回来" + (value.nickname ? "，" + value.nickname : "") + "。", "success");
      }
    } catch (error) { showSave(errorMessage(error), "error"); }
    finally { busy = false; render(); }
  }
  restartButton.addEventListener("click", () => { if (!busy && !paused) { newRound(); showSave("已开始新一局。已有存档保持原样，直到你再次保存。"); } });
  saveButton.addEventListener("click", () => saveOrLoad("save")); loadButton.addEventListener("click", () => saveOrLoad("load"));
  newRound();
  if (!window.autumn) {
    byId("runtime-status").textContent = "试玩 · 未连接 AutumnOS";
    byId("capability-status").textContent = "SDK 未加载，存档不可用。";
    showSave("请通过 AutumnOS 打开 .autumn 包，以使用本地存档。"); return;
  }
  window.autumn.onLifecycle((state) => { applyLifecycle(state); if (state === "foreground") refreshLifecycle(); });
  document.addEventListener("visibilitychange", () => { if (!document.hidden && hostReady) refreshLifecycle(); });
  (async () => {
    try {
      const capabilities = await window.autumn.request("platform.getCapabilities");
      hostReady = capabilities.capabilities.includes("saves");
      byId("capability-status").textContent = "协议 " + capabilities.protocolVersion + " · " + (capabilities.accountMode === "guest" ? "本地游客" : "账号独立数据") + " · " + capabilities.capabilities.join(" / ");
      await refreshLifecycle();
      if (!hostReady) showSave("当前 AutumnOS 未提供存档能力，游戏仍可继续。");
    } catch (error) {
      byId("runtime-status").textContent = "试玩 · 未连接 AutumnOS";
      byId("capability-status").textContent = "宿主能力不可用（" + (error.code || "UNKNOWN_ERROR") + "）";
      showSave("请通过 AutumnOS 打开 .autumn 包，以使用本地存档。");
    }
    render();
  })();
})();
