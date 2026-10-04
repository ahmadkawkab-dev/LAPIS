import { useEffect, useId, useRef, type KeyboardEvent, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { X } from 'lucide-react';
import { IconButton } from './Button';

/** Native modal supplies focus containment and makes the underlying page inert. */
export function Dialog({ title, children, onClose, urgent = false, busy = false, className = '' }: {
  title: string;
  children: ReactNode;
  onClose: () => void;
  urgent?: boolean;
  busy?: boolean;
  className?: string;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();
  const focusable = () => Array.from(ref.current?.querySelectorAll<HTMLElement>(
    'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
  ) ?? []).filter((element) => element.getClientRects().length > 0 && !element.matches(':disabled') && !element.closest('[inert], [aria-hidden="true"]'));
  useEffect(() => {
    const previous = document.activeElement;
    const dialog = ref.current;
    dialog?.showModal();
    const frame = requestAnimationFrame(() => {
      const autofocus = dialog?.querySelector<HTMLElement>('[autofocus]:not(:disabled)');
      (autofocus ?? dialog?.querySelector<HTMLElement>('h2') ?? dialog)?.focus({ preventScroll: true });
    });
    return () => {
      cancelAnimationFrame(frame);
      dialog?.close();
      if (previous instanceof HTMLElement && previous.isConnected) previous.focus();
    };
  }, []);
  function containFocus(event: KeyboardEvent<HTMLDialogElement>) {
    if (event.key !== 'Tab') return;
    const targets = focusable();
    if (!targets.length) {
      event.preventDefault();
      ref.current?.focus();
      return;
    }
    const first = targets[0];
    const last = targets.at(-1)!;
    const active = document.activeElement;
    if (!targets.includes(active as HTMLElement)) {
      event.preventDefault();
      (event.shiftKey ? last : first).focus();
    } else if (event.shiftKey && active === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }
  return createPortal(
    <dialog ref={ref} className={`wk-dialog ${className}`.trim()} aria-labelledby={titleId} role={urgent ? 'alertdialog' : undefined} tabIndex={-1}
      onKeyDown={containFocus}
      onCancel={(event) => { event.preventDefault(); if (!busy) onClose(); }} aria-busy={busy || undefined}>
      <h2 id={titleId} tabIndex={-1}>{title}</h2>
      <IconButton className="wk-dialog-close" label={`Close ${title}`} disabled={busy} onClick={onClose}><X size={18} aria-hidden="true" /></IconButton>
      {children}
    </dialog>, document.body,
  );
}
