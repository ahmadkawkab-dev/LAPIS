import test from 'node:test';
import assert from 'node:assert/strict';
import { localTimeChoices, scheduledTaskLabel, scheduledTaskSubmission } from '../src/features/chat/scheduledTask.ts';
import { ChatController } from '../src/features/chat/ChatController.ts';

const draft = (values = {}) => ({ title: ' Deploy beta ', description: ' Review notes ',
  localStart: '2026-10-09T14:00', localEnd: '', timeZoneId: 'Asia/Beirut',
  startOffsetMinutes: '', endOffsetMinutes: '', ...values });

test('local times resolve DST gaps and overlaps without changing the requested wall time', () => {
  assert.deepEqual(localTimeChoices('2026-03-08T02:30', 'America/New_York'), { state: 'gap', choices: [] });
  assert.deepEqual(localTimeChoices('2026-11-01T01:30', 'America/New_York'), { state: 'valid', choices: [
    { offsetMinutes: -240, utc: Date.parse('2026-11-01T05:30:00Z') },
    { offsetMinutes: -300, utc: Date.parse('2026-11-01T06:30:00Z') },
  ] });
  assert.equal(localTimeChoices('2026-10-09T14:00', 'Asia/Kathmandu').choices[0].offsetMinutes, 345);
  assert.equal(localTimeChoices('2026-02-30T14:00', 'UTC').state, 'invalid-time');
  assert.equal(localTimeChoices('2026-10-09T14:00', 'No/Such_Zone').state, 'invalid-zone');
});

test('task submission requires an explicit overlap choice and orders actual instants', () => {
  const overlap = draft({ localStart: '2026-11-01T01:30', timeZoneId: 'America/New_York' });
  assert.match(scheduledTaskSubmission(overlap).error, /occurrence/);
  const first = scheduledTaskSubmission({ ...overlap, startOffsetMinutes: '-240' });
  assert.equal(first.request.startOffsetMinutes, -240);
  assert.equal(first.request.localStart, overlap.localStart);
  assert.deepEqual(scheduledTaskSubmission(draft()).request, {
    title: 'Deploy beta', description: 'Review notes', localStart: '2026-10-09T14:00',
    localEnd: null, timeZoneId: 'Asia/Beirut', startOffsetMinutes: null, endOffsetMinutes: null,
  });
  const reversed = { ...overlap, startOffsetMinutes: '-300', localEnd: '2026-11-01T01:45', endOffsetMinutes: '-240' };
  assert.match(scheduledTaskSubmission(reversed).error, /after the start/);
  assert.match(scheduledTaskSubmission(draft({ localStart: '2026-03-08T02:30', timeZoneId: 'America/New_York' })).error, /does not exist/);
});

test('card time uses the viewer zone while retaining the authored zone context', () => {
  const label = scheduledTaskLabel({ startsAtUtc: '2026-10-09T11:00:00Z', endsAtUtc: null,
    timeZoneId: 'Asia/Beirut', originalOffsetMinutes: 180 }, 'UTC');
  assert.match(label.when, /11:00/);
  assert.equal(label.authored, 'Asia/Beirut (UTC+03:00)');
});

test('scheduled task keeps its operation and payload through a lost reply and retry', async () => {
  const sent = [];
  const scheduled = { ...scheduledTaskSubmission(draft()).request };
  const saved = { id: 'message', boardId: 'board', sequence: '1', cursor: 'cursor',
    type: 'scheduledTask', body: null, createdAt: '2026-10-03T00:00:00Z', clientMessageId: 'operation',
    sender: { userId: 'me', username: 'me', displayName: null, avatarUrl: null, avatarVersion: null },
    attachment: null, scheduledTask: { title: scheduled.title, description: scheduled.description,
      startsAtUtc: '2026-10-09T11:00:00Z', endsAtUtc: null,
      timeZoneId: scheduled.timeZoneId, originalOffsetMinutes: 180 } };
  const api = {
    history: async () => ({ items: [], olderCursor: null, newerCursor: 'cursor',
      hasMore: false, catchUpThrough: 'cursor', serverTime: '2026-10-03T00:00:00Z' }),
    schedule: async (_board, request) => {
      sent.push(request);
      if (sent.length === 1) throw Error('reply lost');
      return { message: saved, serverTime: '2026-10-03T00:00:00Z', nextSendAllowedAt: null,
        isReplay: true, settingsRevision: 1 };
    },
  };
  const controller = new ChatController('board', 'me', api, () => ({ start() {}, rejoin() {}, async stop() {} }),
    { now: () => Date.parse('2026-10-03T00:00:00Z'), operationId: () => 'operation' });
  controller.activate();
  await new Promise(resolve => setImmediate(resolve));
  controller.setScheduledDraft({ title: 'Draft for later' });
  await controller.sendScheduledTask(scheduled);
  assert.equal(controller.getSnapshot().pending[0].status, 'failed');
  assert.equal(controller.getSnapshot().pending[0].task, scheduled);
  assert.equal(controller.getSnapshot().scheduledDraft.title, '');
  await controller.retry('operation');
  assert.deepEqual(sent, [
    { clientMessageId: 'operation', ...scheduled },
    { clientMessageId: 'operation', ...scheduled },
  ]);
  assert.equal(controller.getSnapshot().pending.length, 0);
  assert.equal(controller.getSnapshot().messages[0].scheduledTask.title, 'Deploy beta');
  controller.stop();
});

test('mute, cooldown, and membership removal prevent scheduled posting', async () => {
  let handlers, calls = 0;
  const serverTime = '2026-10-03T00:00:00Z';
  const controller = new ChatController('board', 'me', {
    history: async () => ({ items: [], olderCursor: null, newerCursor: 'cursor',
      hasMore: false, catchUpThrough: 'cursor', serverTime }),
    schedule: async () => { calls++; throw Error('must not send'); },
  }, (_board, value) => { handlers = value; return { start() {}, rejoin() {}, async stop() {} }; },
  { now: () => Date.parse(serverTime), operationId: () => 'operation' });
  controller.activate();
  await new Promise(resolve => setImmediate(resolve));
  const joined = { boardId: 'board', membershipInstanceId: 'member', latestCursor: 'cursor',
    slowModeSeconds: 10, settingsRevision: 1, moderationRevision: 1,
    isMuted: true, mutedUntil: null, serverTime, nextSendAllowedAt: null, isOwner: false };
  handlers.joined(joined);
  assert.equal(controller.canSchedule(), false);
  await controller.sendScheduledTask(scheduledTaskSubmission(draft()).request);
  assert.equal(calls, 0);
  handlers.joined({ ...joined, moderationRevision: 2, isMuted: false,
    nextSendAllowedAt: '2026-10-03T00:00:10Z' });
  assert.equal(controller.canSchedule(), false);
  handlers.revoked({ boardId: 'board', eventVersion: 1, membershipInstanceId: 'member' });
  assert.equal(controller.getSnapshot().revoked, true);
  assert.equal(controller.canSchedule(), false);
  assert.equal(calls, 0);
  controller.stop();
});
