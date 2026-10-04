import test from 'node:test';
import assert from 'node:assert/strict';
import { ChatTypingState, typingLabel } from '../src/features/chat/chatTyping.ts';
import { ChatController } from '../src/features/chat/ChatController.ts';
import { BoardChatTransport } from '../src/features/chat/chatTransport.ts';
import { ChatApiError } from '../src/features/chat/chatApi.ts';

const at = Date.parse('2026-10-03T00:00:00Z');
const sender = id => ({ userId: id, username: id, displayName: null, avatarUrl: null, avatarVersion: null });
const event = (sequence, active = true, connection = 'tab-1', user = 'guest') => ({ eventVersion: 1, boardId: 'board',
  connectionId: connection, userId: user, membershipInstanceId: 'instance', sequence: String(sequence), isTyping: active,
  expiresAt: new Date(at + 8000).toISOString(), sender: active ? sender(user) : null });
const joined = update => ({ boardId: 'board', membershipInstanceId: 'own-instance', latestCursor: 'hint', slowModeSeconds: 0,
  settingsRevision: 1, moderationRevision: 0, isMuted: false, mutedUntil: null, serverTime: new Date(at).toISOString(),
  nextSendAllowedAt: null, isOwner: false, ...update });
const settle = () => new Promise(resolve => setImmediate(resolve));
function fixture(state = async () => joined()) {
  let handlers, now = at; const jobs = new Set(), sent = [];
  const controller = new ChatController('board', 'me', {
    state, history: async () => ({ items: [], newerCursor: 'c0', hasMore: false, catchUpThrough: 'c0', serverTime: new Date(at).toISOString() }),
    send: async () => { throw Error('not used'); },
  }, (_id, value) => { handlers = value; return { start() {}, rejoin() {}, stop: async () => {}, setTyping: active => sent.push(active) }; }, {
    now: () => now, schedule: (callback, delay) => { const job = { callback, delay }; jobs.add(job); return job; }, cancel: job => jobs.delete(job),
  });
  controller.activate(); handlers.joined(joined()); handlers.status('connected');
  return { controller, handlers, sent, jobs, advance: ms => { now += ms; }, run: job => { jobs.delete(job); job.callback(); } };
}

test('typing leases reject old/duplicate sequences and expire without a stop event', () => {
  const state = new ChatTypingState(); state.accept(event('9007199254740993'), 'board', 'me', at);
  state.accept(event('9007199254740992', false), 'board', 'me', at);
  assert.equal(state.members(at).length, 1); assert.equal(state.nextExpiry(at), at + 8000);
  state.accept(event('9007199254740994', false), 'board', 'me', at);
  state.accept(event('9007199254740993'), 'board', 'me', at);
  assert.equal(state.members(at).length, 0);
  state.accept(event('9007199254740995'), 'board', 'me', at);
  assert.equal(state.members(at + 8000).length, 0); assert.equal(state.nextExpiry(at + 8000), null);
});
test('two tabs aggregate by user and one stop leaves the other tab visible', () => {
  const state = new ChatTypingState(); state.accept(event(1), 'board', 'me', at); state.accept(event(1, true, 'tab-2'), 'board', 'me', at);
  state.accept(event(2, false), 'board', 'me', at); assert.deepEqual(state.members(at).map(x => x.userId), ['guest']);
  state.clearMember('guest', 'old-instance'); assert.equal(state.members(at).length, 1);
  state.clearMember('guest', 'instance'); assert.equal(state.members(at).length, 0);
});
test('typing ignores wrong board/version, own user, bad sequence and forged sender and keeps bounded tombstones', () => {
  const state = new ChatTypingState();
  for (const invalid of [{ ...event(1), boardId: 'other' }, { ...event(1), eventVersion: 2 }, event(1, true, 'own', 'me'),
    { ...event(1), sequence: '-1' }, { ...event(1), sender: sender('different') }, { ...event(1), expiresAt: 'invalid' }]) state.accept(invalid, 'board', 'me', at);
  assert.deepEqual(state.members(at), []);
  for (let index = 0; index < 300; index++) state.accept(event(1, false, `connection-${index}`), 'board', 'me', at);
  assert.equal(state.rows.size, 256);
  assert.equal(typingLabel([sender('A')]), 'A is typing…');
  assert.equal(typingLabel([sender('A'), sender('B'), sender('C')]), 'A and B and 1 more are typing…');
});
test('local activity throttles renewals and ends on idle, empty draft, blur/close and disconnect', async () => {
  const f = fixture(); await settle(); f.controller.setDraft('hello'); f.controller.typingActivity();
  f.controller.typingActivity(); f.advance(2499); f.controller.typingActivity(); assert.deepEqual(f.sent, [true]);
  f.advance(1); f.controller.typingActivity(); assert.deepEqual(f.sent, [true, true]);
  f.run([...f.jobs].find(job => job.delay === 3000)); assert.deepEqual(f.sent, [true, true, false]);
  f.controller.typingActivity(); f.controller.setDraft(''); assert.deepEqual(f.sent.slice(-2), [true, false]);
  f.controller.setDraft('again'); f.controller.typingActivity(); f.controller.stopTyping(); assert.deepEqual(f.sent.slice(-2), [true, false]);
  f.controller.typingActivity(); f.handlers.status('offline'); assert.equal(f.sent.at(-1), false);
  f.controller.typingActivity(); assert.equal(f.sent.at(-1), false); f.controller.stop(); assert.equal(f.jobs.size, 0);
});
test('remote typing expires on its timer, clears on disconnect, and does not persist with the draft', async () => {
  const f = fixture(); await settle(); f.handlers.typing(event(1)); assert.equal(f.controller.getSnapshot().typing.length, 1);
  f.advance(8000); f.run([...f.jobs].find(job => job.delay === 8000)); assert.equal(f.controller.getSnapshot().typing.length, 0);
  f.handlers.typing({ ...event(2), expiresAt: new Date(at + 16000).toISOString() });
  assert.equal(f.controller.getSnapshot().typing.length, 1); f.handlers.status('reconnecting'); assert.equal(f.controller.getSnapshot().typing.length, 0);
  f.controller.stop(); assert.equal(f.jobs.size, 0);
});
test('mute notification fetches authoritative state, stops local typing, and stale instance references are ignored', async () => {
  let reads = 0;
  const f = fixture(async () => { reads++; return joined({ isMuted: true, moderationRevision: 1 }); }); await settle();
  f.controller.setDraft('hello'); f.controller.typingActivity();
  f.handlers.memberState({ eventVersion: 1, boardId: 'board', memberUserId: 'me', membershipInstanceId: 'old', revision: '1' });
  await settle(); assert.equal(reads, 0);
  f.handlers.memberState({ eventVersion: 1, boardId: 'board', memberUserId: 'me', membershipInstanceId: 'own-instance', revision: '1' });
  await settle(); assert.equal(reads, 1); assert.equal(f.controller.isMuted(), true); assert.deepEqual(f.sent, [true, false]);
  f.controller.typingActivity(); assert.deepEqual(f.sent, [true, false]); f.controller.stop();
});
test('state refresh coalesces events during a read and revisions cannot roll back a newer join', async () => {
  let release, reads = 0;
  const held = new Promise(resolve => { release = resolve; });
  const f = fixture(async () => { reads++; return reads === 1 ? held : joined({ slowModeSeconds: 10, settingsRevision: 2 }); }); await settle();
  f.handlers.settings({ eventVersion: 1, boardId: 'board', revision: '2' });
  f.handlers.settings({ eventVersion: 1, boardId: 'board', revision: '3' }); assert.equal(reads, 1);
  f.handlers.joined(joined({ slowModeSeconds: 30, settingsRevision: 3, moderationRevision: 2, isMuted: true }));
  release(joined()); await settle();
  assert.equal(reads, 2); assert.equal(f.controller.getSnapshot().joined.settingsRevision, 3);
  assert.equal(f.controller.getSnapshot().muted, true); f.controller.stop();
});
test('transport invokes typing only after join, handles denial, and routes scoped events', async () => {
  const events = {}, calls = []; let joinResolve, reconnect; const received = [];
  const connection = { state: 'Disconnected', on: (name, fn) => { events[name] = fn; }, onclose() {}, onreconnected() {}, onreconnecting: fn => { reconnect = fn; },
    start: async () => { connection.state = 'Connected'; }, stop: async () => {}, invoke: async (name, ...args) => {
      calls.push([name, ...args]); if (name === 'JoinBoard') return await new Promise(resolve => { joinResolve = resolve; });
      throw Error('chat_muted');
    } };
  const transport = new BoardChatTransport('board', connection, { status() {}, joined() {}, message() {}, revoked() {}, forbidden() {},
    typing: event => received.push(event), settings: event => received.push(event), memberState: event => received.push(event), typingDenied: () => received.push('denied') });
  transport.setTyping(true); transport.start(); await settle(); transport.setTyping(true); assert.equal(calls.length, 1);
  joinResolve(joined()); await settle(); transport.setTyping(true); await settle(); assert.equal(received[0], 'denied');
  events.ChatTypingChanged({ ...event(1), boardId: 'other' }); events.ChatTypingChanged(event(1)); assert.equal(received.length, 2);
  reconnect(); transport.setTyping(true); assert.equal(calls.length, 2); await transport.stop();
  events.ChatSettingsChanged({ eventVersion: 1, boardId: 'board', revision: '2' }); assert.equal(received.length, 2);
});

test('late send reply from an older settings revision cannot restore a cleared cooldown', async () => {
  let handlers, release;
  const held = new Promise(resolve => { release = resolve; });
  const controller = new ChatController('board', 'me', {
    state: async () => joined(), history: async () => ({ items: [], newerCursor: 'c0', hasMore: false, catchUpThrough: 'c0', serverTime: new Date(at).toISOString() }),
    send: async () => held,
  }, (_id, value) => { handlers = value; return { start() {}, rejoin() {}, stop: async () => {}, setTyping() {} }; },
  { now: () => at, operationId: () => 'send-op' });
  controller.activate(); handlers.joined(joined({ slowModeSeconds: 30, settingsRevision: 2 })); await settle();
  controller.setDraft('hello'); const sending = controller.sendDraft();
  handlers.joined(joined({ slowModeSeconds: 0, settingsRevision: 3 }));
  release({ settingsRevision: 2, nextSendAllowedAt: new Date(at + 30000).toISOString(), serverTime: new Date(at).toISOString(),
    isReplay: false, message: { id: 'message', boardId: 'board', sequence: '1', cursor: 'c1', sender: sender('me'), clientMessageId: 'send-op' } });
  await sending; assert.equal(controller.getSnapshot().cooldownUntil, 0); controller.stop();
});

test('late muted-send rejection cannot reapply a mute after a newer unmute state', async () => {
  let handlers, reject;
  const held = new Promise((_resolve, no) => { reject = no; });
  const controller = new ChatController('board', 'me', {
    state: async () => joined(), history: async () => ({ items: [], newerCursor: 'c0', hasMore: false, catchUpThrough: 'c0', serverTime: new Date(at).toISOString() }),
    send: async () => held,
  }, (_id, value) => { handlers = value; return { start() {}, rejoin() {}, stop: async () => {}, setTyping() {} }; },
  { now: () => at, operationId: () => 'send-op' });
  controller.activate(); handlers.joined(joined()); await settle();
  controller.setDraft('hello'); const sending = controller.sendDraft();
  handlers.joined(joined({ moderationRevision: 2, isMuted: false }));
  reject(new ChatApiError('chat_muted', 403, 0, new Date(at).toISOString(), null, null, null, 1, 'own-instance'));
  await sending; assert.equal(controller.isMuted(), false); assert.equal(controller.getSnapshot().pending[0].status, 'failed'); controller.stop();
});
