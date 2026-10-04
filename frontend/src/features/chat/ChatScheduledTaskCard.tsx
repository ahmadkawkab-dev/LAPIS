import { useEffect, useState } from "react";
import { CalendarClock, Check, Download } from "lucide-react";
import { calendarApi, errorMessage } from "../../api";
import { Button } from "../../components/ui/Button";
import { chatApi, ChatApiError, chatErrorMessage } from "./chatApi";
import { scheduledTaskLabel } from "./scheduledTask";
import type { ChatMessage } from "./types";

export function ChatScheduledTaskCard({ message, task }: { message: ChatMessage; task: NonNullable<ChatMessage["scheduledTask"]> }) {
  const [downloading, setDownloading] = useState(false);
  const [checking, setChecking] = useState(true);
  const [adding, setAdding] = useState(false);
  const [added, setAdded] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const display = scheduledTaskLabel(task);
  useEffect(() => {
    const abort = new AbortController();
    setChecking(true); setAdded(false);
    void calendarApi.chatEvent(message.boardId, message.id, abort.signal).then(item => {
      if (!abort.signal.aborted) setAdded(item !== undefined);
    }).catch(cause => {
      if (!abort.signal.aborted) setError(errorMessage(cause));
    }).finally(() => {
      if (!abort.signal.aborted) setChecking(false);
    });
    return () => abort.abort();
  }, [message.boardId, message.id]);
  async function add() {
    if (checking || adding || added) return;
    setAdding(true); setError(null);
    try {
      await calendarApi.addChatEvent(message.boardId, message.id);
      setAdded(true);
    } catch (cause) { setError(errorMessage(cause)); }
    finally { setAdding(false); }
  }
  async function download() {
    if (downloading) return;
    setDownloading(true); setError(null);
    try {
      const blob = await chatApi.calendar(message.boardId, message.id, new AbortController().signal);
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement("a");
      anchor.href = url; anchor.download = `wukna-task-${message.id}.ics`;
      document.body.append(anchor); anchor.click(); anchor.remove();
      window.setTimeout(() => URL.revokeObjectURL(url), 30_000);
    } catch (cause) { setError(cause instanceof ChatApiError && cause.status === 404
      ? "This task is no longer available." : chatErrorMessage(cause)); }
    finally { setDownloading(false); }
  }
  return <div className="chat-task-card">
    <div className="chat-task-title"><CalendarClock size={17} aria-hidden="true" /><strong dir="auto">{task.title}</strong></div>
    <time dateTime={task.startsAtUtc} title="Shown in your time zone">{display.when} · your time</time>
    <span className="chat-task-zone">Set in {display.authored}</span>
    {task.description && <p dir="auto">{task.description}</p>}
    <div className="chat-task-actions">
      <Button size="compact" onClick={() => void add()} disabled={checking || adding || added}
        aria-label={added ? `${task.title} added to Calendar` : `Add ${task.title} to Wukna Calendar`}>
        {added ? <Check size={15} aria-hidden="true" /> : <CalendarClock size={15} aria-hidden="true" />}
        <span aria-live="polite">{added ? "Added to Calendar" : adding ? "Adding…" : "Add to Wukna Calendar"}</span>
      </Button>
      <Button variant="quiet" size="compact" className="chat-task-download" onClick={() => void download()}
        disabled={downloading} aria-label={`Download .ics for ${task.title}`}>
        <Download size={15} aria-hidden="true" />{downloading ? "Preparing…" : "Download .ics"}</Button>
    </div>
    {error && <p className="chat-pending-error" role="alert">{error}</p>}
  </div>;
}
