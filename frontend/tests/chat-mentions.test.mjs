import test from 'node:test';
import assert from 'node:assert/strict';
import { editMentions, normalizedMentions, mentionQuery } from '../src/features/chat/chatMentions.ts';
import { notificationSoundKey } from '../src/features/notifications/notificationState.ts';
import { notificationPath } from '../src/features/notifications/notificationView.ts';

test('mention selection survives unrelated edits, shifts by UTF-16 offsets and loses its ping when edited', () => {
  const tokens = [{ userId: 'member', start: 3, length: 5 }];
  assert.deepEqual(editMentions('Hi @alex', 'Hey @alex', tokens), [{ userId: 'member', start: 4, length: 5 }]);
  assert.deepEqual(editMentions('Hi @alex', 'Hi @al', tokens), []);
  assert.deepEqual(editMentions('Hi @alex', 'Hi @alex!', tokens), tokens);
  assert.deepEqual(normalizedMentions('  😀 @alex\r\n ', [{ userId: 'member', start: 5, length: 5 }]), [{ userId: 'member', start: 3, length: 5 }]);
  assert.deepEqual(mentionQuery('Hi @al', 6), { start: 3, search: 'al' });
  assert.equal(mentionQuery('mail@example.com', 8), null);
});
test('scheduled posting and due reminders use distinct supplied sounds, and semantic activity has no ordinary chat sound', () => {
  assert.equal(notificationSoundKey({ type: 'chatActivity', activityKind: 'scheduledTaskPosted' }), 'scheduledTaskPostedSoundEnabled');
  assert.equal(notificationSoundKey({ type: 'scheduledTaskReminder', activityKind: null }), 'taskReminderSoundEnabled');
  assert.equal(notificationSoundKey({ type: 'chatActivity', activityKind: 'mention' }), 'chatSoundEnabled');
  assert.equal(notificationSoundKey({ type: 'taskActivity', activityKind: 'taskCompleted' }), 'taskCompletedSoundEnabled');
  assert.equal(notificationSoundKey({ type: 'sharedBoardActivity', activityKind: 'noteCreated' }), null);
});
test('notification routes address existing chat, calendar and note resources without supplied URLs', () => {
  assert.equal(notificationPath({ boardId: 'board', resourceKind: 'chatMessage', resourceId: 'message' }), '/boards/board?chat=1&message=message');
  assert.equal(notificationPath({ resourceKind: 'calendarEvent', resourceId: 'event' }), '/calendar?event=event');
  assert.equal(notificationPath({ boardId: 'board', resourceKind: 'note', resourceId: 'note' }), '/boards/board?note=note');
  assert.equal(notificationPath({ type: 'unknown', resourceKind: 'url', resourceId: 'https://evil.test/' }), null);
});

test('login return routes reject outside origins, scheme URLs and unexpected query parameters', async () => {
  const { safeNotificationReturnPath } = await import('../src/features/notifications/notificationRoutes.ts');
  const id = '11111111-1111-1111-1111-111111111111';
  assert.equal(safeNotificationReturnPath(`/boards/${id}?chat=1&message=${id}`), `/boards/${id}?chat=1&message=${id}`);
  assert.equal(safeNotificationReturnPath(`/calendar?event=${id}`), `/calendar?event=${id}`);
  assert.equal(safeNotificationReturnPath('//evil.test/'), null);
  assert.equal(safeNotificationReturnPath('javascript:alert(1)'), null);
  assert.equal(safeNotificationReturnPath(`/calendar?redirect=https://evil.test`), null);
});
