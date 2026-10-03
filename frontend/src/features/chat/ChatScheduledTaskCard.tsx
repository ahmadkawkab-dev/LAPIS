import { useState } from "react";
import { CalendarClock, Download } from "lucide-react";
import { Button } from "../../components/ui/Button";
import { chatApi, ChatApiError, chatErrorMessage } from "./chatApi";
import { scheduledTaskLabel } from "./scheduledTask";
import type { ChatMessage } from "./types";

export function ChatScheduledTaskCard({ message, task }: { message: ChatMessage; task: NonNullable<ChatMessage["scheduledTask"]> }) {
  const [downloading, setDownloading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const display = scheduledTaskLabel(task);
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
    <Button variant="quiet" size="compact" className="chat-task-download" onClick={() => void download()}
      disabled={downloading} aria-label={`Add ${task.title} to calendar`}>
      <Download size={15} aria-hidden="true" />{downloading ? "Preparing…" : "Add to calendar"}</Button>
    {error && <p className="chat-pending-error" role="alert">{error}</p>}
  </div>;
}
