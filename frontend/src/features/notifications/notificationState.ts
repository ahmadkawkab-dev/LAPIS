export type ChatNotificationView = { boardId: string; visible: boolean; latest: boolean };
let chat: ChatNotificationView | null = null;
export const chatNotificationView = () => chat;
export function setChatNotificationView(value: ChatNotificationView | null) { if (chat?.boardId === value?.boardId && chat?.visible === value?.visible && chat?.latest === value?.latest) return; chat = value; window.dispatchEvent(new Event("wukna:notification-presence")); }
let unread = 0;
let boardUnread: ReadonlyMap<string, number> = new Map();
export const chatUnreadCounts = () => boardUnread;
export function setChatUnreadCounts(rows: { boardId: string; unreadCount: number }[]) { boardUnread = new Map(rows.map(row => [row.boardId, row.unreadCount])); for (const listener of listeners) listener(); }
const listeners = new Set<() => void>();
export const notificationUnread = () => unread;
export const subscribeNotificationUnread = (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener); }; };
export function setNotificationUnread(value: number) { unread = value; for (const listener of listeners) listener(); window.dispatchEvent(new Event("wukna:notifications-changed")); }

let currentInstallationId: string | null = null;
export function installationId(): string {
  if (currentInstallationId) return currentInstallationId;
  try { const existing = localStorage.getItem("wukna.notification.installation");
    if (existing && /^[0-9a-f-]{36}$/i.test(existing)) return currentInstallationId = existing;
    const id = crypto.randomUUID(); localStorage.setItem("wukna.notification.installation", id); return currentInstallationId = id;
  } catch { return currentInstallationId = crypto.randomUUID(); }
}
function database(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open("wukna-notifications", 1);
    request.onupgradeneeded = () => { request.result.createObjectStore("seen"); request.result.createObjectStore("session"); };
    request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error);
  });
}
export async function bindNotificationSession(userId: string | null, installation: string) {
  const db = await database();
  await new Promise<void>((resolve, reject) => { const transaction = db.transaction("session", "readwrite");
    const store = transaction.objectStore("session"); if (userId) store.put({ userId, installationId: installation }, "binding"); else store.delete("binding");
    transaction.oncomplete = () => resolve(); transaction.onerror = () => reject(transaction.error); }); db.close();
}
export async function clearNotificationBinding(expectedUserId?: string): Promise<boolean> {
  const db = await database();
  try { return await new Promise<boolean>((resolve, reject) => {
    const transaction = db.transaction("session", "readwrite"), store = transaction.objectStore("session"); let cleared = false;
    const request = store.get("binding"); request.onsuccess = () => {
      if (!expectedUserId || request.result?.userId === expectedUserId) { store.delete("binding"); cleared = true; }
    };
    transaction.oncomplete = () => resolve(cleared); transaction.onerror = () => reject(transaction.error);
  }); } finally { db.close(); }
}
/** IndexedDB serializes this claim across tabs and the service worker. */
export async function claimNotification(userId: string, id: string, revision: number): Promise<boolean> {
  const db = await database(); const key = `${userId}:${id}:${revision}`;
  try { return await new Promise<boolean>((resolve, reject) => { const transaction = db.transaction("seen", "readwrite"), store = transaction.objectStore("seen");
    let claimed = false; const get = store.get(key); get.onsuccess = () => { if (!get.result) { claimed = true; store.put(Date.now(), key); }
      const cursor = store.openCursor(); cursor.onsuccess = () => { const value = cursor.result; if (value) { if (value.value < Date.now() - 7 * 86400000) value.delete(); value.continue(); } }; };
    transaction.oncomplete = () => resolve(claimed); transaction.onerror = () => reject(transaction.error); });
  } finally { db.close(); }
}
export function notificationSoundKey(item: { type: string; activityKind: string | null }) {
  if (item.type === "chatActivity") return item.activityKind === "scheduledTaskPosted" ? "scheduledTaskPostedSoundEnabled" as const : "chatSoundEnabled" as const;
  if (item.type === "taskReminder" || item.type === "scheduledTaskReminder") return "taskReminderSoundEnabled" as const;
  if (item.type === "boardInvitation") return "boardInvitationSoundEnabled" as const;
  if (item.type === "taskActivity" && item.activityKind === "taskCompleted") return "taskCompletedSoundEnabled" as const;
  return null;
}
