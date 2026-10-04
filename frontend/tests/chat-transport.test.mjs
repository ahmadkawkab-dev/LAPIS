import test from 'node:test';
import assert from 'node:assert/strict';
import { HubConnectionState } from '@microsoft/signalr';
import { BoardChatTransport } from '../src/features/chat/chatTransport.ts';
const settle = () => new Promise(resolve => setImmediate(resolve));
function setup() {
  const events = {}, lifecycle = {}, calls = [], statuses = [], received = [], jobs = [];
  let startFailure = false, joinFailure = false, denied = false, joins = 0, stopped = 0;
  const connection = { state: HubConnectionState.Disconnected,
    async start() { calls.push('start'); if (startFailure) throw Error(); this.state = HubConnectionState.Connected; },
    async stop() { stopped++; this.state = HubConnectionState.Disconnected; lifecycle.close?.(); },
    async invoke(method, id) { calls.push([method, id]); joins++; if (denied) throw Error('chat_forbidden'); if (joinFailure) throw Error('offline');
      return { boardId: id, membershipInstanceId: 'instance' }; },
    on(name, callback) { events[name] = callback; }, onclose(fn) { lifecycle.close = fn; },
    onreconnecting(fn) { lifecycle.reconnecting = fn; }, onreconnected(fn) { lifecycle.reconnected = fn; } };
  const transport = new BoardChatTransport('board', connection, { status: value => statuses.push(value),
    joined: value => received.push(['join', value]), message: value => received.push(['message', value]),
    attachment: value => received.push(['attachment', value]),
    revoked: value => received.push(['revoke', value]), forbidden: () => received.push(['forbidden']) },
    { schedule: (fn, delay) => { const job = { fn, delay }; jobs.push(job); return job; }, cancel: job => jobs.splice(jobs.indexOf(job), 1) });
  return { transport, connection, lifecycle, events, calls, statuses, received, jobs,
    failStart(value) { startFailure = value; }, failJoin(value) { joinFailure = value; }, deny() { denied = true; },
    get joins() { return joins; }, get stopped() { return stopped; } };
}
test('initial connection failure retries, then revalidates membership before reporting connected', async () => {
  const f = setup(); f.failStart(true); f.transport.start(); await settle();
  assert.equal(f.statuses.at(-1), 'offline'); assert.equal(f.jobs[0].delay, 1000);
  f.failStart(false); f.jobs.shift().fn(); await settle();
  assert.equal(f.joins, 1); assert.equal(f.received[0][0], 'join'); assert.equal(f.statuses.at(-1), 'connected'); await f.transport.stop();
});
test('reconnect revalidates membership, while repeated rejoin shares one invocation', async () => {
  const f = setup(); f.transport.start(); await settle(); f.lifecycle.reconnecting();
  assert.equal(f.statuses.at(-1), 'reconnecting'); f.lifecycle.reconnected(); f.transport.rejoin(); await settle();
  assert.equal(f.joins, 2); assert.equal(f.statuses.at(-1), 'connected'); await f.transport.stop();
});
test('failed JoinBoard retries on the connected socket and stops permanently when membership is denied', async () => {
  const f = setup(); f.failJoin(true); f.transport.start(); await settle(); assert.equal(f.jobs.length, 1);
  f.deny(); f.jobs.shift().fn(); await settle(); assert.equal(f.received.at(-1)[0], 'forbidden');
  assert.equal(f.stopped, 1); assert.equal(f.jobs.length, 0); f.transport.rejoin(); assert.equal(f.joins, 2);
});
test('events are board/version scoped and cease after disposal', async () => {
  const f = setup(); f.transport.start(); await settle();
  f.events.ChatMessageCreated({ boardId: 'other', eventVersion: 1 }); f.events.ChatMessageCreated({ boardId: 'board', eventVersion: 2 });
  f.events.ChatMessageCreated({ boardId: 'board', eventVersion: 1 }); f.events.ChatAccessRevoked({ boardId: 'board', eventVersion: 1 });
  f.events.ChatAttachmentChanged({ boardId: 'other', eventVersion: 1 });
  f.events.ChatAttachmentChanged({ boardId: 'board', eventVersion: 2 });
  f.events.ChatAttachmentChanged({ boardId: 'board', eventVersion: 1, messageId: 'm-1' });
  assert.deepEqual(f.received.map(item => item[0]), ['join','message','revoke','attachment']); await f.transport.stop();
  f.events.ChatMessageCreated({ boardId: 'board', eventVersion: 1 }); assert.equal(f.received.length, 4);
});
test('dispose cancels initial retry and a late start result never joins', async () => {
  const f = setup(); f.failStart(true); f.transport.start(); await settle(); await f.transport.stop(); assert.equal(f.jobs.length, 0);
  const g = setup(); let resolve; g.connection.start = () => new Promise(done => { resolve = done; });
  g.transport.start(); await g.transport.stop(); resolve(); await settle(); assert.equal(g.joins, 0); assert.equal(g.jobs.length, 0);
});
