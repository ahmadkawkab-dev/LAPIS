import { useEffect, useRef, useState } from "react";
import type { NoteDto } from "../../../api";
import { useEditorActions, useNoteDraft } from "../editor/EditorStateProvider";

export function InlineNoteText({
  note,
  field,
  label,
  editable,
  autoEdit = false,
  activation = "click",
  onSave,
  onEditingChange,
}: {
  note: NoteDto;
  field: "title" | "content";
  label: string;
  editable: boolean;
  autoEdit?: boolean;
  activation?: "click" | "double";
  onSave: (value: string) => Promise<void>;
  onEditingChange?: (editing: boolean) => void;
}) {
  const [editing, setEditing] = useState(autoEdit);
  const draft = useNoteDraft(note.id);
  const editor = useEditorActions();
  const value = draft?.[field] ?? note[field];
  const skipSave = useRef(false);
  const entryValue = useRef(value);
  const finishing = useRef(false);
  const [saveFailed, setSaveFailed] = useState(false);
  const editableRef = useRef(editable);
  editableRef.current = editable;
  const editingAnnounced = useRef(false);
  const editingChangeRef = useRef(onEditingChange);
  useEffect(() => {
    editingChangeRef.current = onEditingChange;
  }, [onEditingChange]);
  useEffect(() => () => {
    if (editingAnnounced.current) editingChangeRef.current?.(false);
  }, []);
  function announceEditing(active: boolean) {
    if (editingAnnounced.current === active) return;
    editingAnnounced.current = active;
    editingChangeRef.current?.(active);
  }
  async function finish(nextValue: string) {
    if (finishing.current) return;
    finishing.current = true;
    announceEditing(false);
    editor.stopEditing(note.id);
    setEditing(false);
    try {
      if (skipSave.current) {
        skipSave.current = false;
        if (editor.getCreation(note.id)) {
          // Exiting initial creation preserves useful work; only an empty card is canceled.
          if (editableRef.current && nextValue.trim()) await onSave(nextValue);
        } else editor.updateDraft(note, { [field]: entryValue.current });
        return;
      }
      if (!editableRef.current) return;
      const creation = editor.getCreation(note.id);
      const next = field === "title" ? nextValue.trim() || (creation ? "" : "Untitled") : nextValue;
      editor.updateDraft(note, { [field]: next });
      // Empty local titles remain available while moving into body/item editing.
      // Leaving the card resolves the whole creation, not just this field.
      if (creation && !next.trim()) return;
      if (creation || next !== note[field]) await onSave(next);
      setSaveFailed(false);
    } catch {
      // The caller reports the failure. Keep the editor draft visible and reopenable.
      setSaveFailed(true);
    } finally { finishing.current = false; }
  }
  function beginEditing() {
    entryValue.current = value;
    setEditing(true);
  }
  if (editing && editable) {
    const change = (event: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) =>
      editor.updateDraft(note, { [field]: event.target.value });
    const focus = () => {
      editor.startEditing(note.id);
      announceEditing(true);
    };
    if (field === "content") return (
      <textarea
        className="inline-edit inline-edit--content"
        aria-label={label}
        autoFocus
        rows={4}
        value={value}
        onFocus={focus}
        onChange={change}
        onBlur={(event) => void finish(event.currentTarget.value)}
        onClick={(event) => event.stopPropagation()}
        onKeyDown={(event) => {
          if (event.key === "Escape") {
            event.preventDefault();
            event.stopPropagation();
            skipSave.current = true;
            event.currentTarget.blur();
          } else if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
            event.preventDefault();
            event.currentTarget.blur();
          }
        }}
      />
    );
    return (
      <input
        className="inline-edit inline-edit--title"
        aria-label={label}
        autoFocus
        maxLength={200}
        value={value}
        onFocus={focus}
        onChange={change}
        onBlur={(event) => void finish(event.currentTarget.value)}
        onClick={(event) => event.stopPropagation()}
        onKeyDown={(event) => {
          if (event.key === "Enter") {
            event.preventDefault();
            event.currentTarget.blur();
          } else if (event.key === "Escape") {
            event.preventDefault();
            event.stopPropagation();
            skipSave.current = true;
            event.currentTarget.blur();
          }
        }}
      />
    );
  }
  return (
    <span
      aria-label={label}
      data-save-failed={saveFailed || undefined}
      className={`${editable ? "editable-text" : ""} ${field === "content" ? "note-body-text" : "note-title-text"} ${!value ? "is-placeholder" : ""}`.trim()}
      role={editable ? "button" : undefined}
      tabIndex={editable ? 0 : undefined}
      title={editable ? (activation === "double" ? "Double-click to edit" : "Click to edit") : undefined}
      onClick={editable && activation === "click" ? (event) => {
        event.stopPropagation();
        beginEditing();
      } : undefined}
      onDoubleClick={editable && activation === "double" ? (event) => {
        event.stopPropagation();
        beginEditing();
      } : undefined}
      onKeyDown={editable ? (event) => {
        if (event.key === "Enter" || event.key === " ") {
          event.preventDefault();
          beginEditing();
        }
      } : undefined}
    >
      {value || (field === "title" ? "Untitled" : editable ? "Add body text…" : "No body text") }
    </span>
  );
}
