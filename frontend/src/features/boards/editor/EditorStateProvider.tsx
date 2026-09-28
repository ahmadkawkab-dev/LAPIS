import type { NoteDto, PatchNote } from "../../../api";
import {
  createContext,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useSyncExternalStore,
  type ReactNode,
} from "react";
import { createEditorDraftStore } from "./draftStore.ts";
import { EditorStateController } from "./editorStateController.ts";
import type {
  EditorNavigationState,
  EditorNoteSnapshot,
  EditorPresentation,
  NoteDraft,
} from "./editorState.ts";

type EditorActions = {
  createNote: (note: NoteDto) => void;
  updateCreation: (noteId: string, changes: PatchNote) => void;
  getCreation: (noteId: string) => ReturnType<EditorStateController["getCreations"]>[string] | undefined;
  commitCreation: (noteId: string, save: (note: NoteDto) => Promise<NoteDto>, force?: boolean) => Promise<NoteDto | null>;
  hydrate: (notes: readonly EditorNoteSnapshot[]) => void;
  select: (noteId: string | null) => void;
  startEditing: (noteId: string) => void;
  stopEditing: (noteId?: string) => void;
  openInspector: (noteId: string, origin?: HTMLElement | null) => void;
  closeInspector: (restoreFocus?: boolean) => void;
  updateDraft: (
    note: EditorNoteSnapshot,
    changes: Partial<Pick<NoteDraft, "title" | "content">>,
  ) => void;
  reconcileAuthoritative: (note: EditorNoteSnapshot) => void;
  reconcileSaved: (note: EditorNoteSnapshot) => void;
  markConflict: (noteId: string, serverVersion?: number) => void;
  removeNote: (noteId: string) => void;
  clearBoard: () => void;
};

const ControllerContext = createContext<EditorStateController | null>(null);
const ActionsContext = createContext<EditorActions | null>(null);

function currentPresentation(): EditorPresentation {
  if (typeof window === "undefined") return "desktop";
  if (window.innerWidth <= 700) return "mobile";
  if (window.innerWidth <= 1100) return "tablet";
  return "desktop";
}

function sessionDraftStore() {
  try {
    return createEditorDraftStore(window.sessionStorage);
  } catch {
    return createEditorDraftStore();
  }
}

function focusNoteOrBoard(noteId: string | null) {
  const note = Array.from(document.querySelectorAll<HTMLElement>("[data-note-id]"))
    .find((candidate) => candidate.dataset.noteId === noteId);
  const target = note ?? document.querySelector<HTMLElement>(".canvas");
  target?.focus({ preventScroll: true });
}

export function EditorStateProvider({
  userId,
  boardId,
  children,
}: {
  userId: string;
  boardId: string;
  children: ReactNode;
}) {
  const controller = useMemo(() => new EditorStateController(
    userId,
    boardId,
    typeof window === "undefined" ? createEditorDraftStore() : sessionDraftStore(),
    currentPresentation(),
  ), [boardId, userId]);
  const focusOrigin = useRef<HTMLElement | null>(null);

  useEffect(() => {
    const update = () => controller.setPresentation(currentPresentation());
    window.addEventListener("resize", update, { passive: true });
    return () => window.removeEventListener("resize", update);
  }, [controller]);

  const actions = useMemo<EditorActions>(() => ({
    createNote: (note) => controller.createNote(note),
    updateCreation: (noteId, changes) => controller.updateCreation(noteId, changes),
    getCreation: (noteId) => controller.getCreations()[noteId],
    commitCreation: (noteId, save, force) => controller.commitCreation(noteId, save, force),
    hydrate: (notes) => controller.hydrate(notes),
    select: (noteId) => controller.select(noteId),
    startEditing: (noteId) => controller.startEditing(noteId),
    stopEditing: (noteId) => controller.stopEditing(noteId),
    openInspector: (noteId, origin) => {
      focusOrigin.current = origin ?? (document.activeElement as HTMLElement | null);
      controller.openInspector(noteId);
    },
    closeInspector: (restoreFocus = true) => {
      const returnNoteId = controller.getNavigation().focusReturnNoteId;
      const origin = focusOrigin.current;
      controller.closeInspector();
      focusOrigin.current = null;
      if (!restoreFocus) return;
      requestAnimationFrame(() => {
        if (origin?.isConnected) origin.focus({ preventScroll: true });
        else focusNoteOrBoard(returnNoteId);
      });
    },
    updateDraft: (note, changes) => controller.updateDraft(note, changes),
    reconcileAuthoritative: (note) => controller.reconcileAuthoritative(note),
    reconcileSaved: (note) => controller.reconcileSaved(note),
    markConflict: (noteId, serverVersion) => controller.markConflict(noteId, serverVersion),
    removeNote: (noteId) => {
      const navigation = controller.getNavigation();
      const affectsFocus = navigation.selectedNoteId === noteId ||
        navigation.inspectorNoteId === noteId ||
        navigation.editingNoteId === noteId ||
        navigation.focusReturnNoteId === noteId;
      const returnNoteId = navigation.focusReturnNoteId;
      const origin = focusOrigin.current;
      controller.removeNote(noteId);
      if (!affectsFocus) return;
      focusOrigin.current = null;
      requestAnimationFrame(() => {
        if (origin?.isConnected) origin.focus({ preventScroll: true });
        else focusNoteOrBoard(returnNoteId === noteId ? null : returnNoteId);
      });
    },
    clearBoard: () => {
      focusOrigin.current = null;
      controller.clearBoard();
    },
  }), [controller]);

  return (
    <ControllerContext.Provider value={controller}>
      <ActionsContext.Provider value={actions}>{children}</ActionsContext.Provider>
    </ControllerContext.Provider>
  );
}

function useController() {
  const controller = useContext(ControllerContext);
  if (!controller) throw new Error("Editor state must be used inside EditorStateProvider.");
  return controller;
}

export function useEditorActions() {
  const actions = useContext(ActionsContext);
  if (!actions) throw new Error("Editor actions must be used inside EditorStateProvider.");
  return actions;
}

export function useEditorNavigation(): EditorNavigationState {
  const controller = useController();
  return useSyncExternalStore(
    controller.subscribe,
    controller.getNavigation,
    controller.getNavigation,
  );
}

export function useNoteDraft(noteId: string): NoteDraft | null {
  const controller = useController();
  return useSyncExternalStore(
    controller.subscribe,
    () => controller.getDraft(noteId),
    () => controller.getDraft(noteId),
  );
}

export function useNoteCreations() {
  const controller = useController();
  return useSyncExternalStore(controller.subscribe, controller.getCreations, controller.getCreations);
}
