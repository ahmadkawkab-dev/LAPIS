import type { NotificationDto, NotificationType } from "../../api";

const labels: Record<NotificationType, string> = {
  taskReminder: "Task reminder", chatActivity: "Chat activity", scheduledTaskReminder: "Scheduled-task reminder",
  sharedBoardActivity: "Board activity", boardInvitation: "Board invitation", taskActivity: "Task activity",
};
export const notificationLabel = (type: NotificationType) => labels[type] ?? "Activity";

// Only construct Wukna routes from typed resource identifiers; never navigate a supplied URL.
export function notificationPath(item: NotificationDto): string | null {
  if (item.type === "taskReminder" && item.taskId) return `/tasks/${encodeURIComponent(item.taskId)}`;
  if (item.boardId) {
    const base = `/boards/${encodeURIComponent(item.boardId)}`;
    if (item.resourceKind === "chatMessage" && item.resourceId) return `${base}?chat=1&message=${encodeURIComponent(item.resourceId)}`;
    if (item.resourceKind === "note" && item.resourceId) return `${base}?note=${encodeURIComponent(item.resourceId)}`;
    return base;
  }
  if (item.resourceKind === "calendarEvent" && item.resourceId) return `/calendar?event=${encodeURIComponent(item.resourceId)}`;
  return null;
}
