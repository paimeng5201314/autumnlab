(function () {
  'use strict';
  const byId = id => document.getElementById(id), input = byId('probe-input'), result = byId('probe-result');
  const formatVersion = Number(document.body.dataset.saveFormat || 1);
  let busy = false;
  async function act(operation) {
    if (busy) return;
    busy = true;
    for (const id of ['probe-save', 'probe-load', 'probe-profile-permission', 'probe-profile']) byId(id).disabled = true;
    try { await operation(); } catch (error) { result.textContent = '操作未完成：' + (error.code || 'UNKNOWN_ERROR'); }
    finally {
      busy = false;
      for (const id of ['probe-save', 'probe-load', 'probe-profile-permission', 'probe-profile']) byId(id).disabled = false;
    }
  }
  async function requireSaves() {
    let permission = await window.autumn.request('permissions.query', { name: 'saves' });
    if (permission.state === 'prompt') {
      result.textContent = '请在 AutumnOS 提示中决定是否允许本地存档。';
      permission = await window.autumn.request('permissions.request', { name: 'saves' }, { timeoutMs: 120000 });
    }
    if (permission.state !== 'granted') throw { code: permission.state === 'revoked' ? 'PERMISSION_REVOKED' : 'PERMISSION_DENIED' };
  }
  byId('probe-save').addEventListener('click', () => act(async () => {
    await requireSaves();
    await window.autumn.request('saves.write', { slot: 'probe', value: { text: input.value }, formatVersion });
    result.textContent = '已保存到独立游客测试存档。';
  }));
  byId('probe-load').addEventListener('click', () => act(async () => {
    await requireSaves();
    const saved = await window.autumn.request('saves.read', { slot: 'probe' });
    if (saved.exists && saved.value && typeof saved.value.text === 'string') {
      input.value = saved.value.text.slice(0, 400); result.textContent = '已读回此前保存的测试文字。';
    } else result.textContent = '还没有保存测试文字。';
  }));
  byId('probe-profile-permission').addEventListener('click', () => act(async () => {
    const permission = await window.autumn.request('permissions.request', { name: 'identity.profile' }, { timeoutMs: 120000 });
    result.textContent = '资料权限：' + permission.state;
  }));
  byId('probe-profile').addEventListener('click', () => act(async () => {
    const profile = await window.autumn.request('identity.requestProfile', {}, { timeoutMs: 120000 });
    // Only an actual host response supplies these fields. Never create a fake guest profile.
    result.textContent = '已获得最小资料：' + profile.displayName + (profile.isCached ? '（缓存）' : '');
  }));
  function lifecycle(state) {
    byId('probe-instance').textContent = '宿主生命周期：' + state;
    result.textContent = state === 'foreground' ? '可以输入文字、保存或读档。返回桌面时页面输入继续保留。' : '应用状态：' + state;
  }
  window.autumn.onLifecycle(lifecycle);
  act(async () => {
    const state = await window.autumn.request('lifecycle.getState');
    lifecycle(state.state);
  });
}());
