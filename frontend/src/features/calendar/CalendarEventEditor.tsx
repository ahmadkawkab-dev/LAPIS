import { CalendarReminderPanel } from "./CalendarReminderPanel";
import { useState, type FormEvent } from "react";
import { calendarApi, errorMessage, type CalendarEventDto } from "../../api";
import { addDays } from "../../date";
import { Button } from "../../components/ui/Button";
import { Dialog } from "../../components/ui/Dialog";
import { eventWritePayload } from "./calendarView";

const timeZoneOptions = <datalist id="wk-calendar-zones"><option value="UTC" />
  {(typeof Intl.supportedValuesOf === "function" ? Intl.supportedValuesOf("timeZone") : []).map((value) => <option key={value} value={value} />)}</datalist>;

export function CalendarEventEditor({ event, date, zone, onClose, onSaved, onDeleted }: {
  event: CalendarEventDto | null; date: string; zone: string;
  onClose: () => void; onSaved: () => void; onDeleted: () => void;
}) {
  const [title, setTitle] = useState(event?.title ?? "");
  const [description, setDescription] = useState(event?.description ?? "");
  const [location, setLocation] = useState(event?.location ?? "");
  const [isAllDay, setIsAllDay] = useState(event?.isAllDay ?? false);
  const [startDate, setStartDate] = useState(event?.allDayStartDate ?? event?.localStart?.slice(0, 10) ?? date);
  const [endDate, setEndDate] = useState(event?.allDayEndDateExclusive
    ? addDays(event.allDayEndDateExclusive, -1) : event?.localEnd?.slice(0, 10) ?? date);
  const [startTime, setStartTime] = useState(event?.localStart?.slice(11, 16) ?? "09:00");
  const [endTime, setEndTime] = useState(event?.localEnd?.slice(11, 16) ?? "10:00");
  const [timeZoneId, setTimeZoneId] = useState(event?.timeZoneId ?? zone);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  async function save(submit: FormEvent) {
    submit.preventDefault();
    setBusy(true); setError("");
    try {
      const payload = eventWritePayload({ title, description, location, isAllDay,
        startDate, endDate, startTime, endTime, timeZoneId });
      if (event) await calendarApi.updateEvent(event.id, payload);
      else await calendarApi.createEvent(payload);
      onSaved();
    } catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  }
  async function remove() {
    if (!event) return;
    setBusy(true); setError("");
    try { await calendarApi.removeEvent(event.id); onDeleted(); }
    catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  }
  return <Dialog title={confirmDelete ? "Delete event?" : event ? "Edit event" : "New event"} onClose={onClose} urgent={confirmDelete}>
    {confirmDelete ? <div className="wk-calendar-editor">
      <p>“{event?.title}” will be permanently deleted.</p>
      {error && <p className="wk-calendar-error" role="alert">{error}</p>}
      <div className="wk-dialog-actions"><Button variant="danger" disabled={busy} onClick={() => void remove()}>Delete event</Button>
        <Button variant="secondary" disabled={busy} onClick={() => setConfirmDelete(false)}>Cancel</Button></div>
    </div> : <form className="wk-calendar-editor" onSubmit={(submit) => void save(submit)}>
      <label>Title <input value={title} maxLength={200} required onChange={(change) => setTitle(change.target.value)} autoFocus /></label>
      <label className="wk-calendar-check"><input type="checkbox" checked={isAllDay} onChange={(change) => setIsAllDay(change.target.checked)} /> All day</label>
      <div className="wk-calendar-editor-row"><label>Start date <input type="date" value={startDate} required onChange={(change) => { setStartDate(change.target.value); if (endDate < change.target.value) setEndDate(change.target.value); }} /></label>
        <label>End date <input type="date" value={endDate} min={startDate} required onChange={(change) => setEndDate(change.target.value)} /></label></div>
      {!isAllDay && <><div className="wk-calendar-editor-row"><label>Start time <input type="time" value={startTime} required onChange={(change) => setStartTime(change.target.value)} /></label>
        <label>End time <input type="time" value={endTime} required onChange={(change) => setEndTime(change.target.value)} /></label></div>
        <label>Time zone <input value={timeZoneId} maxLength={100} required list="wk-calendar-zones" onChange={(change) => setTimeZoneId(change.target.value)} /></label>
        {timeZoneOptions}</>}
      <label>Location <input value={location} maxLength={200} onChange={(change) => setLocation(change.target.value)} /></label>
      <label>Description <textarea value={description} maxLength={4000} rows={3} onChange={(change) => setDescription(change.target.value)} /></label>
      {event?.sourceChatMessageId && !isAllDay && <CalendarReminderPanel eventId={event.id} />}
      {error && <p className="wk-calendar-error" role="alert">{error}</p>}
      <div className="wk-dialog-actions"><Button type="submit" disabled={busy || !title.trim()}>{event ? "Save changes" : "Create event"}</Button>
        {event && <Button variant="quiet" disabled={busy} onClick={() => setConfirmDelete(true)}>Delete</Button>}
        <Button variant="secondary" disabled={busy} onClick={onClose}>Cancel</Button></div>
    </form>}
  </Dialog>;
}
