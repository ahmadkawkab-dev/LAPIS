import { errorMessage, pushApi } from "../../api";
import { bindNotificationSession, clearNotificationBinding, installationId } from "./notificationState";
class BrowserPushSetupError extends Error {}
export function browserPushErrorMessage(cause: unknown): string {
  return cause instanceof BrowserPushSetupError ? cause.message : errorMessage(cause);
}
export const browserPushSupported = () => window.isSecureContext && "serviceWorker" in navigator && "PushManager" in window && "Notification" in window;
export async function enableBrowserPush(userId: string) {
  if (!browserPushSupported()) throw new BrowserPushSetupError("Push notifications are not supported in this browser. On iPhone or iPad, add Wukna to your Home Screen first.");
  const installation = installationId();
  try { if (localStorage.getItem("wukna.notification.installation") !== installation) throw new Error(); }
  catch { throw new BrowserPushSetupError("Allow site storage in your browser to enable notifications."); }
  // Permission is requested only from the explicit button click.
  let permission: NotificationPermission;
  try { permission = await Notification.requestPermission(); }
  catch { throw new BrowserPushSetupError("Could not request notification permission. Allow notifications in your browser settings and try again."); }
  if (permission !== "granted") throw new BrowserPushSetupError(permission === "denied" ? "Notifications are blocked. Allow them in your browser settings." : "Notification permission was not granted.");
  const config = await pushApi.configuration();
  if (!config.enabled || !config.publicKey) throw new BrowserPushSetupError("Browser notifications are not available on this server yet.");
  let registration: ServiceWorkerRegistration;
  try {
    await navigator.serviceWorker.register("/notification-worker.js", { scope: "/" });
    registration = await navigator.serviceWorker.ready;
  } catch { throw new BrowserPushSetupError("Could not start browser notifications. Allow site storage, reload Wukna and try again."); }
  // Refresh browser keys when linking an installation, so account ownership never gets transferred by endpoint reuse.
  try {
    const previous = await registration.pushManager.getSubscription();
    if (previous) await previous.unsubscribe();
  } catch { throw new BrowserPushSetupError("Could not refresh this browser’s push subscription. Restart your browser and try again."); }
  const encoded = config.publicKey.replaceAll("-", "+").replaceAll("_", "/");
  const key = Uint8Array.from(atob(encoded + "=".repeat((4 - encoded.length % 4) % 4)), value => value.charCodeAt(0));
  let subscription: PushSubscription;
  try { subscription = await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key }); }
  catch (cause) {
    if (cause instanceof DOMException && cause.name === "NotAllowedError")
      throw new BrowserPushSetupError("Notifications are blocked. Allow them in your browser settings.");
    if (cause instanceof DOMException && cause.name === "AbortError")
      throw new BrowserPushSetupError("Your browser could not connect to its push service. In Brave, enable “Use Google services for push messaging” under Settings → Privacy and security, restart Brave and try again.");
    throw new BrowserPushSetupError("Your browser could not enable push notifications. Check its notification settings and push service connection, then try again.");
  }
  const data = subscription.toJSON();
  if (!data.endpoint || !data.keys?.p256dh || !data.keys?.auth) { await subscription.unsubscribe().catch(() => undefined); throw new BrowserPushSetupError("Your browser returned an incomplete push subscription. Reload Wukna and try again."); }
  try {
    await pushApi.register(installation, data.endpoint, data.keys.p256dh, data.keys.auth);
    try { await bindNotificationSession(userId, installation); }
    catch { throw new BrowserPushSetupError("Allow site storage in your browser to finish enabling notifications."); }
  } catch (cause) { await subscription.unsubscribe().catch(() => undefined); throw cause; }
}
export async function clearBrowserNotificationSession(deactivate = true, expectedUserId?: string) {
  const installation = installationId();
  const cleared = await clearNotificationBinding(expectedUserId).catch(() => !expectedUserId);
  if (deactivate) await pushApi.disableInstallation(installation).catch(() => undefined);
  if (cleared && "serviceWorker" in navigator) {
    const registration = await navigator.serviceWorker.getRegistration("/");
    await (await registration?.pushManager.getSubscription())?.unsubscribe();
    for (const notification of await registration?.getNotifications() ?? []) notification.close();
  }
}
