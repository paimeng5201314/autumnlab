"use strict";
const fs = require("node:fs/promises"), path = require("node:path"), http = require("node:http"), assert = require("node:assert/strict");
const { chromium } = require("../website/node_modules/playwright");
const project = path.resolve(__dirname, "../samples/chemleap");
const reportIndex = process.argv.indexOf("--report"), reportDir = path.resolve(reportIndex >= 0 ? process.argv[reportIndex + 1] : "artifacts/chemleap-browser");
const executable = process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE || path.join(process.env.LOCALAPPDATA, "ms-playwright/chromium_headless_shell-1234/chrome-headless-shell-win64/chrome-headless-shell.exe");
const results = [], screenshots = [], sourceRequests = [], errors = [], blockedExternal = [];
let browser, server, base;
async function capture(page, name) { const file = path.join(reportDir, name + ".png"); await page.screenshot({ path: file, fullPage: true }); screenshots.push(file); }
async function fresh(viewport = { width: 1440, height: 1000 }, options = {}) {
  const context = await browser.newContext({ viewport, ...options });
  const page = await context.newPage();
  page.on("pageerror", error => errors.push(error.message));
  page.on("request", request => { if (!request.url().startsWith(base)) blockedExternal.push(request.url()); });
  await page.goto(base); await page.locator("#start-button").waitFor();
  await page.waitForFunction(() => document.querySelector("#world").dataset.renderer);
  return { page, context };
}
async function start(page) { await page.locator("#start-button").click(); await page.locator(".platform-option:enabled").first().waitFor(); }
async function pick(page, safe = true) {
  return page.evaluate(safe => {
    const text = document.querySelector("#current-substance").getAttribute("aria-label"), C = window.ChemLeapChemistry;
    const id = Object.keys(C.species).find(id => text.startsWith("当前物质 " + C.species[id].formula + " "));
    return [...document.querySelectorAll(".platform-option")].findIndex(button => !!C.findReaction(id, button.dataset.substance) === safe);
  }, safe);
}
async function waitScore(page, score) { await page.waitForFunction(score => document.querySelector("#score").textContent === String(score), score); }
async function scenario(name, body) {
  const begin = Date.now();
  try { await body(); results.push({ name, status: "passed", milliseconds: Date.now() - begin }); console.log("PASS " + name); }
  catch (error) { results.push({ name, status: "failed", detail: error.message }); console.error("FAIL " + name + ": " + error.message); }
}
(async () => {
  await fs.mkdir(reportDir, { recursive: false });
  const files = new Set(await fs.readdir(project));
  server = http.createServer(async (req, res) => {
    try {
      const name = decodeURIComponent(new URL(req.url, "http://localhost").pathname).slice(1) || "index.html";
      if (!files.has(name) || name.includes("/") || name.includes("\\")) { res.writeHead(404); res.end(); return; }
      sourceRequests.push(name); const bytes = await fs.readFile(path.join(project, name));
      res.writeHead(200, { "Content-Type": ({ ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".css": "text/css; charset=utf-8", ".json": "application/json" })[path.extname(name)] || "application/octet-stream", "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff" }); res.end(bytes);
    } catch { res.writeHead(500); res.end(); }
  });
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve)); base = "http://127.0.0.1:" + server.address().port + "/";
  browser = await chromium.launch({ executablePath: executable, chromiumSandbox: true, args: ["--use-angle=swiftshader", "--use-gl=angle"] });
  await scenario("actual_webgl_intro_and_fifteen_reactions", async () => {
    const { page, context } = await fresh();
    assert.equal(await page.locator("#world").getAttribute("data-renderer"), "webgl");
    await capture(page, "01-intro"); await start(page); await capture(page, "02-gameplay");
    assert.equal(await page.locator(".platform-option").count(), 3);
    await page.locator("#help-button").click(); assert.equal(await page.locator("#reaction-table tr").count(), 15);
    assert.ok((await page.locator("#field-guide").textContent()).includes("多产物")); await context.close();
  });
  await scenario("twenty_real_click_jumps_and_increasing_difficulty", async () => {
    const { page, context } = await fresh(); await start(page);
    for (let turn = 0; turn < 20; turn++) {
      const index = await pick(page); assert.ok(index >= 0);
      await page.locator(".platform-option").nth(index).click(); await waitScore(page, (turn + 1) * 10);
      assert.equal(await page.locator(".platform-option").count(), turn < 4 ? 3 : turn < 9 ? 4 : turn < 19 ? 5 : 6);
    }
    assert.equal(await page.locator("#best-score").textContent(), "200"); await capture(page, "03-two-hundred-points");
    await page.locator("#restart-button").click(); await waitScore(page, 0); assert.equal(await page.locator("#best-score").textContent(), "200"); await context.close();
  });
  await scenario("wrong_platform_ends_game_without_awarding_points", async () => {
    const { page, context } = await fresh(); await start(page); const bad = await pick(page, false); assert.ok(bad >= 0);
    await page.locator(".platform-option").nth(bad).click(); await page.locator("#game-over").waitFor({ state: "visible" });
    assert.equal(await page.locator("#score").textContent(), "0"); assert.equal(await page.locator(".platform-option:enabled").count(), 0);
    await capture(page, "04-game-over"); await page.locator("#again-button").click(); assert.equal(await page.locator(".platform-option:enabled").count(), 3); await context.close();
  });
  await scenario("pause_during_jump_preserves_unsettled_score", async () => {
    const { page, context } = await fresh(); await start(page); await page.locator(".platform-option").nth(await pick(page)).click();
    await page.locator("#pause-button").click(); await page.waitForTimeout(950); assert.equal(await page.locator("#score").textContent(), "0");
    assert.equal(await page.locator("#pause-overlay").isVisible(), true); await page.locator("#pause-button").click(); await waitScore(page, 10); await context.close();
  });
  await scenario("charge_pointer_release_selects_exactly_one_platform", async () => {
    const { page, context } = await fresh(); await start(page); const bounds = await page.locator("#jump-button").boundingBox();
    await page.mouse.move(bounds.x + 20, bounds.y + 20); await page.mouse.down(); await page.waitForTimeout(550);
    assert.ok(Number(await page.locator("#charge-meter").getAttribute("aria-valuenow")) > 10);
    const selection = await page.locator(".platform-option[aria-pressed=true]").getAttribute("data-index"); assert.ok(selection !== null);
    await page.mouse.up(); await page.waitForFunction(() => !document.querySelector("#game-status").textContent.startsWith("正在跳向"));
    assert.ok(["0", "10"].includes(await page.locator("#score").textContent())); assert.equal(await page.locator("#charge-meter").getAttribute("aria-valuenow"), "0"); await context.close();
  });
  await scenario("keyboard_number_and_space_charge_are_operational", async () => {
    const { page, context } = await fresh(); await start(page); const index = await pick(page);
    await page.locator("#world").focus(); await page.keyboard.press(String(index + 1)); await waitScore(page, 10);
    await page.keyboard.down("Space"); await page.waitForTimeout(260); assert.ok(Number(await page.locator("#charge-meter").getAttribute("aria-valuenow")) > 0);
    await page.keyboard.up("Space"); await page.waitForFunction(() => !document.querySelector("#game-status").textContent.startsWith("正在跳向")); await context.close();
  });
  await scenario("browser_save_reload_restore_and_corruption_keep_real_progress", async () => {
    const { page, context } = await fresh(); await start(page); await page.locator(".platform-option").nth(await pick(page)).click(); await waitScore(page, 10);
    const current = await page.locator("#current-substance").getAttribute("aria-label");
    await page.locator("#save-button").click(); await page.waitForFunction(() => document.querySelector("#save-status").textContent.startsWith("已保存"));
    await page.reload(); await page.locator("#load-button").click(); await waitScore(page, 10); assert.equal(await page.locator("#current-substance").getAttribute("aria-label"), current);
    assert.ok((await page.locator("#last-reaction").textContent()).includes("→"));
    await page.evaluate(() => localStorage.setItem("cn.labchronicles.chemleap.save.v1", "{bad")); await page.locator("#load-button").click();
    await page.waitForFunction(() => document.querySelector("#save-status").textContent.includes("无效")); assert.equal(await page.locator("#score").textContent(), "10"); await context.close();
  });
  await scenario("offline_after_load_needs_no_network", async () => {
    const { page, context } = await fresh(); await context.setOffline(true); await start(page);
    await page.locator(".platform-option").nth(await pick(page)).click(); await waitScore(page, 10); await context.close();
  });
  for (const width of [390, 768, 1440]) await scenario("responsive_controls_" + width, async () => {
    const { page, context } = await fresh({ width, height: 900 }); await start(page);
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true);
    const bounds = await page.locator(".platform-option").first().boundingBox(); assert.ok(bounds.height >= 44 && bounds.width >= 44);
    await page.locator(".platform-option").nth(await pick(page)).click(); await waitScore(page, 10); await capture(page, "05-width-" + width); await context.close();
  });
  await scenario("reduced_motion_preserves_gameplay", async () => {
    const { page, context } = await fresh({ width: 1280, height: 800 }, { reducedMotion: "reduce" }); await start(page);
    await page.locator(".platform-option").nth(await pick(page)).click(); await waitScore(page, 10); await context.close();
  });
  await scenario("real_webgl_context_loss_pauses_and_restores_progress", async () => {
    const { page, context } = await fresh(); await start(page);
    await page.evaluate(() => { const gl = document.querySelector("#world").getContext("webgl"); window.chemleapLossTest = gl.getExtension("WEBGL_lose_context"); if (!window.chemleapLossTest) throw new Error("Context loss extension unavailable"); window.chemleapLossTest.loseContext(); });
    await page.locator("#pause-overlay").waitFor({ state: "visible" }); assert.equal(await page.locator(".platform-option:enabled").count(), 0);
    await page.waitForTimeout(200); await page.evaluate(() => window.chemleapLossTest.restoreContext());
    await page.locator("#pause-overlay").waitFor({ state: "hidden" }); await page.locator(".platform-option").nth(await pick(page)).click(); await waitScore(page, 10); await context.close();
  });
  await scenario("no_javascript_errors_or_external_requests", async () => { assert.deepEqual(errors, []); assert.deepEqual(blockedExternal, []); });
})().catch(error => { results.push({ name: "browser_harness", status: "failed", detail: error.stack }); }).finally(async () => {
  if (browser) await browser.close(); if (server) await new Promise(resolve => server.close(resolve));
  const failed = results.filter(test => test.status !== "passed").length;
  await fs.writeFile(path.join(reportDir, "result.json"), JSON.stringify({ status: failed ? "failed" : "passed", total: results.length, failed, results, screenshots, errors, external_requests: blockedExternal, source_requests: [...new Set(sourceRequests)], browser: executable, rendering: "ANGLE SwiftShader software WebGL; Chromium sandbox enabled", host_sdk: "not_run_in_browser; separate native report", remote_writes: "none" }, null, 2));
  process.exitCode = failed ? 1 : 0;
});
