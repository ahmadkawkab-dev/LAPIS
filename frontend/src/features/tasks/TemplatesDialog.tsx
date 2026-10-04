import { useEffect, useState, type FormEvent } from "react";
import { Pencil, Plus, Trash2 } from "lucide-react";
import { errorMessage, taskTemplateApi, type PersonalTaskDto, type TaskTemplateDto, type TaskTemplateItem } from "../../api";
import { Button } from "../../components/ui/Button";
import { Dialog } from "../../components/ui/Dialog";
import type { TemplateAction } from "./WeeklyPlanner";

function dayName(date: string) {
  return new Intl.DateTimeFormat(undefined, { weekday: "long", month: "short", day: "numeric", timeZone: "UTC" })
    .format(new Date(`${date}T12:00:00Z`));
}

export function TemplatesDialog({ action, dayTasks, zone, onClose, onApplied, onSaved, initialTemplateId, startCreating = false }: {
  action: TemplateAction; dayTasks: PersonalTaskDto[]; zone: string; onClose: () => void;
  onApplied: () => void; onSaved: (message: string) => void; initialTemplateId?: string; startCreating?: boolean;
}) {
  const [mode, setMode] = useState<"list" | "apply" | "editor">(startCreating || action.mode === "save" ? "editor" : action.mode === "apply" ? "apply" : "list");
  const [templates, setTemplates] = useState<TaskTemplateDto[]>([]);
  const [hasMore, setHasMore] = useState(false);
  const [nextOffset, setNextOffset] = useState(0);
  const [loadingMore, setLoadingMore] = useState(false);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [applyingId, setApplyingId] = useState<string | null>(null);
  const [error, setError] = useState("");
  const [editingId, setEditingId] = useState<string | null>(null);
  const [name, setName] = useState(action.mode === "save" && action.date ? `${dayName(action.date).split(",")[0]} routine` : "");
  const [items, setItems] = useState<TaskTemplateItem[]>(action.mode === "save"
    ? dayTasks.map((task) => ({ title: task.title, description: task.description,
      plannedTime: task.plannedTime, timeZoneId: task.timeZoneId }))
    : [{ title: "", description: null, plannedTime: null, timeZoneId: null }]);
  const [confirmApply, setConfirmApply] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    void Promise.all([taskTemplateApi.page(), initialTemplateId ? taskTemplateApi.get(initialTemplateId) : Promise.resolve(null)])
      .then(([page, selected]) => { if (active) {
        setTemplates(page.items); setHasMore(page.hasMore); setNextOffset(page.items.length);
        if (selected) edit(selected);
      } })
      .catch((cause) => { if (active) setError(errorMessage(cause)); })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, []);

  async function loadMore() {
    if (!hasMore || loadingMore) return;
    setLoadingMore(true); setError("");
    try {
      const page = await taskTemplateApi.page("", nextOffset);
      setTemplates((current) => [...current, ...page.items]);
      setHasMore(page.hasMore); setNextOffset((current) => current + page.items.length);
    } catch (cause) { setError(errorMessage(cause)); }
    finally { setLoadingMore(false); }
  }

  function edit(template?: TaskTemplateDto) {
    setEditingId(template?.id ?? null); setName(template?.name ?? "");
    setItems(template?.items.map((item) => ({ ...item })) ?? [{ title: "", description: null, plannedTime: null, timeZoneId: null }]);
    setError(""); setMode("editor");
  }
  async function save(event: FormEvent) {
    event.preventDefault(); if (busy) return; setBusy(true); setError("");
    try {
      const cleaned = items.filter((item) => item.title.trim()).map((item) => ({ ...item, title: item.title.trim() }));
      const saved = editingId ? await taskTemplateApi.update(editingId, name, cleaned) : await taskTemplateApi.create(name, cleaned);
      setTemplates((current) => [...current.filter((item) => item.id !== saved.id), saved].sort((a, b) => a.name.localeCompare(b.name)));
      onSaved(editingId ? "Template updated" : "Template saved");
      if (action.mode === "save") onClose(); else setMode(action.mode === "apply" ? "apply" : "list");
    } catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  }
  async function remove(id: string) {
    if (busy) return;
    setBusy(true); setError("");
    try { await taskTemplateApi.remove(id); setTemplates((current) => current.filter((item) => item.id !== id)); setConfirmDelete(null); onSaved("Template deleted"); }
    catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  }
  async function apply(id: string) {
    if (busy || !action.date) return;
    setApplyingId(id); setBusy(true); setError("");
    try { await taskTemplateApi.apply(id, action.date); onApplied(); onClose(); }
    catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); setApplyingId(null); setConfirmApply(null); }
  }
  const title = mode === "editor" ? (editingId ? "Edit template" : action.mode === "save" ? "Save day as template" : "New template")
    : mode === "apply" ? `Apply to ${action.date ? dayName(action.date) : "day"}` : "Templates";
  return <Dialog busy={busy} title={title} onClose={onClose} className="wk-template-dialog">
    <fieldset className="wk-template-controls" disabled={busy} aria-label="Template controls">
    {error && <p className="wk-task-error" role="alert">{error}</p>}
    {mode === "editor" ? <form className="wk-template-editor" onSubmit={(event) => void save(event)}>
      <label>Template name <input value={name} maxLength={80} required autoFocus onChange={(event) => setName(event.target.value)} placeholder="Monday routine" /></label>
      <div className="wk-template-items"><span className="wk-task-eyebrow">Tasks</span>
        {items.map((item, index) => <div className="wk-template-item" key={index}>
          <input aria-label={`Task ${index + 1} name`} value={item.title} maxLength={200} placeholder="Task name"
            onChange={(event) => setItems((current) => current.map((entry, position) => position === index ? { ...entry, title: event.target.value } : entry))} />
          <input aria-label={`Task ${index + 1} time`} type="time" value={item.plannedTime?.slice(0, 5) ?? ""}
            onChange={(event) => setItems((current) => current.map((entry, position) => position === index ? {
              ...entry, plannedTime: event.target.value || null, timeZoneId: event.target.value ? entry.timeZoneId ?? zone : null
            } : entry))} />
          <button type="button" aria-label={`Remove task ${index + 1}`} onClick={() => setItems((current) => current.filter((_, position) => position !== index))}><Trash2 size={16} aria-hidden="true" /></button>
        </div>)}
        <Button variant="quiet" size="compact" onClick={() => setItems((current) => [...current, { title: "", description: null, plannedTime: null, timeZoneId: null }])}
          disabled={items.length >= 50}><Plus size={16} aria-hidden="true" /> Add task</Button>
      </div>
      <div className="wk-dialog-actions"><Button type="submit" loading={busy} disabled={!name.trim() || !items.some((item) => item.title.trim())}>Save template</Button>
        <Button variant="secondary" onClick={() => action.mode === "save" ? onClose() : setMode(action.mode === "apply" ? "apply" : "list")}>Cancel</Button></div>
    </form> : <div className="wk-template-list">
      {loading && <p role="status">Loading templates…</p>}
      {!loading && templates.length === 0 && <p className="wk-template-empty">No templates yet. Save a day or make a routine to reuse later.</p>}
      {templates.map((template) => <div className="wk-template-entry" key={template.id}>
        <div><strong>{template.name}</strong><small>{template.items.length} {template.items.length === 1 ? "task" : "tasks"}</small></div>
        {mode === "apply" ? <Button variant="secondary" size="compact" loading={applyingId === template.id} onClick={() => dayTasks.length ? setConfirmApply(template.id) : void apply(template.id)}>Apply</Button>
          : <div className="wk-template-entry-actions"><button aria-label={`Edit ${template.name}`} onClick={() => edit(template)}><Pencil size={16} aria-hidden="true" /></button>
            <button aria-label={`Delete ${template.name}`} onClick={() => setConfirmDelete(template.id)}><Trash2 size={16} aria-hidden="true" /></button></div>}
      </div>)}
      {hasMore && <Button variant="secondary" size="compact" disabled={loadingMore} onClick={() => void loadMore()}>{loadingMore ? "Loading…" : "Load more templates"}</Button>}
      {confirmApply && <div className="wk-template-confirm"><p>This day has {dayTasks.length} existing {dayTasks.length === 1 ? "task" : "tasks"}. Add the template tasks alongside them?</p>
        <div className="wk-dialog-actions"><Button loading={busy} onClick={() => void apply(confirmApply)}>Add tasks</Button>
          <Button variant="secondary" onClick={() => setConfirmApply(null)}>Cancel</Button></div></div>}
      {confirmDelete && <div className="wk-template-confirm"><p>Delete “{templates.find((item) => item.id === confirmDelete)?.name}”? Tasks already created from it will stay.</p>
        <div className="wk-dialog-actions"><Button variant="danger" loading={busy} onClick={() => void remove(confirmDelete)}>Delete template</Button>
          <Button variant="secondary" onClick={() => setConfirmDelete(null)}>Cancel</Button></div></div>}
      <div className="wk-dialog-actions"><Button variant="secondary" onClick={() => edit()}><Plus size={16} aria-hidden="true" /> New template</Button>
        <Button variant="quiet" onClick={onClose}>Close</Button></div>
    </div>}
    </fieldset>
  </Dialog>;
}
