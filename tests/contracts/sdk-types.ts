import type { AutumnSdk, Capabilities, JsonValue, Profile, LifecycleState, SdkEvents } from "../../sdk/autumn-sdk";
declare const autumn: AutumnSdk;
async function validUsage() {
  const capabilities: Capabilities = await autumn.request("platform.getCapabilities");
  const state = await autumn.request("lifecycle.getState", {});
  const profile: Profile = await autumn.request("identity.getProfile", {});
  await autumn.request("identity.requestProfile", {});
  await autumn.request("permissions.query", { name: "saves" });
  await autumn.request("permissions.request", { name: "identity.profile" });
  await autumn.request("saves.list", {});
  const read = await autumn.request("saves.read", { slot: "game" });
  const value: JsonValue = read.value;
  await autumn.request("saves.write", { slot: "game", value: { round: 1 }, formatVersion: 2 }, { timeoutMs: 120000, signal: new AbortController().signal });
  await autumn.request("saves.restore", { slot: "game" });
  await autumn.request("storage.read", { key: "note" });
  await autumn.request("storage.write", { key: "note", data: "YQ==" });
  await autumn.request("storage.delete", { key: "note" });
  await autumn.request("preferences.get", { key: "theme" });
  await autumn.request("preferences.set", { key: "theme", value: "dark" });
  const file = await autumn.request("files.pickOpen", {});
  await autumn.request("files.pickSave", {});
  await autumn.request("files.read", { handle: file.handle });
  await autumn.request("files.write", { handle: file.handle, data: "YQ==" });
  await autumn.request("files.close", { handle: file.handle });
  await autumn.request("appearance.get", {});
  const notification = await autumn.request("notifications.show", { id: "notice", title: "你好", body: "正文", action: null });
  if (!notification.shown) { const reason: "muted" | "duplicate" = notification.reason; void reason; }
  await autumn.request("notifications.setBadge", { count: 3 });
  await autumn.request("shortcuts.register", { ids: ["resume"] });
  await autumn.request("widgets.update", { id: "note", lines: ["纯文本"] });
  await autumn.request("links.openInternal", { action: "resume", arguments: { origin: "button" } });
  const unknown: unknown = await autumn.request("identity.beginAppSession", {});
  const dynamic: string = "future.method";
  await autumn.request(dynamic, { arbitraryJson: null });
  const stop = autumn.onLifecycle((next: LifecycleState) => void next);
  autumn.onEvent("appearance.changed", value => { const same: SdkEvents["appearance.changed"] = value; void same; });
  autumn.onEvent("permissions.changed", value => void value.state);
  autumn.onEvent("identity.changed", value => void value.state);
  autumn.onEvent("links.opened", value => void value.arguments);
  autumn.onEvent("shortcuts.invoked", value => void value.id);
  autumn.onEvent("lifecycle.changed", value => void value.blocksMaintenance);
  stop(); void [capabilities, state, profile, value, unknown];
}
// Every directive must consume a real compiler error; a weakened broad overload fails this compilation.
// @ts-expect-error known method cannot fall back to an unknown method overload
autumn.request("saves.write", { slot: 12, value: null });
// @ts-expect-error save value is mandatory
autumn.request("saves.write", { slot: "game" });
// @ts-expect-error identity/profile never accepts a caller-selected user ID
autumn.request("identity.getProfile", { userId: "other" });
// @ts-expect-error unknown permission is not part of protocol 1
autumn.request("permissions.request", { name: "host.execute" });
// @ts-expect-error file path cannot replace an opaque handle
autumn.request("files.read", { path: "C:/private" });
// @ts-expect-error count is a number
autumn.request("notifications.setBadge", { count: "3" });
// @ts-expect-error JSON cannot carry a function
autumn.request("preferences.set", { key: "x", value: () => 1 });
// @ts-expect-error mutation options are typed even for unknown methods
autumn.request("future.method", {}, { timeoutMs: "100" });
// @ts-expect-error profile is not a token-bearing response
autumn.request("identity.getProfile", {}).then(value => value.accessToken);
// @ts-expect-error unknown events explicitly unavailable
autumn.onEvent("identity.token", () => {});
// @ts-expect-error false notification branch does not carry an ID
autumn.request("notifications.show", { id: "x", title: "x", body: "", action: null }).then(value => { if (!value.shown) return value.id; });
void validUsage;
