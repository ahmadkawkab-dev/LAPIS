import { useEffect, useRef, useState, type CSSProperties, type DragEvent, type FormEvent } from "react";
import { Check, ChevronLeft, ChevronRight, MoreHorizontal, Plus } from "lucide-react";
import { errorMessage, taskApi, type PersonalTaskDto } from "../../api";
import { Button } from "../../components/ui/Button";
import { addDays } from "../../date";
import { taskWritePayload, weekDates, weekStart } from "./taskView";

export type TemplateAction = { mode: "manage" | "save" | "apply"; date?: string };

function label(date: string, options: Intl.DateTimeFormatOptions) {
  return new Intl.DateTimeFormat(undefined, { ...options, timeZone: "UTC" }).format(new Date(`${date}T12:00:00Z`));
}

export function TaskRow({ task, selected, onComplete, onOpen, onActions, onDragStart, onDragEnd, moving = false }: {
  task: PersonalTaskDto; selected: boolean; onComplete: (task: PersonalTaskDto) => void;
  onOpen: (task: PersonalTaskDto) => void; onActions: (task: PersonalTaskDto) => void;
  onDragStart?: (event: DragEvent<HTMLButtonElement>, task: PersonalTaskDto) => void;
  onDragEnd?: () => void; moving?: boolean;
}) {
  return <li className={`wk-board-task${selected ? " wk-board-task--selected" : ""}${task.completedAt ? " wk-board-task--done" : ""}`}
    onContextMenu={(event) => { event.preventDefault(); onActions(task); }}>
    <button className={`wk-task-complete${task.completedAt ? " wk-task-complete--done" : ""}`}
      aria-label={task.completedAt ? `Reopen ${task.title}` : `Complete ${task.title}`}
      onClick={() => onComplete(task)}>{task.completedAt && <Check size={14} aria-hidden="true" />}</button>
    <button className="wk-board-task-open" onClick={() => onOpen(task)} aria-label={`Open ${task.title}`}
      aria-keyshortcuts="Shift+F10" onKeyDown={(event) => {
        if (event.key === "ContextMenu" || event.key === "F10" && event.shiftKey) { event.preventDefault(); onActions(task); }
      }}
      draggable={!!onDragStart && !moving} onDragStart={onDragStart ? (event) => onDragStart(event, task) : undefined}
      onDragEnd={onDragEnd} disabled={moving}>
      <span>{moving ? "Moving…" : task.title}</span>{task.plannedTime && <time>{task.plannedTime.slice(0, 5)}</time>}
    </button>
  </li>;
}

function InlineTaskAdd({ date, zone, onCreated }: {
  date: string | null; zone: string; onCreated: (task: PersonalTaskDto) => void;
}) {
  const [editing, setEditing] = useState(false);
  const [title, setTitle] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const trigger = useRef<HTMLButtonElement>(null);
  const wasEditing = useRef(false);
  useEffect(() => {
    if (wasEditing.current && !editing) trigger.current?.focus();
    wasEditing.current = editing;
  }, [editing]);
  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!title.trim()) return;
    setBusy(true); setError("");
    try {
      const created = await taskApi.create(taskWritePayload(title, "", date ?? "", "", zone));
      setTitle(""); onCreated(created);
    } catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  }
  return <div className="wk-board-add">
    {editing ? <form onSubmit={(event) => void submit(event)}>
      <label className="wk-sr-only" htmlFor={`wk-add-${date ?? "quick"}`}>New {date ? label(date, { weekday: "long" }) : "quick"} task</label>
      <input id={`wk-add-${date ?? "quick"}`} autoFocus maxLength={200} value={title} placeholder="Task name"
        onChange={(event) => setTitle(event.target.value)} onKeyDown={(event) => { if (event.key === "Escape") { event.preventDefault(); setEditing(false); setTitle(""); setError(""); } }} />
      <div className="wk-board-add-actions"><Button size="compact" type="submit" disabled={busy || !title.trim()}>Add</Button>
        <button type="button" className="wk-board-add-cancel" onClick={() => { setEditing(false); setTitle(""); setError(""); }}>Cancel</button></div>
    </form> : <button ref={trigger} className="wk-board-add-trigger" onClick={() => setEditing(true)}><Plus size={16} aria-hidden="true" /> Add task</button>}
    {error && <p className="wk-task-error" role="alert">{error}</p>}
  </div>;
}

function DayTaskBoard({ date, today, tasks, selectedId, zone, mobileOrder, mobileSelected, dropDate, movingId,
  onCreated, onComplete, onOpen, onActions, onClearDay, onTemplate, onDragStart, onDragEnd, onDragOver, onDragLeave, onDrop }: {
  date: string; today: string; tasks: PersonalTaskDto[]; selectedId: string | null; zone: string; mobileOrder: number; mobileSelected: boolean;
  dropDate: string | null; movingId: string | null;
  onCreated: (task: PersonalTaskDto) => void; onComplete: (task: PersonalTaskDto) => void;
  onOpen: (task: PersonalTaskDto) => void; onActions: (task: PersonalTaskDto) => void;
  onClearDay: (date: string) => void;
  onTemplate: (action: TemplateAction) => void;
  onDragStart: (event: DragEvent<HTMLButtonElement>, task: PersonalTaskDto) => void; onDragEnd: () => void;
  onDragOver: (event: DragEvent<HTMLElement>, date: string) => void;
  onDragLeave: (event: DragEvent<HTMLElement>, date: string) => void;
  onDrop: (event: DragEvent<HTMLElement>, date: string) => void;
}) {
  const [menuOpen, setMenuOpen] = useState(false);
  const menuTrigger = useRef<HTMLButtonElement>(null);
  const [showCompleted, setShowCompleted] = useState(false);
  const active = tasks.filter((task) => !task.completedAt);
  const completed = tasks.filter((task) => task.completedAt);
  const shownCompleted = showCompleted ? completed : completed.slice(0, 2);
  const dayName = label(date, { weekday: "long" });
  function chooseTemplate(mode: "save" | "apply") {
    menuTrigger.current?.focus();
    setMenuOpen(false);
    onTemplate({ mode, date });
  }
  return <section className={`wk-day-board${date === today ? " wk-day-board--today" : ""}${mobileSelected ? " wk-day-board--mobile-selected" : ""}${dropDate === date ? " wk-day-board--drop" : ""}`}
    style={{ "--mobile-order": mobileOrder } as CSSProperties} aria-label={`${dayName}, ${label(date, { month: "long", day: "numeric" })}`}
    onDragOver={(event) => onDragOver(event, date)} onDragLeave={(event) => onDragLeave(event, date)} onDrop={(event) => onDrop(event, date)}>
    <div className="wk-day-board-head">
      <div><div className="wk-day-board-title"><h3>{dayName}</h3>{date === today && <span>Today</span>}</div>
        <p>{label(date, { month: "short", day: "numeric" })}</p></div>
      <div className="wk-day-board-menu-wrap" onBlur={(event) => { if (!event.currentTarget.contains(event.relatedTarget)) setMenuOpen(false); }}
        onKeyDown={(event) => { if (event.key === "Escape") { event.preventDefault(); setMenuOpen(false); menuTrigger.current?.focus(); } }}>
        <button ref={menuTrigger} className="wk-day-board-menu-button" aria-label={`${dayName} options`} aria-expanded={menuOpen}
          onClick={() => setMenuOpen(!menuOpen)}><MoreHorizontal size={18} aria-hidden="true" /></button>
        {menuOpen && <div className="wk-day-board-menu">
          <button onClick={() => chooseTemplate("save")} disabled={tasks.length === 0}>Save as template</button>
          <button onClick={() => chooseTemplate("apply")}>Apply template</button>
          <button className="wk-day-board-menu-danger" disabled={tasks.length === 0}
            onClick={() => { menuTrigger.current?.focus(); setMenuOpen(false); onClearDay(date); }}>Clear day tasks</button>
        </div>}
      </div>
    </div>
    <div className="wk-day-board-body">
      {tasks.length === 0 && <p className="wk-day-empty">Nothing planned</p>}
      <ul className="wk-board-task-list">{[...active, ...shownCompleted].map((task) =>
        <TaskRow key={task.id} task={task} selected={selectedId === task.id} onComplete={onComplete} onOpen={onOpen} onActions={onActions}
          onDragStart={onDragStart} onDragEnd={onDragEnd} moving={movingId === task.id} />)}</ul>
      {completed.length > 2 && <button className="wk-board-completed-toggle" onClick={() => setShowCompleted(!showCompleted)}>
        {showCompleted ? "Show fewer" : `Show ${completed.length - 2} more completed`}</button>}
    </div>
    <InlineTaskAdd key={date} date={date} zone={zone} onCreated={onCreated} />
  </section>;
}

export function WeeklyPlanner({ start, today, tasks, quickTasks, quickLoading, quickError, quickHasMore,
  selectedId, zone, onWeekChange, onCreated, onComplete, onOpen, onActions, onClearDay, onTemplate, onQuickLoadMore, onScheduled }: {
  start: string; today: string; tasks: PersonalTaskDto[]; quickTasks: PersonalTaskDto[];
  quickLoading: boolean; quickError: string; quickHasMore: boolean; selectedId: string | null; zone: string;
  onWeekChange: (start: string) => void; onCreated: (task: PersonalTaskDto) => void;
  onComplete: (task: PersonalTaskDto) => void; onOpen: (task: PersonalTaskDto) => void;
  onActions: (task: PersonalTaskDto) => void;
  onClearDay: (date: string) => void;
  onTemplate: (action: TemplateAction) => void; onQuickLoadMore: () => void;
  onScheduled: (task: PersonalTaskDto) => void;
}) {
  const [dragTaskId, setDragTaskId] = useState<string | null>(null);
  const [dropDate, setDropDate] = useState<string | null>(null);
  const [movingId, setMovingId] = useState<string | null>(null);
  const [mobileDate, setMobileDate] = useState(() => weekDates(start).includes(today) ? today : start);
  const [scheduleError, setScheduleError] = useState("");
  useEffect(() => {
    setMobileDate((current) => weekDates(start).includes(current) ? current : weekDates(start).includes(today) ? today : start);
  }, [start, today]);
  function startDrag(event: DragEvent<HTMLButtonElement>, task: PersonalTaskDto) {
    event.dataTransfer.effectAllowed = "move";
    event.dataTransfer.setData("application/x-wukna-task", task.id);
    setDragTaskId(task.id);
  }
  function endDrag() { setDragTaskId(null); setDropDate(null); }
  function overDay(event: DragEvent<HTMLElement>, date: string) {
    if (!dragTaskId) return;
    event.preventDefault(); event.dataTransfer.dropEffect = "move";
    if (dropDate !== date) setDropDate(date);
  }
  function leaveDay(event: DragEvent<HTMLElement>, date: string) {
    if (!event.currentTarget.contains(event.relatedTarget as Node) && dropDate === date) setDropDate(null);
  }
  function dropOnDay(event: DragEvent<HTMLElement>, date: string) {
    const id = event.dataTransfer.getData("application/x-wukna-task");
    if (!id || id !== dragTaskId) return;
    event.preventDefault(); endDrag();
    const source = [...tasks, ...quickTasks].find((task) => task.id === id);
    if (!source || source.plannedDate === date || movingId) return;
    setMovingId(id); setScheduleError("");
    void taskApi.schedule(id, date).then(onScheduled)
      .catch((cause) => setScheduleError(errorMessage(cause)))
      .finally(() => setMovingId(null));
  }
  const dates = weekDates(start);
  const currentStart = weekStart(today);
  const currentIndex = dates.indexOf(today);
  const range = `${label(start, { month: "short", day: "numeric" })} – ${label(dates[6], { month: "short", day: "numeric", year: start.slice(0, 4) !== dates[6].slice(0, 4) ? "numeric" : undefined })}`;
  return <>
    <div className="wk-week-nav" aria-label="Week navigation">
      <button aria-label="Previous week" onClick={() => onWeekChange(addDays(start, -7))}><ChevronLeft size={17} /> Previous</button>
      <span>{range}</span>
      <div><Button variant="secondary" size="compact" onClick={() => onTemplate({ mode: "manage" })}>Templates</Button>
        {start !== currentStart && <button onClick={() => onWeekChange(currentStart)}>Today</button>}
        <button aria-label="Next week" onClick={() => onWeekChange(addDays(start, 7))}>Next <ChevronRight size={17} /></button></div>
    </div>
    <div className="wk-week-mobile-days" aria-label="Choose a day">
      {dates.map((date) => <button key={date} aria-current={mobileDate === date ? "date" : undefined}
        className={mobileDate === date ? "wk-week-mobile-day--active" : ""}
        onClick={() => setMobileDate(date)}><span>{label(date, { weekday: "short" })}</span><strong>{date.slice(-2)}</strong>
        <small>{tasks.filter((task) => task.plannedDate === date && !task.completedAt).length}</small></button>)}
    </div>
    {scheduleError && <div className="wk-task-load-error" role="alert"><p>{scheduleError}</p>
      <Button variant="quiet" size="compact" onClick={() => setScheduleError("")}>Dismiss</Button></div>}
    <div className="wk-week-grid">{dates.map((date, index) => <DayTaskBoard key={date} date={date} today={today} mobileSelected={mobileDate === date}
      tasks={tasks.filter((task) => task.plannedDate === date)} selectedId={selectedId} zone={zone}
      mobileOrder={currentIndex < 0 ? index : (index - currentIndex + 7) % 7} dropDate={dropDate} movingId={movingId}
      onCreated={onCreated} onComplete={onComplete} onOpen={onOpen} onActions={onActions} onClearDay={onClearDay} onTemplate={onTemplate}
      onDragStart={startDrag} onDragEnd={endDrag} onDragOver={overDay} onDragLeave={leaveDay} onDrop={dropOnDay} />)}</div>
    <section className="wk-quick-tasks" aria-labelledby="wk-quick-tasks-heading">
      <div><h2 id="wk-quick-tasks-heading">Quick Tasks</h2></div>
      {quickError && <p className="wk-task-error" role="alert">{quickError}</p>}
      {quickLoading && <p className="wk-quick-status" role="status">Loading quick tasks…</p>}
      {!quickLoading && quickTasks.length === 0 && !quickError && <p className="wk-quick-status">Nothing to remember right now.</p>}
      <ul className="wk-board-task-list">{quickTasks.map((task) => <TaskRow key={task.id} task={task}
        selected={selectedId === task.id} onComplete={onComplete} onOpen={onOpen} onActions={onActions}
        onDragStart={startDrag} onDragEnd={endDrag} moving={movingId === task.id} />)}</ul>
      {quickHasMore && <Button variant="quiet" size="compact" onClick={onQuickLoadMore}>Load more</Button>}
      <InlineTaskAdd date={null} zone={zone} onCreated={onCreated} />
    </section>
  </>;
}
