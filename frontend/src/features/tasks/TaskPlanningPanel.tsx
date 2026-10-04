import { useState, type FormEvent } from "react";
import { Pencil, Plus, Settings2, Trash2 } from "lucide-react";
import { errorMessage, planningSettingsApi, taskListApi, type TaskListDto } from "../../api";
import { Button, IconButton } from "../../components/ui/Button";
import { Dialog } from "../../components/ui/Dialog";
import { taskViews, type TaskView } from "./taskView";

const timeZoneOptions = <datalist id="wk-time-zones"><option value="UTC" />
  {(typeof Intl.supportedValuesOf === "function" ? Intl.supportedValuesOf("timeZone") : []).map((item) => <option key={item} value={item} />)}</datalist>;

type Mode = "create" | "rename" | "delete" | "zone" | null;

export function TaskPlanningPanel({ view, selectedListId, lists, zone, onView, onList,
  onCreated, onRenamed, onDeleted, onZoneChanged }: {
  view: TaskView; selectedListId: string | null; lists: TaskListDto[]; zone: string;
  onView: (view: TaskView) => void; onList: (id: string) => void;
  onCreated: (list: TaskListDto) => void; onRenamed: (list: TaskListDto) => void;
  onDeleted: (id: string) => void; onZoneChanged: (zone: string) => void;
}) {
  const [mode, setMode] = useState<Mode>(null);
  const [name, setName] = useState("");
  const [zoneDraft, setZoneDraft] = useState(zone);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const selectedList = lists.find((item) => item.id === selectedListId);
  function open(next: Mode) {
    setMode(next); setError(""); setName(next === "rename" ? selectedList?.name ?? "" : ""); setZoneDraft(zone);
  }
  async function saveList(event: FormEvent) {
    event.preventDefault();
    if (!name.trim()) return;
    setBusy(true); setError("");
    try {
      if (mode === "rename" && selectedList) onRenamed(await taskListApi.rename(selectedList.id, name));
      else onCreated(await taskListApi.create(name));
      setMode(null);
    } catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  }
  async function remove() {
    if (!selectedList) return;
    setBusy(true); setError("");
    try { await taskListApi.remove(selectedList.id); onDeleted(selectedList.id); setMode(null); }
    catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  }
  async function saveZone(event: FormEvent) {
    event.preventDefault();
    setBusy(true); setError("");
    try { onZoneChanged((await planningSettingsApi.update(zoneDraft)).timeZoneId!); setMode(null); }
    catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  }
  return <>
    <nav className="wk-task-views" aria-label="Task views">
      <p className="wk-task-eyebrow">Your tasks</p>
      <div className="wk-task-view-links">{taskViews.map(({ key, label }) => <button key={key} className={!selectedListId && view === key ? "wk-task-view--active" : ""}
        aria-current={!selectedListId && view === key ? "page" : undefined} onClick={() => onView(key)}>{label}</button>)}</div>
      <div className="wk-task-list-heading"><span className="wk-task-eyebrow">Lists</span>
        <IconButton label="New task list" onClick={() => open("create")}><Plus size={16} aria-hidden="true" /></IconButton></div>
      <div className="wk-task-list-links"><button className="wk-task-new-list-mobile" onClick={() => open("create")}><Plus size={15} aria-hidden="true" /> New list</button>
        {lists.map((list) => <button key={list.id} className={selectedListId === list.id ? "wk-task-view--active" : ""}
          aria-current={selectedListId === list.id ? "page" : undefined} onClick={() => onList(list.id)}>{list.name}</button>)}
        {selectedList && <div className="wk-task-list-actions">
          <IconButton label={`Rename ${selectedList.name}`} onClick={() => open("rename")}><Pencil size={15} aria-hidden="true" /></IconButton>
          <IconButton label={`Delete ${selectedList.name}`} onClick={() => open("delete")}><Trash2 size={15} aria-hidden="true" /></IconButton>
        </div>}
        <button className="wk-task-zone-mobile" onClick={() => open("zone")}><Settings2 size={15} aria-hidden="true" /> Time zone</button>
      </div>
      <button className="wk-task-zone-button" onClick={() => open("zone")} title={zone}>
        <Settings2 size={15} aria-hidden="true" /> Planning time zone <small>{zone}</small>
      </button>
    </nav>
    {(mode === "create" || mode === "rename") && <Dialog busy={busy} title={mode === "create" ? "New list" : "Rename list"} onClose={() => setMode(null)}>
      <form className="wk-task-dialog-form" onSubmit={(event) => void saveList(event)}>
        <label>List name <input value={name} maxLength={80} required onChange={(event) => setName(event.target.value)} /></label>
        {error && <p className="wk-task-error" role="alert">{error}</p>}
        <div className="wk-dialog-actions"><Button type="submit" loading={busy} disabled={!name.trim()}>{mode === "create" ? "Create list" : "Save name"}</Button>
          <Button variant="secondary" loading={busy} onClick={() => setMode(null)}>Cancel</Button></div>
      </form>
    </Dialog>}
    {mode === "delete" && selectedList && <Dialog busy={busy} title="Delete list?" onClose={() => setMode(null)} urgent>
      <p>“{selectedList.name}” and every task in it will be permanently deleted.</p>
      {error && <p className="wk-task-error" role="alert">{error}</p>}
      <div className="wk-dialog-actions"><Button variant="danger" loading={busy} onClick={() => void remove()}>Delete list</Button>
        <Button variant="secondary" loading={busy} onClick={() => setMode(null)}>Cancel</Button></div>
    </Dialog>}
    {mode === "zone" && <Dialog busy={busy} title="Planning time zone" onClose={() => setMode(null)}>
      <form className="wk-task-dialog-form" onSubmit={(event) => void saveZone(event)}>
        <p>This Week, Today, and Upcoming use this time zone. Existing scheduled times keep their own zones.</p>
        <label>Time zone <input value={zoneDraft} maxLength={100} required list="wk-time-zones"
          onChange={(event) => setZoneDraft(event.target.value)} /></label>
        {timeZoneOptions}
        {error && <p className="wk-task-error" role="alert">{error}</p>}
        <div className="wk-dialog-actions"><Button type="submit" loading={busy} disabled={!zoneDraft.trim()}>Save time zone</Button>
          <Button variant="secondary" loading={busy} onClick={() => setZoneDraft(Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC")}>Use device zone</Button></div>
      </form>
    </Dialog>}
  </>;
}
