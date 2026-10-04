import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFile } from 'node:fs/promises';
const source = await readFile(new URL('../public/notification-worker.js', import.meta.url), 'utf8');
const user = '11111111-1111-1111-1111-111111111111', other = '22222222-2222-2222-2222-222222222222';
const installation = '33333333-3333-3333-3333-333333333333', notification = '44444444-4444-4444-4444-444444444444';
function worker(binding) {
  const handlers = {}, shown = [], navigated = [], posted = [];
  const stores = new Map([['session', new Map(binding ? [['binding', binding]] : [])], ['seen', new Map()]]);
  const db = { close() {}, transaction(names) {
    const tx = { objectStore(name) { const store = stores.get(name); return {
      get(key) { const req = {}; queueMicrotask(() => { req.result = store.get(key); req.onsuccess?.(); }); return req; },
      put(value, key) { store.set(key, value); },
    }; } }; setImmediate(() => tx.oncomplete?.()); return tx;
  } };
  const indexedDB = { open() { const req = {}; queueMicrotask(() => { req.result = db; req.onsuccess?.(); }); return req; } };
  const self = { addEventListener(name, listener) { handlers[name] = listener; }, location: { origin: 'https://wukna.test' },
    registration: { async showNotification(title, options) { shown.push({ title, options }); } },
    clients: { async matchAll() { return [{ url: 'https://wukna.test/home', postMessage(data) { posted.push(data); }, async navigate(path) { navigated.push(path); }, async focus() {} }]; }, async openWindow(path) { navigated.push(path); } },
  };
  vm.runInNewContext(source, { self, indexedDB, URL, Date, Number });
  const dispatch = (name, data) => new Promise((resolve, reject) => { handlers[name]({ ...data, waitUntil(promise) { promise.then(resolve, reject); } }); });
  return { shown, navigated, posted, dispatch, stores };
}
const payload = { userId: user, installationId: installation, notificationId: notification, revision: 1,
  body: 'Opted-in preview', path: `/boards/${other}?chat=1&message=${notification}`, silent: false };
test('worker uses OS notifications only, replaces duplicates and never vibrates muted notifications', async () => {
  const w = worker({ userId: user, installationId: installation });
  await w.dispatch('push', { data: { json: () => payload } });
  await w.dispatch('push', { data: { json: () => payload } });
  assert.equal(w.shown.length, 2); assert.equal(w.shown[0].options.tag, w.shown[1].options.tag);
  assert.equal(w.shown[0].options.body, 'Opted-in preview'); assert.equal(w.shown[1].options.silent, true);
  assert.equal(w.shown[0].options.renotify, false); assert.equal('vibrate' in w.shown[1].options, false);
  assert.equal(w.posted[0].type, 'wukna:push-shown');
  assert.equal(w.posted[0].alreadyHandled, false);
  assert.equal(w.posted[1].alreadyHandled, true);
});
test('a page audio claim makes subsequent push silent and tells pages to let the clip finish', async () => {
  const w = worker({ userId: user, installationId: installation });
  w.stores.get('seen').set(`${user}:${notification}:1`, Date.now());
  await w.dispatch('push', { data: { json: () => payload } });
  assert.equal(w.shown.length, 1, 'the OS notification is still displayed');
  assert.equal(w.shown[0].options.silent, true);
  assert.equal(w.posted[0].alreadyHandled, true);
});
test('account changes hide old previews and notification clicks use only safe internal routes', async () => {
  const w = worker({ userId: other, installationId: installation });
  await w.dispatch('push', { data: { json: () => payload } });
  assert.equal(w.shown[0].options.body, 'You have new activity'); assert.equal(w.shown[0].options.silent, true);
  await w.dispatch('notificationclick', { notification: { data: { userId: user, path: payload.path }, close() {} } });
  assert.equal(w.navigated.at(-1), '/notifications');
  const bound = worker({ userId: user, installationId: installation });
  await bound.dispatch('notificationclick', { notification: { data: { userId: user, path: 'https://evil.test/' }, close() {} } });
  assert.equal(bound.navigated.at(-1), '/notifications');
  await bound.dispatch('notificationclick', { notification: { data: { userId: user, path: payload.path }, close() {} } });
  assert.equal(bound.navigated.at(-1), payload.path);
});
