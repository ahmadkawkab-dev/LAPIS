import { useEffect, useRef, useState, type FormEvent } from "react";
import { MoreHorizontal, X } from "lucide-react";
import { boardApi, errorMessage, type BoardDetailDto, type MemberDto, type NoteDto } from "../../../api";
import { Avatar, identityLabel } from "../../../components/ui/Avatar";
import { Button, IconButton } from "../../../components/ui/Button";
import { Dialog } from "../../../components/ui/Dialog";
import { collaboratorStyle } from "../collaboratorIdentity";
import { InlineNoteText } from "./InlineNoteText";
import { MemberActionsMenu, memberActionError, type MemberMenuAnchor } from "../MemberActionsMenu";
import type { BoardPresenceSnapshot } from "../../../realtime/events";

export function TasksPanel({
  notes,
  editable,
  toggle,
  edit,
  remove,
  editingChanged,
  close,
}: {
  notes: NoteDto[];
  editable: boolean;
  toggle: (note: NoteDto) => void;
  edit: (note: NoteDto, title: string) => Promise<void>;
  remove: (note: NoteDto) => void;
  editingChanged: (noteId: string, active: boolean) => void;
  close: () => void;
}) {
  const items = notes.filter((note) => note.kind === 2);
  const [confirmRemove, setConfirmRemove] = useState<string | null>(null);
  return (
    <aside className="side-panel">
      <div className="panel-head">
        <h2>Tasks</h2>
        <IconButton label="Close tasks" onClick={close}>
          <X size={18} aria-hidden="true" />
        </IconButton>
      </div>
      <div className="task-group">
        {items.length ? (
          items.map((item) => (
            <div
              className={`panel-task ${item.isCompleted ? "completed" : ""}`}
              key={item.id}
            >
              <label className="wk-checkbox-target">
                <input
                  type="checkbox"
                  aria-label={`Complete ${item.title}`}
                  checked={item.isCompleted}
                  disabled={!editable}
                  onChange={() => toggle(item)}
                />
              </label>
              <InlineNoteText
                note={item}
                field="title"
                label="Checklist item title"
                editable={editable}
                onSave={(title) => edit(item, title)}
                onEditingChange={(active) =>
                  editingChanged(item.parentNoteId ?? item.id, active)}
              />
              {editable && (
                confirmRemove === item.id ? (
                  <>
                    <button className="task-action" onClick={() => setConfirmRemove(null)}>Cancel</button>
                    <button className="task-action danger" onClick={() => {
                      setConfirmRemove(null);
                      remove(item);
                    }}>Confirm delete</button>
                  </>
                ) : (
                  <button className="task-action" onClick={() => setConfirmRemove(item.id)}>
                    Delete
                  </button>
                )
              )}
            </div>
          ))
        ) : (
          <p>No checklist items yet.</p>
        )}
      </div>
      <div className="panel-footer">
        {items.filter((item) => item.isCompleted).length} / {items.length}{" "}
        complete
      </div>
    </aside>
  );
}

export function SharePanel({
  board,
  members,
  presence,
  presenceAvailable,
  setGuest,
  changePermission,
  removeGuest,
  close,
}: {
  board: BoardDetailDto;
  members: MemberDto[];
  presence: BoardPresenceSnapshot | null;
  presenceAvailable: boolean;
  setGuest: (email: string, edit: boolean) => Promise<void>;
  changePermission: (member: MemberDto, canEdit: boolean) => Promise<void>;
  removeGuest: (id: string) => Promise<void>;
  close: () => void;
}) {
  const [email, setEmail] = useState(""),
    [edit, setEdit] = useState(false),
    [busy, setBusy] = useState(false),
    [error, setError] = useState(""),
    [menu, setMenu] = useState<{ member: MemberDto; anchor: MemberMenuAnchor } | null>(null),
    [removeTarget, setRemoveTarget] = useState<MemberDto | null>(null);
  const heading = useRef<HTMLHeadingElement>(null);
  const removalOrigin = useRef<HTMLElement | null>(null);
  const online = new Set(presenceAvailable ? presence?.viewers.map((viewer) => viewer.userId) : []);
  const [maxGuests, setMaxGuests] = useState<number | null>(null);
  const guestCount = members.filter(member => member.role === 0).length;
  useEffect(() => {
    let current = true;
    void boardApi.guestLimit(board.id).then(limit => { if (current) setMaxGuests(limit.maxGuests); }).catch(() => undefined);
    return () => { current = false; };
  }, [board.id, guestCount]);
  function closeMenu(restoreFocus = true) {
    const trigger = menu?.anchor.trigger;
    setMenu(null);
    if (restoreFocus && trigger?.isConnected) queueMicrotask(() => trigger.focus());
  }
  function openMenu(member: MemberDto, anchor: MemberMenuAnchor) {
    if (board.role !== 1 || member.role !== 0) return;
    setMenu({ member, anchor });
  }
  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError("");
    try {
      await setGuest(email.trim(), edit);
      setEmail("");
    } catch (cause) {
      setError(errorMessage(cause));
    } finally {
      setBusy(false);
    }
  }
  async function remove() {
    if (!removeTarget) return;
    setBusy(true);
    setError("");
    try {
      await removeGuest(removeTarget.userId);
      setRemoveTarget(null);
      queueMicrotask(() => heading.current?.focus());
    } catch (cause) {
      setError(memberActionError(cause));
    } finally {
      setBusy(false);
    }
  }
  return (
    <aside className="side-panel">
      <div className="panel-head">
        <h2 ref={heading} tabIndex={-1}>Board members</h2>
        <IconButton label="Close sharing" onClick={close}>
          <X size={18} aria-hidden="true" />
        </IconButton>
      </div>
      {maxGuests !== null && <p className="wk-muted">{guestCount}/{maxGuests} guests · owner separate
        {guestCount >= maxGuests && " · Existing guest permissions can still be updated."}</p>}
      <div className="member-list">
        {members.map((member) => {
          const name = identityLabel(member);
          const manageable = board.role === 1 && member.role === 0;
          return (
            <div className="member-row" key={member.userId} role="group"
              aria-label={`${name}, ${member.role === 1 ? "Owner" : member.canEdit ? "Editor" : "Viewer"}`}
              tabIndex={manageable ? 0 : undefined}
              aria-keyshortcuts={manageable ? "Shift+F10" : undefined}
              onContextMenu={(event) => {
                if (!manageable) return;
                event.preventDefault();
                openMenu(member, { x: event.clientX, y: event.clientY, trigger: event.currentTarget });
              }}
              onKeyDown={(event) => {
                if (!manageable || !(event.key === "ContextMenu" || event.key === "F10" && event.shiftKey)) return;
                event.preventDefault();
                const rect = event.currentTarget.getBoundingClientRect();
                openMenu(member, { x: rect.left, y: rect.bottom, trigger: event.currentTarget });
              }}>
              <Avatar identity={member} size="small" className="avatar" style={collaboratorStyle(member.userId)} />
              <div className="wk-member-copy">
                <strong>{name}</strong>
                <span>@{member.username}</span>
                <small>{member.role === 1 ? "Owner" : member.canEdit ? "Editor" : "Viewer"}</small>
                {online.has(member.userId) && <small className="wk-member-online">Online now</small>}
              </div>
              {manageable && (
                <IconButton label={`Actions for ${name}`} className="wk-member-actions"
                  onClick={(event) => {
                    const rect = event.currentTarget.getBoundingClientRect();
                    openMenu(member, { x: rect.left, y: rect.bottom, trigger: event.currentTarget });
                  }}>
                  <MoreHorizontal size={19} aria-hidden="true" />
                </IconButton>
              )}
            </div>
          );
        })}
      </div>
      {menu && (
        <MemberActionsMenu member={menu.member} anchor={menu.anchor}
          onClose={closeMenu} onPermission={changePermission}
          onRemove={(member) => {
            removalOrigin.current = menu.anchor.trigger;
            closeMenu(false);
            setError("");
            setRemoveTarget(member);
          }} />
      )}
      {removeTarget && (
        <Dialog title={`Remove ${identityLabel(removeTarget)}?`} urgent
          onClose={() => {
            if (busy) return;
            setRemoveTarget(null);
            queueMicrotask(() => removalOrigin.current?.focus());
          }}>
          <p>They will immediately lose access to this board.</p>
          {error && <p className="wk-alert" role="alert">{error}</p>}
          <div className="wk-dialog-actions">
            <Button variant="quiet" disabled={busy} onClick={() => {
              setRemoveTarget(null);
              queueMicrotask(() => removalOrigin.current?.focus());
            }}>Cancel</Button>
            <Button variant="danger" disabled={busy} onClick={() => void remove()}>
              {busy ? "Removing…" : "Remove collaborator"}
            </Button>
          </div>
        </Dialog>
      )}
      {board.role === 1 && (
        <form className="share-form" onSubmit={submit}>
          <label>
            Registered guest email
            <input
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              required
            />
          </label>
          <label className="check-label">
            <input
              type="checkbox"
              checked={edit}
              onChange={(e) => setEdit(e.target.checked)}
            />{" "}
            Allow editing
          </label>
          <Button type="submit" disabled={busy || !email.trim()}>
            Add or update guest
          </Button>
        </form>
      )}
      {error && (
        <p className="form-error" role="alert">
          {error}
        </p>
      )}
    </aside>
  );
}
