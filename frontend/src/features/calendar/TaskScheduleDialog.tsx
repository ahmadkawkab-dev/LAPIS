import { useState, type FormEvent } from "react";
import { errorMessage } from "../../api";
import { Button } from "../../components/ui/Button";
import { Dialog } from "../../components/ui/Dialog";

export function TaskScheduleDialog({ title, initialDate, isTimed, onSchedule, onClose }: {
  title: string; initialDate: string; isTimed: boolean;
  onSchedule: (date: string) => Promise<void>; onClose: () => void;
}) {
  const [date, setDate] = useState(initialDate);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!date) return;
    setBusy(true); setError("");
    try { await onSchedule(date); onClose(); }
    catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  }
  return <Dialog busy={busy} title="Plan task" onClose={onClose}>
    <form className="wk-calendar-schedule-form" onSubmit={(event) => void submit(event)}>
      <p>{title}</p>
      <label>Date <input type="date" value={date} required onChange={(event) => setDate(event.target.value)} /></label>
      {isTimed && <small>The task keeps its local time and time zone.</small>}
      {error && <p className="wk-calendar-error" role="alert">{error}</p>}
      <div className="wk-dialog-actions"><Button type="submit" loading={busy} disabled={!date}>Save date</Button>
        <Button variant="secondary" disabled={busy} onClick={onClose}>Cancel</Button></div>
    </form>
  </Dialog>;
}
