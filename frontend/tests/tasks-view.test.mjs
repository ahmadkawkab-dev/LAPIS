import assert from 'node:assert/strict';
import test from 'node:test';
import { dateInZone } from '../src/date.ts';
import { taskWritePayload, todayGroups, weekDates, weekStart } from '../src/features/tasks/taskView.ts';

const task = (id, plannedDate, plannedTime) => ({ id, plannedDate, plannedTime });

test('quick add keeps unscheduled and date-only tasks distinct from timed tasks', () => {
  assert.deepEqual(taskWritePayload('  Buy groceries  ', '', '', '', 'Asia/Beirut'), {
    title: 'Buy groceries', description: null, plannedDate: null, plannedTime: null, timeZoneId: null, listId: null,
  });
  assert.deepEqual(taskWritePayload('Buy groceries', '', '2026-09-30', '', 'Asia/Beirut'), {
    title: 'Buy groceries', description: null, plannedDate: '2026-09-30', plannedTime: null, timeZoneId: null, listId: null,
  });
  assert.deepEqual(taskWritePayload('Buy groceries', ' Milk ', '2026-09-30', '14:00', 'Asia/Beirut'), {
    title: 'Buy groceries', description: 'Milk', plannedDate: '2026-09-30', plannedTime: '14:00', timeZoneId: 'Asia/Beirut', listId: null,
  });
  assert.equal(taskWritePayload('Buy groceries', '', '', '14:00', 'Asia/Beirut').plannedTime, null);
  assert.equal(taskWritePayload('Buy groceries', '', '', '', 'Asia/Beirut', 'list-id').listId, 'list-id');
});

test('planning date follows the saved zone across midnight', () => {
  const instant = new Date('2026-09-30T22:30:00Z');
  assert.equal(dateInZone(instant, 'UTC'), '2026-09-30');
  assert.equal(dateInZone(instant, 'Asia/Beirut'), '2026-10-01');
});

test('Today groups overdue, timed, and anytime entries without inventing a time', () => {
  const groups = todayGroups([
    task('overdue', '2026-09-29', null),
    task('timed', '2026-09-30', '09:00:00'),
    task('anytime', '2026-09-30', null),
    task('future', '2026-10-01', '11:00:00'),
  ], '2026-09-30');
  assert.deepEqual(groups.map(({ label, items }) => [label, items.map((item) => item.id)]), [
    ['Overdue', ['overdue']], ['Timed', ['timed']], ['Anytime', ['anytime']],
  ]);
});

test('week planning starts on Monday and crosses month and year boundaries', () => {
  assert.equal(weekStart('2026-10-01'), '2026-09-28');
  assert.equal(weekStart('2027-01-03'), '2026-12-28');
  assert.deepEqual(weekDates('2026-09-28'), [
    '2026-09-28', '2026-09-29', '2026-09-30', '2026-10-01',
    '2026-10-02', '2026-10-03', '2026-10-04',
  ]);
});
