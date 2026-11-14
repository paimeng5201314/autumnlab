"use strict";
const result = document.getElementById("result"), status = document.getElementById("status");
const buttons = [document.getElementById("fetch"), document.getElementById("negative")];
function showError(error) { result.textContent = "请求失败：" + (error.code || "UNKNOWN") + "\n" + (error.message || ""); }
async function run(action) { buttons.forEach(b => b.disabled = true); try { await action(); } catch (error) { showError(error); } finally { buttons.forEach(b => b.disabled = false); } }
async function readStatus() {
  const caps = await autumn.request("platform.getCapabilities");
  if (!caps.capabilities.includes("network")) { status.textContent = "此宿主未提供网络 SDK"; buttons.forEach(b => b.disabled = true); return; }
  const current = await autumn.request("network.getStatus", {});
  status.textContent = "SDK " + autumn.version + " · 链路/实例策略 " + (current.available ? "可用" : "不可用") + " · 互联网可达性 " + current.internetAccess;
}
buttons[0].onclick = () => run(async () => {
  const permission = await autumn.request("permissions.request", { name: "network" });
  if (permission.state !== "granted") { result.textContent = "授权状态：" + permission.state; return; }
  result.textContent = "正在通过宿主读取 api.github.com…";
  const response = await autumn.request("network.request", { url: "https://api.github.com/rate_limit", headers: { accept: "application/json" }, timeoutMs: 10000 });
  const body = new TextDecoder().decode(Uint8Array.from(atob(response.body), c => c.charCodeAt(0)));
  result.textContent = "HTTP " + response.status + " · 实际响应 " + body.length + " 字符\n" + body;
});
buttons[1].onclick = () => run(async () => {
  result.textContent = "正在检查未声明目标…";
  const response = await autumn.request("network.request", { url: "https://www.microsoft.com/", timeoutMs: 10000 });
  result.textContent = "意外取得响应，需排查：HTTP " + response.status;
});
readStatus().catch(showError);
