import test from 'node:test';
import assert from 'node:assert/strict';
import { ChatController } from '../src/features/chat/ChatController.ts';
import { ChatApiError } from '../src/features/chat/chatApi.ts';
import { chatDraftKey, mergeChatMessages, readChatDraft, saveChatDraft } from '../src/features/chat/chatState.ts';

const board = 'board', user = 'me';
const at = '2026-10-03T00:00:00Z';
const message = (sequence, sender = 'guest', op = `op-${sequence}`) => ({ id: `m-${sequence}`, boardId: board,
  sequence: String(sequence), cursor: `c-${sequence}`, type: 'text', body: `message ${sequence}`, createdAt: at,
  clientMessageId: op, sender: { userId: sender, username: sender, displayName: null, avatarUrl: null, avatarVersion: null },
  attachment: null, scheduledTask: null });
const page = (items = [], more = false, through = null, newer = null) => ({ items, olderCursor: items[0]?.cursor ?? null,
  newerCursor: newer ?? items.at(-1)?.cursor ?? 'c-0', hasMore: more, catchUpThrough: through ?? items.at(-1)?.cursor ?? 'c-0', serverTime: at });
const joined = (instance = 'instance') => ({ boardId: board, membershipInstanceId: instance, latestCursor: 'head-hint',
  slowModeSeconds: 0, settingsRevision: 1, isMuted: false, mutedUntil: null, serverTime: at });
const deferred = () => { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };
const settle = () => new Promise(resolve => setImmediate(resolve));
function fixture(history, send = async () => { throw new Error('offline'); }, options = {}) {
  const requests = [], sends = [], jobs = [];
  let handlers, stopped = 0;
  const api = { history: async (id, query, signal) => { requests.push({ id, ...query }); return history(query, signal); },
    send: async (id, request, signal) => { sends.push(request); return send(request, signal); }, ...options.api };
  const controller = new ChatController(board, user, api, (_id, value) => {
    handlers = value; return { start() {}, rejoin() {}, async stop() { stopped++; } };
  }, { now: () => Date.parse(at), operationId: () => 'operation', schedule: (fn, delay) => { const job = { fn, delay }; jobs.push(job); return job; },
    cancel: job => { const index = jobs.indexOf(job); if (index >= 0) jobs.splice(index, 1); }, ...options });
  return { controller, requests, sends, jobs, get handlers() { return handlers; }, get stopped() { return stopped; } };
}

test('drafts are versioned, user/board scoped, bounded and resilient to blocked storage', () => {
  const rows = new Map(); const storage = { getItem: key => rows.get(key), setItem: (key, value) => rows.set(key, value), removeItem: key => rows.delete(key) };
  const key = chatDraftKey(user, board); saveChatDraft(storage, key, 'local draft');
  assert.equal(readChatDraft(storage, key), 'local draft'); assert.equal(readChatDraft(storage, chatDraftKey('other', board)), '');
  rows.set(key, '{bad'); assert.equal(readChatDraft(storage, key), '');
  rows.set(key, JSON.stringify({ version: 2, body: 'old' })); assert.equal(readChatDraft(storage, key), '');
  rows.set(key, JSON.stringify({ version: 1, body: 'x'.repeat(4001) })); assert.equal(readChatDraft(storage, key), '');
  const blocked = { getItem() { throw Error(); }, setItem() { throw Error(); }, removeItem() { throw Error(); } };
  assert.equal(readChatDraft(blocked, key), ''); assert.doesNotThrow(() => saveChatDraft(blocked, key, 'draft'));
  saveChatDraft(storage, key, ''); assert.equal(rows.has(key), false);
});
test('merge uses decimal long sequences, rejects other boards and replaces duplicate identities', () => {
  const a = message('9007199254740993'), b = message('9007199254740992');
  const updated = { ...a, sender: { ...a.sender, displayName: 'New name' } };
  const merged = mergeChatMessages([a], [b, updated, { ...message(1), boardId: 'other' }], board);
  assert.deepEqual(merged.map(item => item.sequence), [b.sequence, a.sequence]); assert.equal(merged[1].sender.displayName, 'New name');
});
test('an older history response cannot roll back a completed attachment scan', () => {
  const pending = { ...message(1), type: 'attachment', body: null,
    attachment: { id: 'a-1', fileName: 'photo.png', contentType: 'image/webp', byteSize: 100,
      width: 10, height: 10, scanStatus: 'Pending' } };
  const available = { ...pending, attachment: { ...pending.attachment, scanStatus: 'Available' } };
  assert.equal(mergeChatMessages([available], [pending], board)[0].attachment.scanStatus, 'Available');
});
test('image upload retains its file and operation ID through an uncertain retry', async () => {
  const file = new File(['image bytes'], 'photo.png', { type: 'image/png' });
  const uploads = []; let attempts = 0;
  const saved = { ...message(1, user, 'operation'), type: 'attachment', body: 'caption',
    attachment: { id: 'attachment', fileName: file.name, contentType: 'image/webp', byteSize: 100,
      width: 10, height: 10, scanStatus: 'Pending' } };
  const f = fixture(() => page([]), undefined, { api: {
    upload: async (_board, request) => {
      uploads.push(request); if (++attempts === 1) throw Error('reply lost');
      return { message: saved, serverTime: at, nextSendAllowedAt: null, isReplay: true };
    }
  } });
  f.controller.activate(); await settle(); f.controller.setDraft('caption'); f.controller.selectAttachment(file);
  await f.controller.sendDraft(); await settle();
  assert.equal(f.controller.getSnapshot().pending[0].file, file);
  assert.equal(f.controller.getSnapshot().pending[0].status, 'failed');
  assert.equal(f.controller.getSnapshot().selectedFile, null);
  await f.controller.retry('operation');
  assert.equal(uploads.length, 2);
  assert.equal(uploads[0].file, file); assert.equal(uploads[1].file, file);
  assert.equal(uploads[0].clientMessageId, uploads[1].clientMessageId);
  assert.equal(f.controller.getSnapshot().pending.length, 0);
  assert.equal(f.controller.getSnapshot().messages[0].attachment.scanStatus, 'Pending');
  f.controller.stop();
});
test('attachment event refreshes the saved row without moving the history cursor', async () => {
  const pending = { ...message(1), type: 'attachment', body: null,
    attachment: { id: 'attachment', fileName: 'photo.png', contentType: 'image/webp', byteSize: 100,
      width: 10, height: 10, scanStatus: 'Pending' } };
  const reads = [];
  const f = fixture(query => query.after ? page([], false, 'c-1', 'c-1') : page([pending]), undefined,
    { api: { message: async (boardId, id) => { reads.push([boardId, id]);
      return { ...pending, attachment: { ...pending.attachment, scanStatus: 'Available' } }; } } });
  f.controller.activate(); await settle();
  f.handlers.attachment({ boardId: 'other', eventVersion: 1, messageId: pending.id }); await settle();
  assert.equal(reads.length, 0);
  f.handlers.attachment({ boardId: board, eventVersion: 1, messageId: pending.id }); await settle();
  assert.deepEqual(reads, [[board, pending.id]]);
  assert.equal(f.controller.getSnapshot().messages[0].attachment.scanStatus, 'Available');
  assert.equal(f.requests[1].after, 'c-1');
  f.controller.stop();
});
test('a second scan event during a status read triggers one final authoritative read', async () => {
  const pending = { ...message(1), type: 'attachment', body: null,
    attachment: { id: 'attachment', fileName: 'photo.png', contentType: 'image/webp', byteSize: 100,
      width: 10, height: 10, scanStatus: 'Pending' } };
  const first = deferred(); let reads = 0;
  const f = fixture(query => query.after ? page([], false, 'c-1', 'c-1') : page([pending]), undefined,
    { api: { message: async () => {
      reads++; return reads === 1 ? first.promise : { ...pending, attachment: { ...pending.attachment, scanStatus: 'Available' } };
    } } });
  f.controller.activate(); await settle();
  f.handlers.attachment({ boardId: board, eventVersion: 1, messageId: pending.id });
  f.handlers.attachment({ boardId: board, eventVersion: 1, messageId: pending.id });
  first.resolve({ ...pending, attachment: { ...pending.attachment, scanStatus: 'Scanning' } });
  await settle();
  assert.equal(reads, 2);
  assert.equal(f.controller.getSnapshot().messages[0].attachment.scanStatus, 'Available');
  f.controller.stop();
});
test('refresh recovers an unresolved attachment after a missed realtime event', async () => {
  const pending = { ...message(1), type: 'attachment', body: null,
    attachment: { id: 'attachment', fileName: 'photo.png', contentType: 'image/webp', byteSize: 100,
      width: 10, height: 10, scanStatus: 'ScanFailed' } };
  let reads = 0;
  const f = fixture(query => query.after ? page([], false, 'c-1', 'c-1') : page([pending]), undefined,
    { api: { message: async () => { reads++; return { ...pending,
      attachment: { ...pending.attachment, scanStatus: 'Available' } }; } } });
  f.controller.activate(); await settle(); await f.controller.refresh();
  assert.equal(reads, 1);
  assert.equal(f.controller.getSnapshot().messages[0].attachment.scanStatus, 'Available');
  f.controller.stop();
});
test('the composer rejects mismatched image types and clears the previous selection', () => {
  const f = fixture(() => page([]));
  f.controller.selectAttachment(new File(['x'], 'portrait.jpg', { type: 'image/jpeg' }));
  assert.equal(f.controller.getSnapshot().selectedFile.name, 'portrait.jpg');
  f.controller.selectAttachment(new File(['x'], 'wrong.png', { type: 'image/jpeg' }));
  assert.equal(f.controller.getSnapshot().selectedFile, null);
  assert.match(f.controller.getSnapshot().attachmentError, /JPEG/);
  f.controller.stop();
});
test('join head hint and notifications during initial load never become catch-up progress', async () => {
  const initial = deferred();
  const f = fixture(query => !query.after ? initial.promise : page([message(2)]));
  f.controller.activate(); f.handlers.joined(joined()); f.handlers.message({ boardId: board, eventVersion: 1, sequence: '999' });
  initial.resolve(page([message(1)])); await settle();
  assert.deepEqual(f.requests, [{ id: board, limit: 50 }, { id: board, after: 'c-1', limit: 50 }]);
  assert.deepEqual(f.controller.getSnapshot().messages.map(item => item.sequence), ['1', '2']);
  f.controller.stop();
});
test('forward catch-up pins a batch boundary then fetches newer arrivals in a separate batch', async () => {
  let count = 0; const boundary = deferred();
  const f = fixture(query => {
    count++; if (count === 1) return page([message(1)]);
    if (count === 2) return page([message(2)], true, 'c-4');
    if (count === 3) return boundary.promise;
    return page([message(5)]);
  });
  f.controller.activate(); await settle(); const sync = f.controller.reconcile(); await settle();
  f.handlers.message({ boardId: board, eventVersion: 1, sequence: '5' });
  boundary.resolve(page([message(3), message(4)], false, 'c-4')); await sync;
  assert.deepEqual(f.requests.slice(1).map(({ id, ...query }) => query), [
    { after: 'c-1', limit: 50 }, { after: 'c-2', through: 'c-4', limit: 50 }, { after: 'c-4', limit: 50 }]);
  assert.deepEqual(f.controller.getSnapshot().messages.map(item => item.sequence), ['1','2','3','4','5']); f.controller.stop();
});
test('send response ahead of history does not skip intervening messages; own confirmation is deduplicated', async () => {
  const f = fixture(query => query.after ? page([message(2), message(3, user, 'operation')]) : page([message(1)]),
    async request => ({ message: message(3, user, request.clientMessageId), serverTime: at, nextSendAllowedAt: null, isReplay: false }));
  f.controller.activate(); await settle(); f.controller.setDraft('hello'); await f.controller.sendDraft(); await settle();
  assert.equal(f.requests[1].after, 'c-1'); assert.equal(f.controller.getSnapshot().pending.length, 0);
  assert.deepEqual(f.controller.getSnapshot().messages.map(item => item.sequence), ['1','2','3']); f.controller.stop();
});
test('lost HTTP reply confirmed by history clears pending without retrying the durable send', async () => {
  const response = deferred(); let committed = false;
  const f = fixture(query => query.after && committed ? page([message(2, user, 'operation')]) : page([message(1)]), () => response.promise);
  f.controller.activate(); await settle(); f.controller.setDraft('hello'); const sending = f.controller.sendDraft();
  committed = true; await f.controller.reconcile(); response.reject(Error('reply lost')); await sending;
  assert.equal(f.controller.getSnapshot().pending.length, 0); assert.equal(f.sends.length, 1); f.controller.stop();
});
test('failed retry retains its immutable operation ID and body, separate from the new draft', async () => {
  const f = fixture(query => query.after ? page([], false, 'c-1', 'c-1') : page([message(1)]));
  f.controller.activate(); await settle(); f.controller.setDraft(' original\r\nbody '); await f.controller.sendDraft(); await settle();
  f.controller.setDraft('different draft'); await f.controller.retry('operation');
  assert.deepEqual(f.sends, [{ clientMessageId: 'operation', body: 'original\nbody' }, { clientMessageId: 'operation', body: 'original\nbody' }]);
  assert.equal(f.controller.getSnapshot().draft, 'different draft'); f.controller.stop();
});
test('double submit makes one pending operation; another sender using its ID cannot confirm it', async () => {
  const response = deferred();
  const f = fixture(query => query.after ? page([message(2, 'guest', 'operation')]) : page([message(1)]), () => response.promise);
  f.controller.activate(); await settle(); f.controller.setDraft('hello'); const sending = f.controller.sendDraft();
  await f.controller.sendDraft(); await f.controller.reconcile(); assert.equal(f.sends.length, 1); assert.equal(f.controller.getSnapshot().pending.length, 1);
  response.reject(Error()); await sending; f.controller.stop();
});
test('server clock skew informs mute expiry and cooldown, with settings changes clearing old cooldown', async () => {
  const f = fixture(() => page(), async request => ({ message: message(1, user, request.clientMessageId), serverTime: '2026-10-03T01:00:00Z',
    nextSendAllowedAt: '2026-10-03T01:00:12Z', isReplay: false }));
  f.controller.activate(); await settle(); f.handlers.joined({ ...joined(), isMuted: true, mutedUntil: '2026-10-03T01:00:05Z', serverTime: '2026-10-03T01:00:00Z' });
  assert.equal(f.controller.isMuted(Date.parse(at)), true); assert.equal(f.controller.isMuted(Date.parse(at) + 6000), false);
  f.handlers.joined(joined()); f.controller.setDraft('hello'); await f.controller.sendDraft();
  assert.equal(f.controller.getSnapshot().cooldownUntil, Date.parse(at) + 12000);
  f.handlers.joined({ ...joined(), settingsRevision: 2 }); assert.equal(f.controller.getSnapshot().cooldownUntil, 0); f.controller.stop();
});
test('older pagination does not move forward recovery progress', async () => {
  const f = fixture(query => query.before ? page([message(1)]) : query.after ? page([message(3)]) : page([message(2)], true));
  f.controller.activate(); await settle(); await f.controller.loadOlder(); await f.controller.reconcile();
  assert.equal(f.requests[1].before, 'c-2'); assert.equal(f.requests[2].after, 'c-2');
  assert.deepEqual(f.controller.getSnapshot().messages.map(item => item.sequence), ['1','2','3']); f.controller.stop();
});
test('bounded window preserves pinned older history, then jump to latest reloads an authoritative head', async () => {
  let headLoads = 0; const f = fixture(query => {
    if (!query.after) return ++headLoads === 1 ? page(Array.from({ length: 500 }, (_, i) => message(i + 1)), true) : page([message(501)]);
    return page([message(501)]);
  });
  f.controller.activate(); await settle(); f.controller.setViewingLatest(false, true); await f.controller.reconcile();
  assert.equal(f.controller.getSnapshot().messages.length, 500); assert.equal(f.controller.getSnapshot().messages.at(-1).sequence, '500');
  assert.equal(f.controller.getSnapshot().windowHasNewer, true); await f.controller.showLatest();
  assert.equal(f.controller.getSnapshot().messages.at(-1).sequence, '501'); assert.equal(f.controller.getSnapshot().windowHasNewer, false); f.controller.stop();
});
test('matching revocation clears private cache/draft and ignores late requests; old membership revocation is ignored', async () => {
  const rows = new Map(); const storage = { getItem: key => rows.get(key), setItem: (key, value) => rows.set(key, value), removeItem: key => rows.delete(key) };
  const next = deferred(); const f = fixture(query => query.after ? next.promise : page([message(1)]), undefined, { storage });
  f.controller.activate(); await settle(); f.handlers.joined(joined('new')); f.controller.setDraft('private');
  f.handlers.revoked({ boardId: board, eventVersion: 1, membershipInstanceId: 'old' }); assert.equal(f.controller.getSnapshot().revoked, false);
  f.handlers.revoked({ boardId: board, eventVersion: 1, membershipInstanceId: 'new' }); next.resolve(page([message(2)])); await settle();
  assert.equal(f.controller.getSnapshot().revoked, true); assert.deepEqual(f.controller.getSnapshot().messages, []);
  assert.equal(rows.size, 0); assert.equal(f.controller.getSnapshot().draft, ''); assert.equal(f.stopped, 1);
});
test('revocation arriving before JoinBoard reply is applied after its membership instance is known', async () => {
  const f = fixture(() => page([message(1)])); f.controller.activate(); await settle();
  f.handlers.revoked({ boardId: board, eventVersion: 1, membershipInstanceId: 'instance' }); f.handlers.joined(joined());
  assert.equal(f.controller.getSnapshot().revoked, true);
});
test('stop and reactivate ignore stale in-flight results and reuse the local draft', async () => {
  const stale = deferred(); let count = 0;
  const f = fixture(() => ++count === 1 ? stale.promise : page([message(2)]));
  f.controller.activate(); f.controller.setDraft('draft'); f.controller.stop(); f.controller.activate(); await settle();
  stale.resolve(page([message(1)])); await settle();
  assert.deepEqual(f.controller.getSnapshot().messages.map(item => item.sequence), ['2']); assert.equal(f.controller.getSnapshot().draft, 'draft'); f.controller.stop();
});
test('HTTP membership denial clears chat even without a realtime event', async () => {
  const f = fixture(() => { throw new ChatApiError('not_found', 404); }); f.controller.activate(); await settle();
  assert.equal(f.controller.getSnapshot().revoked, true); assert.equal(f.jobs.length, 0);
});
test('long catch-up yields after ten pages and retains its fixed boundary across continuation', async () => {
  let seq = 0; const f = fixture(query => !query.after ? page([message(++seq)]) : page([message(++seq)], seq < 13, 'c-13'));
  f.controller.activate(); await settle(); await f.controller.reconcile(); assert.equal(f.requests.length, 11);
  assert.equal(f.jobs[0].delay, 0); f.jobs.shift().fn(); await settle();
  assert.equal(f.requests[11].through, 'c-13'); assert.equal(f.controller.getSnapshot().messages.at(-1).sequence, '13'); f.controller.stop();
});
