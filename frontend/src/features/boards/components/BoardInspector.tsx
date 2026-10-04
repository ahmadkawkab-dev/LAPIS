import { useEffect, useState } from "react";
import { ArrowDown, ArrowLeft, ArrowRight, ArrowUp, X } from "lucide-react";
import type { NoteDto } from "../../../api";
import { Button, IconButton } from "../../../components/ui/Button";
import { notePigments } from "../noteAppearance";
import { clampDimension, noteDimensionBounds } from "../noteDimensions";
import { useNoteDraft } from "../editor/EditorStateProvider";
import type { VisualPatch } from "../boardTypes";
import { clampBoardPosition } from "../boardBounds";

export function PropertiesEditor({
  note,
  items,
  children,
  visualColor,
  visualWidth,
  visualHeight,
  editable,
  removeNote,
  deleted,
  preview,
  cancelPreview,
  commitVisual,
  changeColor,
  failed,
  notify,
}: {
  note: NoteDto;
  items: NoteDto[];
  children: React.ReactNode;
  visualColor: string;
  visualWidth: number;
  visualHeight: number;
  editable: boolean;
  removeNote: (id: string) => Promise<void>;
  deleted: (id: string) => void;
  preview: (id: string, patch: VisualPatch) => void;
  cancelPreview: (id: string, patch: VisualPatch) => void;
  commitVisual: (id: string, patch: VisualPatch) => Promise<void>;
  changeColor: (id: string, color: string) => void;
  failed: (id: string, cause: unknown) => void;
  notify: (m: string) => void;
}) {
  const textDraft = useNoteDraft(note.id);
  const sourceBounds = noteDimensionBounds(note, items);
  const displayedWidth = clampDimension(note.width, sourceBounds.minWidth, sourceBounds.maxWidth);
  const displayedBounds = noteDimensionBounds(note, items, displayedWidth);
  const displayedHeight = clampDimension(note.height, displayedBounds.minHeight, displayedBounds.maxHeight);
  const [width, setWidth] = useState(String(displayedWidth)),
    [height, setHeight] = useState(String(displayedHeight)),
    [busy, setBusy] = useState(false),
    [confirmDelete, setConfirmDelete] = useState(false);
  const [moving, setMoving] = useState(false);
  async function moveBy(x: number, y: number) {
    if (moving) return;
    const position = clampBoardPosition({ x: (note.positionX ?? 0) + x, y: (note.positionY ?? 0) + y },
      { width: visualWidth, height: visualHeight });
    setMoving(true);
    try { await commitVisual(note.id, { positionX: position.x, positionY: position.y }); }
    finally { setMoving(false); }
  }
  const candidateWidth = Number(width);
  const bounds = noteDimensionBounds(note, items, Number.isFinite(candidateWidth) && width ? candidateWidth : displayedWidth);
  useEffect(() => {
    setWidth(String(displayedWidth));
    setHeight(String(displayedHeight));
  }, [displayedWidth, displayedHeight]);
  async function remove() {
    setBusy(true);
    try {
      await removeNote(note.id);
      deleted(note.id);
      notify(note.kind === 1 ? "Task list deleted" : "Note deleted");
    } catch (cause) {
      failed(note.id, cause);
    } finally {
      setBusy(false);
    }
  }
  function setDimension(field: "width" | "height", value: string) {
    if (field === "width") setWidth(value);
    else setHeight(value);
    const number = Number(value);
    if (!value || !Number.isFinite(number)) return;
    const nextBounds = field === "width" ? sourceBounds : bounds;
    preview(note.id, { [field]: clampDimension(number,
      field === "width" ? nextBounds.minWidth : nextBounds.minHeight,
      field === "width" ? nextBounds.maxWidth : nextBounds.maxHeight) });
  }
  function finishDimension(field: "width" | "height") {
    const value = field === "width" ? width : height;
    const number = Number(value);
    if (!value || !Number.isFinite(number)) {
      cancelPreview(note.id, { [field]: field === "width" ? visualWidth : visualHeight });
      if (field === "width") setWidth(String(displayedWidth));
      else setHeight(String(displayedHeight));
      return;
    }
    const nextWidth = field === "width"
      ? clampDimension(number, sourceBounds.minWidth, sourceBounds.maxWidth)
      : clampDimension(Number(width) || displayedWidth, sourceBounds.minWidth, sourceBounds.maxWidth);
    const nextBounds = noteDimensionBounds(note, items, nextWidth);
    const nextHeight = field === "height"
      ? clampDimension(number, nextBounds.minHeight, nextBounds.maxHeight)
      : clampDimension(note.height, nextBounds.minHeight, nextBounds.maxHeight);
    const changes: VisualPatch = field === "width" ? { width: nextWidth } : { height: nextHeight };
    if (field === "width") {
      setWidth(String(nextWidth));
      if (nextHeight !== note.height) changes.height = nextHeight;
    } else setHeight(String(nextHeight));
    if (Object.entries(changes).some(([key, next]) => note[key as "width" | "height"] !== next))
      void commitVisual(note.id, changes);
  }
  return (
    <div className="note-editor">
      <div className="note-properties-intro">
        <span className="inspector-object-kind">{note.kind === 1 ? "Task list" : "Note"}</span>
        <p>
          {note.kind === 1
            ? "Edit the title, checklist items, and completion directly on the card."
            : "Edit the title and body directly on the card."}
        </p>
      </div>
      {textDraft && (
        <div className={`draft-recovery ${textDraft.recovery === "stale" ? "is-stale" : ""}`} role={textDraft.recovery === "stale" ? "alert" : "status"}>
          <strong>{textDraft.recovery === "stale" ? "Local draft retained" : "Unsaved text retained"}</strong>
          <p>
            {textDraft.recovery === "stale"
              ? "A newer saved version exists. Your local text remains on the card for review and retry."
              : "This draft is kept for this browser session until it saves successfully."}
          </p>
        </div>
      )}
      {editable && (
        <section className="inspector-section" aria-labelledby={`appearance-${note.id}`}>
          <h3 id={`appearance-${note.id}`}>Appearance</h3>
          <div className="note-edit-fields">
            <fieldset className="note-color-field">
              <legend>Color</legend>
              <div className="note-color-options">
                {notePigments.map((pigment) => (
                  <button
                    key={pigment.value}
                    className="note-color-option"
                    type="button"
                    aria-label={`Use ${pigment.name.toLowerCase()} note color`}
                    aria-pressed={visualColor.slice(0, 7).toUpperCase() === pigment.value}
                    style={{ backgroundColor: pigment.value }}
                    onClick={() => changeColor(note.id, pigment.value)}
                  />
                ))}
              </div>
            </fieldset>
            <div className="dimension-row">
              <label>
                Width
                <input
                  type="number"
                  min={bounds.minWidth}
                  max={bounds.maxWidth}
                  step={8}
                  inputMode="numeric"
                  value={width}
                  onChange={(e) => setDimension("width", e.target.value)}
                  onBlur={() => finishDimension("width")}
                />
              </label>
              <label>
                Height
                <input
                  type="number"
                  min={bounds.minHeight}
                  max={bounds.maxHeight}
                  step={8}
                  inputMode="numeric"
                  value={height}
                  onChange={(e) => setDimension("height", e.target.value)}
                  onBlur={() => finishDimension("height")}
                />
              </label>
            </div>
            <p className="dimension-help">Width {bounds.minWidth}–{bounds.maxWidth}px · Height {bounds.minHeight}–{bounds.maxHeight}px. The minimum adapts to the card's content.</p>
            <div className="note-move-controls" role="group" aria-label="Move card without dragging">
              <span>Move card</span>
              {[
                { label: 'Move left', x: -32, y: 0, Icon: ArrowLeft },
                { label: 'Move up', x: 0, y: -32, Icon: ArrowUp },
                { label: 'Move down', x: 0, y: 32, Icon: ArrowDown },
                { label: 'Move right', x: 32, y: 0, Icon: ArrowRight },
              ].map(({ label, x, y, Icon }) => <IconButton key={label} label={label} disabled={moving}
                onClick={() => void moveBy(x, y)}><Icon size={18} aria-hidden="true" /></IconButton>)}
            </div>
            <p className="dimension-help">Move 32 pixels at a time. You can also focus the card and use arrow keys.</p>
          </div>
        </section>
      )}
      {children}
      <section className="inspector-section inspector-details" aria-labelledby={`details-${note.id}`}>
        <h3 id={`details-${note.id}`}>Details</h3>
        <dl>
          <div><dt>Type</dt><dd>{note.kind === 1 ? "Board task list" : "Board note"}</dd></div>
          <div><dt>Created</dt><dd><time dateTime={note.createdAt}>{new Date(note.createdAt).toLocaleDateString()}</time></dd></div>
        </dl>
      </section>
      {editable && (
        <section className="inspector-section inspector-danger" aria-labelledby={`danger-${note.id}`}>
          <h3 id={`danger-${note.id}`}>Danger zone</h3>
          <div className="editor-actions">
            {confirmDelete ? (
              <>
                <Button variant="quiet" onClick={() => setConfirmDelete(false)}>Cancel</Button>
                <Button variant="danger" loading={busy} onClick={() => void remove()}>Confirm delete</Button>
              </>
            ) : (
              <Button variant="danger" loading={busy} onClick={() => setConfirmDelete(true)}>Delete</Button>
            )}
          </div>
        </section>
      )}
    </div>
  );
}

export function InspectorFrame({
  title,
  presentation,
  close,
  children,
}: {
  title: string;
  presentation: "desktop" | "tablet" | "mobile";
  close: () => void;
  children: React.ReactNode;
}) {
  return (
    <aside
      className={`editor-panel editor-panel--${presentation}`}
      aria-label={title}
      onKeyDown={(event) => {
        if (event.key === "Escape") {
          event.preventDefault();
          close();
        }
      }}
    >
      <div className="panel-head">
        <h2>{title}</h2>
        <IconButton label="Close properties" autoFocus onClick={close}>
          <X size={18} aria-hidden="true" />
        </IconButton>
      </div>
      <div className="editor-panel-content">{children}</div>
    </aside>
  );
}
