import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
  type PointerEvent as Pointer,
} from "react";
import {
  ArrowLeft,
  Link2,
  ListChecks,
  Plus,
  Share2,
  StickyNote,
} from "lucide-react";
import {
  AuthApiError,
  boardApi,
  connectionApi,
  errorMessage,
  noteApi,
  type BoardDetailDto,
  type ConnectionDto,
  type ConnectionSide,
  type MemberDto,
  type NoteDto,
  type PatchNote,
} from "../../api";
import { Dialog } from "../../components/ui/Dialog";
import { Button } from "../../components/ui/Button";
import { ChatWorkspace } from "../chat/ChatWorkspace";
import { BoardPresence } from "./BoardPresence";
import { SharePanel, TasksPanel } from "./components/BoardPanels";
import type { VisualPatch } from "./boardTypes";
import { isNoteConflict, useNoteMutationQueue } from "./hooks/useNoteMutationQueue";
import { useBoardCursors } from "./hooks/useBoardCursors";
import { useBoardEditing } from "./hooks/useBoardEditing";
import { NoteCard } from "./components/NoteCard";
import { ConnectionEditor } from "./components/ConnectionEditor";
import { InspectorFrame, PropertiesEditor } from "./components/BoardInspector";
import type { EditingViewer } from "./NoteEditingIndicator";
import {
  RemoteCursors,
} from "./RemoteCursors";
import { collaboratorInitials } from "./collaboratorIdentity";
import { identityLabel } from "../../components/ui/Avatar";
import { CollaborationAnnouncements } from "./CollaborationAnnouncements";
import {
  connectedNoteIds,
  connectionEndpoints, connectionHandleReach,
  isConnectionSide,
  nearestConnectionSide,
  notesAreConnected,
} from "./connectionGeometry";
import { contentBounds } from "./boardZoom";
import { boardBounds, clampBoardPosition, constrainBoardGeometry } from "./boardBounds";
import { useBoardViewport } from "./hooks/useBoardViewport";
import { ConnectionLayer, type ConnectionDraft } from "./components/ConnectionLayer";
import { ZoomControls } from "./components/ZoomControls";
import { clampDimension, noteDimensionBounds } from "./noteDimensions";
import { RealtimeHealth } from "./RealtimeHealth";
import {
  clientPointToBoard,
  revealBoardNode,
} from "./boardViewport";
import {
  EditorStateProvider,
  useNoteCreations,
  useEditorActions,
  useEditorNavigation,
  useNoteDraft,
} from "./editor/EditorStateProvider";
import { realtimeConnection } from "../../realtime/connection";
import {
  realtimeEvents,
  type ProfileChangedEvent,
  type BoardScopedEvent,
  type BoardPresenceSnapshot,
  type BoardCursorMovedEvent,
  type BoardCursorStoppedEvent,
  type BoardUpdatedEvent,
  type ConnectionCreatedEvent,
  type ConnectionDeletedEvent,
  type NoteChangedEvent,
  type NoteDeletedEvent,
  type NoteGeometryOperation,
  type NoteGeometryPreviewEndedEvent,
  type NoteGeometryPreviewEvent,
  type NoteEditingStartedEvent,
  type NoteEditingStoppedEvent,
} from "../../realtime/events";
import {
  mergePresenceSnapshot,
  mergeVersionedNote,
  mergeVersionedConnection,
  removeVersionedNote,
  shouldAcceptGeometryPreview,
  shouldClearGeometryPreview,
  shouldEndGeometryPreview,
} from "../../realtime/reconcile";
import { editingUserIds } from "../../realtime/editing";

type Panel = "tasks" | "share" | null;
type WorkspaceProps = {
  id: string;
  titleOverride?: string;
  currentUserId: string;
  profileIdentityVersion: string;
  back: () => void;
  boardLoaded: (board: BoardDetailDto) => void;
  notify: (message: string) => void;
};

export function Workspace(props: WorkspaceProps) {
  return (
    <EditorStateProvider userId={props.currentUserId} boardId={props.id}>
      <WorkspaceContent {...props} />
    </EditorStateProvider>
  );
}

function WorkspaceContent({
  id,
  titleOverride,
  currentUserId,
  profileIdentityVersion,
  back,
  boardLoaded,
  notify,
}: WorkspaceProps) {
  const chatHost = useRef<HTMLElement>(null);
  const editor = useEditorActions();
  const editorNavigation = useEditorNavigation();
  const selected = editorNavigation.selectedNoteId;
  const creations = useNoteCreations();
  const creationKeys = useRef(new Map<string, string>());
  const [board, setBoard] = useState<BoardDetailDto | null>(null),
    [notes, setNotes] = useState<NoteDto[]>([]),
    [edges, setEdges] = useState<ConnectionDto[]>([]),
    [members, setMembers] = useState<MemberDto[]>([]);
  const linkedNote = useRef<string | null>(null);
  useEffect(() => {
    const linked = () => {
      const noteId = new URLSearchParams(window.location.search).get("note");
      if (noteId && linkedNote.current !== noteId && notes.some(note => note.id === noteId)) { linkedNote.current = noteId; editor.select(noteId);
        requestAnimationFrame(() => document.querySelector<HTMLElement>(`[data-note-id="${noteId}"]`)?.scrollIntoView({ block: "center", inline: "center" })); }
    };
    linked(); window.addEventListener("wukna:navigation", linked); window.addEventListener("popstate", linked);
    return () => { window.removeEventListener("wukna:navigation", linked); window.removeEventListener("popstate", linked); };
  }, [notes, editor]);
  const [loading, setLoading] = useState(true),
    [failure, setFailure] = useState(""),
    [editTitleId, setEditTitleId] = useState<string | null>(null),
    [panel, setPanel] = useState<Panel>(null),
    [connecting, setConnecting] = useState(false),
    [type, setType] = useState<0 | 1>(0),
    [conflicted, setConflicted] = useState<string | null>(null),
    [conflictLatest, setConflictLatest] = useState<NoteDto | null>(null),
    [visuals, setVisuals] = useState<Record<string, VisualPatch>>({}),
    [remotePreviews, setRemotePreviews] = useState<Record<string, NoteGeometryPreviewEvent>>({}),
    [presence, setPresence] = useState<BoardPresenceSnapshot | null>(null),
    [connectionDraft, setConnectionDraft] = useState<ConnectionDraft | null>(null),
    [connectionTargetId, setConnectionTargetId] = useState("");
  const realtimeStatus = useSyncExternalStore(
    realtimeConnection.subscribe,
    realtimeConnection.getSnapshot,
  );
  useEffect(() => {
    void boardApi.members(id).then(setMembers).catch(() => undefined);
  }, [id, profileIdentityVersion]);
  const [selectedEdgeId, setSelectedEdgeId] = useState<string | null>(null);
  const [editingEdgeId, setEditingEdgeId] = useState<string | null>(null);
  const editingEdge = edges.find((edge) => edge.id === editingEdgeId);
  const pendingConnections = useRef(new Set<string>());
  const connectionTombstones = useRef(new Map<string, number>());
  const notesRef = useRef<NoteDto[]>([]);
  const permissionRef = useRef<boolean | null>(null);
  const noteTombstones = useRef(new Map<string, number>());
  const { enqueueNote, blockedNotes } = useNoteMutationQueue(notesRef);
  const colorTimers = useRef(new Map<string, { timer: ReturnType<typeof setTimeout>; color: string }>());
  const draftRef = useRef<ConnectionDraft | null>(null);
  const geometrySequence = useRef(0);
  const remotePreviewsRef = useRef<Record<string, NoteGeometryPreviewEvent>>({});
  const remotePreviewEnds = useRef(new Map<string, number>());
  const remotePreviewTimers = useRef(new Map<string, ReturnType<typeof setTimeout>>());
  const canvasRef = useRef<HTMLDivElement>(null);
  const top = [...notes.filter((note) => note.kind !== 2 && !creations[creationKeys.current.get(note.id) ?? ""]), ...Object.values(creations).map(({ note }) => note)],
    visualTop = top.map((note) => {
      const remote = remotePreviews[note.id];
      const remoteGeometry: VisualPatch | undefined = remote?.operation === 0
        ? { positionX: remote.x ?? note.positionX ?? 0, positionY: remote.y ?? note.positionY ?? 0 }
        : remote?.operation === 1
          ? { width: remote.width ?? note.width, height: remote.height ?? note.height }
          : undefined;
      const candidate = constrainBoardGeometry({ ...note, ...remoteGeometry, ...visuals[note.id] });
      const bounds = noteDimensionBounds(candidate, notes.filter((item) => item.parentNoteId === note.id), candidate.width);
      return constrainBoardGeometry({
        ...candidate,
        width: clampDimension(candidate.width, bounds.minWidth, bounds.maxWidth),
        height: clampDimension(candidate.height, bounds.minHeight, bounds.maxHeight),
      });
    });
  const viewport = useBoardViewport(canvasRef, contentBounds(visualTop), !loading && !failure, currentUserId, id);
  const { remoteEditing, clearAllRemoteEditing, clearRemoteEditingForNote,
    acceptRemoteEditing, endRemoteEditing, stopLocalEditingNow,
    editingChanged, finishEditing, reannounceLocalEditing } = useBoardEditing(id);
  const { cursorStore, clearAllRemoteCursors, acceptRemoteCursor, endRemoteCursor,
    stopLocalCursor, moveLocalCursor } = useBoardCursors(id, currentUserId, canvasRef, viewport.controller.getCamera);
  const publishNotes = useCallback((update: (current: NoteDto[]) => NoteDto[]) => {
    const next = update(notesRef.current);
    notesRef.current = next;
    setNotes(next);
  }, []);
  const clearRemotePreview = useCallback((noteId: string) => {
    const timer = remotePreviewTimers.current.get(noteId);
    if (timer) clearTimeout(timer);
    remotePreviewTimers.current.delete(noteId);
    if (!remotePreviewsRef.current[noteId]) return;
    const next = { ...remotePreviewsRef.current };
    delete next[noteId];
    remotePreviewsRef.current = next;
    setRemotePreviews(next);
  }, []);
  const clearAllRemotePreviews = useCallback(() => {
    for (const timer of remotePreviewTimers.current.values()) clearTimeout(timer);
    remotePreviewTimers.current.clear();
    remotePreviewsRef.current = {};
    setRemotePreviews({});
  }, []);
  const acceptRemotePreview = useCallback((incoming: NoteGeometryPreviewEvent) => {
    const note = notesRef.current.find((candidate) => candidate.id === incoming.noteId);
    const senderKey = `${incoming.noteId}:${incoming.connectionId}`;
    if (!note || note.kind === 2 || !shouldAcceptGeometryPreview(
        remotePreviewsRef.current[incoming.noteId],
        incoming,
        note.version,
        remotePreviewEnds.current.get(senderKey))) return;

    const next = { ...remotePreviewsRef.current, [incoming.noteId]: incoming };
    remotePreviewsRef.current = next;
    setRemotePreviews(next);
    const previousTimer = remotePreviewTimers.current.get(incoming.noteId);
    if (previousTimer) clearTimeout(previousTimer);
    const timer = setTimeout(() => {
      const current = remotePreviewsRef.current[incoming.noteId];
      if (current?.connectionId === incoming.connectionId &&
          current.sequence === incoming.sequence) {
        remotePreviewEnds.current.set(senderKey, incoming.sequence);
        clearRemotePreview(incoming.noteId);
      }
    }, 1_500);
    remotePreviewTimers.current.set(incoming.noteId, timer);
  }, [clearRemotePreview]);
  const endRemotePreview = useCallback((ended: NoteGeometryPreviewEndedEvent) => {
    const senderKey = `${ended.noteId}:${ended.connectionId}`;
    remotePreviewEnds.current.set(senderKey, Math.max(
      remotePreviewEnds.current.get(senderKey) ?? -1,
      ended.sequence));
    if (shouldEndGeometryPreview(remotePreviewsRef.current[ended.noteId], ended))
      clearRemotePreview(ended.noteId);
  }, [clearRemotePreview]);
  const mergeAuthoritativeNote = useCallback((incoming: NoteDto) => {
    editor.reconcileAuthoritative(incoming);
    const current = notesRef.current.find((note) => note.id === incoming.id);
    if (shouldClearGeometryPreview(current?.version, incoming.version))
      clearRemotePreview(incoming.id);
    publishNotes((current) => mergeVersionedNote(
      current,
      incoming,
      noteTombstones.current.get(incoming.id),
    ));
  }, [clearRemotePreview, editor, publishNotes]);
  const mergeConnection = useCallback((incoming: ConnectionDto) => {
    setEdges((current) => mergeVersionedConnection(current, incoming, connectionTombstones.current.get(incoming.id)));
  }, []);
  const preview = useCallback((noteId: string, patch: VisualPatch) => {
    setVisuals((current) => ({
      ...current,
      [noteId]: { ...current[noteId], ...patch },
    }));
  }, []);
  const broadcastGeometry = useCallback((
    noteId: string,
    baseVersion: number,
    operation: NoteGeometryOperation,
    patch: VisualPatch,
  ) => {
    if (editor.getCreation(noteId)) return 0;
    const sequence = ++geometrySequence.current;
    void realtimeConnection.sendNoteGeometryPreview({
      boardId: id,
      noteId,
      operation,
      x: operation === 0 ? patch.positionX ?? null : null,
      y: operation === 0 ? patch.positionY ?? null : null,
      width: operation === 1 ? patch.width ?? null : null,
      height: operation === 1 ? patch.height ?? null : null,
      baseVersion,
      sequence,
    });
    return sequence;
  }, [id, editor]);
  const endGeometry = useCallback((noteId: string, sequence: number) => {
    if (sequence > 0) void realtimeConnection.endNoteGeometryPreview(id, noteId, sequence);
  }, [id]);
  const cancelPreview = useCallback((noteId: string, patch: VisualPatch) => {
    setVisuals((current) => {
      if (!current[noteId]) return current;
      const next = { ...current[noteId] };
      for (const key of Object.keys(patch) as (keyof VisualPatch)[]) {
        if (next[key] === patch[key]) delete next[key];
      }
      const all = { ...current };
      if (Object.keys(next).length) all[noteId] = next;
      else delete all[noteId];
      return all;
    });
  }, []);
  const patchNote = useCallback((noteId: string, changes: PatchNote) => {
    const creation = editor.getCreation(noteId);
    if (creation) {
      editor.updateCreation(noteId, changes);
      return Promise.resolve({ ...creation.note, ...changes });
    }
    return enqueueNote(noteId, async (current) => {
      const updated = await noteApi.patch(id, noteId, current.version, changes);
      publishNotes((all) => all.map((note) => note.id === noteId ? updated : note));
      editor.reconcileSaved(updated);
      return updated;
    });
  }, [editor, enqueueNote, id, publishNotes]);
  const deleteNote = useCallback((noteId: string) => {
    if (editor.getCreation(noteId)) {
      editor.removeNote(noteId);
      return Promise.resolve();
    }
    return enqueueNote(noteId, async (current) => {
      await noteApi.remove(id, noteId, current.version);
      publishNotes((all) => all.filter((note) => note.id !== noteId));
      setEdges((all) => all.filter((edge) =>
        edge.sourceNoteId !== noteId && edge.targetNoteId !== noteId));
      setVisuals((all) => {
        const next = { ...all };
        delete next[noteId];
        return next;
      });
      editor.removeNote(noteId);
    });
  }, [editor, enqueueNote, id, publishNotes]);
  const failed = useCallback((noteId: string, cause: unknown) => {
    if (isNoteConflict(cause)) {
      editor.markConflict(noteId);
      setConflictLatest(null);
      setConflicted(noteId);
    }
    notify(errorMessage(cause));
  }, [editor, notify]);
  const commitVisual = useCallback(async (noteId: string, changes: VisualPatch) => {
    try {
      await patchNote(noteId, changes);
    } catch (cause) {
      failed(noteId, cause);
    } finally {
      cancelPreview(noteId, changes);
    }
  }, [patchNote, failed, cancelPreview]);
  function changeColor(noteId: string, color: string) {
    preview(noteId, { color });
    const pending = colorTimers.current.get(noteId);
    if (pending) clearTimeout(pending.timer);
    const timer = setTimeout(() => {
      colorTimers.current.delete(noteId);
      void commitVisual(noteId, { color });
    }, 250);
    colorTimers.current.set(noteId, { timer, color });
  }
  const load = useCallback(async (showLoading = true) => {
    if (showLoading) setLoading(true);
    setFailure("");
    try {
      const [b, n, e, m] = await Promise.all([
        boardApi.get(id),
        noteApi.list(id),
        connectionApi.list(id),
        boardApi.members(id),
      ]);
      setBoard(b);
      permissionRef.current = b.canEdit;
      notesRef.current = n;
      setNotes(n);
      editor.hydrate(n);
      setEdges(e);
      setMembers(m);
      if (showLoading) setVisuals({});
      clearAllRemotePreviews();
      clearAllRemoteEditing();
      clearAllRemoteCursors();
      remotePreviewEnds.current.clear();
      noteTombstones.current.clear();
      blockedNotes.current.clear();
      boardLoaded(b);
    } catch (cause) {
      if (cause instanceof AuthApiError && cause.status === 404) {
        editor.clearBoard();
        if (!showLoading) {
          notify("Your access to this board is no longer available.");
          back();
          return;
        }
      }
      setFailure(errorMessage(cause));
      throw cause;
    } finally {
      if (showLoading) setLoading(false);
    }
  }, [back, id, boardLoaded, clearAllRemoteCursors, clearAllRemoteEditing, clearAllRemotePreviews, editor, notify]);
  useEffect(() => {
    void load().catch(() => undefined);
  }, [load]);
  useEffect(() => realtimeConnection.onReconnected(() => load(false)), [load]);
  useEffect(() => realtimeConnection.onPresence((incoming) => {
    if (incoming.boardId !== id) return;
    setPresence((current) => mergePresenceSnapshot(current, incoming));
  }), [id]);
  useEffect(() => realtimeConnection.subscribe(() => {
    if (realtimeConnection.getSnapshot() !== "connected") {
      setPresence(null);
      clearAllRemoteEditing();
      clearAllRemoteCursors();
      stopLocalCursor();
      return;
    }
    reannounceLocalEditing();
  }), [clearAllRemoteCursors, clearAllRemoteEditing, reannounceLocalEditing, stopLocalCursor]);
  useEffect(() => {
    const cleanups = [
      realtimeConnection.on<NoteChangedEvent>(realtimeEvents.noteCreated, (message) => {
        if (message.boardId === id) mergeAuthoritativeNote(message);
      }),
      realtimeConnection.on<NoteChangedEvent>(realtimeEvents.noteUpdated, (message) => {
        if (message.boardId === id) mergeAuthoritativeNote(message);
      }),
      realtimeConnection.on<NoteDeletedEvent>(realtimeEvents.noteDeleted, (message) => {
        if (message.boardId !== id) return;
        clearRemotePreview(message.noteId);
        clearRemoteEditingForNote(message.noteId);
        const previous = noteTombstones.current.get(message.noteId) ?? -1;
        noteTombstones.current.set(message.noteId, Math.max(previous, message.version));
        publishNotes((current) => removeVersionedNote(
          current, message.noteId, message.version));
        setEdges((current) => current.filter((edge) =>
          edge.sourceNoteId !== message.noteId && edge.targetNoteId !== message.noteId));
        setVisuals((current) => {
          if (!current[message.noteId]) return current;
          const next = { ...current };
          delete next[message.noteId];
          return next;
        });
        editor.removeNote(message.noteId);
      }),
      realtimeConnection.on<ConnectionCreatedEvent>(
        realtimeEvents.connectionCreated,
        (message) => {
          if (message.boardId === id) mergeConnection(message);
        },
      ),
      realtimeConnection.on<ConnectionCreatedEvent>(realtimeEvents.connectionUpdated, (message) => {
        if (message.boardId === id) mergeConnection(message);
      }),
      realtimeConnection.on<ConnectionDeletedEvent>(
        realtimeEvents.connectionDeleted,
        (message) => {
          if (message.boardId === id) {
            const version = message.version ?? Number.MAX_SAFE_INTEGER;
            connectionTombstones.current.set(message.connectionId, Math.max(version, connectionTombstones.current.get(message.connectionId) ?? -1));
            setEdges((current) => current.filter((edge) => edge.id !== message.connectionId || (edge.version ?? 0) > version));
            setSelectedEdgeId((current) => current === message.connectionId ? null : current);
          }
        },
      ),
      realtimeConnection.on<BoardUpdatedEvent>(realtimeEvents.boardUpdated, (message) => {
        if (message.boardId !== id) return;
        setBoard((current) => current &&
          Date.parse(current.updatedAt) > Date.parse(message.updatedAt)
          ? current
          : current && { ...current, title: message.title, updatedAt: message.updatedAt });
      }),
      realtimeConnection.on<BoardScopedEvent>(realtimeEvents.membersChanged, (message) => {
        if (message.boardId !== id) return;
        void Promise.all([boardApi.get(id), boardApi.members(id)]).then(([currentBoard, currentMembers]) => {
          const downgraded = permissionRef.current === true && !currentBoard.canEdit;
          permissionRef.current = currentBoard.canEdit;
          setBoard(currentBoard);
          setMembers(currentMembers);
          boardLoaded(currentBoard);
          if (downgraded) {
            stopLocalEditingNow();
            editor.stopEditing();
            setVisuals({});
            notify("Your permission changed to Viewer. Unsaved text is retained in this browser session.");
          }
        }).catch(() => undefined);
      }),
      realtimeConnection.on<ProfileChangedEvent>(realtimeEvents.profileChanged, (message) => {
        if (message.boardId === id)
          void boardApi.members(id).then(setMembers).catch(() => undefined);
      }),
      realtimeConnection.on<NoteGeometryPreviewEvent>(
        realtimeEvents.noteGeometryPreview,
        (message) => {
          if (message.boardId === id) acceptRemotePreview(message);
        },
      ),
      realtimeConnection.on<NoteGeometryPreviewEndedEvent>(
        realtimeEvents.noteGeometryPreviewEnded,
        (message) => {
          if (message.boardId === id) endRemotePreview(message);
        },
      ),
      realtimeConnection.on<NoteEditingStartedEvent>(
        realtimeEvents.noteEditingStarted,
        (message) => {
          if (message.boardId === id) acceptRemoteEditing(message);
        },
      ),
      realtimeConnection.on<NoteEditingStoppedEvent>(
        realtimeEvents.noteEditingStopped,
        (message) => {
          if (message.boardId === id) endRemoteEditing(message);
        },
      ),
      realtimeConnection.on<BoardCursorMovedEvent>(
        realtimeEvents.boardCursorMoved,
        (message) => {
          if (message.boardId === id) acceptRemoteCursor(message);
        },
      ),
      realtimeConnection.on<BoardCursorStoppedEvent>(
        realtimeEvents.boardCursorStopped,
        (message) => {
          if (message.boardId === id) endRemoteCursor(message);
        },
      ),
      realtimeConnection.on<BoardScopedEvent>(realtimeEvents.boardAccessRevoked, (message) => {
        if (message.boardId !== id) return;
        clearAllRemotePreviews();
        clearAllRemoteEditing();
        clearAllRemoteCursors();
        stopLocalEditingNow();
        stopLocalCursor();
        editor.clearBoard();
        setPresence(null);
        notify("Your access to this board was removed.");
        back();
      }),
    ];
    return () => cleanups.forEach((cleanup) => cleanup());
  }, [
    acceptRemoteEditing,
    acceptRemoteCursor,
    acceptRemotePreview,
    back,
    clearAllRemoteEditing,
    clearAllRemoteCursors,
    clearAllRemotePreviews,
    clearRemoteEditingForNote,
    clearRemotePreview,
    endRemoteEditing,
    endRemoteCursor,
    endRemotePreview,
    editor,
    id,
    mergeAuthoritativeNote,
    mergeConnection,
    notify,
    publishNotes,
    stopLocalEditingNow,
    stopLocalCursor,
  ]);
  useEffect(() => () => {
    for (const timer of remotePreviewTimers.current.values()) clearTimeout(timer);
    remotePreviewTimers.current.clear();
    remotePreviewsRef.current = {};
    remotePreviewEnds.current.clear();
    for (const [noteId, pending] of colorTimers.current) {
      clearTimeout(pending.timer);
      void commitVisual(noteId, { color: pending.color });
    }
    colorTimers.current.clear();
  }, [commitVisual, stopLocalCursor, stopLocalEditingNow]);
  async function finishCreation(noteId: string, force = false) {
    const saved = await editor.commitCreation(noteId, async (note) => {
      const result = await noteApi.create(id, {
        kind: note.kind, title: note.title, content: note.content,
        positionX: note.positionX ?? 0, positionY: note.positionY ?? 0,
        width: note.width, height: note.height, color: note.color, zIndex: note.zIndex,
      });
      // Stable render identity preserves inline task-item state during the local-to-server handoff.
      creationKeys.current.set(result.id, noteId);
      mergeAuthoritativeNote(result);
      return result;
    }, force);
    if (saved) mergeAuthoritativeNote(saved);
    if (!editor.getCreation(noteId)) {
      setEditTitleId((current) => current === noteId ? null : current);
      setVisuals((all) => { const next = { ...all }; delete next[noteId]; return next; });
    }
    return saved;
  }
  function create(kind: 0 | 1) {
    setConnecting(false);
    draftRef.current = null;
    setConnectionDraft(null);
    const canvas = canvasRef.current;
    const scale = viewport.controller.getScale();
    // Creation is local until meaningful text is committed; no placeholder POST.
    const noteId = `local:${crypto.randomUUID()}`;
    const center = canvas ? clientPointToBoard(canvas.getBoundingClientRect(),
      viewport.controller.getCamera(),
      { x: canvas.getBoundingClientRect().left + canvas.clientWidth / 2,
        y: canvas.getBoundingClientRect().top + canvas.clientHeight / 2 }) : { x: 240, y: 200 };
    const width = kind === 1 ? 300 : 280;
    const height = kind === 1 ? 144 : 220;
    const base = { x: Math.round(center.x - width / 2), y: Math.round(center.y - height / 2) };
    const candidates = [[0, 0], [1, 0], [-1, 0], [0, 1], [0, -1], [1, 1], [-1, 1]]
      .map(([x, y]) => clampBoardPosition({ x: base.x + x * (width + 24), y: base.y + y * (height + 24) }, { width, height }));
    const position = candidates.find((point) =>
      Math.abs(point.x + width / 2 - center.x) <= (canvas?.clientWidth ?? 800) / scale / 2 - width / 2 &&
      Math.abs(point.y + height / 2 - center.y) <= (canvas?.clientHeight ?? 600) / scale / 2 - height / 2 &&
      visualTop.every((note) => point.x + width + 16 <= (note.positionX ?? 0) ||
        point.x >= (note.positionX ?? 0) + note.width + 16 ||
        point.y + height + 16 <= (note.positionY ?? 0) || point.y >= (note.positionY ?? 0) + note.height + 16))
      ?? clampBoardPosition({ x: base.x + top.length % 6 * 24, y: base.y + top.length % 6 * 24 }, { width, height });
    editor.createNote({
      id: noteId, boardId: id, kind, parentNoteId: null, title: "", content: "",
      positionX: position.x, positionY: position.y,
      width, height, zIndex: top.reduce((highest, note) => Math.max(highest, note.zIndex), 0) + 1,
      color: kind === 1 ? "#EEE2BF" : "#EEE8DB", isCompleted: false,
      createdAt: new Date().toISOString(), version: 0,
    });
    setEditTitleId(noteId);
  }
  async function addItem(parentId: string, title: string) {
    try {
      const creation = editor.getCreation(parentId);
      const parent = creation ? await finishCreation(parentId, true) : null;
      const value = await noteApi.create(id, {
        kind: 2,
        title: title.trim(),
        parentNoteId: parent?.id ?? parentId,
      });
      mergeAuthoritativeNote(value);
      notify("Checklist item added");
    } catch (cause) {
      notify(errorMessage(cause));
      throw cause;
    }
  }
  async function toggle(item: NoteDto) {
    try {
      await enqueueNote(item.id, async (current) => {
        const updated = await noteApi.patch(id, item.id, current.version, {
          isCompleted: !current.isCompleted,
        });
        publishNotes((all) => all.map((note) => note.id === item.id ? updated : note));
      });
    } catch (cause) {
      failed(item.id, cause);
    }
  }
  async function editTitle(noteId: string, title: string) {
    try {
      if (editor.getCreation(noteId)) await finishCreation(noteId);
      else await patchNote(noteId, { title });
      setEditTitleId(null);
    } catch (cause) {
      failed(noteId, cause);
      throw cause;
    }
  }
  async function editContent(noteId: string, content: string) {
    try {
      if (editor.getCreation(noteId)) await finishCreation(noteId);
      else await patchNote(noteId, { content });
      notify("Note body updated");
    } catch (cause) {
      failed(noteId, cause);
      throw cause;
    }
  }
  async function editItem(item: NoteDto, title: string) {
    try {
      await patchNote(item.id, { title });
      notify("Checklist item updated");
    } catch (cause) {
      failed(item.id, cause);
      throw cause;
    }
  }
  async function removeItem(item: NoteDto) {
    try {
      await deleteNote(item.id);
      notify("Checklist item deleted");
    } catch (cause) {
      failed(item.id, cause);
    }
  }
  function targetAt(clientX: number, clientY: number, fixedId: string) {
    const point = viewport.controller.clientToWorld({ x: clientX, y: clientY });
    const eligible = (note: NoteDto) => note.id !== fixedId && !editor.getCreation(note.id);
    const candidates = document.elementsFromPoint(clientX, clientY).flatMap((element) => {
      const targetId = element.closest<HTMLElement>("[data-note-id]")?.dataset.noteId;
      const note = visualTop.find((candidate) => candidate.id === targetId);
      if (!note || !eligible(note)) return [];
      const side = element.closest<HTMLElement>("[data-connection-side]")?.dataset.connectionSide;
      return [{ note, side: isConnectionSide(side) ? side : nearestConnectionSide(note, point) }];
    });
    const distance = (note: NoteDto) => Math.hypot(
      Math.max((note.positionX ?? 0) - point.x, 0, point.x - (note.positionX ?? 0) - note.width),
      Math.max((note.positionY ?? 0) - point.y, 0, point.y - (note.positionY ?? 0) - note.height));
    // Card content wins over the outward hit area of a nearby card's handle.
    const body = candidates.find(({ note }) => distance(note) === 0);
    if (body) return { id: body.note.id, side: body.side };
    // Edge drops can land just outside the DOM box, including while handles are hidden.
    const nearby = visualTop.filter(eligible).reduce<NoteDto | null>((nearest, note) =>
      distance(note) <= 5 / viewport.controller.getScale() && (!nearest || distance(note) < distance(nearest)) ? note : nearest, null);
    if (nearby) return { id: nearby.id, side: nearestConnectionSide(nearby, point) };
    const handle = candidates[0];
    return handle ? { id: handle.note.id, side: handle.side } : null;
  }
  function connectStart(sourceId: string, side: ConnectionSide, event: Pointer<HTMLButtonElement>) {
    if (!canvasRef.current || !board?.canEdit || editor.getCreation(sourceId)) return;
    event.preventDefault();
    const point = viewport.controller.clientToWorld({ x: event.clientX, y: event.clientY });
    const value: ConnectionDraft = { fixedNoteId: sourceId, fixedSide: side, endpoint: "target", ...point, targetId: null, targetSide: null };
    draftRef.current = value; setConnectionDraft(value); setConnecting(true);
    setSelectedEdgeId(null); editor.select(sourceId);
  }
  function reconnectStart(edge: ConnectionDto, endpoint: "source" | "target", event: Pointer<HTMLButtonElement>) {
    if (!board?.canEdit || pendingConnections.current.has(edge.id)) return;
    const source = visualTop.find((note) => note.id === edge.sourceNoteId), target = visualTop.find((note) => note.id === edge.targetNoteId);
    if (!source || !target) return;
    event.preventDefault();
    const anchors = connectionEndpoints(source, target, edge.sourceHandle, edge.targetHandle);
    const fixed = endpoint === "target" ? anchors.start : anchors.end;
    const point = viewport.controller.clientToWorld({ x: event.clientX, y: event.clientY });
    const value: ConnectionDraft = { fixedNoteId: endpoint === "target" ? source.id : target.id,
      fixedSide: fixed.side!, endpoint, connectionId: edge.id, version: edge.version,
      ...point, targetId: null, targetSide: null };
    draftRef.current = value; setConnectionDraft(value); setConnecting(true);
  }
  function connectMove(event: Pointer<HTMLButtonElement>) {
    const current = draftRef.current;
    if (!current) return;
    if (!(event.buttons & 1)) return connectEnd(event);
    const point = viewport.controller.clientToWorld({ x: event.clientX, y: event.clientY });
    const target = targetAt(event.clientX, event.clientY, current.fixedNoteId);
    const value = { ...current, ...point, targetId: target?.id ?? null, targetSide: target?.side ?? null };
    draftRef.current = value; setConnectionDraft(value);
  }
  const connectCancel = useCallback(() => {
    draftRef.current = null; setConnectionDraft(null); setConnecting(false);
  }, []);
  useEffect(() => {
    if (!board?.canEdit) connectCancel();
  }, [board?.canEdit, connectCancel]);
  const hasConnectionDraft = connectionDraft !== null;
  useEffect(() => {
    if (!hasConnectionDraft) return;
    const escape = (event: KeyboardEvent) => { if (event.key === "Escape") connectCancel(); };
    window.addEventListener("keydown", escape); window.addEventListener("blur", connectCancel);
    return () => { window.removeEventListener("keydown", escape); window.removeEventListener("blur", connectCancel); };
  }, [hasConnectionDraft, connectCancel]);
  async function createConnection(sourceId: string, targetId: string, sourceSide?: ConnectionSide, targetSide?: ConnectionSide) {
    if (!board?.canEdit) return;
    if (editor.getCreation(sourceId) || editor.getCreation(targetId)) { notify("Save both cards before connecting them."); return; }
    if (sourceId === targetId || notesAreConnected(sourceId, targetId, edges)) { notify("These cards are already connected."); return; }
    const source = visualTop.find((note) => note.id === sourceId), target = visualTop.find((note) => note.id === targetId);
    if (!source || !target) return;
    const anchors = connectionEndpoints(source, target, sourceSide, targetSide);
    const key = [sourceId, targetId].sort().join(":");
    if (pendingConnections.current.has(key)) return;
    pendingConnections.current.add(key);
    try {
      const edge = await connectionApi.create(id, sourceId, targetId, type, anchors.start.side!, anchors.end.side!);
      mergeConnection(edge); setConnectionTargetId(""); notify("Connection created");
    } catch (cause) { notify(errorMessage(cause)); }
    finally { pendingConnections.current.delete(key); }
  }
  async function connectEnd(event: Pointer<HTMLButtonElement>) {
    const current = draftRef.current;
    const target = current && targetAt(event.clientX, event.clientY, current.fixedNoteId);
    connectCancel();
    if (!board?.canEdit || !current || !target) return;
    if (!current.connectionId) { await createConnection(current.fixedNoteId, target.id, current.fixedSide, target.side); return; }
    const sourceNoteId = current.endpoint === "source" ? target.id : current.fixedNoteId;
    const targetNoteId = current.endpoint === "target" ? target.id : current.fixedNoteId;
    await reconnectConnection(current.connectionId, current.version ?? 0, {
      sourceNoteId, targetNoteId,
      sourceHandle: current.endpoint === "source" ? target.side : current.fixedSide,
      targetHandle: current.endpoint === "target" ? target.side : current.fixedSide,
    });
  }
  async function reconnectConnection(connectionId: string, version: number, payload: {
    sourceNoteId: string; targetNoteId: string; sourceHandle: ConnectionSide; targetHandle: ConnectionSide;
  }) {
    if (!board?.canEdit) return false;
    if (payload.sourceNoteId === payload.targetNoteId || notesAreConnected(payload.sourceNoteId, payload.targetNoteId, edges, connectionId)) {
      notify("Choose two different cards that are not already connected."); return false;
    }
    if (pendingConnections.current.has(connectionId)) return false;
    pendingConnections.current.add(connectionId);
    try {
      const updated = await connectionApi.reconnect(id, connectionId, version, payload);
      mergeConnection(updated); notify("Connection updated"); return true;
    } catch (cause) {
      if (cause instanceof AuthApiError && cause.code === "connection_version_conflict") {
        try {
          const latest = (await connectionApi.list(id)).find((edge) => edge.id === connectionId);
          // Preserve a newer event received while the HTTP reload was in flight.
          if (latest) mergeConnection(latest);
          else setEdges((edges) => edges.filter((edge) => edge.id !== connectionId));
        } catch { notify("Could not reload the connection. Try refreshing the board."); }
      }
      notify(errorMessage(cause)); return false;
    } finally { pendingConnections.current.delete(connectionId); }
  }
  async function removeEdge(edgeId: string) {
    try {
      await connectionApi.remove(id, edgeId);
      connectionTombstones.current.set(edgeId, Number.MAX_SAFE_INTEGER);
      setEdges((previous) => previous.filter((edge) => edge.id !== edgeId));
      setSelectedEdgeId((current) => current === edgeId ? null : current);
      notify("Connection removed");
    } catch (cause) { notify(errorMessage(cause)); }
  }
  async function setGuest(email: string, edit: boolean) {
    await boardApi.setGuest(id, email, edit);
    setMembers(await boardApi.members(id));
    notify("Member access updated");
  }
  async function changePermission(member: MemberDto, canEdit: boolean) {
    await boardApi.setMemberPermission(id, member.userId, canEdit);
    setMembers(await boardApi.members(id));
    notify(`${identityLabel(member)} is now a ${canEdit ? "Editor" : "Viewer"}.`);
  }
  async function removeGuest(guestId: string) {
    const member = members.find((candidate) => candidate.userId === guestId);
    await boardApi.removeGuest(id, guestId);
    setMembers(await boardApi.members(id));
    notify(member ? `${identityLabel(member)} no longer has access to this board.` : "Collaborator removed.");
  }
  async function latest() {
    if (!conflicted) return;
    try {
      const fresh = await noteApi.get(id, conflicted);
      editor.reconcileAuthoritative(fresh);
      publishNotes((all) => all.map((note) => note.id === fresh.id ? fresh : note));
      blockedNotes.current.delete(conflicted);
      setVisuals((all) => {
        const next = { ...all };
        delete next[conflicted];
        return next;
      });
      setConflictLatest(fresh);
    } catch (cause) {
      notify(errorMessage(cause));
    }
  }
  const memberById = useMemo(
    () => new Map(members.map((member) => [member.userId, member])),
    [members],
  );
  const conflictDraft = useNoteDraft(conflicted ?? "");
  const editorsForNote = (noteId: string): EditingViewer[] =>
    editingUserIds(remoteEditing[noteId], currentUserId).map((userId) => {
      const member = memberById.get(userId);
      return {
        userId,
        label: member ? identityLabel(member) : "Someone",
        initials: member
          ? collaboratorInitials(identityLabel(member))
          : userId.replaceAll("-", "").slice(0, 2).toUpperCase(),
        identity: member,
      };
    });
  const inspectorNote = top.find((note) => note.id === editorNavigation.inspectorNoteId),
    editable = board?.canEdit ?? false;
  const inspectorConnections = useMemo(
    () => inspectorNote ? connectedNoteIds(inspectorNote.id, edges) : new Set<string>(),
    [edges, inspectorNote],
  );
  const eligibleConnectionTargets = inspectorNote
    ? top.filter((candidate) =>
        candidate.id !== inspectorNote.id && !inspectorConnections.has(candidate.id))
    : [];
  const activeConnectionTargetId = eligibleConnectionTargets.some(
    (note) => note.id === connectionTargetId,
  ) ? connectionTargetId : "";
  useEffect(() => {
    setConnectionTargetId("");
  }, [editorNavigation.inspectorNoteId]);
  useEffect(() => {
    const noteId = editorNavigation.inspectorNoteId;
    const canvas = canvasRef.current;
    if (!noteId || !canvas) return;
    let frame = 0;
    const reveal = () => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => {
        const node = Array.from(canvas.querySelectorAll<HTMLElement>("[data-note-id]"))
          .find((candidate) => candidate.dataset.noteId === noteId);
        if (node) revealBoardNode(canvas, node, viewport.controller.panBy);
      });
    };
    reveal();
    const observer = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(reveal);
    observer?.observe(canvas);
    return () => {
      cancelAnimationFrame(frame);
      observer?.disconnect();
    };
  }, [viewport.controller, editorNavigation.inspectorNoteId]);
  return (
    <div className="workspace">
      <CollaborationAnnouncements
        editing={remoteEditing}
        members={members}
        notes={notes}
        currentUserId={currentUserId}
        realtimeStatus={realtimeStatus}
      />
      <header className="board-header">
        <div className="board-heading">
          <Button variant="quiet" className="back-button" onClick={back}>
            <ArrowLeft size={18} aria-hidden="true" /> Boards
          </Button>
          <span className="divider" />
          <h1>{titleOverride ?? board?.title ?? "Board"}</h1>
        </div>
        <div className="board-actions">
          <RealtimeHealth status={realtimeStatus} />
          <BoardPresence
            snapshot={presence}
            members={members}
            available={realtimeStatus === "connected"}
          />
          {board && (
            <span className="permission-label">
              {board.role === 1 ? "Owner" : editable ? "Editor" : "Viewer"}
            </span>
          )}
          <Button
            variant="secondary"
            disabled={!board}
            onClick={() => {
              const next = panel === "share" ? null : "share";
              if (next) editor.closeInspector(false);
              setPanel(next);
            }}
          >
            <Share2 size={18} aria-hidden="true" /> Share
          </Button>
        </div>
      </header>
      {loading ? (
        <div className="workspace-state">Loading board…</div>
      ) : failure ? (
        <div className="workspace-state">
          <h2>Board unavailable</h2>
          <p>{failure}</p>
          <Button variant="secondary" onClick={() => void load()}>
            Retry
          </Button>
        </div>
      ) : (
        <main className="board-main" ref={chatHost}>
          <ZoomControls controller={viewport.controller} />
          <div className="canvas-tools">
            {editable && (
              <>
                <Button variant="secondary" size="compact" className="board-tool-button"
                  aria-label="New note"
                  onClick={() => void create(0)}
                >
                  <StickyNote size={16} aria-hidden="true" /><span>Note</span>
                </Button>
                <Button variant="secondary" size="compact" className="board-tool-button"
                  aria-label="New task list"
                  onClick={() => void create(1)}
                >
                  <ListChecks size={16} aria-hidden="true" /><span>Task list</span>
                </Button>
                <Button variant="secondary" size="compact" className={`board-tool-button${connecting ? " active" : ""}`}
                  aria-label={connecting ? "Cancel connection mode" : "Connect notes"}
                  aria-pressed={connecting}
                  onClick={() => {
                    if (connecting) connectCancel();
                    else setConnecting(true);
                  }}
                >
                  <Link2 size={16} aria-hidden="true" /><span>Connect</span>
                </Button>
              </>
            )}
            {editable && selectedEdgeId && <Button variant="secondary" size="compact" onClick={() => setEditingEdgeId(selectedEdgeId)}>Edit connection</Button>}
            <span className="tool-rule" />
            <Button variant="secondary" size="compact" className="board-tool-button"
              aria-label="Tasks"
              aria-pressed={panel === "tasks"}
              onClick={() => {
                const next = panel === "tasks" ? null : "tasks";
                if (next) editor.closeInspector(false);
                setPanel(next);
              }}
            >
              <ListChecks size={16} aria-hidden="true" /><span>Tasks</span>
            </Button>
            <ChatWorkspace boardId={id} userId={currentUserId} boardTitle={board?.title ?? "Board"} host={chatHost} />
          </div>
          <div
            className="canvas"
            ref={canvasRef}
            tabIndex={0}
            aria-label="Board canvas. Ctrl or Command plus wheel to zoom. Drag empty space, scroll, use arrow keys, or Space and drag to pan."
            onPointerDown={(event) => {
              if (!(event.target as HTMLElement).closest("[data-note-id]")) {
                setSelectedEdgeId(null);
                editor.select(null);
                event.currentTarget.focus({ preventScroll: true });
              }
            }}
            onPointerMove={moveLocalCursor}
            onPointerLeave={stopLocalCursor}
          >

            {connecting && (
              <div className="canvas-label">
                Drag from any card edge to another card{" "}
                <select
                  value={type}
                  onChange={(e) => setType(Number(e.target.value) as 0 | 1)}
                >
                  <option value={0}>Related</option>
                  <option value={1}>Prerequisite</option>
                </select>
              </div>
            )}
            <div className="board-world" ref={viewport.worldRef}>
            <div className="board-boundary" aria-hidden="true" style={{ left: boardBounds.left, top: boardBounds.top,
              width: boardBounds.right - boardBounds.left, height: boardBounds.bottom - boardBounds.top }} />
            <RemoteCursors store={cursorStore} members={members} />
            <ConnectionLayer notes={visualTop} edges={edges} selectedId={selectedEdgeId} editable={editable}
              draft={connectionDraft} select={(edgeId) => { setSelectedEdgeId(edgeId); editor.select(null); }}
              start={reconnectStart} move={connectMove} end={connectEnd} cancel={connectCancel} />
            {visualTop.map((note) => {
              const remote = remotePreviews[note.id];
              const remoteMember = remote ? memberById.get(remote.userId) : undefined;
              const remoteLabel = remoteMember ? identityLabel(remoteMember) : "Someone";
              return (
                <NoteCard
                  key={creationKeys.current.get(note.id) ?? note.id}
                  note={note}
                  creation={creations[note.id]}
                  finishCreation={() => void finishCreation(note.id).catch((cause) => notify(errorMessage(cause)))}
                  viewport={viewport.controller}
                  items={notes.filter((item) => item.parentNoteId === note.id)}
                  selected={selected === note.id}
                  editable={editable && creations[note.id]?.status !== "saving"}
                  select={() => { setSelectedEdgeId(null); editor.select(note.id); }}
                  toggle={toggle}
                  addItem={(title) => addItem(note.id, title)}
                  editTitle={(title) => editTitle(note.id, title)}
                  editContent={(content) => editContent(note.id, content)}
                  editItem={(item, title) => editItem(item, title)}
                  removeItem={(item) => void removeItem(item)}
                  openProperties={(origin) => {
                    setPanel(null);
                    editor.openInspector(note.id, origin);
                  }}
                  autoEditTitle={editTitleId === note.id}
                  preview={preview}
                  cancelPreview={cancelPreview}
                  commitVisual={commitVisual}
                  broadcastGeometry={broadcastGeometry}
                  endGeometry={endGeometry}
                  connectStart={connectStart}
                  connectMove={connectMove}
                  connectEnd={(event) => void connectEnd(event)}
                  connectCancel={connectCancel}
                  targetHighlighted={connectionDraft?.targetId === note.id}
                  targetSide={connectionDraft?.targetId === note.id ? connectionDraft.targetSide : null}
                  connectionReach={connectionHandleReach(note, visualTop)}
                  remoteGeometry={remote ? {
                    userId: remote.userId,
                    label: remoteLabel,
                    initials: collaboratorInitials(remoteLabel),
                    operation: remote.operation,
                    profileImageUrl: remoteMember?.profileImageUrl,
                    username: remoteMember?.username,
                  } : undefined}
                  editors={editorsForNote(note.id)}
                  editingChanged={(noteId, active) => { if (!editor.getCreation(noteId)) editingChanged(noteId, active); }}
                />
              );
            })}
            </div>
            {!top.length && (
              <div className="canvas-empty" role="status">
                <h2>{editable ? "Start with a note" : "No notes yet"}</h2>
                <p>{editable
                  ? "Capture an idea here, then add a task list when you're ready."
                  : "This board is waiting for its first idea."}</p>
                {editable && (
                  <Button className="canvas-empty-add-note" onClick={() => void create(0)}>
                    <Plus size={18} aria-hidden="true" />
                    <span>Add note</span>
                  </Button>
                )}
              </div>
            )}
          </div>
          {inspectorNote && (
            <InspectorFrame
              title={`Properties for ${inspectorNote.title}`}
              presentation={editorNavigation.presentation}
              close={editor.closeInspector}
            >
                <PropertiesEditor
                  key={inspectorNote.id}
                  note={inspectorNote}
                  items={notes.filter((item) => item.parentNoteId === inspectorNote.id)}
                  visualColor={visuals[inspectorNote.id]?.color ?? inspectorNote.color}
                  visualWidth={visualTop.find((note) => note.id === inspectorNote.id)?.width ?? inspectorNote.width}
                  visualHeight={visualTop.find((note) => note.id === inspectorNote.id)?.height ?? inspectorNote.height}
                  editable={editable}
                  removeNote={deleteNote}
                  deleted={() => {
                    finishEditing(inspectorNote.id, true);
                  }}
                  preview={preview}
                  cancelPreview={cancelPreview}
                  commitVisual={commitVisual}
                  changeColor={changeColor}
                  failed={failed}
                  notify={notify}
                >
                  <section className="connection-list inspector-section" aria-labelledby={`connections-${inspectorNote.id}`}>
                    <h3 id={`connections-${inspectorNote.id}`}>Connections</h3>
                    {editable && eligibleConnectionTargets.length > 0 && (
                      <div className="connection-create">
                        <label>
                          Connect to
                          <select
                            value={activeConnectionTargetId}
                            onChange={(event) => setConnectionTargetId(event.target.value)}
                          >
                            <option value="">Choose a note…</option>
                            {eligibleConnectionTargets.map((candidate) => (
                                <option key={candidate.id} value={candidate.id}>{candidate.title}</option>
                              ))}
                          </select>
                        </label>
                        <label>
                          Relationship
                          <select value={type} onChange={(event) => setType(Number(event.target.value) as 0 | 1)}>
                            <option value={0}>Related</option>
                            <option value={1}>Prerequisite</option>
                          </select>
                        </label>
                        <Button
                          variant="secondary"
                          disabled={!activeConnectionTargetId}
                          onClick={() => void createConnection(inspectorNote.id, activeConnectionTargetId)}
                        >
                          <Link2 size={17} aria-hidden="true" /> Add connection
                        </Button>
                      </div>
                    )}
                    {editable && top.length > 1 && eligibleConnectionTargets.length === 0 && (
                      <p className="connection-complete">Every other note is already connected.</p>
                    )}
                    {edges.every((edge) =>
                      edge.sourceNoteId !== inspectorNote.id && edge.targetNoteId !== inspectorNote.id) && (
                      <p className="connection-complete">No connections yet.</p>
                    )}
                    {edges
                      .filter(
                        (edge) =>
                          edge.sourceNoteId === inspectorNote.id ||
                          edge.targetNoteId === inspectorNote.id,
                      )
                      .map((edge) => (
                        <div key={edge.id}>
                          {edge.type === 1 ? "Prerequisite" : "Related"} ·{" "}
                          {
                            top.find(
                              (n) =>
                                n.id ===
                                (edge.sourceNoteId === inspectorNote.id
                                  ? edge.targetNoteId
                                  : edge.sourceNoteId),
                            )?.title
                          }
                          {editable && (
                            <Button variant="quiet" size="compact" onClick={() => setEditingEdgeId(edge.id)}>Edit connection</Button>
                          )}
                          {editable && (
                            <Button variant="quiet" size="compact" onClick={() => void removeEdge(edge.id)}>
                              Remove
                            </Button>
                          )}
                        </div>
                      ))}
                  </section>
                </PropertiesEditor>
            </InspectorFrame>
          )}
          {panel === "tasks" && (
            <TasksPanel
              notes={notes}
              editable={editable}
              toggle={toggle}
              edit={(item, title) => editItem(item, title)}
              remove={(item) => void removeItem(item)}
              editingChanged={editingChanged}
              close={() => setPanel(null)}
            />
          )}
          {panel === "share" && board && (
            <SharePanel
              board={board}
              members={members}
              presence={presence}
              presenceAvailable={realtimeStatus === "connected"}
              setGuest={setGuest}
              changePermission={changePermission}
              removeGuest={removeGuest}
              close={() => setPanel(null)}
            />
          )}
        </main>
      )}
      {editingEdge && editable && <ConnectionEditor key={editingEdge.id} edge={editingEdge} notes={top}
        close={() => setEditingEdgeId(null)} save={(payload) => reconnectConnection(editingEdge.id, editingEdge.version, payload)} />}
      {conflicted && (
        <Dialog title="This note changed while you were editing" urgent onClose={() => setConflicted(null)}>
          <p>
            {conflictDraft
              ? "Your draft is still safe in this browser session. Review the latest saved version before deciding what to keep."
              : "A newer saved version is available. Review it before trying your change again."}
          </p>
          {conflictLatest && (
            <div className="wk-conflict-comparison">
              {conflictDraft && (
                <section>
                  <h3>Your unsaved draft</h3>
                  <strong>{conflictDraft.title}</strong>
                  {conflictDraft.content && <p>{conflictDraft.content}</p>}
                </section>
              )}
              <section>
                <h3>Latest saved version</h3>
                <strong>{conflictLatest.title}</strong>
                {conflictLatest.content && <p>{conflictLatest.content}</p>}
              </section>
            </div>
          )}
          <div className="wk-dialog-actions">
            <Button onClick={() => void latest()}>
              {conflictLatest ? "Refresh latest saved version" : "Review latest saved version"}
            </Button>
            <Button variant="quiet" onClick={() => setConflicted(null)}>Keep editing my draft</Button>
          </div>
        </Dialog>
      )}
    </div>
  );
}
