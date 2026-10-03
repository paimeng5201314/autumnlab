/* Local actual TypeScript/Ajv/OpenAPI checks. No network, UI, native product launch, or production identity claims. */
"use strict";
const fs = require("node:fs"), path = require("node:path"), crypto = require("node:crypto"), cp = require("node:child_process");
const { createRequire } = require("node:module");
const args = process.argv.slice(2), options = {};
for (let i = 0; i < args.length; i += 2) {
  if (!["--kit-root", "--report"].includes(args[i]) || !args[i + 1] || options[args[i]]) throw new Error("Use [--kit-root Developer] --report NEW_REPORT.json");
  options[args[i]] = args[i + 1];
}
if (!options["--report"]) throw new Error("A new report path is required.");
const root = path.resolve(options["--kit-root"] || path.join(__dirname, "../..")), kit = !!options["--kit-root"];
const reportPath = path.resolve(options["--report"]);
if (fs.existsSync(reportPath)) throw new Error("Existing evidence preserved; choose a new report.");
const sdkDir = path.join(root, kit ? "SDK" : "sdk"), schemaDir = path.join(root, kit ? "Schemas" : "schemas");
const testDir = path.join(root, kit ? "Tests/contracts" : "tests/contracts");
const templateDir = path.join(root, kit ? "Templates" : "samples");
const docsDir = path.join(root, kit ? "Docs/0.5.1" : "docs/versions/0.5.1");
const toolsDir = path.join(root, kit ? "Tools/contract-checks" : "tools/contract-checks");
const backendDir = path.join(root, kit ? "BackendIdentity/server" : "samples/server-identity");
const nativeDir = path.join(root, kit ? "BackendIdentity/client" : "samples/native-identity-client");
const localRequire = createRequire(path.join(root, ".tools/contracts/package.json"));
const report = { schemaVersion: 1, task: "T06", status: "failed", executedUtc: new Date().toISOString(), layout: kit ? "delivered-developer-kit" : "workspace", root,
  scope: "Actual TypeScript compilation and strict JSON Schema/OpenAPI validation; source/fixture checks are not Windows product or real-provider tests.", checks: [], inputs: [], tools: {}, limitations: [
    "No UI/application installation, production signature verification, real Logto login, or independent external developer acceptance performed by this checker.",
    "Schema structure does not replace runtime archive/path/same-origin/cross-field/date/replay/cryptographic validation; contentEncoding is an annotation, not base64 cryptographic validation.",
    "TypeScript @ts-expect-error fixtures must produce actual errors. Known literal methods remain strict; a genuinely dynamic string is intentionally an unknown-response escape hatch."
  ] };
const hash = bytes => crypto.createHash("sha256").update(bytes).digest("hex");
function read(file) { const bytes = fs.readFileSync(file); report.inputs.push({ path: path.relative(root, file).replaceAll("\\", "/"), bytes: bytes.length, sha256: hash(bytes) }); return bytes.toString("utf8").replace(/^\uFEFF/, ""); }
function json(file) {
  const text = read(file), stack = [];
  for (const token of text.match(/"(?:\\.|[^"\\])*"|[{}\[\],]/g) || []) {
    if (token === "{") stack.push({ object: true, key: true, names: new Set() });
    else if (token === "[") stack.push({ object: false });
    else if (token === "}" || token === "]") stack.pop();
    else if (token === ",") { if (stack.at(-1)?.object) stack.at(-1).key = true; }
    else if (stack.at(-1)?.object && stack.at(-1).key) {
      const frame = stack.at(-1), name = JSON.parse(token); if (frame.names.has(name)) throw new Error("Duplicate JSON key in " + path.basename(file)); frame.names.add(name); frame.key = false;
    }
  }
  return JSON.parse(text);
}
function check(name, condition, detail = "") { report.checks.push({ name, status: condition ? "passed" : "failed", detail }); if (!condition) throw new Error(name + (detail ? ": " + detail : "")); }
function equal(actual, expected) { return JSON.stringify(actual) === JSON.stringify(expected); }
function schemaCheck(name, validate, value, expected = true) { const result = validate(value); check(name, result === expected, result === expected ? "" : JSON.stringify(validate.errors)); }
async function main() {
  const ts = localRequire("typescript"), Ajv = localRequire("ajv/dist/2020"), addFormats = localRequire("ajv-formats"), SwaggerParser = localRequire("@apidevtools/swagger-parser");
  report.tools = { node: process.version, nodePath: process.execPath, typescript: ts.version, ajv: localRequire("ajv/package.json").version,
    ajvFormats: localRequire("ajv-formats/package.json").version, swaggerParser: localRequire("@apidevtools/swagger-parser/package.json").version };
  const packageFile = json(path.join(toolsDir, "package.json")), lock = json(path.join(toolsDir, "package-lock.json"));
  for (const [name, version] of Object.entries(packageFile.dependencies)) check("fixed dependency " + name, localRequire(name + "/package.json").version === version && lock.packages["node_modules/" + name].version === version);
  for (const [name, value] of Object.entries(lock.packages)) if (name) check("locked official integrity " + name, value.resolved.startsWith("https://registry.npmjs.org/") && /^sha512-/.test(value.integrity));
  const types = read(path.join(sdkDir, "autumn-sdk.d.ts"));
  const sourceFile = ts.createSourceFile("autumn-sdk.d.ts", types, ts.ScriptTarget.Latest, true);
  const declarations = ts.createPrinter({ removeComments: true, newLine: ts.NewLineKind.LineFeed }).printFile(sourceFile);
  const baseline = json(path.join(testDir, "public-api-baseline.json"));
  check("public declaration contract unchanged", declarations === baseline.declarations);
  const runtimeJs = read(path.join(sdkDir, "autumn-sdk.js"));
  check("SDK version and protocol agree", baseline.sdkVersion === "0.3.0" && baseline.protocolVersion === 1 && runtimeJs.includes('version: "0.3.0", protocolVersion: 1'));
  const compiled = cp.spawnSync(process.execPath, [localRequire.resolve("typescript/bin/tsc"), "--project", path.join(testDir, "tsconfig.json"), "--pretty", "false"], { encoding: "utf8", windowsHide: true, timeout: 60000 });
  report.typescript = { exitCode: compiled.status, stdout: compiled.stdout, stderr: compiled.stderr, negativeFixtures: (read(path.join(testDir, "sdk-types.ts")).match(/@ts-expect-error/g) || []).length };
  check("actual TypeScript strict compilation with negative cases", compiled.status === 0, compiled.stdout || compiled.stderr || "");
  const ajv = new Ajv({ allErrors: true, strict: true, validateFormats: true }); addFormats(ajv);
  const schemas = {};
  for (const [key, file] of Object.entries({ manifest: path.join(sdkDir, "manifest.schema.json"), store: path.join(sdkDir, "store.schema.json"), release: path.join(sdkDir, "release.schema.json"),
    message: path.join(sdkDir, "message.schema.json"), developer: path.join(sdkDir, "developer.schema.json"), update: path.join(schemaDir, "autumn.update.schema.json"), signature: path.join(schemaDir, "autumn.update.sig.schema.json"), trust: path.join(schemaDir, "autumn.update.trust.schema.json"), native: path.join(nativeDir, "config.schema.json") })) {
    const value = json(file), id = value.$id || "urn:autumnos:contract-test:" + key; schemas[key] = { value, id }; ajv.addSchema(value, id);
  }
  for (const [key, entry] of Object.entries(schemas)) { entry.validate = ajv.getSchema(entry.id); check("strict draft2020 schema compiles " + key, typeof entry.validate === "function"); }
  const methods = json(path.join(testDir, "method-cases.json")).cases;
  const methodNames = sourceFile.statements.find(node => ts.isInterfaceDeclaration(node) && node.name.text === "SdkMethods").members.map(node => node.name.text).sort();
  const declared = schemas.message.value.$defs.request.allOf.map(item => item.if.properties.method.const).sort();
  check("26 actual public methods have fixtures and schemas", equal(methodNames, methods.map(item => item.method).sort()) && equal(methodNames, declared) && methodNames.length === 26);
  for (const item of methods) {
    schemaCheck(item.method + " valid request", schemas.message.validate, { protocolVersion: 1, requestId: "fixture1", method: item.method, params: item.params });
    schemaCheck(item.method + " rejects identity smuggling envelope", schemas.message.validate, { protocolVersion: 1, requestId: "fixture1", method: item.method, params: item.params, appId: "other.app" }, false);
    schemaCheck(item.method + " rejects unknown params", schemas.message.validate, { protocolVersion: 1, requestId: "fixture1", method: item.method, params: { ...item.params, callerUserId: "another-user" } }, false);
    const result = ajv.compile({ $ref: schemas.message.id + "#/$defs/" + item.resultSchema });
    schemaCheck(item.method + " valid result", result, item.result);
    schemaCheck(item.method + " rejects token-like extra result", result, { ...item.result, accessToken: "not-a-real-credential" }, false);
  }
  for (const [method, params] of [["saves.read", { slot: "../other" }], ["storage.read", { key: "CON" }], ["preferences.get", { key: "x".repeat(60) }], ["notifications.setBadge", { count: -1 }], ["files.read", { handle: "C:/private" }], ["widgets.update", { id: "note", lines: Array(5).fill("x") }], ["links.openInternal", { action: "file:///private", arguments: {} }]])
    schemaCheck(method + " security boundary", schemas.message.validate, { protocolVersion: 1, requestId: "negative1", method, params }, false);
  const eventValues = { "appearance.changed": { theme: "dark", language: "zh-CN", scale: 1, reduceMotion: false }, "permissions.changed": { name: "saves", state: "revoked" }, "identity.changed": { state: "signed_in" }, "links.opened": { action: "focus", arguments: {} }, "shortcuts.invoked": { id: "focus", action: "focus" }, "lifecycle.changed": { state: "background", blocksMaintenance: true } };
  check("six event names match schema", equal(Object.keys(eventValues).sort(), schemas.message.value.$defs.sdkEvent.oneOf.map(x => x.properties.event.const).sort()));
  for (const [event, data] of Object.entries(eventValues)) { schemaCheck(event + " valid", schemas.message.validate, { event, data }); schemaCheck(event + " foreign payload rejected", schemas.message.validate, { event, data: { ...data, userId: "other" } }, false); }
  const previewRequest = { protocolVersion: 1, command: "preview", packagePath: "D:/new-project/hello.autumn", sha256: "a".repeat(64), sessionId: null };
  schemaCheck("developer preview exact package envelope", schemas.developer.validate, previewRequest);
  schemaCheck("developer relative path rejected", schemas.developer.validate, { ...previewRequest, packagePath: "./hello.autumn" }, false);
  schemaCheck("developer executable import rejected", schemas.developer.validate, { ...previewRequest, packagePath: "D:/new-project/arbitrary.exe" }, false);
  schemaCheck("developer uppercase digest rejected", schemas.developer.validate, { ...previewRequest, sha256: "A".repeat(64) }, false);
  const developerActions = ["status", "trace", "clear-trace", "foreground", "background", "close", "deny-permissions", "restore-permissions", "account-a", "account-b", "account-guest", "offline", "online", "reset-simulation"];
  check("developer debug has fourteen fixed actions", equal(developerActions, schemas.developer.value.$defs.debugCommand.enum));
  for (const command of developerActions) {
    const request = { protocolVersion: 1, command, packagePath: null, sha256: null, sessionId: "1".repeat(32) };
    schemaCheck("developer " + command + " exact request", schemas.developer.validate, request);
    schemaCheck("developer " + command + " arbitrary argument denied", schemas.developer.validate, { ...request, arguments: { accountId: "untrusted" } }, false);
  }
  const developerResponse = { ok: true, code: "OK", sessionId: "1".repeat(32), appId: "cn.example.hello", state: "Foreground", trace: [{ timestamp: "2026-10-02T00:00:00Z", method: "identity.getProfile", resultCode: "OK" }],
    simulation: { isTestSimulation: true, account: "account-a", permissionsDenied: false, offline: true, networkScope: "preview_only_external_network_already_blocked" } };
  schemaCheck("developer simulated response has no credential", schemas.developer.validate, developerResponse);
  schemaCheck("developer response arbitrary token denied", schemas.developer.validate, { ...developerResponse, accessToken: "not-real" }, false);
  schemaCheck("developer simulated account cannot claim live state", schemas.developer.validate, { ...developerResponse, simulation: { ...developerResponse.simulation, isTestSimulation: false } }, false);
  schemaCheck("developer simulation cannot claim OS network change", schemas.developer.validate, { ...developerResponse, simulation: { ...developerResponse.simulation, networkScope: "all_system_network_disabled" } }, false);
  schemaCheck("developer arbitrary account input denied", schemas.developer.validate, { ...developerResponse, simulation: { ...developerResponse.simulation, account: "real-account-id" } }, false);
  schemaCheck("developer trace cannot contain payload", schemas.developer.validate, { ...developerResponse, trace: [{ ...developerResponse.trace[0], payload: "private-input" }] }, false);
  for (const name of ["hello-app", "identity-app", "save-game", "desktop-extension"]) {
    const directory = path.join(templateDir, name), manifest = json(path.join(directory, "manifest.json"));
    schemaCheck(name + " manifest", schemas.manifest.validate, manifest);
    schemaCheck(name + " unknown manifest authority denied", schemas.manifest.validate, { ...manifest, execute: "arbitrary.exe" }, false);
    check(name + " entry exists", fs.existsSync(path.join(directory, manifest.entry)));
    for (const file of fs.readdirSync(directory).filter(x => x.endsWith(".js"))) { const p = path.join(directory, file); read(p); const syntax = cp.spawnSync(process.execPath, ["--check", p], { encoding: "utf8", windowsHide: true }); check(name + "/" + file + " JavaScript parses", syntax.status === 0, syntax.stderr); }
  }
  const manifest = json(path.join(templateDir, "hello-app/manifest.json"));
  const store = { schemaVersion: 1, appId: manifest.appId, name: manifest.name, description: "本地合同测试", category: "sq", developer: { name: "派蒙" }, screenshots: [], offlineCapable: true };
  const release = { schemaVersion: 1, appId: manifest.appId, version: "0.1.0", channel: "stable", runtime: "web", minHostVersion: "0.5.1", minSdkVersion: "0.3.0", entry: "index.html", asset: "hello-app.autumn", bytes: 1024, sha256: "a".repeat(64), permissions: [], saveFormatVersion: 1 };
  schemaCheck("store valid", schemas.store.validate, store); schemaCheck("store invalid category", schemas.store.validate, { ...store, category: "verified" }, false);
  schemaCheck("release valid", schemas.release.validate, release); schemaCheck("release rejects source ZIP", schemas.release.validate, { ...release, asset: "source.zip" }, false); schemaCheck("release rejects traversal", schemas.release.validate, { ...release, entry: "../index.html" }, false);
  const update = { schemaVersion: 1, productId: "cn.labchronicles.autumnos", repository: "paimeng5201314/autumnlab", channel: "plus", version: "0.5.2", buildId: "contract-fixture", targetRid: "win-x64", minimumUpdaterVersion: "1.0.0", sequence: 1, issuedAtUtc: "2026-10-02T00:00:00Z", expiresAtUtc: "2026-10-03T00:00:00Z", keyId: "schema-fixture", trustRootVersion: 1, payload: { asset: "payload.zip", bytes: 1024, sha256: "a".repeat(64), releaseId: 1, assetId: 1 }, files: [{ path: "AutumnOS.Client.exe", bytes: 500, sha256: "b".repeat(64) }], removeFiles: [], data: { schemaVersion: 1, minimumReadableVersion: 1, rollbackCompatible: true } };
  schemaCheck("update structural fixture", schemas.update.validate, update); schemaCheck("update foreign repository rejected", schemas.update.validate, { ...update, repository: "attacker/repo" }, false); schemaCheck("update wrong architecture rejected", schemas.update.validate, { ...update, targetRid: "linux-x64" }, false);
  const signature = { schemaVersion: 1, algorithm: "RSA-PSS-SHA256", keyId: "schema-fixture", trustRootVersion: 1, signature: "A".repeat(512) };
  schemaCheck("signature structural fixture only", schemas.signature.validate, signature); schemaCheck("unsigned algorithm rejected", schemas.signature.validate, { ...signature, algorithm: "none" }, false);
  schemaCheck("empty production roots valid fail-closed configuration", schemas.trust.validate, { schemaVersion: 1, version: 1, purpose: "production", keys: [] }); schemaCheck("remote arbitrary root field rejected", schemas.trust.validate, { schemaVersion: 1, version: 1, purpose: "production", keys: [], sourceUrl: "https://attacker.invalid" }, false);
  schemaCheck("native public configuration structural fixture", schemas.native.validate, { schemaVersion: 1, Authority: "https://fixture.invalid/oidc", MetadataAddress: "https://fixture.invalid/oidc/.well-known/openid-configuration", ClientId: "independent-native-fixture", RedirectUri: "http://127.0.0.1:17854/callback/", Resource: "https://resource.fixture.invalid", RequiredScope: "player:read" });
  schemaCheck("empty native template stays invalid", schemas.native.validate, json(path.join(nativeDir, "config.example.json")), false);
  const api = json(path.join(backendDir, "openapi.json"));
  const parsedApi = await SwaggerParser.validate(structuredClone(api), { resolve: { external: false } });
  check("OpenAPI 3.1 actual parser validation", parsedApi.openapi === "3.1.0");
  const apiExamples = { Health: { service: "AutumnOS independent identity example", producer: "派蒙", status: "configured", real_provider_verified: false }, Player: { subject: "fixture-subject", issuer: "https://fixture.invalid/oidc", clientId: "fixture-client" }, Challenge: { challenge: "a".repeat(43), expiresIn: 60 }, Confirmation: { accepted: true, effect: "test confirmation only; no score or game state modified" }, InvalidCredential: { error: "INVALID_CREDENTIAL" }, InsufficientScope: { error: "INSUFFICIENT_SCOPE" }, KeysUnavailable: { error: "KEYS_UNAVAILABLE" }, CapacityReached: { error: "CAPACITY_REACHED" }, ChallengeRejected: { error: "INVALID_OR_REPLAYED_CHALLENGE" } };
  for (const [name, example] of Object.entries(apiExamples)) { const validator = ajv.compile(api.components.schemas[name]); schemaCheck("OpenAPI " + name + " actual body contract", validator, example); schemaCheck("OpenAPI " + name + " extra secret field rejected", validator, { ...example, accessToken: "fixture-not-real" }, false); }
  for (const [route, verbs] of Object.entries(api.paths)) for (const [verb, operation] of Object.entries(verbs)) check("OpenAPI operation and status mapping " + verb + " " + route, !!operation.operationId && !!operation.responses["200"] && (route === "/health" || !!operation.responses["401"]));
  const pages = ["README.md", "product.md", "architecture.md", "core-development.md", "design.md", "first-app.md", "packages.md", "identity-permissions.md", "sdk-reference.md", "data.md", "server.md", "desktop-extension.md", "network.md", "app-publishing.md", "host-publishing.md", "troubleshooting.md", "collaboration.md"];
  for (const name of pages) {
    const file = path.join(docsDir, name), content = read(file); check("substantive versioned page " + name, content.length >= 400 && /0\.5\.1/.test(content) && !/^\s*(TODO|稍后补充)\s*$/m.test(content));
    for (const match of content.matchAll(/\[[^\]]*\]\(([^)]+)\)/g)) { const link = match[1].split("#")[0]; if (!link || /^[a-z]+:/i.test(link)) continue; check("documentation local link " + name + " -> " + link, fs.existsSync(path.resolve(path.dirname(file), decodeURIComponent(link)))); }
  }
  const saveModel = require(path.join(templateDir, "save-game/save-model.js"));
  const original = { formatVersion: 1, nickname: "本地测试", deck: ["H", "He", "C", "N", "O", "Ne", "H", "He", "C", "N", "O", "Ne"], matched: [0, 6], moves: 1 };
  const before = JSON.stringify(original), converted = saveModel.toV2(original);
  check("real save format migration preserves game and source", converted.formatVersion === 2 && converted.player.nickname === original.nickname && converted.turns === 1 && equal(saveModel.validate(converted), original) && JSON.stringify(original) === before);
  for (const bad of [{ ...original, formatVersion: 99 }, { ...original, matched: [0, 1] }, { ...original, deck: Array(12).fill("H") }, { ...original, moves: -1 }, { ...converted, player: { nickname: "a".repeat(25) } }]) { let code; try { saveModel.validate(bad); } catch (error) { code = error.code; } check("malformed save rejected without mutation " + report.checks.length, code === "SAVE_FORMAT_UNSUPPORTED" && JSON.stringify(original) === before); }
  report.status = "passed";
}
main().catch(error => { report.failure = error.message; }).finally(() => {
  report.checkCount = report.checks.length; report.passedChecks = report.checks.filter(x => x.status === "passed").length;
  report.inputs = [...new Map(report.inputs.map(x => [x.path, x])).values()];
  fs.mkdirSync(path.dirname(reportPath), { recursive: true }); fs.writeFileSync(reportPath, JSON.stringify(report, null, 2) + "\n", { flag: "wx" });
  console.log(JSON.stringify({ status: report.status, checks: report.checkCount, passed: report.passedChecks, report: reportPath, failure: report.failure }));
  process.exitCode = report.status === "passed" ? 0 : 1;
});
