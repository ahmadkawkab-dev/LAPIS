const returnKey = "wukna.notification.returnPath.v1";
export function safeNotificationReturnPath(path: string | null): string | null {
  return path && /^\/(notifications|calendar(?:\?event=[0-9a-f-]{36})?|tasks\/[0-9a-f-]{36}|boards\/[0-9a-f-]{36}(?:\?(?:chat=1&message=|note=)[0-9a-f-]{36})?)$/i.test(path) ? path : null;
}
export function rememberNotificationReturnPath(path: string) {
  const safe = safeNotificationReturnPath(path);
  try { if (safe) sessionStorage.setItem(returnKey, safe); } catch { /* In-memory return still works. */ }
  return safe;
}
export function takeNotificationReturnPath(): string | null {
  try { const safe = safeNotificationReturnPath(sessionStorage.getItem(returnKey)); sessionStorage.removeItem(returnKey); return safe; } catch { return null; }
}
