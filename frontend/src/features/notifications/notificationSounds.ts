import type { NotificationSettings } from "../../api";

export const notificationSounds = [
  { key: "chatSoundEnabled", label: "Chat messages, mentions & replies", path: "/sounds/mixkit-positive-notification-for-chat-messages.wav" },
  { key: "taskReminderSoundEnabled", label: "Task & scheduled-task reminders", path: "/sounds/notification-for-task-reminder.wav" },
  { key: "scheduledTaskPostedSoundEnabled", label: "Scheduled task posted in chat", path: "/sounds/notification-when-calendar-task-sent-in-chat.wav" },
  { key: "boardInvitationSoundEnabled", label: "Board invitations", path: "/sounds/notification-when-invited-to-board.wav" },
  { key: "taskCompletedSoundEnabled", label: "Task completed", path: "/sounds/notification-when-tasks-are-finished.wav" },
] as const;
export type NotificationSoundKey = typeof notificationSounds[number]["key"];

export function notificationSoundVolume(settings: NotificationSettings, key: NotificationSoundKey): number {
  if (settings.soundsMuted || !settings[key] || !Number.isFinite(settings.soundVolume)) return 0;
  return Math.max(0, Math.min(1, settings.soundVolume));
}
