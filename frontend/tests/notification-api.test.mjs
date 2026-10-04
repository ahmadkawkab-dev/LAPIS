import assert from 'node:assert/strict';
import test from 'node:test';
import { build } from 'esbuild';

const compiled = await build({
  entryPoints: [new URL('../src/api.ts', import.meta.url).pathname],
  bundle: true, write: false, format: 'esm', platform: 'node',
  plugins: [{ name: 'notification-test-auth', setup(builder) {
    builder.onResolve({ filter: /^\.\/auth$/ }, () => ({ path: 'auth', namespace: 'test' }));
    builder.onLoad({ filter: /.*/, namespace: 'test' }, () => ({ contents: `
      export class AuthApiError extends Error { constructor(code, status, details = []) { super(code); this.code = code; this.status = status; this.details = details; } }
      export const apiFetch = (...args) => globalThis.notificationTestFetch(...args);
    ` }));
  } }],
});
const { notificationApi, errorMessage } = await import(`data:text/javascript;base64,${Buffer.from(compiled.outputFiles[0].text).toString('base64')}`);

test('notification preferences preserve revisions and use authenticated transport with cancellation', async () => {
  const calls = [];
  const controller = new AbortController();
  globalThis.notificationTestFetch = async (path, init) => { calls.push({ path, init }); return Response.json({ revision: 2 }); };
  try {
    await notificationApi.preferences(controller.signal);
    await notificationApi.savePreferences({ soundsMuted: true }, 1, controller.signal);
    await notificationApi.saveBoardPreferences('board/id', { mode: 'mentionsAndReplies', soundsMuted: false, mutedUntil: null, revision: 3 });
    assert.equal(calls[0].init.signal, controller.signal);
    assert.equal(calls[1].init.signal, controller.signal);
    assert.deepEqual(JSON.parse(calls[1].init.body), { settings: { soundsMuted: true }, revision: 1 });
    assert.equal(calls[2].path, '/api/boards/board%2Fid/notification-preferences');
    assert.deepEqual(JSON.parse(calls[2].init.body), { mode: 'mentionsAndReplies', soundsMuted: false, mutedUntil: null, revision: 3 });
    assert.ok(calls.every(({ init }) => !init.body || !('userId' in JSON.parse(init.body))));
  } finally { delete globalThis.notificationTestFetch; }
});

test('read and dismiss carry the observed revision and preference conflicts remain actionable', async () => {
  const paths = [];
  globalThis.notificationTestFetch = async (path) => { paths.push(path); return new Response(null, { status: 204 }); };
  try {
    await notificationApi.read('notice/id', 2);
    await notificationApi.dismiss('notice/id', 2);
    assert.deepEqual(paths, ['/api/notifications/notice%2Fid/read?revision=2', '/api/notifications/notice%2Fid/dismiss?revision=2']);
    globalThis.notificationTestFetch = async () => Response.json({ code: 'notification_preferences_changed' }, { status: 409 });
    await assert.rejects(notificationApi.savePreferences({}, 1), error =>
      error.status === 409 && /another session/.test(errorMessage(error)));
  } finally { delete globalThis.notificationTestFetch; }
});
