import type { CalendarEventWrite, CalendarItemDto, PersonalTaskDto } from "../../api";
import { addDays, dateInZone } from "../../date.ts";

export function monthRange(month: string) {
  const first = `${month.slice(0, 7)}-01`;
  const weekday = new Date(`${first}T12:00:00Z`).getUTCDay();
  const precedingDays = (weekday + 6) % 7;
  const from = addDays(first, -precedingDays);
  const [year, calendarMonth] = first.split("-").map(Number);
  const daysInMonth = new Date(Date.UTC(year, calendarMonth, 0)).getUTCDate();
  const visibleDays = Math.max(35, Math.ceil((precedingDays + daysInMonth) / 7) * 7);
  return { from, to: addDays(from, visibleDays), days: Array.from({ length: visibleDays }, (_, index) => addDays(from, index)) };
}

export function shiftMonth(month: string, delta: number): string {
  const value = new Date(`${month.slice(0, 7)}-01T12:00:00Z`);
  value.setUTCMonth(value.getUTCMonth() + delta);
  return value.toISOString().slice(0, 10);
}

export function itemOccursOn(item: CalendarItemDto, date: string, zone: string): boolean {
  const span = itemSpan(item, zone);
  return span !== null && span.first <= date && date <= span.last;
}

function itemSpan(item: CalendarItemDto, zone: string): { first: string; last: string } | null {
  if (item.scheduleKind === "dateOnlyTask")
    return item.startDate ? { first: item.startDate, last: item.startDate } : null;
  if (item.scheduleKind === "allDayEvent")
    return item.startDate && item.endDateExclusive
      ? { first: item.startDate, last: addDays(item.endDateExclusive, -1) } : null;
  if (!item.startAtUtc) return null;
  const first = dateInZone(new Date(item.startAtUtc), zone);
  if (item.scheduleKind === "timedTask") return { first, last: first };
  return { first, last: dateInZone(new Date(Date.parse(item.endAtUtc!) - 1), zone) };
}

export function itemsByDay(items: CalendarItemDto[], days: string[], zone: string): Map<string, CalendarItemDto[]> {
  const result = new Map(days.map((day) => [day, [] as CalendarItemDto[]]));
  if (!days.length) return result;
  for (const item of items) {
    const span = itemSpan(item, zone);
    if (!span) continue;
    const first = span.first > days[0] ? span.first : days[0];
    const last = span.last < days.at(-1)! ? span.last : days.at(-1)!;
    for (let day = first; day <= last; day = addDays(day, 1)) result.get(day)?.push(item);
  }
  for (const entries of result.values()) entries.sort((a, b) => {
    const aRank = a.scheduleKind === "allDayEvent" || a.scheduleKind === "dateOnlyTask" ? 0 : 1;
    const bRank = b.scheduleKind === "allDayEvent" || b.scheduleKind === "dateOnlyTask" ? 0 : 1;
    return aRank - bRank || (a.startAtUtc ?? "").localeCompare(b.startAtUtc ?? "") || a.id.localeCompare(b.id);
  });
  return result;
}

export function itemTime(item: CalendarItemDto, zone: string): string | null {
  if (!item.startAtUtc) return null;
  return new Intl.DateTimeFormat(undefined, { timeZone: zone, hour: "numeric", minute: "2-digit" }).format(new Date(item.startAtUtc));
}

export function taskDateForCalendarDay(item: CalendarItemDto, targetDay: string, calendarZone: string): string {
  if (item.scheduleKind !== "timedTask" || !item.startAtUtc || !item.timeZoneId) return targetDay;
  const instant = new Date(item.startAtUtc);
  const currentCalendarDay = dateInZone(instant, calendarZone);
  const currentLocalDay = dateInZone(instant, item.timeZoneId);
  const delta = Math.round((Date.parse(`${targetDay}T12:00:00Z`) - Date.parse(`${currentCalendarDay}T12:00:00Z`)) / 86_400_000);
  return addDays(currentLocalDay, delta);
}

export function taskCalendarItem(task: PersonalTaskDto): CalendarItemDto | null {
  if (!task.plannedDate) return null;
  return {
    source: "task", id: task.id, title: task.title,
    scheduleKind: task.plannedTime ? "timedTask" : "dateOnlyTask",
    startDate: task.plannedTime ? null : task.plannedDate,
    endDateExclusive: null,
    startAtUtc: task.plannedAtUtc,
    endAtUtc: null,
    timeZoneId: task.timeZoneId,
    isCompleted: task.completedAt !== null,
  };
}

export function eventWritePayload(values: {
  title: string; description: string; location: string; isAllDay: boolean;
  startDate: string; endDate: string; startTime: string; endTime: string; timeZoneId: string;
}): CalendarEventWrite {
  return {
    title: values.title.trim(),
    description: values.description.trim() || null,
    location: values.location.trim() || null,
    isAllDay: values.isAllDay,
    allDayStartDate: values.isAllDay ? values.startDate : null,
    allDayEndDateExclusive: values.isAllDay ? addDays(values.endDate, 1) : null,
    localStart: values.isAllDay ? null : `${values.startDate}T${values.startTime}:00`,
    localEnd: values.isAllDay ? null : `${values.endDate}T${values.endTime}:00`,
    timeZoneId: values.isAllDay ? null : values.timeZoneId,
  };
}
