import type { PersonalTaskDto, TaskWrite } from "../../api";
import { addDays } from "../../date.ts";

export type TaskView = "week" | "inbox" | "today" | "upcoming" | "all" | "completed";
export const taskViews: { key: TaskView; label: string }[] = [
  { key: "week", label: "This week" }, { key: "inbox", label: "Inbox" }, { key: "today", label: "Today" },
  { key: "upcoming", label: "Upcoming" }, { key: "all", label: "All tasks" },
  { key: "completed", label: "Completed" },
];

export function taskWritePayload(title: string, description: string, date: string, time: string, zone: string, listId: string | null = null): TaskWrite {
  return {
    title: title.trim(),
    description: description.trim() || null,
    plannedDate: date || null,
    plannedTime: date && time ? time : null,
    timeZoneId: date && time ? zone : null,
    listId,
  };
}

export function todayGroups(tasks: PersonalTaskDto[], today: string) {
  return [
    { label: "Overdue", items: tasks.filter((task) => task.plannedDate !== null && task.plannedDate < today) },
    { label: "Timed", items: tasks.filter((task) => task.plannedDate === today && task.plannedTime !== null) },
    { label: "Anytime", items: tasks.filter((task) => task.plannedDate === today && task.plannedTime === null) },
  ];
}

export function weekStart(date: string): string {
  const day = new Date(`${date}T12:00:00Z`).getUTCDay();
  return addDays(date, -((day + 6) % 7));
}

export function weekDates(start: string): string[] {
  return Array.from({ length: 7 }, (_, index) => addDays(start, index));
}
