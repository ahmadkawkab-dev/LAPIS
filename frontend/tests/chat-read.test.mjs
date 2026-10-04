import test from 'node:test';
import assert from 'node:assert/strict';
import { ChatController } from '../src/features/chat/ChatController.ts';

const at = '2026-10-03T00:00:00Z';
const message = sequence => ({ id: `m-${sequence}`, boardId: 'board', sequence: String(sequence),
  cursor: `cursor-${sequence}`, type: 'text', body: `Message ${sequence}`, createdAt: at,
  clientMessageId: `op-${sequence}`, sender: { userId: 'peer', username: 'peer', displayName: null,
    avatarUrl: null, avatarVersion: null }, attachment: null, scheduledTask: null });
const joined = (values = {}) => ({ boardId: 'board', membershipInstanceId: 'member', latestCursor: 'head',
  slowModeSeconds: 0, settingsRevision: 1, moderationRevision: 0, isMuted: false, mutedUntil: null,
  serverTime: at, nextSendAllowedAt: null, isOwner: false, lastReadSequence: '0', unreadCount: 3, ...values });
const deferred = () => { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject }; };
const settle = () => new Promise(resolve => setImmediate(resolve));
function fixture(read) {
  const calls = [], jobs = [];
  let handlers;
  const controller = new ChatController('board', 'me', {
    history: async () => ({ items: [message(1), message(2), message(3)], olderCursor: 'cursor-1',
      newerCursor: 'cursor-3', hasMore: false, catchUpThrough: 'cursor-3', serverTime: at }),
    read: async (_board, cursor, instance, signal) => { calls.push([cursor, instance]); return read(cursor, instance, signal); },
  }, (_board, value) => { handlers = value; return { start() {}, rejoin() {}, async stop() {} }; },
  { now: () => Date.parse(at), schedule: (fn, delay) => { const job = { fn, delay }; jobs.push(job); return job; },
    cancel: job => { const index = jobs.indexOf(job); if (index >= 0) jobs.splice(index, 1); } });
  controller.activate();
  return { controller, calls, jobs, get handlers() { return handlers; } };
}

test('joining restores server unread count; viewing latest alone does not mark messages read', async () => {
  const f = fixture(async () => ({ lastReadSequence: '3', unreadCount: 0 }));
  f.handlers.joined(joined()); await settle();
  assert.equal(f.controller.getSnapshot().unseen, 3);
  f.controller.setViewingLatest(true, false);
  assert.equal(f.controller.getSnapshot().unseen, 3);
  assert.equal(f.calls.length, 0);
  f.controller.markReadThrough(message(3));
  f.controller.markReadThrough(message(3)); await settle();
  assert.deepEqual(f.calls, [['cursor-3', 'member']]);
  assert.equal(f.controller.getSnapshot().lastReadSequence, '3');
  assert.equal(f.controller.getSnapshot().unseen, 0);
  f.controller.stop();
});

test('read acknowledgments coalesce forward and a stale state cannot roll back the cursor', async () => {
  const first = deferred();
  const f = fixture(async () => f.calls.length === 1 ? first.promise : { lastReadSequence: '3', unreadCount: 0 });
  f.handlers.joined(joined()); await settle();
  f.controller.markReadThrough(message(1));
  f.controller.markReadThrough(message(3));
  assert.deepEqual(f.calls, [['cursor-1', 'member']]);
  first.resolve({ lastReadSequence: '1', unreadCount: 2 }); await settle();
  assert.deepEqual(f.calls, [['cursor-1', 'member'], ['cursor-3', 'member']]);
  assert.equal(f.controller.getSnapshot().lastReadSequence, '3');
  f.handlers.joined(joined({ lastReadSequence: '1', unreadCount: 2 }));
  assert.equal(f.controller.getSnapshot().lastReadSequence, '3');
  assert.equal(f.controller.getSnapshot().unseen, 0);
  f.controller.stop();
});

test('uncertain read update retries the same cursor and stops after membership revocation', async () => {
  const f = fixture(async () => {
    if (f.calls.length === 1) throw Error('reply lost');
    return { lastReadSequence: '3', unreadCount: 0 };
  });
  f.handlers.joined(joined()); await settle();
  f.controller.markReadThrough(message(3)); await settle();
  assert.equal(f.jobs.at(-1).delay, 5000);
  f.jobs.pop().fn(); await settle();
  assert.deepEqual(f.calls, [['cursor-3', 'member'], ['cursor-3', 'member']]);
  f.handlers.revoked({ boardId: 'board', membershipInstanceId: 'member', eventVersion: 1 });
  assert.equal(f.controller.getSnapshot().revoked, true);
  assert.equal(f.controller.getSnapshot().unseen, 0);
});
