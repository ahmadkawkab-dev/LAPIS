import { useEffect, useState } from "react";
import { errorMessage, pushApi, type PushDevice } from "../../api";
import { Button } from "../../components/ui/Button";
import { browserPushSupported, browserPushErrorMessage, enableBrowserPush, clearBrowserNotificationSession } from "./browserPush";
import { installationId } from "./notificationState";
export function BrowserPushPreferences({ userId }: { userId: string }) {
  const [devices, setDevices] = useState<PushDevice[]>([]); const [enabled, setEnabled] = useState(false);
  const [busy, setBusy] = useState(false); const [failure, setFailure] = useState("");
  const [loading, setLoading] = useState(true);
  const [attempt, setAttempt] = useState(0);
  const supported = browserPushSupported();
  useEffect(() => { let active = true;
    setLoading(true); setFailure("");
    void Promise.all([pushApi.configuration(), pushApi.devices()]).then(([config, items]) => { if (active) { setEnabled(config.enabled); setDevices(items); } })
      .catch(cause => { if (active) setFailure(errorMessage(cause)); }).finally(() => { if (active) setLoading(false); });
    return () => { active = false; }; }, [userId, attempt]);
  async function enable() {
    setBusy(true); setFailure("");
    try { await enableBrowserPush(userId); setDevices(await pushApi.devices()); }
    catch (cause) { setFailure(browserPushErrorMessage(cause)); } finally { setBusy(false); }
  }
  async function remove(item: PushDevice) {
    setBusy(true); setFailure("");
    try { await pushApi.remove(item.id); if (item.installationId === installationId()) await clearBrowserNotificationSession(false); setDevices(await pushApi.devices()); }
    catch (cause) { setFailure(errorMessage(cause)); } finally { setBusy(false); }
  }
  return <section className="wk-account-section" aria-label="Browser notification devices"><h3>Browser notifications</h3>
    <p>Enable this browser, then turn on browser notifications in your account’s delivery settings.</p>
    {!supported && <p>This browser does not support push here. On iPhone or iPad, add Wukna to your Home Screen and open it there.</p>}
    {loading ? <p role="status">Checking browser notifications…</p> : !failure && !enabled && <p>Browser notifications are not available on this server yet.</p>}
    <Button type="button" variant="secondary" loading={busy} disabled={loading || !supported || !enabled} onClick={() => void enable()}>Enable this browser</Button>
    {devices.length > 0 && <ul>{devices.map((item, index) => <li key={item.id}>{item.installationId === installationId() ? "This browser" : `Browser ${index + 1}`} · enabled {new Date(item.createdAt).toLocaleDateString()}
      <Button type="button" size="compact" variant="quiet" loading={busy} onClick={() => void remove(item)}>Remove</Button></li>)}</ul>}
    {failure && <div role="alert"><p>{failure}</p><Button variant="secondary" disabled={busy || loading} onClick={() => setAttempt(value => value + 1)}>Try again</Button></div>}
  </section>;
}
