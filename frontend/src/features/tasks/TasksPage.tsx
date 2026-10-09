import { LoadingSkeleton } from "../../components/ui/LoadingSkeleton";
import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { Bell, CalendarDays, Check, ChevronRight, Clock3, Plus, SlidersHorizontal, Sparkles, X } from "lucide-react";
import { errorMessage, planningSettingsApi, taskApi, taskListApi, taskReminderApi, taskTemplateApi, type PersonalTaskDto, type TaskListDto, type TaskTemplateDto, type TaskReminderDto } from "../../api";
import { Button, IconButton } from "../../components/ui/Button";
import { Dialog } from "../../components/ui/Dialog";
import { dateInZone } from "../../date";
import { taskViews, taskWritePayload, todayGroups, type TaskView } from "./taskView";
import { TaskPlanningPanel } from "./TaskPlanningPanel";
import { WeeklyPlanner, type TemplateAction } from "./WeeklyPlanner";
import { TemplatesDialog } from "./TemplatesDialog";
import { weekStart } from "./taskView";
import "./tasks.css";

const deviceZone = Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";

function initialDate(view: TaskView, today: string) {
  if (view === "today") return today;
  if (view !== "upcoming") return "";
  const next = new Date(`${today}T12:00:00`);
  next.setDate(next.getDate() + 1);
  return `${next.getFullYear()}-${String(next.getMonth() + 1).padStart(2, "0")}-${String(next.getDate()).padStart(2, "0")}`;
}

function dateLabel(date: string) {
  return new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric" }).format(new Date(`${date}T12:00:00`));
}

function taskSchedule(task: PersonalTaskDto, today: string) {
  if (!task.plannedDate) return "No date";
  const day = task.plannedDate === today ? "Today" : dateLabel(task.plannedDate);
  return task.plannedTime ? `${day} · ${task.plannedTime.slice(0, 5)}` : day;
}

function TaskQuickAdd({ view, today, zone, listId, onCreated }: {
  view: TaskView; today: string; zone: string; listId: string | null; onCreated: (task: PersonalTaskDto) => void;
}) {
  const [title, setTitle] = useState("");
  const [date, setDate] = useState(initialDate(view, today));
  const [time, setTime] = useState("");
  const [expanded, setExpanded] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  useEffect(() => { setDate(initialDate(view, today)); setTime(""); }, [view, today]);
  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!title.trim()) return;
    setBusy(true);
    setError("");
    try {
      const created = await taskApi.create(taskWritePayload(title, "", date, time, zone, listId));
      setTitle("");
      setTime("");
      onCreated(created);
    } catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  }
  return <form className="wk-task-quick-add" onSubmit={(event) => void submit(event)}>
    <div className="wk-task-quick-main"><Plus size={19} aria-hidden="true" />
      <label className="wk-sr-only" htmlFor="wk-new-task">New task</label>
      <input id="wk-new-task" value={title} onChange={(event) => setTitle(event.target.value)}
        maxLength={200} placeholder={view === "today" ? "Add to today" : "Add a task"} />
      <Button size="compact" type="submit" loading={busy} disabled={!title.trim()}>Add</Button>
    </div>
    <button type="button" className="wk-task-more" aria-expanded={expanded} onClick={() => setExpanded(!expanded)}>
      <CalendarDays size={16} aria-hidden="true" /> {date ? `${dateLabel(date)}${time ? ` · ${time}` : ""}` : "Date and time"} <ChevronRight size={15} aria-hidden="true" />
    </button>
    {expanded && <div className="wk-task-quick-options">
      <label>Date <input type="date" value={date} onChange={(event) => { setDate(event.target.value); if (!event.target.value) setTime(""); }} /></label>
      <label>Time <input type="time" value={time} disabled={!date} onChange={(event) => setTime(event.target.value)} /></label>
      {time && <small>Time zone: {zone}</small>}
    </div>}
    {error && <p className="wk-task-error" role="alert">{error}</p>}
  </form>;
}

function TaskDetails({ task, today, defaultZone, lists, onClose, onChanged, onDeleted }: {
  task: PersonalTaskDto; today: string; defaultZone: string; lists: TaskListDto[]; onClose: () => void;
  onChanged: (task: PersonalTaskDto) => void; onDeleted: () => void;
}) {
  const [title, setTitle] = useState(task.title);
  const [description, setDescription] = useState(task.description ?? "");
  const [date, setDate] = useState(task.plannedDate ?? "");
  const [time, setTime] = useState(task.plannedTime?.slice(0, 5) ?? "");
  const [zone, setZone] = useState(task.timeZoneId ?? defaultZone);
  const [listId, setListId] = useState(task.listId ?? "");
  const [busy, setBusy] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [error, setError] = useState("");
  const [narrow, setNarrow] = useState(() => window.matchMedia('(max-width: 980px)').matches);
  useEffect(() => {
    const media = window.matchMedia('(max-width: 980px)');
    const update = () => setNarrow(media.matches);
    media.addEventListener('change', update);
    return () => media.removeEventListener('change', update);
  }, []);
  const details = useRef<HTMLElement>(null);
  useEffect(() => {
    if (narrow) return;
    const origin = document.activeElement;
    details.current?.querySelector<HTMLInputElement>('input')?.focus({ preventScroll: true });
    return () => { if (origin instanceof HTMLElement && origin.isConnected) origin.focus({ preventScroll: true }); };
  }, [narrow]);
  async function save(event: FormEvent) {
    event.preventDefault();
    if (busy) return;
    setBusy(true); setError("");
    try { onChanged(await taskApi.update(task.id, taskWritePayload(title, description, date, time, zone, listId || null))); }
    catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  }
  async function remove() {
    setBusy(true); setError("");
    try { await taskApi.remove(task.id); onDeleted(); }
    catch (cause) { setError(errorMessage(cause)); setConfirmDelete(false); }
    finally { setBusy(false); }
  }
  const content = <aside ref={details} className="wk-task-details" aria-label="Task details"
    onKeyDown={(event) => { if (event.key === 'Escape' && !busy && !confirmDelete) { event.preventDefault(); event.stopPropagation(); onClose(); } }}>
    <div className="wk-task-details-heading"><div><span className="wk-task-eyebrow">Task details</span><p>{taskSchedule(task, today)}</p></div>
      <IconButton label="Close task details" disabled={busy} onClick={onClose}><X size={18} aria-hidden="true" /></IconButton></div>
    <form onSubmit={(event) => void save(event)}>
      <label>Title <input value={title} maxLength={200} required onChange={(event) => setTitle(event.target.value)} /></label>
      <label>Description <textarea value={description} maxLength={4000} rows={5} onChange={(event) => setDescription(event.target.value)} /></label>
      <label>List <select value={listId} onChange={(event) => setListId(event.target.value)}>
        <option value="">No list</option>{lists.map((list) => <option key={list.id} value={list.id}>{list.name}</option>)}
      </select></label>
      <div className="wk-task-form-row"><label>Date <input type="date" value={date} onChange={(event) => { setDate(event.target.value); if (!event.target.value) setTime(""); }} /></label>
        <label>Time <input type="time" value={time} disabled={!date} onChange={(event) => setTime(event.target.value)} /></label></div>
      {time && <label>Time zone <input value={zone} maxLength={100} onChange={(event) => setZone(event.target.value)} /></label>}
      <div className="wk-task-details-actions"><Button type="submit" loading={busy} disabled={!title.trim()}>Save changes</Button>
        <Button type="button" variant="quiet" onClick={() => setConfirmDelete(true)} disabled={busy}>Delete</Button></div>
      {error && <p className="wk-task-error" role="alert">{error}</p>}
    </form>
    {task.plannedAtUtc && !task.completedAt && <TaskReminderControl task={task} />}
    {confirmDelete && <Dialog title="Delete task?" busy={busy} onClose={() => setConfirmDelete(false)} urgent>
      <p>“{task.title}” will be removed.</p><div className="wk-dialog-actions">
        <Button variant="danger" loading={busy} onClick={() => void remove()}>Delete task</Button>
        <Button variant="secondary" loading={busy} onClick={() => setConfirmDelete(false)}>Cancel</Button>
      </div>
    </Dialog>}
  </aside>;
  return narrow ? <Dialog title="Task details" busy={busy} className="wk-task-details-dialog" onClose={onClose}>{content}</Dialog> : content;
}

function TaskReminderControl({ task }: { task: PersonalTaskDto }) {
  const [reminder, setReminder] = useState<TaskReminderDto | undefined>();
  const [minutes, setMinutes] = useState(15);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  useEffect(() => {
    let active = true;
    void taskReminderApi.get(task.id).then((value) => {
      if (active) { setReminder(value); setMinutes(value?.minutesBefore ?? 15); setLoading(false); }
    }).catch((cause) => { if (active) { setError(errorMessage(cause)); setLoading(false); } });
    return () => { active = false; };
  }, [task.id]);
  async function save() {
    setBusy(true); setError("");
    try { setReminder(await taskReminderApi.put(task.id, minutes)); }
    catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  }
  async function remove() {
    setBusy(true); setError("");
    try { await taskReminderApi.remove(task.id); setReminder(undefined); }
    catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  }
  return <section className="wk-task-reminder-control" aria-label="Task reminder">
    <h3><Bell size={16} aria-hidden="true" /> Reminder</h3>
    {loading ? <p role="status">Loading reminder…</p> : <><p>{reminder ? `Set for ${new Date(reminder.dueAtUtc).toLocaleString()}` : "Get notified before this task starts."}</p>
      <label>Remind me<select value={minutes} onChange={(event) => setMinutes(Number(event.target.value))}>
        <option value={0}>At task time</option><option value={5}>5 minutes before</option>
        <option value={15}>15 minutes before</option><option value={30}>30 minutes before</option>
        <option value={60}>1 hour before</option><option value={1440}>1 day before</option>
      </select></label><div><Button size="compact" loading={busy} onClick={() => void save()}>{reminder ? "Update reminder" : "Set reminder"}</Button>
        {reminder && <Button size="compact" variant="quiet" loading={busy} onClick={() => void remove()}>Remove</Button>}</div></>}
    {error && <p role="alert" className="wk-task-error">{error}</p>}
  </section>;
}

function QuickTemplates({ navigate }: { navigate: (path: string) => void }) {
  const [templates, setTemplates] = useState<TaskTemplateDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const load = useCallback(async () => {
    setLoading(true); setError("");
    try { setTemplates((await taskTemplateApi.page()).items.slice(0, 5)); }
    catch (cause) { setError(errorMessage(cause)); }
    finally { setLoading(false); }
  }, []);
  useEffect(() => { void load(); }, [load]);
  return <aside className="wk-quick-template-panel" aria-label="Task templates">
    <div className="wk-quick-template-heading"><div><h2>Templates</h2><p>Reusable plans for recurring work.</p></div>
      <button type="button" onClick={() => navigate("/tasks/templates")}>{!loading && !error && !templates.length ? "Create template" : "Browse all"}</button></div>
    {loading && <LoadingSkeleton label="Loading templates…" />}
    {error && <div role="alert"><p>{error}</p><Button variant="secondary" size="compact" onClick={() => void load()}>Try again</Button></div>}
    {!loading && !error && (templates.length ? <ul>{templates.slice(0, 5).map((template) => <li key={template.id}>
      <div className="wk-quick-template-summary"><span><Sparkles size={15} aria-hidden="true" /></span>
        <strong>{template.name}<small>{template.items.length} {template.items.length === 1 ? "task" : "tasks"}</small></strong></div>
    </li>)}</ul> : <div className="wk-quick-template-empty"><p>No templates yet. Use Create template above to get started.</p></div>)}
  </aside>;
}

export function TasksPage({ notify, openTaskId = null, onCloseLinked, initialView = "week", navigate }: {
  notify: (message: string) => void; openTaskId?: string | null; onCloseLinked?: () => void;
  initialView?: TaskView; navigate: (path: string) => void;
}) {
  const [view, setView] = useState<TaskView>(initialView);
  const [panelOpen, setPanelOpen] = useState(false);
  const planningButtonRef = useRef<HTMLButtonElement>(null);
  const planningWasOpen = useRef(false);
  const [zone, setZone] = useState(deviceZone);
  const [now, setNow] = useState(() => new Date());
  const today = dateInZone(now, zone);
  const [visibleWeek, setVisibleWeek] = useState(() => weekStart(dateInZone(new Date(), deviceZone)));
  const currentWeekRef = useRef(weekStart(today));
  const [lists, setLists] = useState<TaskListDto[]>([]);
  const [activeListId, setActiveListId] = useState<string | null>(null);
  const [planningReady, setPlanningReady] = useState(false);
  const [planningError, setPlanningError] = useState("");
  const [tasks, setTasks] = useState<PersonalTaskDto[]>([]);
  const [hasMore, setHasMore] = useState(false);
  const [quickTasks, setQuickTasks] = useState<PersonalTaskDto[]>([]);
  const [quickHasMore, setQuickHasMore] = useState(false);
  const [quickLoading, setQuickLoading] = useState(false);
  const [quickError, setQuickError] = useState("");
  const [templateAction, setTemplateAction] = useState<TemplateAction | null>(null);
  const [selected, setSelected] = useState<PersonalTaskDto | null>(null);
  const [actionTask, setActionTask] = useState<PersonalTaskDto | null>(null);
  const [actionConfirmDelete, setActionConfirmDelete] = useState(false);
  const [actionBusy, setActionBusy] = useState(false);
  const [actionError, setActionError] = useState("");
  const [clearDayDate, setClearDayDate] = useState<string | null>(null);
  const [clearDayBusy, setClearDayBusy] = useState(false);
  const [clearDayError, setClearDayError] = useState("");
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const requestId = useRef(0);
  const quickRequestId = useRef(0);
  useEffect(() => {
    if (panelOpen) {
      planningWasOpen.current = true;
      const onKeyDown = (event: KeyboardEvent) => {
        if (event.key === "Escape") { event.preventDefault(); setPanelOpen(false); }
      };
      window.addEventListener("keydown", onKeyDown);
      return () => window.removeEventListener("keydown", onKeyDown);
    }
    if (planningWasOpen.current) {
      planningButtonRef.current?.focus();
      planningWasOpen.current = false;
    }
  }, [panelOpen]);
  useEffect(() => {
    const timer = window.setInterval(() => setNow(new Date()), 60_000);
    return () => window.clearInterval(timer);
  }, []);
  useEffect(() => {
    const currentWeek = weekStart(today);
    if (currentWeekRef.current === currentWeek) return;
    setVisibleWeek((previous) => previous === currentWeekRef.current ? currentWeek : previous);
    currentWeekRef.current = currentWeek;
  }, [today]);
  const loadPlanning = useCallback(async () => {
    setPlanningError("");
    try {
      const [saved, userLists] = await Promise.all([planningSettingsApi.get(), taskListApi.list()]);
      setZone(saved.timeZoneId || deviceZone);
      setVisibleWeek(weekStart(dateInZone(new Date(), saved.timeZoneId || deviceZone)));
      setLists(userLists);
      setPlanningReady(true);
    } catch (cause) { setPlanningError(errorMessage(cause)); }
  }, []);
  useEffect(() => { void loadPlanning(); }, [loadPlanning]);
  useEffect(() => {
    if (!openTaskId) return;
    let active = true;
    void taskApi.get(openTaskId).then((task) => { if (active) setSelected(task); })
      .catch((cause) => { if (active) setError(errorMessage(cause)); });
    return () => { active = false; };
  }, [openTaskId]);
  const load = useCallback(async (offset = 0) => {
    if (!planningReady) return;
    const currentRequest = ++requestId.current;
    if (!offset) setLoading(true);
    setError("");
    try {
      const page = await taskApi.list(view, view === "week" ? visibleWeek : today, offset, activeListId);
      if (currentRequest !== requestId.current) return;
      if (view === "week") {
        const items = [...page.items];
        let more = page.hasMore;
        while (more) {
          const next = await taskApi.list("week", visibleWeek, items.length, activeListId);
          if (currentRequest !== requestId.current) return;
          items.push(...next.items);
          more = next.hasMore;
        }
        setTasks(items); setHasMore(false);
      } else {
        setTasks((current) => offset ? [...current, ...page.items] : page.items);
        setHasMore(page.hasMore);
      }
    } catch (cause) { if (currentRequest === requestId.current) setError(errorMessage(cause)); }
    finally { if (currentRequest === requestId.current) setLoading(false); }
  }, [view, today, visibleWeek, activeListId, planningReady]);
  useEffect(() => { void load(); return () => { requestId.current++; }; }, [load]);
  const loadQuick = useCallback(async (offset = 0) => {
    if (!planningReady || view !== "week") return;
    const currentRequest = ++quickRequestId.current;
    if (!offset) setQuickLoading(true);
    setQuickError("");
    try {
      const page = await taskApi.list("inbox", today, offset);
      if (currentRequest !== quickRequestId.current) return;
      setQuickTasks((current) => offset ? [...current, ...page.items] : page.items);
      setQuickHasMore(page.hasMore);
    } catch (cause) { if (currentRequest === quickRequestId.current) setQuickError(errorMessage(cause)); }
    finally { if (currentRequest === quickRequestId.current) setQuickLoading(false); }
  }, [planningReady, view, today]);
  useEffect(() => { void loadQuick(); return () => { quickRequestId.current++; }; }, [loadQuick]);
  const refresh = () => { void load(); if (view === "week") void loadQuick(); };
  function closeTaskActions() { setActionTask(null); setActionConfirmDelete(false); setActionError(""); }
  async function deleteFromActions() {
    if (!actionTask) return;
    setActionBusy(true); setActionError("");
    try {
      await taskApi.remove(actionTask.id);
      if (selected?.id === actionTask.id) setSelected(null);
      closeTaskActions(); notify("Task deleted"); refresh();
    } catch (cause) { setActionError(errorMessage(cause)); }
    finally { setActionBusy(false); }
  }
  async function clearDay() {
    if (!clearDayDate) return;
    setClearDayBusy(true); setClearDayError("");
    try {
      const { deleted } = await taskApi.clearDay(clearDayDate);
      if (selected?.plannedDate === clearDayDate) setSelected(null);
      setClearDayDate(null); notify(`${deleted} ${deleted === 1 ? "task" : "tasks"} cleared`); refresh();
    } catch (cause) { setClearDayError(errorMessage(cause)); }
    finally { setClearDayBusy(false); }
  }
  async function completion(task: PersonalTaskDto) {
    try {
      const updated = task.completedAt ? await taskApi.reopen(task.id) : await taskApi.complete(task.id);
      setSelected((current) => current?.id === task.id ? updated : current);
      refresh();
    } catch (cause) { setError(errorMessage(cause)); }
  }
  const groups = view === "today" ? todayGroups(tasks, today) : [{ label: "", items: tasks }];
  const closeDetails = () => { setSelected(null); if (openTaskId) onCloseLinked?.(); };
  const activeList = lists.find((list) => list.id === activeListId);
  return <div className={`wk-tasks-page${panelOpen ? " wk-tasks-page--panel-open" : ""}${view === "inbox" ? " wk-tasks-page--inbox" : ""}`}>
    {panelOpen && <div className="wk-task-planning-drawer"><div className="wk-task-planning-drawer-head">
      <strong>Task views & lists</strong><IconButton label="Close task views" onClick={() => setPanelOpen(false)}><X size={16} /></IconButton></div>
    <TaskPlanningPanel view={view} selectedListId={activeListId} lists={lists} zone={zone}
      onView={(next) => { setView(next); setActiveListId(null); setSelected(null); setPanelOpen(false); if (next === "week") setVisibleWeek(weekStart(today)); }}
      onList={(id) => { setView("all"); setActiveListId(id); setSelected(null); setPanelOpen(false); }}
      onCreated={(list) => { setLists((current) => [...current, list].sort((a, b) => a.name.localeCompare(b.name))); setView("all"); setActiveListId(list.id); notify("List created"); }}
      onRenamed={(list) => { setLists((current) => current.map((item) => item.id === list.id ? list : item).sort((a, b) => a.name.localeCompare(b.name))); notify("List renamed"); }}
      onDeleted={(id) => { setLists((current) => current.filter((item) => item.id !== id)); setActiveListId(null); setView("all"); setSelected(null); notify("List and its tasks deleted"); }}
      onZoneChanged={(savedZone) => { setZone(savedZone); setNow(new Date()); setVisibleWeek(weekStart(dateInZone(new Date(), savedZone))); notify("Planning time zone saved"); }} /></div>}
    <section className={`wk-task-content${view === "week" ? " wk-task-content--week" : ""}`} aria-labelledby="wk-task-heading">
      <header><div><span className="wk-task-header-eyebrow">{view === "week" ? "My week" : "Your tasks"}</span>
        <h1 id="wk-task-heading">{view === "week" ? `Week of ${new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", timeZone: "UTC" }).format(new Date(`${visibleWeek}T12:00:00Z`))}` : view === "inbox" ? "Quick tasks" : activeList?.name ?? taskViews.find((item) => item.key === view)?.label}</h1>
        {view === "inbox" && <p>Capture now. Decide when it deserves your attention.</p>}
        {view === "week" && <p>{tasks.length}{hasMore ? '+' : ''} tasks · {zone}</p>}
        {view === "today" && !activeList && <p>{new Intl.DateTimeFormat(undefined, { weekday: "long", month: "long", day: "numeric", timeZone: "UTC" }).format(new Date(`${today}T12:00:00Z`))} · {zone}</p>}</div>
        <Button ref={planningButtonRef} variant="secondary" size="compact" aria-expanded={panelOpen} onClick={() => setPanelOpen((open) => !open)}><SlidersHorizontal size={15} aria-hidden="true" /> Task views</Button>
      </header>
      {planningError && <div className="wk-task-load-error" role="alert"><p>{planningError}</p><Button variant="secondary" onClick={() => void loadPlanning()}>Try again</Button></div>}
      {view === "inbox" && <div className="wk-quick-tabbar"><span>Inbox · {tasks.length}{hasMore ? "+" : ""}</span></div>}
      <div className={view === "inbox" ? "wk-quick-layout" : `wk-task-body${view === "week" ? " wk-task-body--week" : ""}`}><div className={view === "inbox" ? "wk-quick-list-panel" : undefined}>
      {planningReady && view !== "completed" && view !== "week" && <TaskQuickAdd view={view} today={today} zone={zone} listId={activeListId} onCreated={(task) => { notify("Task added"); setSelected(task); void load(); }} />}
      {error && <div className="wk-task-load-error" role="alert"><p>{error}</p><Button variant="secondary" onClick={() => void load()}>Try again</Button></div>}
      {(loading || !planningReady && !planningError) && <LoadingSkeleton layout={view === "week" ? "week" : "list"} label="Loading tasks…" />}
      {!loading && !error && view === "week" && <WeeklyPlanner start={visibleWeek} today={today} tasks={tasks}
        quickTasks={quickTasks} quickLoading={quickLoading} quickError={quickError} quickHasMore={quickHasMore}
        selectedId={selected?.id ?? null} zone={zone} onWeekChange={setVisibleWeek}
        onCreated={() => { notify("Task added"); refresh(); }} onComplete={(task) => void completion(task)}
        onOpen={setSelected} onActions={(task) => { setActionTask(task); setActionError(""); }}
        onClearDay={(date) => { setClearDayDate(date); setClearDayError(""); }}
        onTemplate={setTemplateAction} onQuickLoadMore={() => void loadQuick(quickTasks.length)}
        onScheduled={(updated) => { setSelected((current) => current?.id === updated.id ? updated : current); notify("Task rescheduled"); refresh(); }} />}
      {!loading && !error && view !== "week" && tasks.length === 0 && <p className="wk-task-status">{view === "today" ? "Nothing planned for today." : view === "inbox" ? "Your inbox is clear." : view === "completed" ? "No completed tasks yet." : "No tasks here yet."}</p>}
      {!loading && view !== "week" && groups.map((group) => group.items.length > 0 && <section className="wk-task-group" key={group.label || view}>
        {group.label && <h2>{group.label}</h2>}
        <ul>{group.items.map((task) => <li key={task.id} className={selected?.id === task.id ? "wk-task-row--selected" : ""}
          onContextMenu={(event) => { event.preventDefault(); setActionTask(task); setActionError(""); }}>
          <button className={`wk-task-complete${task.completedAt ? " wk-task-complete--done" : ""}`} aria-label={task.completedAt ? `Reopen ${task.title}` : `Complete ${task.title}`}
            onClick={() => void completion(task)}>{task.completedAt && <Check size={15} aria-hidden="true" />}</button>
          <button className="wk-task-open" onClick={() => setSelected(task)} aria-label={`Open ${task.title}`}
            aria-keyshortcuts="Shift+F10" onKeyDown={(event) => {
              if (event.key === "ContextMenu" || event.key === "F10" && event.shiftKey) {
                event.preventDefault(); setActionTask(task); setActionError("");
              }
            }}>
            <span>{task.title}</span><small>{taskSchedule(task, today)}</small></button>
          {task.plannedTime && <Clock3 size={16} aria-hidden="true" />}
        </li>)}</ul>
      </section>)}
      {!loading && view !== "week" && hasMore && <Button variant="quiet" onClick={() => void load(tasks.length)}>Load more</Button>}
      </div>{view === "inbox" && <QuickTemplates navigate={navigate} />}</div>
    </section>
    {templateAction && <TemplatesDialog key={`${templateAction.mode}:${templateAction.date ?? ""}`} action={templateAction}
      dayTasks={tasks.filter((task) => task.plannedDate === templateAction.date)} zone={zone}
      onClose={() => setTemplateAction(null)} onSaved={notify} onApplied={() => { notify("Template tasks added"); refresh(); }} />}
    {actionTask && <Dialog busy={actionBusy} title={actionConfirmDelete ? "Delete task?" : actionTask.title}
      onClose={() => { if (!actionBusy) closeTaskActions(); }} urgent={actionConfirmDelete}>
      <p>{actionConfirmDelete ? `“${actionTask.title}” will be removed.` : "Choose an action for this task."}</p>
      {actionError && <p className="wk-task-error" role="alert">{actionError}</p>}
      <div className="wk-dialog-actions">{actionConfirmDelete ? <>
        <Button variant="danger" disabled={actionBusy} onClick={() => void deleteFromActions()}>Delete task</Button>
        <Button variant="secondary" disabled={actionBusy} onClick={() => setActionConfirmDelete(false)}>Back</Button>
      </> : <>
        <Button disabled={actionBusy} onClick={() => { setSelected(actionTask); closeTaskActions(); }}>Edit task</Button>
        <Button variant="danger" disabled={actionBusy} onClick={() => setActionConfirmDelete(true)}>Delete</Button>
        <Button variant="secondary" disabled={actionBusy} onClick={closeTaskActions}>Cancel</Button>
      </>}</div>
    </Dialog>}
    {clearDayDate && <Dialog busy={clearDayBusy} title="Clear day tasks?" urgent onClose={() => { if (!clearDayBusy) setClearDayDate(null); }}>
      <p>Delete all {tasks.filter((task) => task.plannedDate === clearDayDate).length} tasks on {dateLabel(clearDayDate)}, including completed tasks?</p>
      {clearDayError && <p className="wk-task-error" role="alert">{clearDayError}</p>}
      <div className="wk-dialog-actions">
        <Button variant="danger" disabled={clearDayBusy} onClick={() => void clearDay()}>Clear day tasks</Button>
        <Button variant="secondary" disabled={clearDayBusy} onClick={() => setClearDayDate(null)}>Cancel</Button>
      </div>
    </Dialog>}
    {selected && <TaskDetails key={`${selected.id}:${selected.updatedAt}`} task={selected} today={today} defaultZone={zone} lists={lists} onClose={closeDetails}
      onChanged={(updated) => { setSelected(updated); notify("Task saved"); refresh(); }}
      onDeleted={() => { closeDetails(); notify("Task deleted"); refresh(); }} />}
  </div>;
}
