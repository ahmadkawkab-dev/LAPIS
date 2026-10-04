import assert from 'node:assert/strict';
import test from 'node:test';
import { build } from 'esbuild';

// Exercise the existing API client with only its authenticated transport replaced.
const compiled = await build({
  entryPoints: [new URL('../src/api.ts', import.meta.url).pathname],
  bundle: true, write: false, format: 'esm', platform: 'node',
  plugins: [{ name: 'authenticated-test-transport', setup(builder) {
    builder.onResolve({ filter: /^\.\/auth$/ }, () => ({ path: 'auth', namespace: 'test' }));
    builder.onLoad({ filter: /.*/, namespace: 'test' }, () => ({ contents: `
      export class AuthApiError extends Error {
        constructor(code, status) { super(code); this.code = code; this.status = status; }
      }
      export const apiFetch = (...args) => globalThis.calendarTestFetch(...args);
    ` }));
  } }],
});
const { calendarApi } = await import(`data:text/javascript;base64,${Buffer.from(compiled.outputFiles[0].text).toString('base64')}`);

test('calendar import uses the source route and authenticated transport without client ownership or task data', async () => {
  const calls = [];
  const event = { id: 'calendar-event', sourceChatMessageId: 'message' };
  globalThis.calendarTestFetch = async (path, init) => {
    calls.push({ path, init });
    return init.method === 'GET' ? new Response(null, { status: 204 }) : Response.json(event, { status: 201 });
  };
  const abort = new AbortController();
  try {
    assert.equal(await calendarApi.chatEvent('board/id', 'message', abort.signal), undefined);
    assert.deepEqual(await calendarApi.addChatEvent('board/id', 'message'), event);
    assert.deepEqual(calls.map(call => [call.path, call.init.method, call.init.body]), [
      ['/api/calendar/events/from-chat/board%2Fid/message', 'GET', undefined],
      ['/api/calendar/events/from-chat/board%2Fid/message', 'POST', undefined],
    ]);
    assert.equal(calls[0].init.signal, abort.signal);
  } finally { delete globalThis.calendarTestFetch; }
});

test('calendar import returns the existing event on replay and surfaces revoked access', async () => {
  try {
    globalThis.calendarTestFetch = async () => Response.json({ id: 'existing-event' });
    assert.equal((await calendarApi.addChatEvent('board', 'message')).id, 'existing-event');
    globalThis.calendarTestFetch = async () => new Response(null, { status: 404 });
    await assert.rejects(calendarApi.addChatEvent('board', 'message'), error => error.status === 404);
    await assert.rejects(calendarApi.chatEvent('board', 'message'), error => error.status === 404);
  } finally { delete globalThis.calendarTestFetch; }
});
