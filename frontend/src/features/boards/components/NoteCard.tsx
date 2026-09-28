import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type CSSProperties,
  type KeyboardEvent as ReactKeyboardEvent,
  type PointerEvent as Pointer,
} from "react";
import { GripVertical, ListChecks, Plus, Settings2, StickyNote, X } from "lucide-react";
import type { ConnectionSide, NoteDto } from "../../../api";
import { Avatar } from "../../../components/ui/Avatar";
import { IconButton } from "../../../components/ui/Button";
import { collaboratorStyle } from "../collaboratorIdentity";
import { screenDeltaToWorld, type BoardPoint } from "../boardZoom";
import { connectionSides } from "../connectionGeometry";
import { beginWorldDrag, worldDragPosition, type WorldDragAnchor } from "../boardNavigation";
import type { BoardViewportController } from "../hooks/useBoardViewport";
import type { CreationDraft } from "../editor/editorState";
import { isDragGesture } from "../gesture";
import { InlineNoteText } from "./InlineNoteText";
import { NoteEditingIndicator, type EditingViewer } from "../NoteEditingIndicator";
import { noteAppearanceStyle } from "../noteAppearance";
import { clampDimension, noteDimensionBounds } from "../noteDimensions";
import { useNoteDraft } from "../editor/EditorStateProvider";
import type { NoteGeometryOperation } from "../../../realtime/events";
import type { RemoteGeometryPresentation, VisualPatch } from "../boardTypes";

export function NoteCard({
  note,
  creation,
  finishCreation,
  viewport,
  items,
  selected,
  editable,
  select,
  toggle,
  addItem,
  editTitle,
  editContent,
  editItem,
  removeItem,
  openProperties,
  autoEditTitle,
  preview,
  cancelPreview,
  commitVisual,
  broadcastGeometry,
  endGeometry,
  connectStart,
  connectMove,
  connectEnd,
  connectCancel,
  targetHighlighted,
  targetSide,
  connectionReach,
  remoteGeometry,
  editors,
  editingChanged,
}: {
  note: NoteDto;
  creation?: CreationDraft;
  finishCreation: () => void;
  viewport: BoardViewportController;
  items: NoteDto[];
  selected: boolean;
  editable: boolean;
  select: () => void;
  toggle: (n: NoteDto) => void;
  addItem: (title: string) => Promise<void>;
  editTitle: (title: string) => Promise<void>;
  editContent: (content: string) => Promise<void>;
  editItem: (item: NoteDto, title: string) => Promise<void>;
  removeItem: (item: NoteDto) => void;
  openProperties: (origin: HTMLElement) => void;
  autoEditTitle: boolean;
  preview: (id: string, patch: VisualPatch) => void;
  cancelPreview: (id: string, patch: VisualPatch) => void;
  commitVisual: (id: string, patch: VisualPatch) => Promise<void>;
  broadcastGeometry: (
    noteId: string,
    baseVersion: number,
    operation: NoteGeometryOperation,
    patch: VisualPatch,
  ) => number;
  endGeometry: (noteId: string, sequence: number) => void;
  connectStart: (id: string, side: ConnectionSide, event: Pointer<HTMLButtonElement>) => void;
  connectMove: (event: Pointer<HTMLButtonElement>) => void;
  connectEnd: (event: Pointer<HTMLButtonElement>) => void;
  connectCancel: () => void;
  targetHighlighted: boolean;
  targetSide: ConnectionSide | null;
  connectionReach: Record<ConnectionSide, number>;
  remoteGeometry?: RemoteGeometryPresentation;
  editors: EditingViewer[];
  editingChanged: (noteId: string, active: boolean) => void;
}) {
  const drag = useRef<{
    px: number; py: number; pointer: BoardPoint; anchor: WorldDragAnchor; moved: boolean; last: VisualPatch; onExit: (event: Event) => void;
  } | null>(null);
  const resize = useRef<{
    px: number; py: number; scale: number; width: number; height: number; last: VisualPatch;
  } | null>(null);
  const [interaction, setInteraction] = useState<"dragging" | "resizing" | null>(null);
  const suppressClick = useRef(false);
  const keyboardPosition = useRef({
    x: note.positionX ?? 0,
    y: note.positionY ?? 0,
  });
  const networkPreview = useRef<{
    lastSentAt: number;
    lastSequence: number;
    timer: ReturnType<typeof setTimeout> | null;
    pending: { operation: NoteGeometryOperation; patch: VisualPatch } | null;
  }>({ lastSentAt: 0, lastSequence: 0, timer: null, pending: null });
  const [addingItem, setAddingItem] = useState(false);
  const [itemSaving, setItemSaving] = useState(false);
  const [itemDraft, setItemDraft] = useState("");
  const [confirmRemove, setConfirmRemove] = useState<string | null>(null);
  const textDraft = useNoteDraft(note.id);
  const addingDone = useRef(false);
  useEffect(() => {
    keyboardPosition.current = {
      x: note.positionX ?? 0,
      y: note.positionY ?? 0,
    };
  }, [note.positionX, note.positionY]);
  function emitGeometry(operation: NoteGeometryOperation, patch: VisualPatch) {
    networkPreview.current.lastSentAt = performance.now();
    networkPreview.current.lastSequence = broadcastGeometry(
      note.id,
      note.version,
      operation,
      patch,
    );
  }
  function scheduleGeometry(operation: NoteGeometryOperation, patch: VisualPatch) {
    const state = networkPreview.current;
    const remaining = 75 - (performance.now() - state.lastSentAt);
    if (remaining <= 0) {
      if (state.timer) clearTimeout(state.timer);
      state.timer = null;
      state.pending = null;
      emitGeometry(operation, patch);
      return;
    }
    state.pending = { operation, patch };
    if (state.timer) return;
    state.timer = setTimeout(() => {
      const pending = networkPreview.current.pending;
      networkPreview.current.timer = null;
      networkPreview.current.pending = null;
      if (pending) emitGeometry(pending.operation, pending.patch);
    }, remaining);
  }
  function flushGeometry(operation: NoteGeometryOperation, patch: VisualPatch) {
    const state = networkPreview.current;
    if (state.timer) clearTimeout(state.timer);
    state.timer = null;
    state.pending = null;
    emitGeometry(operation, patch);
    return state.lastSequence;
  }
  const cancelGeometry = useCallback(() => {
    const state = networkPreview.current;
    if (state.timer) clearTimeout(state.timer);
    state.timer = null;
    state.pending = null;
    if (state.lastSequence > 0) endGeometry(note.id, state.lastSequence);
    state.lastSequence = 0;
    state.lastSentAt = 0;
  }, [endGeometry, note.id]);
  function completeGeometry(sequence: number) {
    endGeometry(note.id, sequence);
    if (networkPreview.current.lastSequence !== sequence) return;
    networkPreview.current.lastSequence = 0;
    networkPreview.current.lastSentAt = 0;
  }
  useEffect(() => () => cancelGeometry(), [cancelGeometry]);
  useEffect(() => () => {
    const active = drag.current;
    if (!active) return;
    viewport.stopEdgePan();
    window.removeEventListener("blur", active.onExit);
    document.removeEventListener("visibilitychange", active.onExit);
  }, [viewport]);
  function submitItem() {
    if (addingDone.current) return;
    addingDone.current = true;
    const title = itemDraft.trim();
    if (!title) { setAddingItem(false); setItemDraft(""); return; }
    setItemSaving(true);
    void Promise.resolve(addItem(title)).then(() => {
      setAddingItem(false); setItemDraft("");
      setItemSaving(false);
    }, () => {
      addingDone.current = false;
      setItemSaving(false);
      setAddingItem(true); // Failed POST keeps the item text available for retry.
    });
  }
  function down(e: Pointer<HTMLDivElement>) {
    if (!editable || e.button !== 0 || drag.current) return;
    if ((e.target as HTMLElement).closest("input, textarea, button, select, .editable-text, [contenteditable='true']")) return;
    const pointer = { x: e.clientX, y: e.clientY };
    const onExit = (event: Event) => { if (event.type === "blur" || document.hidden) cancelDrag(); };
    drag.current = {
      px: e.clientX, py: e.clientY, pointer,
      anchor: beginWorldDrag({ x: note.positionX ?? 0, y: note.positionY ?? 0 }, viewport.clientToWorld(pointer)),
      moved: false, last: {}, onExit,
    };
    window.addEventListener("blur", onExit);
    document.addEventListener("visibilitychange", onExit);
    suppressClick.current = false;
    cancelGeometry();
    e.currentTarget.setPointerCapture(e.pointerId);
  }
  function updateDragPreview() {
    const active = drag.current;
    if (!active?.moved) return;
    const position = worldDragPosition(active.anchor, viewport.clientToWorld(active.pointer));
    active.last = { positionX: position.x, positionY: position.y };
    preview(note.id, active.last);
    scheduleGeometry(0, active.last);
  }
  function move(e: Pointer<HTMLDivElement>) {
    const active = drag.current;
    if (!active) return;
    if (!(e.buttons & 1)) return up(e);
    active.pointer = { x: e.clientX, y: e.clientY };
    if (!active.moved && isDragGesture(active.px, active.py, e.clientX, e.clientY)) {
      active.moved = true;
      select();
      setInteraction("dragging");
      viewport.startEdgePan(active.pointer, updateDragPreview);
    } else if (active.moved) viewport.updateEdgePan(active.pointer);
    updateDragPreview();
  }
  function up(e: Pointer<HTMLDivElement>) {
    const active = drag.current;
    if (!active) return;
    viewport.stopEdgePan();
    window.removeEventListener("blur", active.onExit);
    document.removeEventListener("visibilitychange", active.onExit);
    drag.current = null;
    setInteraction(null);
    if (active.moved || isDragGesture(active.px, active.py, e.clientX, e.clientY)) {
      const position = worldDragPosition(active.anchor, viewport.clientToWorld({ x: e.clientX, y: e.clientY }));
      const final = { positionX: position.x, positionY: position.y };
      suppressClick.current = true;
      setTimeout(() => { suppressClick.current = false; }, 0);
      preview(note.id, final);
      const sequence = flushGeometry(0, final);
      void commitVisual(note.id, final).finally(() => completeGeometry(sequence));
    } else cancelGeometry();
  }
  function cancelDrag() {
    const active = drag.current;
    if (!active) return;
    viewport.stopEdgePan();
    window.removeEventListener("blur", active.onExit);
    document.removeEventListener("visibilitychange", active.onExit);
    cancelPreview(note.id, active.last);
    drag.current = null;
    setInteraction(null);
    cancelGeometry();
  }
  function resizeMove(e: Pointer<HTMLButtonElement>) {
    if (!resize.current) return;
    if (!(e.buttons & 1)) return resizeUp(e);
    const nextWidth = resize.current.width + screenDeltaToWorld({ x: e.clientX - resize.current.px, y: 0 }, resize.current.scale).x;
    const bounds = noteDimensionBounds(note, items, nextWidth);
    resize.current.last = {
      width: clampDimension(nextWidth, bounds.minWidth, bounds.maxWidth),
      height: clampDimension(resize.current.height + screenDeltaToWorld({ x: 0, y: e.clientY - resize.current.py }, resize.current.scale).y, bounds.minHeight, bounds.maxHeight),
    };
    preview(note.id, resize.current.last);
    scheduleGeometry(1, resize.current.last);
  }
  function resizeUp(e: Pointer<HTMLButtonElement>) {
    if (!resize.current) return;
    const active = resize.current;
    resize.current = null;
    setInteraction(null);
    const nextWidth = active.width + screenDeltaToWorld({ x: e.clientX - active.px, y: 0 }, active.scale).x;
    const bounds = noteDimensionBounds(note, items, nextWidth);
    const final = {
      width: clampDimension(nextWidth, bounds.minWidth, bounds.maxWidth),
      height: clampDimension(active.height + screenDeltaToWorld({ x: 0, y: e.clientY - active.py }, active.scale).y, bounds.minHeight, bounds.maxHeight),
    };
    if (final.width !== active.width || final.height !== active.height) {
      preview(note.id, final);
      const sequence = flushGeometry(1, final);
      void commitVisual(note.id, final).finally(() => completeGeometry(sequence));
    } else {
      cancelPreview(note.id, active.last);
      cancelGeometry();
    }
  }
  function cancelResize() {
    if (!resize.current) return;
    cancelPreview(note.id, resize.current.last);
    resize.current = null;
    setInteraction(null);
    cancelGeometry();
  }
  function keyboardMove(event: ReactKeyboardEvent<HTMLDivElement>) {
    if (event.key === "Escape" && creation) { event.preventDefault(); finishCreation(); return; }
    if (event.target !== event.currentTarget) return;
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      select();
      return;
    }
    if (!editable || !event.key.startsWith("Arrow")) return;
    const distance = event.shiftKey ? 32 : 8;
    const delta = {
      ArrowLeft: { x: -distance, y: 0 },
      ArrowRight: { x: distance, y: 0 },
      ArrowUp: { x: 0, y: -distance },
      ArrowDown: { x: 0, y: distance },
    }[event.key];
    if (!delta) return;
    event.preventDefault();
    select();
    keyboardPosition.current = {
      x: keyboardPosition.current.x + delta.x,
      y: keyboardPosition.current.y + delta.y,
    };
    const patch = {
      positionX: keyboardPosition.current.x,
      positionY: keyboardPosition.current.y,
    };
    preview(note.id, patch);
    void commitVisual(note.id, patch);
  }
  return (
    <div
      data-note-id={note.id}
      role="group"
      className={`sticky-note ${selected ? "selected" : ""} ${note.kind === 1 ? "task-note" : ""} ${remoteGeometry ? "remote-geometry" : ""} ${targetHighlighted ? "is-connection-target" : ""} ${interaction ? `is-${interaction}` : ""} ${creation ? `is-${creation.status}` : ""}`}
      style={{
        ...noteAppearanceStyle(note.color),
        ...(remoteGeometry ? collaboratorStyle(remoteGeometry.userId) : {}),
        left: note.positionX ?? 0,
        top: note.positionY ?? 0,
        width: note.width,
        height: note.height,
        zIndex: `calc(var(--layer-notes) + ${note.zIndex})`,
      }}
      onBlur={(event) => {
        if (creation && !event.currentTarget.contains(event.relatedTarget as Node | null)) finishCreation();
      }}
      tabIndex={0}
      aria-label={`${note.kind === 1 ? "Task list" : "Note"}: ${note.title}. ${editable ? "Use arrow keys to move; hold Shift for larger steps." : ""}`.trim()}
      aria-keyshortcuts={editable ? "ArrowUp ArrowDown ArrowLeft ArrowRight" : undefined}
      onKeyDown={keyboardMove}
      onClickCapture={(event) => {
        if (!suppressClick.current) return;
        event.preventDefault();
        event.stopPropagation();
        suppressClick.current = false;
      }}
      onFocus={(event) => {
        if (event.target === event.currentTarget) select();
      }}
      onClick={() => {
        if (suppressClick.current) {
          suppressClick.current = false;
          return;
        }
        select();
      }}
    >
      {remoteGeometry && (
        <div className="remote-geometry-outline" aria-hidden="true">
          <Avatar identity={{ displayName: remoteGeometry.label, username: remoteGeometry.username, profileImageUrl: remoteGeometry.profileImageUrl }} size="small" style={collaboratorStyle(remoteGeometry.userId)} />
          <small>{remoteGeometry.label} is {remoteGeometry.operation === 0 ? "moving" : "resizing"}</small>
        </div>
      )}
      <NoteEditingIndicator editors={editors} />
      <div
        className={`note-head ${editable ? "drag-handle" : ""}`}
        onPointerDown={down}
        onPointerMove={move}
        onPointerUp={up}
        onPointerCancel={cancelDrag}
        onLostPointerCapture={cancelDrag}
      >
        {editable && (
          <span className="note-drag-grip" title="Drag note" aria-hidden="true">
            <GripVertical size={15} />
          </span>
        )}
        <span className="note-type">
          {note.kind === 1 ? (
            <ListChecks size={13} />
          ) : (
            <StickyNote size={13} />
          )}
        </span>
        <strong>
          <InlineNoteText
            note={note}
            field="title"
            label={note.kind === 1 ? "Task title" : "Note title"}
            editable={editable}
            autoEdit={autoEditTitle}
            activation="click"
            onSave={editTitle}
            onEditingChange={(active) => editingChanged(note.id, active)}
          />
        </strong>
        <IconButton
          disabled={!!creation}
          label={`Open properties for ${note.title || "Untitled"}`}
          className="note-properties"
          onClick={(event) => {
            event.stopPropagation();
            openProperties(event.currentTarget);
          }}
        >
          <Settings2 size={16} aria-hidden="true" />
        </IconButton>
      </div>
      {note.kind === 1 ? (
        <div className="task-content">
          <div className="checklist" data-board-scroll>
          {!items.length && !addingItem && (!creation || creation.status === "draft") && <p className="task-empty">Add your first item to get started.</p>}
          {items.map((item) => (
            <div key={item.id} className={`checklist-row ${item.isCompleted ? "checked" : ""}`}>
              <label className="wk-checkbox-target" onClick={(event) => event.stopPropagation()}>
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
                onSave={(title) => editItem(item, title)}
                onEditingChange={(active) => editingChanged(note.id, active)}
              />
              {editable && (confirmRemove === item.id ? (
                <span className="checklist-confirm">
                  <button className="task-action" onClick={(e) => {
                    e.stopPropagation();
                    setConfirmRemove(null);
                  }}>Cancel</button>
                  <button className="task-action danger" onClick={(e) => {
                    e.stopPropagation();
                    setConfirmRemove(null);
                    removeItem(item);
                  }}>Confirm delete</button>
                </span>
              ) : (
                <button className="task-action" aria-label={`Delete ${item.title}`} onClick={(e) => {
                  e.stopPropagation();
                  setConfirmRemove(item.id);
                }}><X size={16} aria-hidden="true" /></button>
              ))}
            </div>
          ))}
          </div>
          <div className="task-entry">
          {(editable || itemSaving) && (
            addingItem ? (
              <input
                className="inline-edit new-item-input"
                disabled={itemSaving}
                aria-busy={itemSaving}
                aria-label="New checklist item"
                placeholder="Type checklist item…"
                autoFocus
                maxLength={200}
                value={itemDraft}
                onChange={(e) => setItemDraft(e.target.value)}
                onFocus={() => editingChanged(note.id, true)}
                onBlur={() => {
                  editingChanged(note.id, false);
                  submitItem();
                }}
                onKeyDown={(e) => {
                  if (e.key === "Enter") {
                    e.preventDefault();
                    e.currentTarget.blur();
                  } else if (e.key === "Escape") {
                    addingDone.current = true;
                    editingChanged(note.id, false);
                    setAddingItem(false);
                    setItemDraft("");
                  }
                }}
                onClick={(e) => e.stopPropagation()}
              />
            ) : (
              <button
                className="add-task"
                onClick={(e) => {
                  e.stopPropagation();
                  addingDone.current = false;
                  setAddingItem(true);
                }}
              >
                <Plus size={13} /> Add item
              </button>
            )
          )}
          </div>
          {!!items.length && <footer className="task-footer">
          <div className="progress-line">
            <span
              style={{
                width: `${items.length ? (items.filter((item) => item.isCompleted).length / items.length) * 100 : 0}%`,
              }}
            />
          </div>
          <small>
            {items.filter((item) => item.isCompleted).length} / {items.length}{" "}
            complete
          </small>
          </footer>}
        </div>
      ) : (
        <div className="note-body" data-board-scroll>
          <InlineNoteText
            note={note}
            field="content"
            label="Note body"
            editable={editable}
            activation="click"
            onSave={editContent}
            onEditingChange={(active) => editingChanged(note.id, active)}
          />
        </div>
      )}
      {creation && creation.status !== "draft" && (
        <div className="note-draft-state" role="status">
          {creation.status === "saving" ? "Saving…" : creation.status === "failed" ? "Save failed — draft kept" : "New draft"}
          {creation.status === "failed" && <button type="button" className="task-action" onClick={finishCreation}>Retry save</button>}
        </div>
      )}
      {textDraft && (
        <span className={`note-draft-state ${textDraft.recovery === "stale" ? "is-stale" : ""}`}>
          {textDraft.recovery === "stale" ? "Draft needs review" : "Unsaved draft"}
        </span>
      )}
      {editable && (
        <>
          {!creation && connectionSides.map((side) => <button key={side}
            className={`connection-handle connection-source connection-anchor-${side} ${targetHighlighted && targetSide === side ? "highlighted" : ""}`}
            data-connection-side={side}
            style={{ "--connection-hit-reach": `${connectionReach[side]}px` } as CSSProperties}
            type="button"
            aria-label={`Connect from ${side} of ${note.title || "Untitled"}`}
            title={`Drag from ${side} to another card`}
            onPointerDown={(event) => {
              event.stopPropagation();
              if (event.button !== 0) return;
              event.currentTarget.setPointerCapture(event.pointerId);
              connectStart(note.id, side, event);
            }}
            onPointerMove={connectMove}
            onPointerUp={connectEnd}
            onPointerCancel={connectCancel}
            onLostPointerCapture={connectCancel}
            onClick={(event) => event.stopPropagation()}
          />)}
          <button
            className="resize-handle"
            type="button"
            aria-label={`Resize ${note.title}`}
            title="Drag to resize"
            onPointerDown={(event) => {
              event.stopPropagation();
              if (event.button !== 0) return;
              setInteraction("resizing");
              resize.current = {
                px: event.clientX,
                py: event.clientY,
                scale: viewport.getScale(),
                width: note.width,
                height: note.height,
                last: {},
              };
              cancelGeometry();
              event.currentTarget.setPointerCapture(event.pointerId);
            }}
            onPointerMove={resizeMove}
            onPointerUp={resizeUp}
            onPointerCancel={cancelResize}
            onClick={(event) => event.stopPropagation()}
          />
        </>
      )}

    </div>
  );
}
