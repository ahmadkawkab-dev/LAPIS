import type { NoteDto } from "../../../api";

export type EditorPresentation = "desktop" | "tablet" | "mobile";

export type EditorNoteSnapshot = {
  id: string;
  boardId: string;
  title: string;
  content: string;
  version: number;
};

export type NoteDraft = {
  noteId: string;
  boardId: string;
  title: string;
  content: string;
  baseVersion: number;
  isDirty: true;
  updatedAt: number;
  recovery: "current" | "stale";
  serverVersion?: number;
};

export type StoredNoteDraft = Pick<
  NoteDraft,
  "noteId" | "boardId" | "title" | "content" | "baseVersion" | "updatedAt"
>;

export type EditorNavigationState = {
  selectedNoteId: string | null;
  editingNoteId: string | null;
  inspectorNoteId: string | null;
  presentation: EditorPresentation;
  focusReturnNoteId: string | null;
};

export type CreationDraft = {
  note: NoteDto;
  status: "draft" | "saving" | "failed";
};

export type EditorState = {
  creations: Record<string, CreationDraft>;
  navigation: EditorNavigationState;
  drafts: Record<string, NoteDraft>;
};

export function createEditorState(
  presentation: EditorPresentation = "desktop",
): EditorState {
  return {
    navigation: {
      selectedNoteId: null,
      editingNoteId: null,
      inspectorNoteId: null,
      presentation,
      focusReturnNoteId: null,
    },
    drafts: {},
    creations: {},
  };
}

export function selectEditorNote(state: EditorState, noteId: string | null): EditorState {
  if (state.navigation.selectedNoteId === noteId) return state;
  return {
    ...state,
    navigation: { ...state.navigation, selectedNoteId: noteId },
  };
}

export function startEditorEditing(state: EditorState, noteId: string): EditorState {
  if (
    state.navigation.editingNoteId === noteId &&
    state.navigation.selectedNoteId === noteId
  ) return state;
  return {
    ...state,
    navigation: {
      ...state.navigation,
      selectedNoteId: noteId,
      editingNoteId: noteId,
    },
  };
}

export function stopEditorEditing(state: EditorState, noteId?: string): EditorState {
  if (!state.navigation.editingNoteId) return state;
  if (noteId && state.navigation.editingNoteId !== noteId) return state;
  return {
    ...state,
    navigation: { ...state.navigation, editingNoteId: null },
  };
}

export function openEditorInspector(
  state: EditorState,
  noteId: string,
): EditorState {
  return {
    ...state,
    navigation: {
      ...state.navigation,
      selectedNoteId: noteId,
      inspectorNoteId: noteId,
      focusReturnNoteId: noteId,
    },
  };
}

export function closeEditorInspector(state: EditorState): EditorState {
  if (!state.navigation.inspectorNoteId) return state;
  return {
    ...state,
    navigation: { ...state.navigation, inspectorNoteId: null },
  };
}

export function setEditorPresentation(
  state: EditorState,
  presentation: EditorPresentation,
): EditorState {
  if (state.navigation.presentation === presentation) return state;
  return {
    ...state,
    navigation: { ...state.navigation, presentation },
  };
}

export function updateNoteDraft(
  state: EditorState,
  note: EditorNoteSnapshot,
  changes: Partial<Pick<NoteDraft, "title" | "content">>,
  updatedAt: number,
): EditorState {
  const current = state.drafts[note.id];
  const title = changes.title ?? current?.title ?? note.title;
  const content = changes.content ?? current?.content ?? note.content;
  if (title === note.title && content === note.content) {
    if (!current) return state;
    const drafts = { ...state.drafts };
    delete drafts[note.id];
    return { ...state, drafts };
  }

  const baseVersion = current?.baseVersion ?? note.version;
  const draft: NoteDraft = {
    noteId: note.id,
    boardId: note.boardId,
    title,
    content,
    baseVersion,
    isDirty: true,
    updatedAt,
    recovery: baseVersion === note.version ? "current" : "stale",
    ...(baseVersion === note.version ? {} : { serverVersion: note.version }),
  };
  return {
    ...state,
    drafts: { ...state.drafts, [note.id]: draft },
  };
}

export function hydrateEditorDrafts(
  state: EditorState,
  stored: readonly StoredNoteDraft[],
  notes: readonly EditorNoteSnapshot[],
): EditorState {
  const byId = new Map(notes.map((note) => [note.id, note]));
  const drafts = { ...state.drafts };
  for (const candidate of stored) {
    const note = byId.get(candidate.noteId);
    if (!note || note.boardId !== candidate.boardId) continue;
    if (candidate.title === note.title && candidate.content === note.content) {
      delete drafts[note.id];
      continue;
    }
    drafts[note.id] = {
      ...candidate,
      isDirty: true,
      recovery: candidate.baseVersion === note.version ? "current" : "stale",
      ...(candidate.baseVersion === note.version
        ? {}
        : { serverVersion: note.version }),
    };
  }
  return { ...state, drafts };
}

export function reconcileEditorAuthoritative(
  state: EditorState,
  note: EditorNoteSnapshot,
): EditorState {
  const draft = state.drafts[note.id];
  if (!draft || draft.baseVersion === note.version) return state;
  const next = {
    ...draft,
    recovery: "stale" as const,
    serverVersion: note.version,
  };
  if (draft.recovery === next.recovery && draft.serverVersion === next.serverVersion)
    return state;
  return { ...state, drafts: { ...state.drafts, [note.id]: next } };
}

export function markEditorConflict(
  state: EditorState,
  noteId: string,
  serverVersion?: number,
): EditorState {
  const draft = state.drafts[noteId];
  if (!draft) return state;
  return {
    ...state,
    drafts: {
      ...state.drafts,
      [noteId]: {
        ...draft,
        recovery: "stale",
        ...(serverVersion === undefined ? {} : { serverVersion }),
      },
    },
  };
}

export function reconcileEditorSaved(
  state: EditorState,
  note: EditorNoteSnapshot,
): EditorState {
  const draft = state.drafts[note.id];
  if (!draft) return state;
  const drafts = { ...state.drafts };
  if (draft.title === note.title && draft.content === note.content) {
    delete drafts[note.id];
  } else {
    drafts[note.id] = {
      ...draft,
      baseVersion: note.version,
      recovery: "current",
      serverVersion: undefined,
    };
  }
  return { ...state, drafts };
}

export function removeNoteEditorState(state: EditorState, noteId: string): EditorState {
  const drafts = { ...state.drafts };
  delete drafts[noteId];
  const navigation = state.navigation;
  const creations = { ...state.creations };
  delete creations[noteId];
  return {
    creations,
    drafts,
    navigation: {
      ...navigation,
      selectedNoteId: navigation.selectedNoteId === noteId ? null : navigation.selectedNoteId,
      editingNoteId: navigation.editingNoteId === noteId ? null : navigation.editingNoteId,
      inspectorNoteId: navigation.inspectorNoteId === noteId ? null : navigation.inspectorNoteId,
      focusReturnNoteId: navigation.focusReturnNoteId === noteId ? null : navigation.focusReturnNoteId,
    },
  };
}

export function clearEditorBoardState(state: EditorState): EditorState {
  return createEditorState(state.navigation.presentation);
}
