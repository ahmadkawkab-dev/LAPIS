import assert from 'node:assert/strict';
import test from 'node:test';
import { eventWritePayload, itemOccursOn, itemsByDay, monthRange, shiftMonth, taskCalendarItem, taskDateForCalendarDay } from '../src/features/calendar/calendarView.ts';

const item = (scheduleKind, startDate, endDateExclusive, startAtUtc, endAtUtc) =>
  ({ scheduleKind, startDate, endDateExclusive, startAtUtc, endAtUtc });

test('month range is Monday aligned and uses five or six weeks as needed', () => {
  const range = monthRange('2026-10-01');
  assert.equal(range.from, '2026-09-28');
  assert.equal(range.to, '2026-11-02');
  assert.equal(range.days.length, 35);
  assert.equal(monthRange('2026-08-01').days.length, 42);
  assert.equal(shiftMonth('2026-12-01', 1), '2027-01-01');
});

test('event form keeps date-only and timed schedules distinct', () => {
  const base = { title: '  Visit  ', description: '', location: '', startDate: '2026-10-01',
    endDate: '2026-10-01', startTime: '09:00', endTime: '10:00', timeZoneId: 'Asia/Beirut' };
  assert.deepEqual(eventWritePayload({ ...base, isAllDay: true }), {
    title: 'Visit', description: null, location: null, isAllDay: true,
    allDayStartDate: '2026-10-01', allDayEndDateExclusive: '2026-10-02',
    localStart: null, localEnd: null, timeZoneId: null,
  });
  assert.equal(eventWritePayload({ ...base, isAllDay: false }).localStart, '2026-10-01T09:00:00');
});

test('scheduled task projection retains its task identity and time', () => {
  const timed = taskCalendarItem({ id: 'task-id', title: 'Morning', plannedDate: '2026-03-09',
    plannedTime: '09:00:00', plannedAtUtc: '2026-03-09T13:00:00Z', timeZoneId: 'America/New_York', completedAt: null });
  assert.equal(timed.source, 'task');
  assert.equal(timed.scheduleKind, 'timedTask');
  assert.equal(timed.startAtUtc, '2026-03-09T13:00:00Z');
  assert.equal(taskCalendarItem({ plannedDate: null }), null);
});

test('calendar day move preserves a timed task authored in another zone', () => {
  const timed = { scheduleKind: 'timedTask', startAtUtc: '2026-10-01T02:00:00Z', timeZoneId: 'America/New_York' };
  assert.equal(taskDateForCalendarDay(timed, '2026-10-02', 'Asia/Beirut'), '2026-10-01');
  assert.equal(taskDateForCalendarDay(timed, '2026-09-30', 'Asia/Beirut'), '2026-09-29');
  assert.equal(taskDateForCalendarDay({ scheduleKind: 'dateOnlyTask' }, '2026-10-02', 'Asia/Beirut'), '2026-10-02');
});

test('all-day end is exclusive and timed event overlaps visible dates', () => {
  const allDay = item('allDayEvent', '2026-09-30', '2026-10-02', null, null);
  assert.equal(itemOccursOn(allDay, '2026-10-01', 'UTC'), true);
  assert.equal(itemOccursOn(allDay, '2026-10-02', 'UTC'), false);
  const timed = item('timedEvent', null, null, '2026-09-30T23:00:00Z', '2026-10-01T01:00:00Z');
  assert.equal(itemOccursOn(timed, '2026-09-30', 'UTC'), true);
  assert.equal(itemOccursOn(timed, '2026-10-01', 'UTC'), true);
  assert.equal(itemOccursOn(timed, '2026-10-02', 'UTC'), false);
  assert.equal(itemOccursOn(timed, '2026-10-01', 'Asia/Beirut'), true);
  const buckets = itemsByDay([allDay, timed], ['2026-09-30', '2026-10-01', '2026-10-02'], 'UTC');
  assert.deepEqual([...buckets.values()].map((entries) => entries.length), [2, 2, 0]);
});
