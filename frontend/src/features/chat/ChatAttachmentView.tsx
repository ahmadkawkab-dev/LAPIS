import { useEffect, useRef, useState } from "react";
import { Download, Image as ImageIcon } from "lucide-react";
import { Button } from "../../components/ui/Button";
import { chatApi, ChatApiError, chatErrorMessage } from "./chatApi";
import type { ChatMessage } from "./types";

function downloadName(name: string) {
  const safe = name.split(/[\\/]/).at(-1)?.replace(/[\u0000-\u001f\u007f\u202a-\u202e]/g, "").trim() || "image";
  return `${safe.replace(/\.[^.]+$/, "")}.webp`;
}

export function ChatAttachmentView({ message }: { message: ChatMessage }) {
  const attachment = message.attachment;
  const root = useRef<HTMLDivElement>(null);
  const [visible, setVisible] = useState(false);
  const [previewUrl, setPreviewUrl] = useState<string | null>(null);
  const [previewError, setPreviewError] = useState(false);
  const [previewAttempt, setPreviewAttempt] = useState(0);
  const [downloadError, setDownloadError] = useState<string | null>(null);
  const [downloading, setDownloading] = useState(false);
  const available = attachment?.scanStatus === "Available";

  useEffect(() => {
    if (!available || !root.current) return;
    if (typeof IntersectionObserver === "undefined") { setVisible(true); return; }
    const observer = new IntersectionObserver(entries => {
      if (entries.some(entry => entry.isIntersecting)) { setVisible(true); observer.disconnect(); }
    }, { root: root.current.closest(".chat-history"), rootMargin: "150px" });
    observer.observe(root.current);
    return () => observer.disconnect();
  }, [available]);

  useEffect(() => {
    if (!attachment || !available || !visible) return;
    const abort = new AbortController();
    let url: string | null = null;
    setPreviewError(false); setPreviewUrl(null);
    void chatApi.download(message.boardId, attachment.id, true, abort.signal).then(blob => {
      if (abort.signal.aborted) return;
      url = URL.createObjectURL(blob);
      setPreviewUrl(url);
    }).catch(() => { if (!abort.signal.aborted) setPreviewError(true); });
    return () => { abort.abort(); if (url) URL.revokeObjectURL(url); setPreviewUrl(null); };
  }, [message.boardId, attachment?.id, available, visible, previewAttempt]);

  async function download() {
    if (!attachment || downloading) return;
    setDownloading(true); setDownloadError(null);
    try {
      const blob = await chatApi.download(message.boardId, attachment.id, false, new AbortController().signal);
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement("a");
      anchor.href = url; anchor.download = downloadName(attachment.fileName);
      document.body.append(anchor); anchor.click(); anchor.remove();
      window.setTimeout(() => URL.revokeObjectURL(url), 30_000);
    } catch (cause) { setDownloadError(cause instanceof ChatApiError && cause.status === 404
      ? "This image is no longer available." : cause instanceof ChatApiError && cause.status >= 500
        ? "Image download is temporarily unavailable. Try again later." : chatErrorMessage(cause)); }
    finally { setDownloading(false); }
  }

  if (!attachment) return <div className="chat-attachment">Attachment unavailable</div>;
  const state = attachment.scanStatus;
  const label = state === "Pending" || state === "Scanning" ? "Checking image for safety…" :
    state === "ScanFailed" ? "Safety check delayed. We’ll retry automatically." :
    state === "Rejected" ? "Image blocked by the safety check." : null;
  return <div className="chat-attachment" ref={root}>
    {previewUrl && available ? <img className="chat-attachment-preview" src={previewUrl} alt={attachment.fileName} loading="lazy" /> :
      <div className="chat-attachment-placeholder" role="status" aria-live="polite"><ImageIcon size={22} aria-hidden="true" />
        {available ? previewError ? <>Preview unavailable <Button variant="quiet" size="compact" onClick={() => setPreviewAttempt(value => value + 1)}>Retry</Button></> : "Loading preview…" : label}</div>}
    <div className="chat-attachment-footer"><span title={attachment.fileName}>{attachment.fileName}</span>
      {available && <Button variant="quiet" size="compact" onClick={() => void download()} disabled={downloading}
        aria-label={`Download ${attachment.fileName}`}><Download size={15} aria-hidden="true" /> Download</Button>}
    </div>
    {downloadError && <p className="chat-pending-error" role="alert">{downloadError}</p>}
  </div>;
}
