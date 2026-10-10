import { LoadingSkeleton } from "../../components/ui/LoadingSkeleton";
import { useCallback, useEffect, useState, type FormEvent } from "react";
import { ArrowRight, CalendarDays, Check, Clock3, LayoutDashboard, Plus } from "lucide-react";
import { calendarApi, errorMessage, planningSettingsApi, taskApi, type BoardListItemDto, type CalendarItemDto, type PersonalTaskDto } from "../../api";
import type { AuthSession } from "../../auth";
import { Button } from "../../components/ui/Button";
import { addDays, dateInZone } from "../../date";
import { taskWritePayload } from "../tasks/taskView";
import { boardCardTone } from "../boards/boardCardTone";
import "./dashboard.css";

type Snapshot = {
  date: string;
  zone: string;
  tasks: PersonalTaskDto[];
  tasksHaveMore: boolean;
  calendar: CalendarItemDto[];
  calendarHasMore: boolean;
};

function dateLabel(date: string) {
  return new Intl.DateTimeFormat(undefined, { weekday: "long", month: "long", day: "numeric", timeZone: "UTC" })
    .format(new Date(`${date}T12:00:00Z`));
}

function itemDate(item: CalendarItemDto, zone: string) {
  if (item.startDate) return item.startDate;
  return item.startAtUtc ? dateInZone(new Date(item.startAtUtc), zone) : null;
}

function itemTime(item: CalendarItemDto, zone: string) {
  if (!item.startAtUtc) return "All day";
  return new Intl.DateTimeFormat(undefined, { hour: "numeric", minute: "2-digit", timeZone: zone }).format(new Date(item.startAtUtc));
}

export function DashboardPage({ user, boards, boardsLoading, boardsError, retryBoards, navigate, notify }: {
  user: AuthSession["user"];
  boards: BoardListItemDto[];
  boardsLoading: boolean;
  boardsError: string;
  retryBoards: () => void;
  navigate: (path: string) => void;
  notify: (message: string) => void;
}) {
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [title, setTitle] = useState("");
  const [adding, setAdding] = useState(false);
  const [addError, setAddError] = useState("");
  const [changingId, setChangingId] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError("");
    try {
      const settings = await planningSettingsApi.get();
      const zone = settings.timeZoneId || Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
      const date = dateInZone(new Date(), zone);
      const [taskPage, calendarPage] = await Promise.all([
        taskApi.list("today", date),
        calendarApi.range(date, addDays(date, 7), zone),
      ]);
      setSnapshot({
        date, zone, tasks: taskPage.items, tasksHaveMore: taskPage.hasMore,
        calendar: calendarPage.items, calendarHasMore: calendarPage.hasMore,
      });
    } catch (cause) {
      setError(errorMessage(cause));
    } finally {
      setLoading(false);
    }
  }, []);
  useEffect(() => { void load(); }, [load]);

  async function addTask(event: FormEvent) {
    event.preventDefault();
    if (!title.trim() || !snapshot) return;
    setAdding(true);
    setAddError("");
    try {
      await taskApi.create(taskWritePayload(title, "", snapshot.date, "", snapshot.zone));
      setTitle("");
      notify("Task added");
      await load();
    } catch (cause) {
      setAddError(errorMessage(cause));
    } finally {
      setAdding(false);
    }
  }

  async function completeTask(task: PersonalTaskDto) {
    setChangingId(task.id);
    setError("");
    try {
      await taskApi.complete(task.id);
      notify("Task completed");
      await load();
    } catch (cause) {
      setError(errorMessage(cause));
    } finally {
      setChangingId(null);
    }
  }

  const today = snapshot?.date ?? dateInZone(new Date(), Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC");
  const todayCalendarTasks = snapshot?.calendar.filter((item) => item.source === "task" && itemDate(item, snapshot.zone) === today) ?? [];
  const completedToday = todayCalendarTasks.filter((item) => item.isCompleted).length;
  const totalToday = todayCalendarTasks.length;
  const progressKnown = snapshot && !snapshot.calendarHasMore;
  const progress = totalToday ? Math.round(completedToday / totalToday * 100) : 0;
  const recentBoards = [...boards].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt)).slice(0, 3);
  const todayEvents = snapshot?.calendar.filter((item) => item.source === "event" && itemDate(item, snapshot.zone) === today) ?? [];
  const upcomingEvents = snapshot?.calendar.filter((item) => item.source === "event") ?? [];
  const name = (user.displayName || user.username).split(" ")[0];

  return <section className="wk-dashboard" aria-labelledby="wk-dashboard-title">
    <header className="wk-dashboard-header">
      <div><p className="wk-dashboard-eyebrow">{dateLabel(today)}</p>
        <h1 id="wk-dashboard-title">Hello, {name}</h1>
        <p>A calm plan for the work that matters.</p></div>
      <Button variant="secondary" size="compact" onClick={() => navigate('/tasks/quick')}><Plus size={14} aria-hidden="true" /> Quick tasks</Button>
    </header>
    <div className="wk-dashboard-body">
      {loading && !snapshot && <LoadingSkeleton layout="dashboard" label="Loading your day…" />}
      {error && <div className="wk-dashboard-error" role="alert"><p>{error}</p><Button variant="secondary" size="compact" onClick={() => void load()}>Try again</Button></div>}
      {snapshot && <>
        <div className="wk-dashboard-summary">
          <div className="wk-dashboard-summary-card">
            <div className="wk-dashboard-progress" aria-label={progressKnown ? `${progress}% of today's planned tasks complete` : "Today's tasks"}>
              {progressKnown ? <>
                <svg viewBox="0 0 60 60" aria-hidden="true"><circle className="wk-dashboard-progress-track" cx="30" cy="30" r="24" />
                  <circle className="wk-dashboard-progress-fill" cx="30" cy="30" r="24"
                    strokeDasharray={2 * Math.PI * 24} strokeDashoffset={2 * Math.PI * 24 * (1 - progress / 100)} /></svg>
                <span>{progress}%</span>
              </> : <Check size={18} aria-hidden="true" />}
            </div>
            <div><strong>{progressKnown ? `${completedToday} of ${totalToday} planned tasks complete` : "Today's plan"}</strong>
              <p>{progressKnown ? (totalToday ? `${totalToday - completedToday} remaining today` : "Nothing scheduled today") : "Open Week to see all tasks"}</p>
              <button onClick={() => navigate("/tasks")}>Open week <ArrowRight size={13} aria-hidden="true" /></button></div>
          </div>
          <div className="wk-dashboard-summary-card">
            <div className="wk-dashboard-summary-icon"><LayoutDashboard size={18} aria-hidden="true" /></div>
            <div><strong>{boardsLoading ? "Loading boards…" : boardsError ? "Boards unavailable" : `${boards.length} ${boards.length === 1 ? "board" : "boards"}`}</strong>
              <p>Your spaces for notes and shared work</p>
              </div>
          </div>
          <div className="wk-dashboard-summary-card">
            <div className="wk-dashboard-summary-icon"><CalendarDays size={18} aria-hidden="true" /></div>
            <div><strong>{snapshot.calendarHasMore ? "Upcoming events" : `${upcomingEvents.length} ${upcomingEvents.length === 1 ? "event" : "events"} this week`}</strong>
              <p>{todayEvents.length ? `${todayEvents.length} today` : "No events today"}</p>
              </div>
          </div>
        </div>
        <div className="wk-dashboard-columns">
          <div className="wk-dashboard-main-column">
            <section className="wk-dashboard-section" aria-labelledby="wk-dashboard-today">
              <div className="wk-dashboard-section-head"><div><h2 id="wk-dashboard-today">Today</h2>
                <p>{snapshot.tasksHaveMore ? "More than 50 active tasks due" : `${snapshot.tasks.length} active ${snapshot.tasks.length === 1 ? "task" : "tasks"} due`}</p></div>
                </div>
              {snapshot.tasks.length ? <ul className="wk-dashboard-task-list">
                {snapshot.tasks.slice(0, 8).map((task) => <li key={task.id}>
                  <button className="wk-dashboard-task-check" disabled={changingId === task.id}
                    aria-label={`Complete ${task.title}`} onClick={() => void completeTask(task)} />
                  <button className="wk-dashboard-task-title" onClick={() => navigate(`/tasks/${task.id}`)}>
                    <strong>{task.title}</strong><small>{task.plannedDate && task.plannedDate < today ? "Overdue" : task.plannedTime ? "Scheduled" : "Anytime"}</small>
                  </button>
                  {task.plannedTime && <time>{task.plannedTime.slice(0, 5)}</time>}
                </li>)}
              </ul> : <p className="wk-dashboard-empty">No tasks due today. Add one below or plan your week.</p>}
              <form className="wk-dashboard-quick-add" onSubmit={(event) => void addTask(event)}>
                <Plus size={15} aria-hidden="true" /><input value={title} maxLength={200}
                  onChange={(event) => setTitle(event.target.value)} placeholder="Add a task for today" aria-label="Add a task for today" />
                {title.trim() && <Button type="submit" size="compact" disabled={adding}>Add</Button>}
              </form>
              {addError && <p className="wk-dashboard-inline-error" role="alert">{addError}</p>}
            </section>
            <section className="wk-dashboard-section wk-dashboard-boards" aria-labelledby="wk-dashboard-boards">
              <div className="wk-dashboard-section-head"><div><h2 id="wk-dashboard-boards">Recent boards</h2><p>Continue where you left off</p></div>
                <button onClick={() => navigate("/boards")}>View all <ArrowRight size={13} aria-hidden="true" /></button></div>
              {boardsError ? <div className="wk-dashboard-inline-error" role="alert">{boardsError} <button onClick={retryBoards}>Try again</button></div>
                : boardsLoading ? <LoadingSkeleton layout="cards" label="Loading boards…" />
                  : recentBoards.length ? <div className="wk-dashboard-board-grid">{recentBoards.map((board) =>
                    <button key={board.id} data-board-id={board.id} data-board-tone={board.cardColor ?? boardCardTone(board.id)} onClick={() => navigate(`/boards/${board.id}`)}>
                      <LayoutDashboard size={16} aria-hidden="true" /><strong>{board.title}</strong>
                      <small>{board.noteCount} {board.noteCount === 1 ? "note" : "notes"} · {board.memberCount} {board.memberCount === 1 ? "member" : "members"}</small>
                    </button>)}</div>
                    : <p className="wk-dashboard-empty">No boards yet. Create your first board from Boards.</p>}
            </section>
          </div>
          <aside className="wk-dashboard-side-column" aria-label="Today's schedule">
            <section className="wk-dashboard-section">
              <div className="wk-dashboard-section-head"><div><h2>Schedule</h2><p>{todayEvents.length} {todayEvents.length === 1 ? "event" : "events"} today</p></div>
                <button onClick={() => navigate("/calendar")}>Calendar <ArrowRight size={13} aria-hidden="true" /></button></div>
              {todayEvents.length ? <ol className="wk-dashboard-schedule">{todayEvents.map((item) =>
                <li key={item.id}><span><Clock3 size={14} aria-hidden="true" /> {itemTime(item, snapshot.zone)}</span>
                  <button onClick={() => navigate("/calendar")}>{item.title}</button></li>)}</ol>
                : <p className="wk-dashboard-empty">No events scheduled today.</p>}
            </section>
          </aside>
        </div>
      </>}
    </div>
  </section>;
}
