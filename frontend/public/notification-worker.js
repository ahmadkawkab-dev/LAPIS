/* Dedicated to notifications. Never caches authenticated pages or stores access tokens. */
self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", event => event.waitUntil(self.clients.claim()));
function database() {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open("wukna-notifications", 1);
    request.onupgradeneeded = () => { request.result.createObjectStore("seen"); request.result.createObjectStore("session"); };
    request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error);
  });
}
async function record(data) {
  const db = await database();
  try { return await new Promise((resolve, reject) => {
    const tx = db.transaction(["session", "seen"], "readwrite"); let binding, seen;
    const get = tx.objectStore("session").get("binding"); get.onsuccess = () => { binding = get.result; };
    const key = `${data.userId}:${data.notificationId}:${data.revision}`;
    const previous = tx.objectStore("seen").get(key); previous.onsuccess = () => { seen = !!previous.result; tx.objectStore("seen").put(Date.now(), key); };
    tx.oncomplete = () => resolve({ binding, seen }); tx.onerror = () => reject(tx.error);
  }); } finally { db.close(); }
}
const guid = value => typeof value === "string" && /^[0-9a-f-]{36}$/i.test(value);
function safePath(path) {
  return typeof path === "string" && /^\/(notifications|calendar(?:\?event=[0-9a-f-]{36})?|tasks\/[0-9a-f-]{36}|boards\/[0-9a-f-]{36}(?:\?(?:chat=1&message=|note=)[0-9a-f-]{36})?)$/i.test(path) ? path : "/notifications";
}
self.addEventListener("push", event => event.waitUntil((async () => {
  let data; try { data = event.data?.json(); } catch { data = null; }
  if (!data || !guid(data.userId) || !guid(data.notificationId) || !guid(data.installationId) || !Number.isSafeInteger(data.revision)) data = {};
  let state; try { state = await record(data); } catch { state = {}; }
  const bound = state.binding?.userId === data.userId && state.binding?.installationId === data.installationId;
  const windows = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
  for (const client of windows) client.postMessage({ type: "wukna:push-shown", notificationId: data.notificationId, revision: data.revision, userId: data.userId, alreadyHandled: state.seen === true });
  // Every received push produces an OS notification, including a race with a newly foreground tab.
  // A stable tag replaces duplicate deliveries; never silently drop pushes on WebKit.
  await self.registration.showNotification("Wukna", {
    body: bound && typeof data.body === "string" ? data.body.slice(0, 500) : "You have new activity",
    tag: bound ? `wukna:${data.notificationId}` : "wukna:activity", renotify: false,
    silent: !bound || state.seen || data.silent === true,
    data: { path: bound ? safePath(data.path) : "/notifications", userId: bound ? data.userId : null },
  });
})()));
self.addEventListener("notificationclick", event => {
  event.notification.close(); event.waitUntil((async () => {
    let binding; const db = await database();
    try { binding = await new Promise((resolve, reject) => { const request = db.transaction("session").objectStore("session").get("binding"); request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error); }); } finally { db.close(); }
    const path = binding?.userId === event.notification.data?.userId ? safePath(event.notification.data.path) : "/notifications";
    const clients = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
    const client = clients.find(client => new URL(client.url).origin === self.location.origin);
    if (client) { await client.navigate(path); await client.focus(); } else await self.clients.openWindow(path);
  })());
});
