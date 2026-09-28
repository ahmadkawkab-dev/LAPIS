import type { NoteDto, PatchNote } from "../../../api";
import {
  clearEditorBoardState,
  closeEditorInspector,
  createEditorState,
  hydrateEditorDrafts,
  markEditorConflict,
  openEditorInspector,
  reconcileEditorAuthoritative,
  reconcileEditorSaved,
  removeNoteEditorState,
  selectEditorNote,
  setEditorPresentation,
  startEditorEditing,
  stopEditorEditing,
  updateNoteDraft,
  type CreationDraft,
  type EditorNavigationState,
  type EditorNoteSnapshot,
  type EditorPresentation,
  type EditorState,
  type NoteDraft,
  type StoredNoteDraft,
} from "./editorState.ts";
import type { EditorDraftStore } from "./draftStore.ts";

type Listener = () => void;

export class EditorStateController {
  private state: EditorState;
  private readonly listeners = new Set<Listener>();
  private readonly storedDrafts: StoredNoteDraft[];
  private readonly userId: string;
  private readonly boardId: string;
  private readonly store: EditorDraftStore;
  private hydrated = false;
  private creationRequests = new Map<string, Promise<NoteDto | null>>();

  constructor(
    userId: string,
    boardId: string,
    store: EditorDraftStore,
    presentation: EditorPresentation = "desktop",
  ) {
    this.userId = userId;
    this.boardId = boardId;
    this.store = store;
    this.state = createEditorState(presentation);
    this.storedDrafts = store.load(userId, boardId);
    this.state.creations = store.loadCreations(userId, boardId);
  }

  subscribe = (listener: Listener) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  getNavigation = (): EditorNavigationState => this.state.navigation;
  getDraft = (noteId: string): NoteDraft | null => this.state.drafts[noteId] ?? null;
  getCreations = (): Record<string, CreationDraft> => this.state.creations;
  getDrafts = (): Record<string, NoteDraft> => this.state.drafts;

  private replace(next: EditorState, persist = true) {
    if (next === this.state) return;
    this.state = next;
    if (persist) {
      this.store.save(this.userId, this.boardId, next.drafts);
      this.store.saveCreations(this.userId, this.boardId, next.creations);
    }
    for (const listener of this.listeners) listener();
  }

  hydrate(notes: readonly EditorNoteSnapshot[]) {
    if (!this.hydrated) {
      this.hydrated = true;
      this.replace(hydrateEditorDrafts(this.state, this.storedDrafts, notes));
      return;
    }
    let next = this.state;
    const noteIds = new Set(notes.map((note) => note.id));
    for (const noteId of Object.keys(next.drafts)) {
      if (!noteIds.has(noteId)) next = removeNoteEditorState(next, noteId);
    }
    for (const note of notes) next = reconcileEditorAuthoritative(next, note);
    this.replace(next);
  }

  select(noteId: string | null) {
    this.replace(selectEditorNote(this.state, noteId), false);
  }

  startEditing(noteId: string) {
    this.replace(startEditorEditing(this.state, noteId), false);
  }

  stopEditing(noteId?: string) {
    this.replace(stopEditorEditing(this.state, noteId), false);
  }

  openInspector(noteId: string) {
    this.replace(openEditorInspector(this.state, noteId), false);
  }

  closeInspector() {
    this.replace(closeEditorInspector(this.state), false);
  }

  setPresentation(presentation: EditorPresentation) {
    this.replace(setEditorPresentation(this.state, presentation), false);
  }

  updateDraft(
    note: EditorNoteSnapshot,
    changes: Partial<Pick<NoteDraft, "title" | "content">>,
    updatedAt = Date.now(),
  ) {
    if (this.state.creations[note.id]) {
      this.updateCreation(note.id, changes);
      return;
    }
    this.replace(updateNoteDraft(this.state, note, changes, updatedAt));
  }

  createNote(note: NoteDto) {
    this.replace({
      ...startEditorEditing(this.state, note.id),
      creations: { ...this.state.creations, [note.id]: { note, status: "draft" } },
    });
  }

  updateCreation(noteId: string, changes: PatchNote) {
    const current = this.state.creations[noteId];
    if (!current || current.status === "saving") return;
    this.replace({ ...this.state, creations: {
      ...this.state.creations, [noteId]: { note: { ...current.note, ...changes }, status: "draft" },
    } });
  }

  commitCreation(noteId: string, save: (note: NoteDto) => Promise<NoteDto>, force = false): Promise<NoteDto | null> {
    const pending = this.creationRequests.get(noteId);
    if (pending) return pending;
    const creation = this.state.creations[noteId];
    if (!creation) return Promise.resolve(null);
    const note = creation.note;
    if (!force && !note.title.trim() && !note.content.trim()) {
      this.removeNote(noteId);
      return Promise.resolve(null);
    }
    // Only one POST may own a draft, even when blur and Enter both finish editing.
    const request = Promise.resolve().then(() => save({ ...note, title: note.title.trim() || "Untitled" }))
      .then((saved) => {
        if (this.state.creations[noteId]) {
          const wasSelected = this.state.navigation.selectedNoteId === noteId;
          const next = removeNoteEditorState(this.state, noteId);
          this.replace(wasSelected ? selectEditorNote(next, saved.id) : next);
        }
        return saved;
      }, (error) => {
        if (this.state.creations[noteId]) this.replace({ ...this.state, creations: {
          ...this.state.creations, [noteId]: { note, status: "failed" },
        } });
        throw error;
      }).finally(() => this.creationRequests.delete(noteId));
    this.creationRequests.set(noteId, request);
    // Register before notifying subscribers so reentrant finish events share this request.
    this.replace({ ...this.state, creations: {
      ...this.state.creations, [noteId]: { note, status: "saving" },
    } });
    return request;
  }

  reconcileAuthoritative(note: EditorNoteSnapshot) {
    this.replace(reconcileEditorAuthoritative(this.state, note));
  }

  markConflict(noteId: string, serverVersion?: number) {
    this.replace(markEditorConflict(this.state, noteId, serverVersion));
  }

  reconcileSaved(note: EditorNoteSnapshot) {
    this.replace(reconcileEditorSaved(this.state, note));
  }

  removeNote(noteId: string) {
    this.replace(removeNoteEditorState(this.state, noteId));
  }

  clearBoard() {
    this.store.clearBoard(this.userId, this.boardId);
    this.replace(clearEditorBoardState(this.state), false);
  }
}
