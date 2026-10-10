import { LoadingSkeleton } from "../../components/ui/LoadingSkeleton";
import { useCallback, useEffect, useMemo, useRef, useState, type DragEvent, type KeyboardEvent, type MouseEvent } from "react";
import { ChevronLeft, ChevronRight, Plus } from "lucide-react";
import { calendarApi, errorMessage, planningSettingsApi, taskApi, type CalendarEventDto, type CalendarItemDto, type PersonalTaskDto } from "../../api";
import { dateInZone } from "../../date";
import { Button, IconButton } from "../../components/ui/Button";
import { Dialog } from "../../components/ui/Dialog";
import { CalendarEventEditor } from "./CalendarEventEditor";
import { CalendarTaskTray } from "./CalendarTaskTray";
import { TaskScheduleDialog } from "./TaskScheduleDialog";
import { itemsByDay, itemTime, monthRange, shiftMonth, taskCalendarItem, taskDateForCalendarDay } from "./calendarView";
import "./calendar.css";

const deviceZone = Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
type CalendarView = "month" | "agenda";

function dayLabel(date: string, options: Intl.DateTimeFormatOptions) {
  return new Intl.DateTimeFormat(undefined, { ...options, timeZone: "UTC" }).format(new Date(`${date}T12:00:00Z`));
}

export function CalendarPage({ notify, navigate }: {
  notify: (message: string) => void; navigate: (path: string) => void;
}) {
  const [zone, setZone] = useState(deviceZone);
  const [ready, setReady] = useState(false);
  const [month, setMonth] = useState(() => `${dateInZone(new Date(), deviceZone).slice(0, 7)}-01`);
  const [selectedDate, setSelectedDate] = useState(() => dateInZone(new Date(), deviceZone));
  const [view, setView] = useState<CalendarView>("month");
  const [items, setItems] = useState<CalendarItemDto[]>([]);
  const [hasMore, setHasMore] = useState(false);
  const [nextOffset, setNextOffset] = useState(500);
  const [loadingMore, setLoadingMore] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [editorOpen, setEditorOpen] = useState(false);
  const [activeEvent, setActiveEvent] = useState<CalendarEventDto | null>(null);
  const [actionItem, setActionItem] = useState<CalendarItemDto | null>(null);
  const [actionConfirmDelete, setActionConfirmDelete] = useState(false);
  const [actionBusy, setActionBusy] = useState(false);
  const [actionError, setActionError] = useState("");
  const [inboxTasks, setInboxTasks] = useState<PersonalTaskDto[]>([]);
  const [inboxHasMore, setInboxHasMore] = useState(false);
  const [inboxLoading, setInboxLoading] = useState(true);
  const [inboxError, setInboxError] = useState("");
  const [trayOpen, setTrayOpen] = useState(() => !window.matchMedia("(max-width: 700px)").matches);
  const [scheduleDialog, setScheduleDialog] = useState<{ id: string; title: string; isTimed: boolean; initialDate: string } | null>(null);
  const [scheduleError, setScheduleError] = useState("");
  const [dragTaskId, setDragTaskId] = useState<string | null>(null);
  const [dropDate, setDropDate] = useState<string | null>(null);
  const [pendingMoves, setPendingMoves] = useState<Record<string, { date: string; title: string }>>({});
  const requestId = useRef(0);
  const inboxRequestId = useRef(0);
  const movingIds = useRef(new Set<string>());
  const { from, to, days } = useMemo(() => monthRange(month), [month]);
  const displayItems = useMemo(() => {
    const moves = Object.entries(pendingMoves);
    if (!moves.length) return items;
    const moved = new Set(moves.map(([id]) => id));
    const placeholders: CalendarItemDto[] = moves.map(([id, move]) => ({
      source: "task", id, title: move.title, scheduleKind: "dateOnlyTask",
      startDate: move.date, endDateExclusive: null, startAtUtc: null, endAtUtc: null,
      timeZoneId: null, isCompleted: false,
    }));
    return [...items.filter((item) => item.source !== "task" || !moved.has(item.id)), ...placeholders];
  }, [items, pendingMoves]);
  const dayItems = useMemo(() => itemsByDay(displayItems, days, zone), [displayItems, days, zone]);
  const loadPlanning = useCallback(async () => {
    setError("");
    try {
      const settings = await planningSettingsApi.get();
      const value = settings.timeZoneId || deviceZone;
      setZone(value);
      const today = dateInZone(new Date(), value);
      setSelectedDate(today);
      setMonth(`${today.slice(0, 7)}-01`);
      setReady(true);
    } catch (cause) { setError(errorMessage(cause)); }
  }, []);
  useEffect(() => { void loadPlanning(); }, [loadPlanning]);
  const load = useCallback(async (showLoading = true) => {
    if (!ready) return;
    const id = ++requestId.current;
    if (showLoading) setLoading(true);
    setLoadingMore(false);
    setError("");
    try {
      const page = await calendarApi.range(from, to, zone);
      if (id !== requestId.current) return;
      setItems(page.items); setHasMore(page.hasMore); setNextOffset(500);
    } catch (cause) { if (id === requestId.current) setError(errorMessage(cause)); }
    finally { if (id === requestId.current) setLoading(false); }
  }, [ready, from, to, zone]);
  useEffect(() => {
    if (!ready) return;
    let active = true;
    const openLinked = () => {
      const id = new URLSearchParams(window.location.search).get("event");
      if (!id || !/^[0-9a-f-]{36}$/i.test(id)) return;
      void calendarApi.getEvent(id).then(event => {
        if (!active) return;
        const date = event.localStart?.slice(0, 10) ?? event.allDayStartDate;
        if (date) { setSelectedDate(date); setMonth(`${date.slice(0, 7)}-01`); }
        setActiveEvent(event); setEditorOpen(true);
      }).catch(cause => { if (active) setError(errorMessage(cause)); });
    };
    openLinked(); window.addEventListener("wukna:navigation", openLinked); window.addEventListener("popstate", openLinked);
    return () => { active = false; window.removeEventListener("wukna:navigation", openLinked); window.removeEventListener("popstate", openLinked); };
  }, [ready]);
  async function loadMoreCalendar() {
    if (!hasMore || loadingMore) return;
    const id = requestId.current;
    setLoadingMore(true); setError("");
    try {
      const page = await calendarApi.range(from, to, zone, nextOffset);
      if (id !== requestId.current) return;
      setItems((current) => [...current, ...page.items]);
      setHasMore(page.hasMore); setNextOffset(nextOffset + 500);
    } catch (cause) { if (id === requestId.current) setError(errorMessage(cause)); }
    finally { if (id === requestId.current) setLoadingMore(false); }
  }
  useEffect(() => { void load(); return () => { requestId.current++; }; }, [load]);
  const loadInbox = useCallback(async (offset = 0, showLoading = true) => {
    if (!ready) return;
    const id = ++inboxRequestId.current;
    if (showLoading) setInboxLoading(true);
    setInboxError("");
    try {
      const page = await taskApi.list("inbox", "", offset);
      if (id !== inboxRequestId.current) return;
      setInboxTasks((current) => offset ? [...current, ...page.items] : page.items);
      setInboxHasMore(page.hasMore);
    } catch (cause) { if (id === inboxRequestId.current) setInboxError(errorMessage(cause)); }
    finally { if (id === inboxRequestId.current) setInboxLoading(false); }
  }, [ready]);
  useEffect(() => { void loadInbox(); return () => { inboxRequestId.current++; }; }, [loadInbox]);

  async function scheduleTask(id: string, date: string) {
    if (movingIds.current.has(id)) return;
    const calendarTask = items.find((item) => item.source === "task" && item.id === id);
    const source = calendarTask ?? inboxTasks.find((task) => task.id === id);
    const authoredDate = calendarTask ? taskDateForCalendarDay(calendarTask, date, zone) : date;
    movingIds.current.add(id);
    setScheduleError("");
    setPendingMoves((current) => ({ ...current, [id]: { date, title: source?.title ?? "Task" } }));
    try {
      const updated = await taskApi.schedule(id, authoredDate);
      const projected = taskCalendarItem(updated);
      setItems((current) => [...current.filter((item) => item.source !== "task" || item.id !== id),
        ...(projected ? [projected] : [])]);
      setInboxTasks((current) => current.filter((task) => task.id !== id));
      setSelectedDate(date);
      if (date < from || date >= to) setMonth(`${date.slice(0, 7)}-01`);
      else void load(false);
      void loadInbox(0, false);
      notify("Task scheduled");
    } catch (cause) {
      void load(false); void loadInbox(0, false);
      throw cause;
    } finally {
      movingIds.current.delete(id);
      setPendingMoves((current) => {
        const next = { ...current }; delete next[id]; return next;
      });
    }
  }
  function startTaskDrag(event: DragEvent<HTMLButtonElement>, id: string) {
    event.dataTransfer.effectAllowed = "move";
    event.dataTransfer.setData("application/x-wukna-task", id);
    event.dataTransfer.setData("text/plain", id);
    setDragTaskId(id);
  }
  function endTaskDrag() { setDragTaskId(null); setDropDate(null); }
  function overDay(event: DragEvent<HTMLElement>, day: string) {
    if (!dragTaskId) return;
    event.preventDefault(); event.dataTransfer.dropEffect = "move";
    if (dropDate !== day) setDropDate(day);
  }
  function dropOnDay(event: DragEvent<HTMLElement>, day: string) {
    const id = event.dataTransfer.getData("application/x-wukna-task");
    if (!id || id !== dragTaskId) return;
    event.preventDefault(); event.stopPropagation(); endTaskDrag();
    void scheduleTask(id, day).catch((cause) => setScheduleError(errorMessage(cause)));
  }
  function planTask(task: PersonalTaskDto) {
    setScheduleDialog({ id: task.id, title: task.title, isTimed: false, initialDate: selectedDate });
  }
  function moveTask(item: CalendarItemDto, day: string) {
    setScheduleDialog({ id: item.id, title: item.title, isTimed: item.scheduleKind === "timedTask", initialDate: day });
  }
  function today() {
    const date = dateInZone(new Date(), zone);
    setSelectedDate(date); setMonth(`${date.slice(0, 7)}-01`);
  }
  function moveMonth(delta: number) {
    const next = shiftMonth(month, delta);
    setMonth(next); setSelectedDate(next);
  }
  function newEvent(date = selectedDate) {
    setSelectedDate(date); setActiveEvent(null); setEditorOpen(true);
  }
  async function openItem(item: CalendarItemDto) {
    if (item.source === "task") { navigate(`/tasks/${item.id}`); return; }
    try {
      setActiveEvent(await calendarApi.getEvent(item.id));
      setEditorOpen(true);
    } catch (cause) { setError(errorMessage(cause)); }
  }
  function openActions(item: CalendarItemDto) {
    setActionItem(item); setActionConfirmDelete(false); setActionError("");
  }
  function onItemContextMenu(event: MouseEvent<HTMLButtonElement>, item: CalendarItemDto) {
    event.preventDefault(); openActions(item);
  }
  function onItemActionKey(event: KeyboardEvent<HTMLButtonElement>, item: CalendarItemDto) {
    if (event.key === "ContextMenu" || event.key === "F10" && event.shiftKey) {
      event.preventDefault(); openActions(item);
    }
  }
  function closeActions() { setActionItem(null); setActionConfirmDelete(false); setActionError(""); }
  async function deleteFromActions() {
    if (!actionItem) return;
    setActionBusy(true); setActionError("");
    try {
      if (actionItem.source === "task") await taskApi.remove(actionItem.id);
      else await calendarApi.removeEvent(actionItem.id);
      const kind = actionItem.source === "task" ? "Task" : "Event";
      closeActions(); notify(`${kind} deleted`); void load();
    } catch (cause) { setActionError(errorMessage(cause)); }
    finally { setActionBusy(false); }
  }
  const onDay = (day: string) => { setSelectedDate(day); if (!window.matchMedia("(max-width: 700px)").matches) setView("agenda"); };
  const currentDate = dateInZone(new Date(), zone);
  const agendaDays = days.filter((day) => day === selectedDate || (dayItems.get(day)?.length ?? 0) > 0);
  return <div className="wk-calendar-page">
    <header className="wk-calendar-header">
      <div><span className="wk-calendar-eyebrow">Calendar</span><h1>{dayLabel(month, { month: "long", year: "numeric" })}</h1>
        <p>Tasks and events in one timeline.</p></div>
      <div className="wk-calendar-header-actions"><Button variant="secondary" size="compact" onClick={today}>Today</Button>
        <Button size="compact" onClick={() => newEvent()}><Plus size={15} aria-hidden="true" /> New event</Button></div>
    </header>
    <div className="wk-calendar-toolbar">
      <div className="wk-calendar-paging"><IconButton label="Previous month" onClick={() => moveMonth(-1)}><ChevronLeft size={17} aria-hidden="true" /></IconButton>
        <IconButton label="Next month" onClick={() => moveMonth(1)}><ChevronRight size={17} aria-hidden="true" /></IconButton>
        <strong>{dayLabel(month, { month: "long", year: "numeric" })}</strong></div>
      <div className="wk-calendar-view-switch" aria-label="Calendar view">
        <button className={view === "month" ? "wk-calendar-view--active" : ""} aria-pressed={view === "month"} onClick={() => setView("month")}>Month</button>
        <button className={view === "agenda" ? "wk-calendar-view--active" : ""} aria-pressed={view === "agenda"} onClick={() => setView("agenda")}>Agenda</button>
      </div>
    </div>
    {error && <div className="wk-calendar-load-error" role="alert"><p>{error}</p><Button variant="secondary" onClick={() => ready ? void load() : void loadPlanning()}>Try again</Button></div>}
    {(!error && (loading || !ready)) && <LoadingSkeleton layout={view === "month" ? "calendar" : "list"} label="Loading calendar…" />}
    {scheduleError && <div className="wk-calendar-load-error" role="alert"><p>{scheduleError}</p><Button variant="quiet" onClick={() => setScheduleError("")}>Dismiss</Button></div>}
    {ready && <div className="wk-calendar-body"><div className="wk-calendar-rail">
      <div className="wk-calendar-mini" aria-label="Choose a date">
        <div className="wk-calendar-mini-heading"><strong>{dayLabel(month, { month: "long" })}</strong><small>{month.slice(0, 4)}</small></div>
        <div className="wk-calendar-mini-grid">{["M", "T", "W", "T", "F", "S", "S"].map((day, index) => <span key={index}>{day}</span>)}
          {days.map((day) => <button key={day} className={day === selectedDate ? "wk-calendar-mini-selected" : ""}
            aria-label={dayLabel(day, { weekday: "long", month: "long", day: "numeric" })}
            aria-current={day === currentDate ? "date" : undefined} aria-pressed={day === selectedDate}
            onClick={() => { setSelectedDate(day); if (day.slice(0, 7) !== month.slice(0, 7)) setMonth(`${day.slice(0, 7)}-01`); }}>{Number(day.slice(-2))}</button>)}</div>
      </div>
      <CalendarTaskTray tasks={inboxTasks} hasMore={inboxHasMore} loading={inboxLoading} error={inboxError}
        open={trayOpen} onToggle={() => setTrayOpen((current) => !current)} onLoadMore={() => void loadInbox(inboxTasks.length)}
        onDragStart={startTaskDrag} onDragEnd={endTaskDrag} onPlan={planTask} onOpenTask={(id) => navigate(`/tasks/${id}`)} />
    </div><div className="wk-calendar-main">
    {!loading && !error && <>
      {hasMore && <div className="wk-calendar-limit" role="status">More items are available in this six-week range. <Button variant="secondary" size="compact" disabled={loadingMore} onClick={() => void loadMoreCalendar()}>{loadingMore ? "Loading…" : "Load more"}</Button></div>}
      {view === "month" ? <div className="wk-calendar-month">
        <div className="wk-calendar-weekdays">{["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"].map((day) => <span key={day}><abbr className="wk-calendar-weekday-full" title={day}>{day}</abbr><abbr className="wk-calendar-weekday-short" title={day}>{day.slice(0, 2)}</abbr></span>)}</div>
        <div className="wk-calendar-grid">{days.map((day) => {
          const entries = dayItems.get(day) ?? [];
          return <section key={day} onDragOver={(event) => overDay(event, day)} onDragLeave={() => { if (dropDate === day) setDropDate(null); }} onDrop={(event) => dropOnDay(event, day)}
            className={`wk-calendar-day${day.slice(0, 7) === month.slice(0, 7) ? "" : " wk-calendar-day--outside"}${day === currentDate ? " wk-calendar-day--today" : ""}${day === selectedDate ? " wk-calendar-day--selected" : ""}${dropDate === day ? " wk-calendar-day--drop" : ""}`}>
            <button className="wk-calendar-day-number" aria-current={day === currentDate ? "date" : undefined} aria-pressed={day === selectedDate} aria-label={`${dayLabel(day, { weekday: "long", month: "long", day: "numeric" })}, ${entries.length} items`} onClick={() => onDay(day)}><span className="wk-calendar-date">{Number(day.slice(-2))}</span>{day === currentDate && <span className="wk-sr-only">, Today</span>}</button>
            <div className="wk-calendar-day-items">{entries.slice(0, 3).map((item) => <button key={`${item.source}-${item.id}`} className={`wk-calendar-item wk-calendar-item--${item.source}`}
              onContextMenu={(event) => onItemContextMenu(event, item)} aria-keyshortcuts="Shift+F10"
              onKeyDown={(event) => onItemActionKey(event, item)}
              draggable={item.source === "task" && !pendingMoves[item.id]}
              onDragStart={item.source === "task" ? (event) => startTaskDrag(event, item.id) : undefined} onDragEnd={endTaskDrag}
              onClick={() => void openItem(item)} title={`${item.title}${itemTime(item, zone) ? ` · ${itemTime(item, zone)}` : ""}`}>
              <span>{pendingMoves[item.id] ? "Moving…" : item.scheduleKind === "timedEvent" || item.scheduleKind === "timedTask" ? itemTime(item, zone) : null}</span>{item.title}</button>)}
              {entries.length > 3 && <button className="wk-calendar-more" onClick={() => onDay(day)}>+{entries.length - 3} more</button>}</div>
            {entries.length > 0 && <span className="wk-calendar-mobile-count" aria-hidden="true">{entries.length}</span>}
          </section>;
        })}</div>
        <section className="wk-calendar-mobile-agenda" aria-label="Selected day">
          <div className="wk-calendar-mobile-agenda-heading"><h2>{dayLabel(selectedDate, { weekday: "long", month: "long", day: "numeric" })}</h2></div>
          <p>{(dayItems.get(selectedDate) ?? []).length} items</p>
          {(dayItems.get(selectedDate) ?? []).length ? <ul>{(dayItems.get(selectedDate) ?? []).map((item) =>
            <li key={`${item.source}-${item.id}`}><button onClick={() => void openItem(item)}
              onContextMenu={(event) => onItemContextMenu(event, item)} aria-keyshortcuts="Shift+F10"
              onKeyDown={(event) => onItemActionKey(event, item)}>
              <span className={`wk-calendar-kind wk-calendar-kind--${item.source}`} aria-hidden="true" />
              <span><strong className={item.isCompleted ? "wk-calendar-completed" : ""}>{item.title}</strong>
                <small>{item.source === "task" ? "Task" : "Event"} · {itemTime(item, zone) ?? "All day"}</small></span>
            </button></li>)}</ul> : <p>Nothing scheduled.</p>}
        </section>
      </div> : <div className="wk-calendar-agenda">
        {agendaDays.map((day) => {
          const entries = dayItems.get(day) ?? [];
          return <section key={day} onDragOver={(event) => overDay(event, day)} onDragLeave={() => { if (dropDate === day) setDropDate(null); }} onDrop={(event) => dropOnDay(event, day)}
            className={`wk-calendar-agenda-day${dropDate === day ? " wk-calendar-agenda-day--drop" : ""}`}>
            <h2>{dayLabel(day, { weekday: "long", month: "long", day: "numeric" })}</h2>
            {entries.length ? <ul>{entries.map((item) => <li key={`${item.source}-${item.id}`} className="wk-calendar-agenda-row">
              <button className="wk-calendar-agenda-item" draggable={item.source === "task" && !pendingMoves[item.id]}
                onContextMenu={(event) => onItemContextMenu(event, item)} aria-keyshortcuts="Shift+F10"
                onKeyDown={(event) => onItemActionKey(event, item)}
                onDragStart={item.source === "task" ? (event) => startTaskDrag(event, item.id) : undefined} onDragEnd={endTaskDrag}
                onClick={() => void openItem(item)}>
                <span className={`wk-calendar-kind wk-calendar-kind--${item.source}`} aria-hidden="true" />
                <span><strong className={item.isCompleted ? "wk-calendar-completed" : ""}>{item.title}</strong><small>{pendingMoves[item.id] ? "Moving…" : `${item.source === "task" ? "Task" : "Event"} · ${itemTime(item, zone) ?? (item.scheduleKind === "dateOnlyTask" ? "Anytime" : "All day")}`}</small></span>
                <ChevronRight size={16} aria-hidden="true" />
              </button>{item.source === "task" && !pendingMoves[item.id] && <Button variant="quiet" size="compact" onClick={() => moveTask(item, day)} aria-label={`Move ${item.title}`}>Move</Button>}</li>)}</ul> : <p>Nothing scheduled.</p>}
            <Button variant="quiet" size="compact" onClick={() => newEvent(day)}><Plus size={15} aria-hidden="true" /> Add event</Button>
          </section>;
        })}
      </div>}
    </>}
    </div></div>}
    {scheduleDialog && <TaskScheduleDialog key={scheduleDialog.id} title={scheduleDialog.title} initialDate={scheduleDialog.initialDate}
      isTimed={scheduleDialog.isTimed} onSchedule={(date) => scheduleTask(scheduleDialog.id, date)} onClose={() => setScheduleDialog(null)} />}
    {actionItem && <Dialog busy={actionBusy} title={actionConfirmDelete ? `Delete ${actionItem.source}?` : actionItem.title}
      onClose={() => { if (!actionBusy) closeActions(); }} urgent={actionConfirmDelete}>
      <p>{actionConfirmDelete ? `“${actionItem.title}” will be removed.` : `Choose an action for this ${actionItem.source}.`}</p>
      {actionError && <p className="wk-calendar-load-error" role="alert">{actionError}</p>}
      <div className="wk-dialog-actions">{actionConfirmDelete ? <>
        <Button variant="danger" disabled={actionBusy} onClick={() => void deleteFromActions()}>Delete {actionItem.source}</Button>
        <Button variant="secondary" disabled={actionBusy} onClick={() => setActionConfirmDelete(false)}>Back</Button>
      </> : <>
        <Button disabled={actionBusy} onClick={() => { const item = actionItem; closeActions(); void openItem(item); }}>Edit {actionItem.source}</Button>
        <Button variant="danger" disabled={actionBusy} onClick={() => setActionConfirmDelete(true)}>Delete</Button>
        <Button variant="secondary" disabled={actionBusy} onClick={closeActions}>Cancel</Button>
      </>}</div>
    </Dialog>}
    {editorOpen && <CalendarEventEditor key={activeEvent?.id ?? `new-${selectedDate}`} event={activeEvent} date={selectedDate} zone={zone}
      onClose={() => setEditorOpen(false)}
      onSaved={() => { setEditorOpen(false); notify(activeEvent ? "Event saved" : "Event added"); void load(); }}
      onDeleted={() => { setEditorOpen(false); notify("Event deleted"); void load(); }} />}
  </div>;
}
