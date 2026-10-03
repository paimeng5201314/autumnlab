(function () {
  'use strict';
  const base = '__BASE__', output = document.getElementById('results');
  const observations = {
    probeToken: new URL(base).pathname.split('/').pop(),
    apiAvailable: {
      fetch: typeof fetch === 'function', xhr: typeof XMLHttpRequest === 'function',
      beacon: typeof navigator.sendBeacon === 'function', websocket: typeof WebSocket === 'function',
      img: typeof Image === 'function', iframe: typeof HTMLIFrameElement === 'function'
    },
    runtimeCapabilities: null, capabilityError: null,
    started: [], outcomes: {}, csp: [], complete: false
  };
  function render() { output.value = JSON.stringify(observations); }
  document.addEventListener('securitypolicyviolation', event => {
    observations.csp.push({ directive: event.effectiveDirective, blocked: event.blockedURI }); render();
  });
  function mark(name, value) { observations.outcomes[name] = value; render(); }
  function start(name) { observations.started.push(name); render(); return base + '/probe/' + name; }
  if (window.autumn && typeof window.autumn.request === 'function') {
    window.autumn.request('platform.getCapabilities').then(value => {
      observations.runtimeCapabilities = {
        protocolVersion: value.protocolVersion, accountMode: value.accountMode,
        networkSandboxVerified: value.networkSandboxVerified
      }; render();
    }, error => { observations.capabilityError = error.code || 'SDK_REQUEST_FAILED'; render(); });
  } else { observations.capabilityError = 'HOST_UNAVAILABLE'; }
  document.getElementById('network').addEventListener('click', async () => {
    document.getElementById('network').disabled = true;
    const tasks = [];
    tasks.push((async () => {
      const controller = new AbortController(), timer = setTimeout(() => controller.abort(), 2000);
      try { const response = await fetch(start('fetch'), { signal: controller.signal }); mark('fetch', 'response:' + response.status); }
      catch (error) { mark('fetch', error.name); } finally { clearTimeout(timer); }
    })());
    tasks.push(new Promise(resolve => {
      const xhr = new XMLHttpRequest(); xhr.open('GET', start('xhr')); xhr.timeout = 2000;
      xhr.onload = () => { mark('xhr', 'response:' + xhr.status); resolve(); };
      xhr.onerror = () => { mark('xhr', 'error'); resolve(); }; xhr.ontimeout = () => { mark('xhr', 'timeout'); resolve(); };
      try { xhr.send(); } catch (error) { mark('xhr', error.name); resolve(); }
    }));
    try { mark('beacon', navigator.sendBeacon(start('beacon'), 'test-only') ? 'queued' : 'rejected'); }
    catch (error) { mark('beacon', error.name); }
    tasks.push(new Promise(resolve => {
      let socket; const url = start('websocket').replace('http:', 'ws:');
      const timer = setTimeout(() => { if (socket) socket.close(); mark('websocket', 'timeout'); resolve(); }, 2000);
      try {
        socket = new WebSocket(url);
        socket.onopen = () => { clearTimeout(timer); mark('websocket', 'opened'); socket.close(); resolve(); };
        socket.onerror = () => { clearTimeout(timer); mark('websocket', 'error'); resolve(); };
      } catch (error) { clearTimeout(timer); mark('websocket', error.name); resolve(); }
    }));
    tasks.push(new Promise(resolve => {
      const image = new Image(), timer = setTimeout(() => { mark('img', 'timeout'); resolve(); }, 2000);
      image.onload = () => { clearTimeout(timer); mark('img', 'loaded'); resolve(); };
      image.onerror = () => { clearTimeout(timer); mark('img', 'error'); resolve(); }; image.src = start('img');
    }));
    tasks.push(new Promise(resolve => {
      const frame = document.createElement('iframe'); frame.title = 'owned negative frame'; frame.hidden = true;
      const timer = setTimeout(() => { mark('iframe', 'settled'); frame.remove(); resolve(); }, 2000);
      frame.src = start('iframe'); document.body.appendChild(frame);
      frame.onerror = () => { clearTimeout(timer); mark('iframe', 'error'); frame.remove(); resolve(); };
    }));
    await Promise.all(tasks); observations.complete = true; render();
  });
  document.getElementById('popup').addEventListener('click', () => {
    const result = window.open(start('window-open'), '_blank'); mark('window-open', result === null ? 'null' : 'returned-window-reference');
  });
  document.getElementById('navigate').addEventListener('click', () => {
    const url = start('navigation'); mark('navigation', 'requested'); window.location.assign(url);
  });
  document.getElementById('download').addEventListener('click', () => {
    observations.started.push('download'); mark('download', 'local-blob-requested');
    const url = URL.createObjectURL(new Blob(['owned security fixture'], { type: 'application/octet-stream' }));
    const link = document.createElement('a'); link.href = url; link.download = 'autumnos-owned-negative-probe.txt';
    document.body.appendChild(link); link.click(); link.remove(); setTimeout(() => URL.revokeObjectURL(url), 2000);
  });
  const probeInput=document.getElementById('unreleased-input');
  if(probeInput){probeInput.addEventListener('input',()=>{observations.unsavedInput=probeInput.value;render();});}
  render();
}());
