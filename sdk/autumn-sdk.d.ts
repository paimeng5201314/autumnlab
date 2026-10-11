/** AutumnOS SDK 0.3.0 · 制作人：派蒙. Protocol 1 remains compatible with T01/T02. */
export type JsonValue = null | boolean | number | string | JsonValue[] | { [key: string]: JsonValue };
export type LifecycleState = "starting" | "foreground" | "background" | "suspended" | "closing" | "closed" | "crashed";
export interface AutumnError {
  code: string; message: string; retryable: boolean;
  /** Host diagnostic ID; null for local SDK errors. */
  correlationId: string | null;
}
export interface Request { protocolVersion: 1; requestId: string; method: string; params: { [key: string]: JsonValue } }
export type Response = { requestId: string; ok: true; result: JsonValue }
  | { requestId: string | null; ok: false; error: AutumnError & { correlationId: string } };
export interface LifecycleEvent { event: "lifecycle.stateChanged"; state: LifecycleState }
export type PermissionName = "saves" | "identity.profile" | "storage" | "files.open" | "files.save" | "notifications" | "shortcuts" | "widgets" | "links";
export interface Profile { appScopedUserId: string; displayName: string; avatarUrl: string | null; isCached: boolean }
export interface Appearance { theme: "light" | "dark" | "system"; language: string; scale: number; reduceMotion: boolean }
export interface Shortcut { id: string; title: string; action: string }
export interface SaveSlot { slot: string; formatVersion: number; revision: number; bytes: number; hasBackup: boolean }
/** Opaque host capability; no disk path or operating-system handle. */
export interface FileCapability { handle: string; name: string; bytes: number; expiresUtc: string }
export interface SdkEvents {
  "appearance.changed": Appearance;
  "permissions.changed": PermissionState;
  "identity.changed": { state: "signed_in" | "offline_cached" };
  "links.opened": { action: string; arguments: Record<string, string> };
  "shortcuts.invoked": { id: string; action: string };
  "lifecycle.changed": LifecycleSnapshot;
}
export type SdkEvent = { [E in keyof SdkEvents]: { event: E; data: SdkEvents[E] } }[keyof SdkEvents];
export interface Capabilities {
  protocolVersion: 1;
  capabilities: ("lifecycle" | "permissions" | "identity.profile" | "saves" | "storage" | "preferences" | "files.open" | "files.save" | "appearance" | "notifications" | "shortcuts" | "widgets" | "links.internal")[];
  accountMode: "guest" | "account";
  networkSandboxVerified: false;
  limits: {
    maximumMessageBytes: 32768; maximumPendingRequests: 16; maximumRequestsPerMinute: 120; requestTimeoutMs: number;
    /** Pending IDs plus recent completed IDs; not a cumulative session request limit. */
    maximumRememberedRequestIds: 2048;
    /** Completed request IDs are rejected for 10 minutes; no exactly-once guarantee after expiry. */
    deduplicationWindowMs: 600000;
  };
}
export interface PermissionState { name: PermissionName; state: "prompt" | "granted" | "denied" | "revoked" }
export interface LifecycleSnapshot { state: LifecycleState; blocksMaintenance: boolean }
export interface SdkMethods {
  "platform.getCapabilities": { params: Record<string, never>; result: Capabilities };
  "lifecycle.getState": { params: Record<string, never>; result: LifecycleSnapshot };
  "permissions.query": { params: { name: PermissionName }; result: PermissionState };
  /** Requires a recent native host-observed input when the permission is still prompt. */
  "permissions.request": { params: { name: PermissionName }; result: PermissionState };
  "identity.getProfile": { params: Record<string, never>; result: Profile };
  "identity.requestProfile": { params: Record<string, never>; result: Profile };
  "saves.list": { params: Record<string, never>; result: { slots: SaveSlot[] } };
  "saves.read": { params: { slot: string }; result: { exists: boolean; value: JsonValue } };
  /** Host checks the complete future read response (UTF-8, 32768 bytes, 64-character requestId) before committing. */
  "saves.write": { params: { slot: string; value: JsonValue; formatVersion?: number }; result: { saved: true } };
  "saves.restore": { params: { slot: string }; result: { restored: true } };
  "storage.read": { params: { key: string }; result: { exists: boolean; data: string | null; encoding: "base64" } };
  /** Base64 decoded bytes must be <= 20 KiB, matching storage.read; larger writes leave existing bytes intact. */
  "storage.write": { params: { key: string; data: string }; result: { written: true } };
  "storage.delete": { params: { key: string }; result: { deleted: boolean } };
  /** Separate from storage; keys are 1–59 ASCII letters, digits, underscores or hyphens. */
  "preferences.get": { params: { key: string }; result: { exists: boolean; value: JsonValue } };
  /** Same write-before-read response budget as saves.write; corruption is reported as PREFERENCE_CORRUPT. */
  "preferences.set": { params: { key: string; value: JsonValue }; result: { saved: true } };
  "files.pickOpen": { params: Record<string, never>; result: FileCapability };
  "files.pickSave": { params: Record<string, never>; result: FileCapability };
  "files.read": { params: { handle: string }; result: { data: string; encoding: "base64" } };
  "files.write": { params: { handle: string; data: string }; result: { bytes: number; backupRetained: boolean } };
  "files.close": { params: { handle: string }; result: { closed: boolean } };
  "appearance.get": { params: Record<string, never>; result: Appearance };
  "notifications.show": { params: { id: string; title: string; body: string; action: string | null }; result: { shown: true; id: string } | { shown: false; reason: "muted" | "duplicate" } };
  "notifications.setBadge": { params: { count: number }; result: { count: number } };
  "shortcuts.register": { params: { ids: string[] }; result: { shortcuts: Shortcut[] } };
  "widgets.update": { params: { id: string; lines: string[] }; result: { updated: true; id: string } };
  "links.openInternal": { params: { action: string; arguments: Record<string, string> }; result: { delivered: true; action: string } };
}
export interface RequestOptions {
  /** Default 30000, integer from 1 through 120000 milliseconds. */
  timeoutMs?: number;
  /** Stops waiting locally; does not cancel a submitted host write. */
  signal?: AbortSignal;
}
export interface AutumnSdk {
  readonly version: "0.3.0"; readonly protocolVersion: 1;
  request<M extends keyof SdkMethods>(method: M, params: SdkMethods[M]["params"], options?: RequestOptions): Promise<SdkMethods[M]["result"]>;
  request(method: "platform.getCapabilities", params?: Record<string, never>, options?: RequestOptions): Promise<Capabilities>;
  request(method: "lifecycle.getState", params?: Record<string, never>, options?: RequestOptions): Promise<LifecycleSnapshot>;
  /** Unknown literal or dynamic string methods remain supported; known literal methods cannot bypass typed parameters. */
  request<M extends string>(method: M extends keyof SdkMethods ? never : M, params?: { [key: string]: JsonValue }, options?: RequestOptions): Promise<unknown>;
  onLifecycle(listener: (state: LifecycleState) => void): () => void;
  /** 64 listeners across both APIs. Unknown events fail explicitly. Call the returned function to unsubscribe. */
  onEvent<E extends keyof SdkEvents>(event: E, listener: (value: SdkEvents[E]) => void): () => void;
}
declare global { interface Window { autumn: AutumnSdk } }
