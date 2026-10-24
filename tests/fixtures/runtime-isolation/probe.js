(function () {
  "use strict";
  const config = window.autumnIsolationProbeConfig || {};
  const output = document.getElementById("results"), elements = document.getElementById("probe-elements");
  const observation = {
    schemaVersion: 1, configured: false, token: config.token || null,
    pageOrigin: location.origin, documentNonce: crypto.randomUUID(),
    runtimeCapabilities: null, capabilityError: null, unsavedInput: "",
    network: { started: [], apiAvailable: {}, outcomes: {}, complete: false },
    sdk: { responses: {}, complete: false }, receivedResponseIds: [], pendingPermission: { state: "not_run" }, browserPermissions: {}, storage: {},
    csp: [], lifecycle: [], bridgeEvents: [], channelMessages: [], closeQueue: { started: false, sent: 0, replies: 0 }, aboutBlankRTC: { state: "not_run" }
  };
  const cleanup = new Set(), pending = new Map();
  let sequence = 0;
  function render() { output.value = JSON.stringify(observation); }
  function errorName(error) { return error && typeof error.name === "string" ? error.name : "Error"; }
  function compact(value) {
    if (!value || typeof value !== "object") return value;
    if (value.ok === false) return { ok: false, code: value.error && value.error.code || "INVALID_RESPONSE" };
    if (value.ok === true) return { ok: true, result: value.result };
    return { type: "unrecognized_envelope" };
  }
  function configuration() {
    if (!/^[a-f0-9]{32}$/.test(config.token || "")) throw new Error("TEST_RUNNER_TOKEN_REQUIRED");
    const base = new URL(config.httpBase);
    if (base.protocol !== "http:" || base.hostname !== "127.0.0.1" || !base.port || base.pathname !== "/" + config.token || base.search || base.hash || base.username || base.password)
      throw new Error("OWNED_LOOPBACK_OBSERVER_REQUIRED");
    for (const port of [config.tcpPort, config.udpPort]) if (!Number.isInteger(Number(port)) || Number(port) < 1024 || Number(port) > 65535)
      throw new Error("OWNED_TRANSPORT_PORT_REQUIRED");
    return base.href;
  }
  let base;
  try { base = configuration(); observation.configured = true; }
  catch (error) { observation.configurationError = error.message; render(); return; }
  const deadline = Math.min(8000, Math.max(1000, Number(config.deadlineMs) || 3500));
  const bridge = window.chrome && window.chrome.webview;
  if (bridge && typeof bridge.addEventListener === "function") {
    bridge.addEventListener("message", event => {
      const value = event.data;
      // Record every response, including unmatched IDs. A new document must not silently
      // discard an old session's delayed reply and let the driver call that isolation.
      if (value && typeof value.requestId === "string") {
        if (observation.receivedResponseIds.length < 256) observation.receivedResponseIds.push(value.requestId);
        render();
      }
      if (value && typeof value === "object" && typeof value.requestId === "string" && pending.has(value.requestId)) {
        pending.get(value.requestId)(value); return;
      }
      // Exact-envelope rejection deliberately returns requestId:null. This fixture sends
      // malformed probes serially, so only a sole outstanding call may consume that reply.
      if (value && value.requestId === null && value.ok === false && pending.size === 1) {
        pending.values().next().value(value); return;
      }
      if (value && typeof value === "object" && typeof value.event === "string") {
        observation.bridgeEvents.push(value.event);
        if (value.event.startsWith("lifecycle.")) observation.lifecycle.push({ event: value.event, state: value.state || value.data && value.data.state });
        render();
      }
    });
  }
  function request(method, parameters = {}, changes = {}, fixedId, responseTimeout = deadline) {
    const requestId = fixedId || "probe_" + observation.documentNonce.slice(0, 8) + "_" + config.token.slice(0, 10) + "_" + (++sequence);
    const envelope = Object.assign({ protocolVersion: 1, requestId, method, params: parameters }, changes);
    return new Promise(resolve => {
      if (!bridge || typeof bridge.postMessage !== "function") { resolve({ ok: false, error: { code: "HOST_UNAVAILABLE" } }); return; }
      const timer = setTimeout(() => { pending.delete(requestId); resolve({ ok: false, error: { code: "PROBE_RESPONSE_TIMEOUT" } }); }, responseTimeout);
      pending.set(requestId, value => { clearTimeout(timer); pending.delete(requestId); resolve(value); });
      try { bridge.postMessage(envelope); }
      catch (error) { clearTimeout(timer); pending.delete(requestId); resolve({ ok: false, error: { code: errorName(error) } }); }
    });
  }
  request("platform.getCapabilities").then(value => {
    if (value.ok) observation.runtimeCapabilities = value.result;
    else observation.capabilityError = value.error && value.error.code;
    render();
  });
  document.addEventListener("securitypolicyviolation", event => {
    if (observation.csp.length < 120) observation.csp.push({ directive: event.effectiveDirective, blocked: event.blockedURI });
    render();
  });
  document.getElementById("unsaved").addEventListener("input", event => { observation.unsavedInput = event.target.value; render(); });
  document.getElementById("pending-permission").addEventListener("click", async event => {
    event.target.disabled = true;
    const requestId = "pending_" + observation.documentNonce.replaceAll("-", "");
    observation.pendingPermission = { state: "sent", requestId, trustedClick: event.isTrusted }; render();
    const response = await request("permissions.request", { name: "saves" }, {}, requestId, 60000);
    observation.pendingPermission = { state: "settled", requestId, response: compact(response) }; render();
  });
  function record(name, outcome) { observation.network.outcomes[name] = outcome; render(); }
  function begin(name, available = true) {
    observation.network.started.push(name); observation.network.apiAvailable[name] = !!available; render();
    return base + "/probe/" + name;
  }
  async function attempt(name, available, operation) {
    const url = begin(name, available);
    let timer;
    try { record(name, await Promise.race([Promise.resolve().then(() => operation(url)), new Promise(resolve => { timer = setTimeout(() => resolve({ state: "no_completion" }), deadline + 750); })])); }
    catch (error) { record(name, { state: "exception", name: errorName(error) }); }
    finally { clearTimeout(timer); }
  }
  function elementAttempt(element, url, target = "src", append = true) {
    return new Promise(resolve => {
      let finished = false;
      const settle = state => { if (finished) return; finished = true; clearTimeout(timer); element.remove(); resolve({ state }); };
      const timer = setTimeout(() => settle("no_completion"), deadline);
      element.onload = () => settle("loaded"); element.onerror = () => settle("error");
      element[target] = url;
      if (append) elements.appendChild(element);
    });
  }
  async function rtcAttempt(name, server) {
    await attempt(name, typeof RTCPeerConnection === "function", async () => {
      const connection = new RTCPeerConnection({ iceServers: [server], iceTransportPolicy: server.urls.startsWith("stun:") ? "all" : "relay", iceCandidatePoolSize: 0 });
      const close = () => connection.close(); cleanup.add(close);
      const candidates = [], errors = [];
      connection.onicecandidate = event => { if (event.candidate) candidates.push(event.candidate.type || "unknown"); };
      connection.onicecandidateerror = event => errors.push({ code: event.errorCode, url: event.url });
      connection.createDataChannel("owned-negative-probe");
      try {
        await connection.setLocalDescription(await connection.createOffer());
        await new Promise(resolve => setTimeout(resolve, deadline));
        return { state: "ice_attempted", gatheringState: connection.iceGatheringState, candidates, errors };
      } finally { close(); cleanup.delete(close); }
    });
  }
  async function directRemoteIce(NativePeer) {
    const peers = [], candidates = [], close = () => peers.forEach(peer => peer.close()); cleanup.add(close);
    try {
      const offerer = new NativePeer({ iceServers: [], iceTransportPolicy: "all" }); peers.push(offerer);
      offerer.createDataChannel("owned-direct-ice-probe");
      // createOffer produces native ICE credentials, without starting this peer's ICE gathering.
      // Never call setLocalDescription on offerer: only answerer may contact the supplied candidate.
      const offer = await offerer.createOffer();
      const ufrag = /^a=ice-ufrag:([^\r\n]+)/m.exec(offer.sdp), mid = /^a=mid:([^\r\n]+)/m.exec(offer.sdp);
      if (!ufrag || !mid || offerer.localDescription !== null) return { state: "invalid_native_offer", candidateResult: "not_attempted" };
      const answerer = new NativePeer({ iceServers: [], iceTransportPolicy: "all" }); peers.push(answerer);
      answerer.onicecandidate = event => { if (event.candidate) candidates.push(event.candidate.type || "unknown"); };
      await answerer.setRemoteDescription(offer);
      await answerer.setLocalDescription(await answerer.createAnswer());
      let candidateResult = "accepted", candidateError = null;
      try {
        await answerer.addIceCandidate({ candidate: "candidate:ownedprobe 1 UDP 2130706431 127.0.0.1 " + config.udpPort + " typ host generation 0 ufrag " + ufrag[1],
          sdpMid: mid[1], sdpMLineIndex: 0, usernameFragment: ufrag[1] });
      } catch (error) { candidateResult = "rejected"; candidateError = errorName(error); }
      await new Promise(resolve => setTimeout(resolve, Math.min(2000, deadline)));
      return { state: "remote_candidate_attempted", candidateResult, candidateError, candidateTarget: "127.0.0.1", candidatePort: Number(config.udpPort),
        offererLocalDescriptionUnset: offerer.localDescription === null, iceConnectionState: answerer.iceConnectionState, connectionState: answerer.connectionState, candidates,
        observationLimit: candidateResult === "rejected" ? "Browser rejected supplied candidate; this does not demonstrate an active P2P check reached the observer." : "Candidate accepted; actual direct UDP delivery is determined only by the owned observer." };
    } finally { close(); cleanup.delete(close); }
  }
  document.getElementById("network").addEventListener("click", async event => {
    event.target.disabled = true;
    if (config.allowNetworkAttempts !== true) { observation.network.disabledByRunner = true; render(); return; }
    const tasks = [];
    tasks.push(attempt("fetch", typeof fetch === "function", async url => {
      const controller = new AbortController(), timer = setTimeout(() => controller.abort(), deadline);
      try { const response = await fetch(url, { signal: controller.signal, cache: "no-store" }); return { state: "response", status: response.status }; }
      finally { clearTimeout(timer); }
    }));
    tasks.push(attempt("xhr", typeof XMLHttpRequest === "function", url => new Promise(resolve => {
      const xhr = new XMLHttpRequest(); xhr.open("POST", url); xhr.timeout = deadline;
      xhr.onload = () => resolve({ state: "response", status: xhr.status }); xhr.onerror = () => resolve({ state: "error" });
      xhr.ontimeout = () => resolve({ state: "timeout" });
      try { xhr.send("owned-negative-probe"); } catch (error) { resolve({ state: "exception", name: errorName(error) }); }
    })));
    tasks.push(attempt("beacon", typeof navigator.sendBeacon === "function", url => ({ state: navigator.sendBeacon(url, "owned-negative-probe") ? "queued" : "rejected" })));
    tasks.push(attempt("websocket", typeof WebSocket === "function", url => new Promise(resolve => {
      let socket, finished = false;
      const settle = state => { if (finished) return; finished = true; clearTimeout(timer); if (socket) socket.close(); resolve({ state }); };
      const timer = setTimeout(() => settle("timeout"), deadline);
      try { socket = new WebSocket(url.replace("http:", "ws:")); socket.onopen = () => settle("opened"); socket.onerror = () => settle("error"); }
      catch (error) { settle(errorName(error)); }
    })));
    tasks.push(attempt("eventsource", typeof EventSource === "function", url => new Promise(resolve => {
      let source, finished = false;
      const settle = state => { if (finished) return; finished = true; clearTimeout(timer); if (source) source.close(); resolve({ state }); };
      const timer = setTimeout(() => settle("timeout"), deadline);
      try { source = new EventSource(url); source.onopen = () => settle("opened"); source.onerror = () => settle("error"); }
      catch (error) { settle(errorName(error)); }
    })));
    tasks.push(attempt("img", typeof Image === "function", url => elementAttempt(new Image(), url)));
    tasks.push(attempt("remote-script", true, url => elementAttempt(document.createElement("script"), url)));
    tasks.push(attempt("module-import", true, async url => { await import(url); return { state: "loaded" }; }));
    tasks.push(attempt("css-link", true, url => { const link = document.createElement("link"); link.rel = "stylesheet"; return elementAttempt(link, url, "href"); }));
    tasks.push(attempt("css-nested", true, async () => {
      const link = document.createElement("link"); link.rel = "stylesheet";
      const box = document.createElement("div"); box.className = "probe-background probe-font"; box.textContent = "owned probe"; elements.appendChild(box);
      const stylesheet = await new Promise(resolve => {
        const timer = setTimeout(() => resolve("no_completion"), deadline);
        link.onload = () => { clearTimeout(timer); resolve("loaded"); }; link.onerror = () => { clearTimeout(timer); resolve("error"); };
        link.href = "network.css"; document.head.appendChild(link);
      });
      // Force style resolution while the sheet is still attached so the background
      // and font URLs are real attempted subresources, independently of @import.
      getComputedStyle(box).backgroundImage; box.getBoundingClientRect();
      await new Promise(resolve => setTimeout(resolve, 200)); box.remove(); link.remove(); return { state: "local_css_attempted", stylesheet };
    }));
    tasks.push(attempt("fontface", typeof FontFace === "function", async url => { await new FontFace("OwnedProbeRemote", "url(" + url + ")").load(); return { state: "loaded" }; }));
    for (const tag of ["audio", "video"]) tasks.push(attempt(tag, true, url => elementAttempt(document.createElement(tag), url)));
    tasks.push(attempt("iframe", true, url => { const frame = document.createElement("iframe"); frame.title = "owned negative frame"; return elementAttempt(frame, url); }));
    tasks.push(attempt("form-frame", true, async url => {
      const frame = document.createElement("iframe"), form = document.createElement("form");
      frame.name = "owned_negative_" + config.token; form.target = frame.name; form.action = url; form.method = "post";
      elements.append(frame, form); try { form.submit(); await new Promise(resolve => setTimeout(resolve, deadline)); return { state: "submitted" }; }
      finally { form.remove(); frame.remove(); }
    }));
    for (const rel of ["prefetch", "preconnect", "dns-prefetch"]) tasks.push(attempt(rel, true, async url => {
      const link = document.createElement("link"); link.rel = rel; link.href = rel === "dns-prefetch" ? "//localhost:" + new URL(base).port : url;
      document.head.appendChild(link); await new Promise(resolve => setTimeout(resolve, deadline)); link.remove(); return { state: "hint_inserted_no_completion_signal" };
    }));
    tasks.push(attempt("worker-local", typeof Worker === "function", () => new Promise(resolve => {
      const worker = new Worker("worker.js"), stop = () => worker.terminate(); cleanup.add(stop);
      const finish = result => { clearTimeout(timer); stop(); cleanup.delete(stop); resolve(result); };
      const timer = setTimeout(() => finish({ state: "timeout" }), deadline);
      worker.onmessage = event => finish({ state: "worker_started", result: event.data }); worker.onerror = () => finish({ state: "error" }); worker.postMessage({ base });
    })));
    tasks.push(attempt("worker-blob", typeof Worker === "function", async () => {
      const url = URL.createObjectURL(new Blob(["self.postMessage('owned-worker-started')"], { type: "text/javascript" }));
      try { const worker = new Worker(url); worker.terminate(); return { state: "constructed" }; } finally { URL.revokeObjectURL(url); }
    }));
    tasks.push(attempt("shared-worker", typeof SharedWorker === "function", () => {
      const worker = new SharedWorker("worker.js"); worker.port.close(); return { state: "constructed" };
    }));
    tasks.push(attempt("service-worker", !!navigator.serviceWorker, async () => {
      const registration = await navigator.serviceWorker.register("service-worker.js");
      const removed = await registration.unregister(); return { state: "registered", removed };
    }));
    tasks.push(rtcAttempt("rtc-stun-udp", { urls: "stun:127.0.0.1:" + config.udpPort }));
    tasks.push(rtcAttempt("rtc-turn-udp", { urls: "turn:127.0.0.1:" + config.udpPort + "?transport=udp", username: config.token, credential: "owned-fixture-only" }));
    tasks.push(rtcAttempt("rtc-turn-tcp", { urls: "turn:127.0.0.1:" + config.tcpPort + "?transport=tcp", username: config.token, credential: "owned-fixture-only" }));
    tasks.push(attempt("rtc-direct-udp", typeof RTCPeerConnection === "function", () => directRemoteIce(RTCPeerConnection)));
    tasks.push(attempt("webtransport", typeof WebTransport === "function", async url => {
      const transport = new WebTransport(url.replace("http:", "https:")), close = () => transport.close(); cleanup.add(close);
      try { await Promise.race([transport.ready, new Promise((_, reject) => setTimeout(() => reject(new DOMException("probe deadline", "TimeoutError")), deadline))]); return { state: "ready" }; }
      finally { close(); cleanup.delete(close); }
    }));
    // A top-level bounded join prevents an API without a completion event from hanging the native driver.
    await Promise.all(tasks.map(task => Promise.race([task, new Promise(resolve => setTimeout(resolve, deadline + 1500))])));
    observation.network.complete = true; render();
  });
  document.getElementById("sdk").addEventListener("click", async event => {
    event.target.disabled = true;
    const probes = [
      ["saves-query", "permissions.query", { name: "saves" }],
      ["save-without-permission", "saves.write", { slot: "game", value: { ownedProbe: config.token } }],
      ["read-without-permission", "saves.read", { slot: "game" }],
      ["storage-without-declaration", "storage.read", { key: "owned-probe" }],
      ["file-without-declaration", "files.read", { handle: "forged-owned-handle", offset: 0, length: 1 }],
      ["forged-app-identity", "platform.getCapabilities", {}, { appId: "cn.labchronicles.chemleap", sourceKey: "forged-owned-source", accountKey: "forged-owned-account" }],
      ["forged-permission-param", "saves.write", { slot: "game", value: { ownedProbe: config.token }, permissionGranted: true, appId: "cn.labchronicles.chemleap" }],
      ["invalid-protocol", "platform.getCapabilities", {}, { protocolVersion: 999 }],
      ["unknown-native-command", "system.execute", { command: "owned-probe-no-command" }],
      ["host-token-unavailable", "identity.getToken", {}]
    ];
    for (const [name, method, parameters, changes] of probes) observation.sdk.responses[name] = compact(await request(method, parameters, changes));
    const duplicateId = "dup_" + config.token.slice(0, 16);
    observation.sdk.responses["duplicate-first"] = compact(await request("platform.getCapabilities", {}, {}, duplicateId));
    observation.sdk.responses["duplicate-second"] = compact(await request("platform.getCapabilities", {}, {}, duplicateId));
    // Default fixture has no declarations, so no prompt should occur. A separate saves-declared
    // package lets the native driver deny the real host prompt and repeat the request below.
    observation.sdk.responses["permission-request-first"] = compact(await request("permissions.request", { name: "saves" }));
    observation.sdk.responses["permission-request-repeat"] = compact(await request("permissions.request", { name: "saves" }));
    observation.sdk.responses["save-after-denial"] = compact(await request("saves.write", { slot: "game", value: { ownedProbe: config.token } }));
    observation.sdk.complete = true; render();
  });
  document.getElementById("permissions").addEventListener("click", async event => {
    event.target.disabled = true;
    const test = async (name, available, action) => {
      if (!available) { observation.browserPermissions[name] = { state: "api_unavailable" }; return; }
      try { observation.browserPermissions[name] = await action(); }
      catch (error) { observation.browserPermissions[name] = { state: "exception", name: errorName(error) }; } render();
    };
    await Promise.all([
      test("geolocation", !!navigator.geolocation, () => new Promise(resolve => navigator.geolocation.getCurrentPosition(
        () => resolve({ state: "granted" }), error => resolve({ state: "denied", code: error.code }), { timeout: deadline, maximumAge: 0 }))),
      test("camera", !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia), async () => {
        const stream = await navigator.mediaDevices.getUserMedia({ video: true }); stream.getTracks().forEach(track => track.stop()); return { state: "granted" };
      }),
      test("microphone", !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia), async () => {
        const stream = await navigator.mediaDevices.getUserMedia({ audio: true }); stream.getTracks().forEach(track => track.stop()); return { state: "granted" };
      }),
      test("notification", typeof Notification !== "undefined", async () => ({ state: await Notification.requestPermission() }))
    ]);
    observation.browserPermissions.complete = true; render();
  });
  document.getElementById("storage").addEventListener("click", async event => {
    event.target.disabled = true;
    observation.storage.complete = false;
    const marker = config.token + "/" + observation.documentNonce;
    observation.storage.marker = marker;
    render();
    for (const name of ["localStorage", "sessionStorage"]) {
      try { const store = window[name]; observation.storage[name] = { previous: store.getItem("autumnos.isolation.fixture") }; store.setItem("autumnos.isolation.fixture", marker); observation.storage[name].after = store.getItem("autumnos.isolation.fixture"); }
      catch (error) { observation.storage[name] = { error: errorName(error) }; }
    }
    try { observation.storage.cookieBefore = document.cookie; document.cookie = "autumnos_owned_probe=" + encodeURIComponent(marker) + "; SameSite=Strict; Secure"; observation.storage.cookieAfter = document.cookie; }
    catch (error) { observation.storage.cookieError = errorName(error); }
    try {
      const request = operation => new Promise((resolve, reject) => { operation.onsuccess = () => resolve(operation.result); operation.onerror = () => reject(operation.error); });
      const opening = indexedDB.open("autumnos-owned-isolation-fixture", 1);
      opening.onupgradeneeded = () => opening.result.createObjectStore("values");
      const database = await request(opening);
      try {
        const before = await request(database.transaction("values", "readonly").objectStore("values").get("marker"));
        const transaction = database.transaction("values", "readwrite");
        const completed = new Promise((resolve, reject) => { transaction.oncomplete = resolve; transaction.onerror = () => reject(transaction.error); transaction.onabort = () => reject(transaction.error); });
        transaction.objectStore("values").put(marker, "marker"); await completed;
        const after = await request(database.transaction("values", "readonly").objectStore("values").get("marker"));
        observation.storage.indexedDb = { before: before === undefined ? null : before, after };
      } finally { database.close(); }
    } catch (error) { observation.storage.indexedDb = { error: errorName(error) }; }
    try {
      const cache = await caches.open("autumnos-owned-isolation-fixture"), key = new URL("owned-cache-marker", location.href).href;
      const previous = await cache.match(key), before = previous ? await previous.text() : null;
      await cache.put(new Request(key), new Response(marker));
      const next = await cache.match(key);
      observation.storage.cache = { before, after: next ? await next.text() : null };
    } catch (error) { observation.storage.cache = { error: errorName(error) }; }
    try {
      const hex = "0061736d0100000001070160027f7f017f030201000707010361646400000a09010700200020016a0b";
      const bytes = Uint8Array.from(hex.match(/../g), value => parseInt(value, 16));
      const module = await WebAssembly.instantiate(bytes); observation.storage.wasm = module.instance.exports.add(20, 22);
    } catch (error) { observation.storage.wasmError = errorName(error); }
    try { eval("1+1"); observation.storage.evalBlocked = false; }
    catch (error) { observation.storage.evalBlocked = errorName(error) === "EvalError"; }
    try {
      const channel = new BroadcastChannel("autumnos-owned-cross-instance-probe");
      channel.onmessage = event => { if (observation.channelMessages.length < 10) observation.channelMessages.push({ token: event.data && event.data.token }); render(); };
      channel.postMessage({ token: config.token }); cleanup.add(() => channel.close()); observation.storage.broadcastChannel = "opened";
    } catch (error) { observation.storage.broadcastChannel = errorName(error); }
    observation.storage.complete = true; render(); event.target.disabled = false;
  });
  document.getElementById("popup").addEventListener("click", () => {
    const url = begin("window-open"); try { record("window-open", { state: window.open(url, "_blank") === null ? "null" : "returned-window-reference" }); }
    catch (error) { record("window-open", { state: errorName(error) }); }
  });
  document.getElementById("navigation").addEventListener("click", () => {
    const url = begin("top-navigation"); record("top-navigation", { state: "requested" }); location.assign(url);
  });
  document.getElementById("form-navigation").addEventListener("click", () => {
    const url = begin("form-top-navigation"), form = document.createElement("form"); form.action = url; form.method = "post";
    elements.appendChild(form); record("form-top-navigation", { state: "requested" }); form.submit(); form.remove();
  });
  document.getElementById("download").addEventListener("click", () => {
    begin("blob-download"); const url = URL.createObjectURL(new Blob(["owned-isolation-probe"], { type: "application/octet-stream" }));
    const link = document.createElement("a"); link.href = url; link.download = "autumnos-owned-isolation-probe.txt"; elements.appendChild(link);
    record("blob-download", { state: "requested" }); link.click(); link.remove(); setTimeout(() => URL.revokeObjectURL(url), deadline);
  });
  document.getElementById("external").addEventListener("click", () => {
    begin("unregistered-protocol"); record("unregistered-protocol", { state: "requested_unregistered_handler_only" });
    location.assign("autumnos-owned-unregistered-probe-" + config.token + ":test");
  });
  document.getElementById("queue-close").addEventListener("click", event => {
    event.target.disabled = true; observation.closeQueue.started = true;
    const stop = () => clearInterval(timer); let count = 0;
    const timer = setInterval(() => {
      if (++count > 30) { stop(); cleanup.delete(stop); return; }
      observation.closeQueue.sent++; render(); request("lifecycle.getState").then(value => { observation.closeQueue.replies++; observation.closeQueue.last = compact(value); render(); });
    }, 50); cleanup.add(stop); render();
  });
  // Optional separate crash/fail-closed test. It is intentionally absent from the main
  // network button: a broken initial-about:blank policy must not erase later UI evidence.
  const blankButton = document.createElement("button"); blankButton.id = "aboutblank-rtc"; blankButton.textContent = "尝试初始 about:blank RTC";
  document.getElementById("queue-close").parentNode.appendChild(blankButton);
  blankButton.addEventListener("click", async () => {
    blankButton.disabled = true; observation.aboutBlankRTC = { state: "requested", mayCrashRenderer: true }; render();
    const frame = document.createElement("iframe"); frame.title = "owned initial aboutblank RTC probe"; elements.appendChild(frame);
    try {
      const NativePeer = frame.contentWindow && frame.contentWindow.RTCPeerConnection;
      if (typeof NativePeer !== "function") observation.aboutBlankRTC = { state: "api_unavailable" };
      else observation.aboutBlankRTC = await directRemoteIce(NativePeer);
    } catch (error) { observation.aboutBlankRTC = { state: "exception", name: errorName(error) }; }
    finally { frame.remove(); render(); }
  });
  window.addEventListener("pagehide", () => { cleanup.forEach(close => { try { close(); } catch (_) {} }); });
  render();
}());
