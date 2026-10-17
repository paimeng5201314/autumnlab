(function () {
  "use strict";
  const C = window.ChemLeapChemistry, M = window.ChemLeapModel, R = window.ChemLeapRenderer;
  const $ = id => document.getElementById(id), host = !!window.chrome?.webview?.postMessage;
  const storageKey = "cn.labchronicles.chemleap.save.v1", abort = new AbortController();
  let state = M.create(0x4348454d), started = false, manualPaused = false, hostPaused = false, graphicsPaused = false, closed = false;
  let charging = false, chargeStarted = 0, charge = 0, jump = null, jumpElapsed = 0, raf = 0, lastFrame = 0;
  let saveBusy = false, boardKey = "", labelKey = "", result = null, toastUntil = 0;
  const reduced = window.matchMedia("(prefers-reduced-motion: reduce)");
  const formula = id => C.species[id]?.formula ?? id, substanceName = id => C.species[id]?.name ?? id;
  const paused = () => manualPaused || hostPaused || graphicsPaused || document.hidden;
  function message(text, error = false) { $("game-status").textContent = text; $("game-status").classList.toggle("error", error); }
  const renderer = R.create($("world"), {
    onSelect(index) { if (canPlay()) { M.select(state, index); updateControls(); drawOnce(); } },
    onActivate(index) { leap(index, 0.5); },
    onStatus(text) { $("renderer-status").textContent = text; },
    onContextLost() { graphicsPaused = true; syncPause(); },
    onContextRestored() { graphicsPaused = false; syncPause(); }
  });
  function canPlay() { return started && !closed && !saveBusy && !paused() && state.phase === "ready"; }
  function updateControls() {
    $("score").textContent = state.score; $("score").setAttribute("aria-label", "得分 " + state.score);
    $("best-score").textContent = state.best; $("round-indicator").textContent = "第 " + state.round + " 跳";
    $("current-substance").replaceChildren(document.createTextNode(formula(state.current) + " "));
    const currentName = document.createElement("small"); currentName.textContent = substanceName(state.current); $("current-substance").append(currentName);
    $("current-substance").setAttribute("aria-label", "当前物质 " + formula(state.current) + " " + substanceName(state.current));
    $("difficulty").textContent = (state.score < 50 ? "初见" : state.score < 100 ? "渐入佳境" : state.score < 200 ? "反应进阶" : "化学漫游") + " · " + state.platforms.length + " 块平台";
    const nextKey = state.platforms.map(p => p.id + p.substance).join("|");
    if (boardKey !== nextKey) {
      boardKey = nextKey; const nodes = state.platforms.map((platform, index) => {
        const button = document.createElement("button"); button.type = "button"; button.className = "platform-option";
        button.dataset.index = String(index); button.dataset.substance = platform.substance;
        const number = document.createElement("span"); number.className = "number"; number.textContent = "0" + (index + 1);
        const symbol = document.createElement("strong"); symbol.textContent = formula(platform.substance);
        const name = document.createElement("small"); name.textContent = substanceName(platform.substance);
        button.append(number, symbol, name); button.setAttribute("aria-label", (index + 1) + " " + formula(platform.substance) + " " + substanceName(platform.substance));
        button.addEventListener("click", () => leap(index, .5)); return button;
      }); $("platform-options").replaceChildren(...nodes);
    }
    for (const [index, button] of [...$("platform-options").children].entries()) {
      button.disabled = !canPlay(); button.classList.toggle("selected", index === state.selected); button.setAttribute("aria-pressed", String(index === state.selected));
    }
    $("intro").hidden = started; $("game-over").hidden = state.phase !== "over"; $("pause-overlay").hidden = !paused() || !started || state.phase === "over";
    $("jump-button").disabled = !canPlay(); $("hint-button").disabled = !canPlay();
    $("pause-button").textContent = manualPaused ? "继续" : "暂停";
    $("pause-button").disabled = !started || state.phase === "over" || closed;
    $("save-button").disabled = saveBusy || !started || state.phase === "jumping" || closed || hostPaused;
    $("load-button").disabled = saveBusy || state.phase === "jumping" || closed || hostPaused;
    if (state.phase === "over") {
      $("final-score").textContent = state.score + " 分";
      $("failure-detail").textContent = result?.target ? formula(state.current) + " + " + formula(result.target) + "：反应表中没有这次组合。" : "本次旅程已经结束。";
    }
  }
  function updateJournal(reaction) {
    const latest = reaction ?? state.history?.at(-1);
    if (latest) $("last-reaction").textContent = latest.equation + " · 下一物质 " + formula(latest.next);
    const items = (state.history ?? []).slice(-12).map(item => {
      const li = document.createElement("li"); li.textContent = item.equation ?? item.reaction?.equation ?? String(item); return li;
    }); $("reaction-history").replaceChildren(...items);
  }
  function drawOnce(time = performance.now()) {
    renderer.setScene({ state, species: C.species, selected: state.selected, charge, jumpProgress: jump ? Math.min(1, jumpElapsed / jump.duration) : 0,
      jump, paused: paused(), reducedMotion: reduced.matches, reaction: time < toastUntil ? result?.reaction : null });
    renderer.render(time);
    const key = state.platforms.map(p => p.id).join("|");
    if (labelKey !== key) {
      labelKey = key; $("platform-labels").replaceChildren(...state.platforms.map((p, index) => {
        const label = document.createElement("div"); label.className = "platform-label";
        const number = document.createElement("span"); number.textContent = String(index + 1); label.append(number, document.createTextNode(formula(p.substance))); return label;
      }));
    }
    for (const projection of renderer.projectPlatforms()) {
      const label = $("platform-labels").children[projection.index]; if (!label) continue;
      label.hidden = !projection.visible; label.classList.toggle("selected", projection.index === state.selected);
      label.style.transform = `translate(${projection.x}px,${projection.y - 16}px) translate(-50%,-100%)`;
    }
  }
  function frame(time) {
    raf = 0; if (closed || paused()) { lastFrame = 0; return; }
    const dt = lastFrame ? Math.min(50, time - lastFrame) : 0; lastFrame = time;
    if (charging) {
      charge = M.chargeAt(time - chargeStarted); M.select(state, M.targetForCharge(state, charge));
      $("charge-fill").style.transform = "scaleX(" + charge + ")"; $("charge-meter").setAttribute("aria-valuenow", String(Math.round(charge * 100)));
      for (const [index, button] of [...$("platform-options").children].entries()) { button.classList.toggle("selected", index === state.selected); button.setAttribute("aria-pressed", String(index === state.selected)); }
    }
    if (jump) {
      jumpElapsed += dt;
      if (jumpElapsed >= jump.duration) {
        const target = jump.target.substance; const outcome = M.land(state); result = { ...outcome, target };
        jump = null; jumpElapsed = 0; toastUntil = time + 1200;
        if (outcome.success) {
          message(outcome.reaction.equation + " · +" + outcome.points + " 分，成为 " + formula(state.current) + "！"); updateJournal(outcome.reaction);
        } else message("没有发生反应，本局结束。可以查看反应图鉴后再试一次。", true);
        updateControls();
      }
    }
    drawOnce(time); raf = requestAnimationFrame(frame);
  }
  function ensureFrame() { if (!raf && !closed && !paused()) { lastFrame = 0; raf = requestAnimationFrame(frame); } }
  function cancelCharge() { charging = false; charge = 0; $("charge-fill").style.transform = "scaleX(0)"; $("charge-meter").setAttribute("aria-valuenow", "0"); }
  function leap(index, power) {
    if (!canPlay()) return; cancelCharge(); jump = M.beginJump(state, index, power); if (!jump) return;
    jumpElapsed = 0; message("正在跳向 " + formula(jump.target.substance) + "…"); updateControls(); ensureFrame();
  }
  function beginCharge() { if (!canPlay() || charging) return; charging = true; chargeStarted = performance.now(); message("松开后跳往高亮的平台。蓄力条会来回移动。"); ensureFrame(); }
  function releaseCharge() { if (!charging) return; const power = charge, index = state.selected; cancelCharge(); leap(index, power); }
  function freshGame() {
    if (saveBusy || closed) return;
    const seed = crypto.getRandomValues(new Uint32Array(1))[0]; state = M.restart ? M.restart(state, seed) : M.create(seed);
    jump = null; jumpElapsed = 0; result = null; started = true; manualPaused = false; cancelCharge();
    message("新的旅程从 H₂ 开始。选择一种可以反应的物质。"); $("last-reaction").textContent = "新的旅程，新的发现。"; updateJournal(); updateControls(); ensureFrame(); drawOnce();
  }
  $("start-button").addEventListener("click", () => { started = true; message("你现在是 H₂。点击一种能与氢气反应的物质，或按住空格蓄力。"); updateControls(); ensureFrame(); });
  $("again-button").addEventListener("click", freshGame); $("restart-button").addEventListener("click", freshGame);
  $("jump-button").addEventListener("pointerdown", event => { if (event.button !== 0 || !canPlay()) return; event.preventDefault(); $("jump-button").setPointerCapture(event.pointerId); beginCharge(); });
  $("jump-button").addEventListener("pointerup", releaseCharge); $("jump-button").addEventListener("pointercancel", cancelCharge);
  $("jump-button").addEventListener("lostpointercapture", () => { if (charging) cancelCharge(); });
  $("jump-button").addEventListener("click", event => { if (event.detail === 0 && canPlay()) leap(state.selected, .5); });
  $("hint-button").addEventListener("click", () => {
    if (!canPlay()) return; const index = state.platforms.findIndex(p => C.findReaction(state.current, p.substance));
    if (index >= 0) { M.select(state, index); message("可以试试第 " + (index + 1) + " 块平台：" + formula(state.platforms[index].substance) + "。这次提示不增加分数。"); updateControls(); drawOnce(); }
  });
  function syncPause() { cancelCharge(); if (paused()) { if (raf) cancelAnimationFrame(raf); raf = 0; lastFrame = 0; } else ensureFrame(); updateControls(); drawOnce(); }
  $("pause-button").addEventListener("click", () => { manualPaused = !manualPaused; syncPause(); });
  document.addEventListener("visibilitychange", syncPause); window.addEventListener("blur", cancelCharge);
  document.addEventListener("keydown", event => {
    if (event.altKey || event.ctrlKey || event.metaKey || event.target.closest("input,textarea,select")) return;
    if (/^[1-6]$/.test(event.key)) { event.preventDefault(); if (!event.repeat) leap(Number(event.key) - 1, .5); }
    if (["ArrowLeft", "ArrowRight"].includes(event.key) && canPlay()) { event.preventDefault(); M.select(state, (state.selected + (event.key === "ArrowRight" ? 1 : state.platforms.length - 1)) % state.platforms.length); updateControls(); drawOnce(); }
    if (event.code === "Space" && (!event.target.closest("button") || event.target.id === "jump-button")) { event.preventDefault(); if (!event.repeat) beginCharge(); }
  });
  document.addEventListener("keyup", event => { if (event.code === "Space" && charging) { event.preventDefault(); releaseCharge(); } });
  function showGuide(show) { $("field-guide").hidden = !show; $("help-button").setAttribute("aria-expanded", String(show)); if (show) $("field-guide").scrollIntoView({ block: "start", behavior: reduced.matches ? "instant" : "smooth" }); }
  $("help-button").addEventListener("click", () => showGuide($("field-guide").hidden)); $("close-guide").addEventListener("click", () => showGuide(false));
  for (const reaction of C.reactions) {
    const tr = document.createElement("tr");
    for (const text of [reaction.equation, formula(reaction.next), reaction.condition]) { const td = document.createElement("td"); td.textContent = text; tr.append(td); }
    $("reaction-table").append(tr);
  }
  async function request(method, params) { return window.autumn.request(method, params, { signal: abort.signal, timeoutMs: 120000 }); }
  async function permission() {
    let p = await request("permissions.query", { name: "saves" });
    if (p.state === "prompt") p = await request("permissions.request", { name: "saves" });
    if (p.state !== "granted") throw Object.assign(new Error("存档权限未获授权"), { code: p.state === "revoked" ? "PERMISSION_REVOKED" : "PERMISSION_DENIED" });
  }
  function saveError(error) {
    const messages = { PERMISSION_DENIED: "未允许存档，仍可继续游戏。", PERMISSION_REVOKED: "存档授权已撤销，仍可继续游戏。", USER_CANCELLED: "存档操作已取消。", CHEMLEAP_SAVE_INVALID: "存档内容无效，当前游戏保持原样。", CHEMLEAP_SAVE_VERSION_UNSUPPORTED: "这是不支持的存档版本，当前游戏保持原样。", QuotaExceededError: "存储空间不足，当前游戏仍然保留。", SecurityError: "浏览器存储不可用，仍可继续游戏。" };
    return messages[error.code ?? error.message] ?? messages[error.name] ?? "存档操作未完成，请稍后重试。当前游戏保持原样。";
  }
  async function saveOrLoad(load) {
    if (saveBusy || closed || state.phase === "jumping" || hostPaused) return;
    saveBusy = true; cancelCharge(); updateControls();
    try {
      if (host) await permission();
      if (closed) return;
      if (load) {
        const record = host ? await request("saves.read", { slot: "game" }) : { exists: localStorage.getItem(storageKey) !== null, value: localStorage.getItem(storageKey) };
        if (!record.exists) { $("save-status").textContent = "还没有保存过进度。"; return; }
        const restored = M.restore(record.value); if (closed) return;
        state = restored; started = true; manualPaused = false; jump = null; result = null; updateJournal();
        message("已恢复进度。你现在是 " + formula(state.current) + "。"); $("save-status").textContent = "已读取存档 · " + (host ? "AutumnOS 当前用户的应用存档" : "此浏览器的本地存档");
      } else {
        const value = M.serialize(state);
        if (host) { const saved = await request("saves.write", { slot: "game", value, formatVersion: 1 }); if (!saved.saved) throw new Error("SAVE_NOT_CONFIRMED"); }
        else localStorage.setItem(storageKey, JSON.stringify(value));
        if (!closed) $("save-status").textContent = "已保存进度 · " + (host ? "AutumnOS 当前用户的应用存档" : "此浏览器的本地存档") + " · " + state.score + " 分";
      }
    } catch (error) { if (!closed) $("save-status").textContent = saveError(error); }
    finally { saveBusy = false; if (!closed) { updateControls(); ensureFrame(); drawOnce(); } }
  }
  $("save-button").addEventListener("click", () => saveOrLoad(false)); $("load-button").addEventListener("click", () => saveOrLoad(true));
  $("host-status").textContent = host ? "AutumnOS 内部运行 · 离线" : "浏览器试玩 · 本地存档";
  if (host && window.autumn) {
    let epoch = 0;
    function lifecycle(value) {
      epoch++; hostPaused = value !== "foreground";
      if (["closed", "crashed", "closing"].includes(value)) { closed = true; abort.abort(); if (raf) cancelAnimationFrame(raf); renderer.dispose(); return; }
      syncPause();
    }
    window.autumn.onLifecycle(lifecycle);
    const before = epoch; request("lifecycle.getState", {}).then(value => { if (before === epoch) lifecycle(value.state); }).catch(() => { $("host-status").textContent = "AutumnOS 生命周期连接暂不可用"; });
  }
  window.addEventListener("resize", () => { renderer.resize(); drawOnce(); });
  window.addEventListener("pagehide", () => { closed = true; abort.abort(); if (raf) cancelAnimationFrame(raf); renderer.dispose(); });
  updateControls(); updateJournal(); drawOnce(); ensureFrame();
})();
