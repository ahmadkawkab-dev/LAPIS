import { useEffect, useRef, useState } from 'react';
import { AlertTriangle, X } from 'lucide-react';
import { IconButton } from './Button';

export function Notice({ message, tone = 'default', onDismiss }: {
  message: string;
  tone?: 'default' | 'warning';
  onDismiss: () => void;
}) {
  const [closing, setClosing] = useState(false);
  const [paused, setPaused] = useState(false);
  const dismissRef = useRef(onDismiss);
  const manualRemove = useRef<number | null>(null);
  const generation = useRef(0);
  dismissRef.current = onDismiss;
  useEffect(() => {
    const current = ++generation.current;
    setClosing(false);
    // Warnings require an explicit dismissal; reading or focusing a notice pauses expiry.
    const exit = tone === 'warning' || paused ? undefined : window.setTimeout(() => setClosing(true), 5800);
    const remove = tone === 'warning' || paused ? undefined : window.setTimeout(() => { if (generation.current === current) dismissRef.current(); }, 6000);
    return () => {
      generation.current++;
      window.clearTimeout(exit);
      window.clearTimeout(remove);
      if (manualRemove.current !== null) window.clearTimeout(manualRemove.current);
    };
  }, [message, tone, paused]);
  function close() {
    if (closing) return;
    const current = generation.current;
    setClosing(true);
    manualRemove.current = window.setTimeout(() => {
      if (generation.current === current) dismissRef.current();
    }, 200);
  }
  return (
    <div onPointerEnter={() => setPaused(true)} onPointerLeave={(event) => { if (!event.currentTarget.contains(document.activeElement)) setPaused(false); }}
      onFocus={() => setPaused(true)} onBlur={(event) => { if (!event.currentTarget.contains(event.relatedTarget) && !event.currentTarget.matches(":hover")) setPaused(false); }}
      className={`wk-notice${tone === 'warning' ? ' wk-notice--warning' : ''}${closing ? ' wk-notice--closing' : ''}`}>
      {tone === 'warning' ? <AlertTriangle size={18} aria-hidden="true" /> : null}
      <p role={tone === 'warning' ? 'alert' : 'status'} aria-atomic="true">{message}</p>
      <IconButton label="Dismiss notification" onClick={close}><X size={18} aria-hidden="true" /></IconButton>
    </div>
  );
}
