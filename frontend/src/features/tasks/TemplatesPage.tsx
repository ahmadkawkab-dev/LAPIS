import { LoadingSkeleton } from "../../components/ui/LoadingSkeleton";
import { useCallback, useEffect, useRef, useState } from "react";
import { Boxes, Plus, Search, Sparkles } from "lucide-react";
import { errorMessage, planningSettingsApi, taskTemplateApi, type TaskTemplateDto } from "../../api";
import { Button } from "../../components/ui/Button";
import { dateInZone } from "../../date";
import { TemplatesDialog } from "./TemplatesDialog";
import "./templates-page.css";

export function TemplatesPage({ notify, navigate }: { notify: (message: string) => void; navigate: (path: string) => void }) {
  const [templates, setTemplates] = useState<TaskTemplateDto[]>([]);
  const [hasMore, setHasMore] = useState(false);
  const [totalCount, setTotalCount] = useState(0);
  const [loadingMore, setLoadingMore] = useState(false);
  const requestId = useRef(0);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [zone, setZone] = useState(Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC");
  const [date, setDate] = useState(() => dateInZone(new Date(), Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC"));
  const [dialogTarget, setDialogTarget] = useState<string | "new" | null>(null);
  const [loading, setLoading] = useState(true);
  const [applying, setApplying] = useState(false);
  const [error, setError] = useState("");
  const [applyError, setApplyError] = useState("");
  const load = useCallback(async () => {
    const id = ++requestId.current;
    setLoading(true);
    setLoadingMore(false);
    setError("");
    try {
      const [page, settings] = await Promise.all([taskTemplateApi.page(search), planningSettingsApi.get()]);
      if (id !== requestId.current) return;
      setTemplates(page.items); setHasMore(page.hasMore); setTotalCount(page.totalCount);
      setSelectedId((current) => page.items.some((item) => item.id === current) ? current : page.items[0]?.id ?? null);
      const savedZone = settings.timeZoneId || Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
      setZone(savedZone);
      setDate(dateInZone(new Date(), savedZone));
    } catch (cause) {
      if (id === requestId.current) setError(errorMessage(cause));
    } finally {
      if (id === requestId.current) setLoading(false);
    }
  }, [search]);
  useEffect(() => {
    const timer = window.setTimeout(() => { void load(); }, search ? 200 : 0);
    return () => { window.clearTimeout(timer); requestId.current++; };
  }, [load, search]);
  async function loadMore() {
    if (!hasMore || loadingMore) return;
    const id = requestId.current;
    setLoadingMore(true); setError("");
    try {
      const page = await taskTemplateApi.page(search, templates.length);
      if (id !== requestId.current) return;
      setTemplates((current) => [...current, ...page.items]);
      setHasMore(page.hasMore); setTotalCount(page.totalCount);
    } catch (cause) { if (id === requestId.current) setError(errorMessage(cause)); }
    finally { if (id === requestId.current) setLoadingMore(false); }
  }
  const selected = templates.find((item) => item.id === selectedId) ?? null;
  async function applyTemplate() {
    if (!selected || !date) return;
    setApplying(true);
    setApplyError("");
    try {
      await taskTemplateApi.apply(selected.id, date);
      notify(`Added ${selected.items.length} ${selected.items.length === 1 ? "task" : "tasks"} to ${date}`);
      navigate("/tasks");
    } catch (cause) {
      setApplyError(errorMessage(cause));
    } finally {
      setApplying(false);
    }
  }
  return <section className="wk-templates-page" aria-labelledby="wk-templates-title">
    <header className="wk-templates-header">
      <div><span className="wk-templates-eyebrow">Reusable task systems</span><h1 id="wk-templates-title">Templates</h1><p>Start with structure, then make it yours.</p></div>
      <Button aria-haspopup="dialog" onClick={() => setDialogTarget("new")}><Plus size={18} aria-hidden="true" /> Create template</Button>
    </header>
    <div className="wk-templates-toolbar"><label><Search size={15} aria-hidden="true" /><input value={search}
      onChange={(event) => setSearch(event.target.value)} placeholder="Search templates" aria-label="Search templates" /></label></div>
    {error && <div className="wk-templates-error" role="alert"><p>{error}</p><Button variant="secondary" size="compact" onClick={() => void load()}>Try again</Button></div>}
    {loading && <LoadingSkeleton layout="cards" label="Loading templates…" />}
    {!loading && !error && <div className="wk-templates-layout">
      <div className="wk-templates-library">
        <div className="wk-templates-section-heading"><h2>Your templates</h2><p>{totalCount} {search ? "matching" : "saved"} {totalCount === 1 ? "template" : "templates"}</p></div>
        {templates.length ? <div className="wk-templates-grid">{templates.map((template) =>
          <button key={template.id} className={selectedId === template.id ? "wk-template-card--selected" : ""}
            aria-pressed={selectedId === template.id} onClick={() => setSelectedId(template.id)}>
            <span className="wk-template-card-icon"><Sparkles size={15} aria-hidden="true" /></span>
            <strong>{template.name}</strong>
            <small>{template.items.slice(0, 2).map((item) => item.title).join(" · ") || "Reusable tasks"}</small>
            <span className="wk-template-card-footer"><em>Personal</em><small>{template.items.length} {template.items.length === 1 ? "task" : "tasks"}</small></span>
          </button>)}</div> : <div className="wk-templates-empty">
            <Boxes size={24} aria-hidden="true" /><h3>{search ? "No matching templates" : "No templates yet"}</h3>
            <p>{search ? "Try another name." : "Use Create template above to reuse a set of tasks."}</p>
          </div>}
        {hasMore && <Button variant="secondary" size="compact" disabled={loadingMore} onClick={() => void loadMore()}>{loadingMore ? "Loading…" : "Load more templates"}</Button>}
      </div>
      <aside className="wk-template-preview" aria-label="Template preview">
        {selected ? <>
          <span className="wk-template-preview-icon"><Sparkles size={18} aria-hidden="true" /></span>
          <h2>{selected.name}</h2>
          <p>{selected.items.length} {selected.items.length === 1 ? "task" : "tasks"} ready to add to your plan.</p>
          <ol>{selected.items.map((item, index) => <li key={index}><span>{index + 1}</span><div><strong>{item.title}</strong>
            {item.plannedTime && <small>{item.plannedTime.slice(0, 5)}</small>}
            {item.description && <small>{item.description}</small>}</div></li>)}</ol>
          <label>Apply to date<input type="date" value={date} onChange={(event) => setDate(event.target.value)} /></label>
          {applyError && <p className="wk-templates-error" role="alert">{applyError}</p>}
          <div className="wk-template-preview-actions"><Button variant="secondary" size="compact" onClick={() => setDialogTarget(selected.id)}>Edit</Button>
            <Button size="compact" disabled={applying || !date} onClick={() => void applyTemplate()}><Plus size={14} aria-hidden="true" /> Use template</Button></div>
        </> : <p>Select a template to preview its tasks.</p>}
      </aside>
    </div>}
    {dialogTarget && <TemplatesDialog action={{ mode: "manage" }} dayTasks={[]} zone={zone}
      initialTemplateId={dialogTarget === "new" ? undefined : dialogTarget} startCreating={dialogTarget === "new"}
      onClose={() => { setDialogTarget(null); void load(); }} onApplied={() => { notify("Template tasks added"); void load(); }}
      onSaved={(message) => { notify(message); void load(); }} />}
  </section>;
}
