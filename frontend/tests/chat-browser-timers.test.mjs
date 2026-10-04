import test from 'node:test';
import assert from 'node:assert/strict';
import { ChatController } from '../src/features/chat/ChatController.ts';
import { BoardChatTransport } from '../src/features/chat/chatTransport.ts';
import { HubConnectionState } from '@microsoft/signalr';

test('default retry timers do not use chat objects as the browser native timer receiver', async () => {
  const originalSet = globalThis.setTimeout, originalClear = globalThis.clearTimeout;
  const jobs = new Set();
  // Native browser timers reject an unrelated receiver; Node timers do not.
  globalThis.setTimeout = function (callback, delay) {
    assert.ok(this === undefined || this === globalThis, 'native timer receiver must be global');
    const job = { callback, delay }; jobs.add(job); return job;
  };
  globalThis.clearTimeout = function (job) {
    assert.ok(this === undefined || this === globalThis, 'native timer receiver must be global'); jobs.delete(job);
  };
  const noop = () => {};
  const controller = new ChatController('board', 'user', { async history() { throw Error('offline'); }, async send() { throw Error(); } },
    () => ({ start: noop, rejoin: noop, async stop() {} }));
  const transport = new BoardChatTransport('board', { state: HubConnectionState.Disconnected,
    async start() { throw Error('offline'); }, async stop() {}, async invoke() {}, on: noop, onclose: noop, onreconnected: noop, onreconnecting: noop },
    { status: noop, joined: noop, message: noop, revoked: noop, forbidden: noop });
  try {
    controller.activate(); transport.start(); await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual([...jobs].map(job => job.delay).sort((a,b) => a-b), [1000, 5000]);
    controller.stop(); await transport.stop(); assert.equal(jobs.size, 0);
  } finally { controller.stop(); await transport.stop(); globalThis.setTimeout = originalSet; globalThis.clearTimeout = originalClear; }
});
