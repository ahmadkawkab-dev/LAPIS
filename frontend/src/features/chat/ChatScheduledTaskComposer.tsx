import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { ArrowLeft, CalendarClock } from "lucide-react";
import { Button } from "../../components/ui/Button";
import type { ChatController } from "./ChatController";
import { localTimeChoices, offsetLabel, scheduledTaskSubmission } from "./scheduledTask";
import type { ScheduledTaskDraft, ScheduledTaskWrite } from "./types";

const zones = typeof Intl.supportedValuesOf === "function" ? Intl.supportedValuesOf("timeZone") : [];

export function ChatScheduledTaskComposer({ controller, draft, canSchedule, hint, onBack, onSend }: {
  controller: ChatController; draft: ScheduledTaskDraft; canSchedule: boolean; hint: string;
  onBack: () => void; onSend: (request: ScheduledTaskWrite) => void;
}) {
  const title = useRef<HTMLInputElement>(null);
  const [error, setError] = useState("");
  const startChoices = useMemo(() => draft.localStart ? localTimeChoices(draft.localStart, draft.timeZoneId) : null,
    [draft.localStart, draft.timeZoneId]);
  const endChoices = useMemo(() => draft.localEnd ? localTimeChoices(draft.localEnd, draft.timeZoneId) : null,
    [draft.localEnd, draft.timeZoneId]);
  useEffect(() => { title.current?.focus({ preventScroll: true }); }, []);
  function update(values: Partial<ScheduledTaskDraft>) { controller.setScheduledDraft(values); setError(""); }
  function submit(event: FormEvent) {
    event.preventDefault();
    if (!canSchedule) return;
    const result = scheduledTaskSubmission(draft);
    if (!result.request) { setError(result.error ?? "Check the task details."); return; }
    onSend(result.request);
  }
  return <form className="chat-schedule-form" onSubmit={submit} aria-label="Create scheduled task">
    <div className="chat-schedule-heading"><Button variant="quiet" size="compact" aria-label="Back to message" onClick={onBack}>
      <ArrowLeft size={16} aria-hidden="true" /></Button><h3><CalendarClock size={17} aria-hidden="true" /> Scheduled task</h3></div>
    <label htmlFor="chat-task-title">Title</label>
    <input id="chat-task-title" ref={title} value={draft.title} maxLength={200} required
      onChange={event => update({ title: event.target.value })} placeholder="What needs to happen?" />
    <label htmlFor="chat-task-start">Start</label>
    <input id="chat-task-start" type="datetime-local" value={draft.localStart} step={60} required
      onChange={event => update({ localStart: event.target.value, startOffsetMinutes: "" })} />
    {startChoices?.state === "gap" && <p className="chat-schedule-warning" role="status">This clock time does not exist in the chosen time zone.</p>}
    {startChoices?.state === "invalid-zone" && <p className="chat-schedule-warning" role="status">Choose a valid time zone.</p>}
    {startChoices && startChoices.choices.length > 1 && <><label htmlFor="chat-task-start-occurrence">Which start time?</label>
      <select id="chat-task-start-occurrence" required value={draft.startOffsetMinutes}
        onChange={event => update({ startOffsetMinutes: event.target.value })}>
        <option value="">Choose an occurrence</option>
        {startChoices.choices.map((choice, index) => <option key={choice.offsetMinutes} value={choice.offsetMinutes}>
          {index === 0 ? "First" : "Second"} occurrence · {offsetLabel(choice.offsetMinutes)}</option>)}
      </select></>}
    <label htmlFor="chat-task-end">End <span>(optional)</span></label>
    <input id="chat-task-end" type="datetime-local" value={draft.localEnd} step={60}
      onChange={event => update({ localEnd: event.target.value, endOffsetMinutes: "" })} />
    {endChoices?.state === "gap" && <p className="chat-schedule-warning" role="status">This end time does not exist in the chosen time zone.</p>}
    {endChoices && endChoices.choices.length > 1 && <><label htmlFor="chat-task-end-occurrence">Which end time?</label>
      <select id="chat-task-end-occurrence" required value={draft.endOffsetMinutes}
        onChange={event => update({ endOffsetMinutes: event.target.value })}>
        <option value="">Choose an occurrence</option>
        {endChoices.choices.map((choice, index) => <option key={choice.offsetMinutes} value={choice.offsetMinutes}>
          {index === 0 ? "First" : "Second"} occurrence · {offsetLabel(choice.offsetMinutes)}</option>)}
      </select></>}
    <label htmlFor="chat-task-zone">Time zone</label>
    <input id="chat-task-zone" value={draft.timeZoneId} maxLength={100} required list="chat-task-zones"
      onChange={event => update({ timeZoneId: event.target.value, startOffsetMinutes: "", endOffsetMinutes: "" })} />
    <datalist id="chat-task-zones"><option value="UTC" />{zones.map(zone => <option key={zone} value={zone} />)}</datalist>
    <p className="chat-schedule-hint">Board members will see the same instant in their own time zone.</p>
    <label htmlFor="chat-task-description">Description <span>(optional)</span></label>
    <textarea id="chat-task-description" rows={2} value={draft.description} maxLength={4000}
      onChange={event => update({ description: event.target.value })} />
    {error && <p className="chat-pending-error" role="alert">{error}</p>}
    <div className="chat-schedule-actions"><span>{hint}</span>
      <Button type="submit" size="compact" disabled={!canSchedule}>Post task</Button></div>
  </form>;
}
