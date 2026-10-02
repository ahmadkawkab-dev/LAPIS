import { CalendarPlus2, ChevronDown, ChevronUp } from "lucide-react";
import type { DragEvent } from "react";
import type { PersonalTaskDto } from "../../api";
import { Button } from "../../components/ui/Button";

export function CalendarTaskTray({ tasks, hasMore, loading, error, open, onToggle,
  onLoadMore, onDragStart, onDragEnd, onPlan, onOpenTask }: {
  tasks: PersonalTaskDto[]; hasMore: boolean; loading: boolean; error: string; open: boolean;
  onToggle: () => void; onLoadMore: () => void;
  onDragStart: (event: DragEvent<HTMLButtonElement>, id: string) => void;
  onDragEnd: () => void; onPlan: (task: PersonalTaskDto) => void;
  onOpenTask: (id: string) => void;
}) {
  return <aside className="wk-calendar-tray" aria-label="Unscheduled tasks">
    <button className="wk-calendar-tray-toggle" aria-expanded={open} onClick={onToggle}>
      <span>Unscheduled tasks</span>{open ? <ChevronUp size={17} aria-hidden="true" /> : <ChevronDown size={17} aria-hidden="true" />}
    </button>
    {open && <div className="wk-calendar-tray-content">
      <p>Drag a task onto a day, or choose Plan.</p>
      {error && <p className="wk-calendar-error" role="alert">{error}</p>}
      {loading && <p role="status">Loading tasks…</p>}
      {!loading && tasks.length === 0 && !error && <p>Your inbox is clear.</p>}
      <ul>{tasks.map((task) => <li key={task.id}>
        <button className="wk-calendar-tray-task" draggable onDragStart={(event) => onDragStart(event, task.id)} onDragEnd={onDragEnd}
          onClick={() => onOpenTask(task.id)} title={`Open ${task.title}`}>{task.title}</button>
        <Button variant="quiet" size="compact" onClick={() => onPlan(task)} aria-label={`Plan ${task.title}`}><CalendarPlus2 size={16} aria-hidden="true" /> Plan</Button>
      </li>)}</ul>
      {hasMore && <Button variant="quiet" size="compact" disabled={loading} onClick={onLoadMore}>Load more</Button>}
    </div>}
  </aside>;
}
