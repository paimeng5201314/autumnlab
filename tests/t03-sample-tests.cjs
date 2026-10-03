// Isolated DOM/SDK test doubles. These tests are not WinUI, filesystem or Logto acceptance.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { webcrypto } = require('node:crypto');
const root = path.resolve(__dirname, '..');
const html = fs.readFileSync(path.join(root, 'samples/element-pairs/index.html'), 'utf8');
const source = fs.readFileSync(path.join(root, 'samples/element-pairs/t03-features.js'), 'utf8');
function element(id) {
  return { id, textContent: '', value: '', disabled: true, dataset: {}, style: {}, handlers: {},
    addEventListener(name, callback) { this.handlers[name] = callback; }, focus() { this.focused = true; } };
}
async function fixture(custom = () => undefined) {
  const elements = new Map([...html.matchAll(/\bid="([^"]+)"/g)].map(match => [match[1], element(match[1])]));
  const controls = [...html.matchAll(/<button\b([^>]*\bid="t03-[^"]+"[^>]*)>/g)].map(match => {
    const id = /\bid="([^"]+)"/.exec(match[1])[1], value = elements.get(id);
    for (const data of match[1].matchAll(/data-(capability|permission)="([^"]+)"/g)) value.dataset[data[1]] = data[2];
    return value;
  });
  elements.get('pairs-count').textContent = '2/6'; elements.get('moves-count').textContent = '5';
  const page = element('root'), app = element('app'), card = element('card'), calls = [], events = new Map(), unsubscriptions = [];
  let lifecycle;
  const capabilities = ['saves', 'identity.profile', 'storage', 'preferences', 'files.open', 'files.save', 'appearance', 'notifications', 'shortcuts', 'widgets', 'links.internal'];
  const sdk = {
    async request(method, params, options) {
      calls.push({ method, params, options }); const answer = custom(method, params, options);
      if (answer !== undefined) return answer;
      if (method === 'platform.getCapabilities') return { capabilities, accountMode: 'guest' };
      if (method === 'lifecycle.getState') return { state: 'foreground' };
      if (method === 'appearance.get') return { theme: 'dark', language: 'zh-CN', scale: 1.5, reduceMotion: true };
      throw Object.assign(new Error('Unimplemented mock'), { code: 'CAPABILITY_UNAVAILABLE' });
    },
    onLifecycle(callback) { lifecycle = callback; return () => unsubscriptions.push('lifecycle'); },
    onEvent(name, callback) { events.set(name, callback); return () => { events.delete(name); unsubscriptions.push(name); }; }
  };
  const media = { matches: false, addEventListener(name, handler) { this.handler = handler; }, removeEventListener() { this.removed = true; } };
  const document = { documentElement: page, getElementById: id => elements.get(id), querySelectorAll: () => controls,
    querySelector: selector => selector === '.app' ? app : card };
  const window = { autumn: sdk, matchMedia: () => media, devicePixelRatio: 1.5, addEventListener() {} };
  vm.runInNewContext(source, { window, document, TextEncoder, TextDecoder, AbortController, crypto: webcrypto, btoa, atob, console });
  await new Promise(resolve => setImmediate(resolve));
  return { elements, controls, calls, events, page, app, media, unsubscriptions, lifecycle: state => lifecycle(state),
    click: id => elements.get(id).handlers.click() };
}
test('sample.mock_bootstrap_reads_only_no_automatic_permissions_identity_or_write', async () => {
  const f = await fixture();
  assert.deepEqual(f.calls.map(call => call.method), ['platform.getCapabilities', 'lifecycle.getState', 'appearance.get']);
  assert.equal(f.page.dataset.autumnTheme, 'dark'); assert.equal(f.page.dataset.autumnMotion, 'reduced');
  assert.equal(f.page.lang, 'zh-CN'); assert.equal(f.app.style.zoom, '1');
  assert.ok(f.elements.get('t03-events').textContent.includes('游客'));
  assert.ok(!/<details id="t03-features"[^>]*\bopen\b/.test(html));
});
test('sample.mock_permission_request_and_picker_require_two_distinct_clicks', async () => {
  const f = await fixture(method => {
    if (method === 'permissions.request') return { name: 'files.open', state: 'granted' };
    if (method === 'files.pickOpen') return { handle: 'a'.repeat(64), name: 'chosen.txt', bytes: 5, expiresUtc: '2030-01-01' };
  });
  f.calls.length = 0;
  await f.click('t03-open-permit');
  assert.deepEqual(f.calls.map(call => call.method), ['permissions.request']);
  await f.click('t03-file-pick');
  assert.deepEqual(f.calls.map(call => call.method), ['permissions.request', 'files.pickOpen']);
  assert.equal(Object.keys(f.calls[1].params).length, 0);
  assert.ok(!f.elements.get('t03-files-result').textContent.includes('a'.repeat(64)));
});
test('sample.mock_denied_profile_does_not_report_user_or_retry_prompt', async () => {
  const f = await fixture(method => {
    if (method === 'identity.requestProfile') return Promise.reject(Object.assign(new Error('private remote description'), { code: 'PERMISSION_DENIED' }));
  });
  f.calls.length = 0; await f.click('t03-profile-request');
  assert.deepEqual(f.calls.map(call => call.method), ['identity.requestProfile']);
  assert.ok(f.elements.get('t03-profile-result').textContent.includes('PERMISSION_DENIED'));
  assert.ok(!f.elements.get('t03-profile-result').textContent.includes('private remote description'));
});
test('sample.mock_muted_and_duplicate_notifications_are_not_success', async () => {
  let reason = 'muted';
  const f = await fixture(method => method === 'notifications.show' ? { shown: false, reason } : undefined);
  await f.click('t03-notify'); assert.ok(f.elements.get('t03-notifications-result').textContent.includes('未显示'));
  reason = 'duplicate'; await f.click('t03-notify-duplicate');
  assert.ok(f.elements.get('t03-notifications-result').textContent.includes('未再次显示'));
  const sent = f.calls.filter(call => call.method === 'notifications.show');
  assert.equal(sent[0].params.id, sent[1].params.id); assert.equal(sent[0].params.action, 'resume');
});
test('sample.mock_external_export_uses_only_picked_handle_and_bounded_utf8', async () => {
  const f = await fixture((method, params) => {
    if (method === 'files.pickSave') return { handle: 'b'.repeat(64), name: 'note.json', bytes: 0, expiresUtc: '2030-01-01' };
    if (method === 'files.write') return { bytes: Buffer.from(params.data, 'base64').length, backupRetained: false };
  });
  f.elements.get('t03-note').value = '派蒙的笔记';
  await f.click('t03-file-save-pick'); assert.equal(f.calls.some(call => call.method === 'files.write'), false);
  await f.click('t03-file-export'); const write = f.calls.find(call => call.method === 'files.write');
  assert.deepEqual(Object.keys(write.params).sort(), ['data', 'handle']); assert.equal(Buffer.from(write.params.data, 'base64').toString(), '派蒙的笔记');
  await f.click('t03-file-export'); assert.equal(f.calls.filter(call => call.method === 'files.write').length, 1);
  assert.ok(f.elements.get('t03-files-result').textContent.includes('FILE_HANDLE_INVALID'));
});
test('sample.mock_revocation_clears_visible_profile_and_closing_unsubscribes', async () => {
  const f = await fixture(); f.elements.get('t03-profile-result').textContent = 'previous data';
  f.events.get('permissions.changed')({ name: 'identity.profile', state: 'revoked' });
  assert.ok(!f.elements.get('t03-profile-result').textContent.includes('previous data'));
  f.lifecycle('closed'); assert.equal(f.events.size, 0); assert.equal(f.unsubscriptions.length, 6); assert.equal(f.media.removed, true);
  assert.ok(f.controls.every(control => control.disabled));
});
