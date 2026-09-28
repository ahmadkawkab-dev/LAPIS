import { useCallback, useEffect, useRef, useState } from "react";
import {
  currentSession,
  exchangeGoogleCode,
  logout,
  logoutEverywhere,
  restoreSession,
  reconcileSessionUser,
  setSessionExpiredHandler,
  type AuthSession,
} from "../auth";
import {
  AuthApiError,
  boardApi,
  errorMessage,
  profileApi,
  type BoardDetailDto,
  type BoardListItemDto,
  type ProfileDto,
} from "../api";
import { AuthScreen } from "../features/auth/AuthScreen";
import { PrivacyPolicyPage, PublicHome, TermsOfServicePage } from "../features/public/PublicPages";
import { AccountPanel } from "../features/account/AccountPanel";
import { accountSectionForPath } from "../features/account/accountRoute";
import { Wordmark } from "../components/brand/Wordmark";
import { AppShell } from "../components/navigation/AppShell";
import { Notice } from "../components/ui/Notice";
import { BoardLanding } from "../features/boards/BoardLanding";
import { SoonPage } from "../features/future/PreviewUI";
import { Workspace } from "../features/boards/BoardWorkspace";
import { realtimeConnection } from "../realtime/connection";
import {
  realtimeEvents,
  type UserProfileChangedEvent,
  type BoardScopedEvent,
  type BoardSummaryChangedEvent,
} from "../realtime/events";
import { mergeBoardSummary } from "../realtime/reconcile";

const boardFromPath = (path: string) =>
  /^\/boards\/([0-9a-f-]{36})$/i.exec(path)?.[1] ?? null;

export default function App() {
  const [path, setPath] = useState(window.location.pathname),
    [session, setSession] = useState<AuthSession | null>(currentSession()),
    [starting, setStarting] = useState(true),
    [startupError, setStartupError] = useState(""),
    [notice, setNotice] = useState<{ message: string; tone: "default" | "warning" } | null>(null),
    [boards, setBoards] = useState<BoardListItemDto[]>([]),
    [loading, setLoading] = useState(false),
    [failure, setFailure] = useState("");
  const started = useRef(false);
  const notify = useCallback((message: string) => setNotice({ message, tone: "default" }), []);
  const warn = useCallback((message: string) => setNotice({ message, tone: "warning" }), []);
  const navigate = useCallback((next: string) => {
    window.history.pushState(null, "", next);
    setPath(next);
  }, []);
  useEffect(() => {
    const pop = () => setPath(window.location.pathname);
    window.addEventListener("popstate", pop);
    return () => window.removeEventListener("popstate", pop);
  }, []);
  useEffect(() => {
    if (started.current) return;
    started.current = true;
    void (async () => {
      try {
        let callbackError = "";
        let linked = false;
        if (window.location.pathname === "/auth/callback") {
          const params = new URLSearchParams(window.location.search),
            code = params.get("code"),
            error = params.get("error");
          linked = params.get("linked") === "google";
          window.history.replaceState(null, "", "/boards");
          setPath("/boards");
          if (error) {
            callbackError = errorMessage(new AuthApiError(error));
            setStartupError(callbackError);
          }
          if (code) {
            setSession(await exchangeGoogleCode(code));
            return;
          }
        }
        const restored = await restoreSession();
        setSession(restored);
        if (restored && callbackError) notify(callbackError);
        else if (restored && linked) notify("Google account linked");
      } catch (cause) {
        setStartupError(errorMessage(cause));
      } finally {
        setStarting(false);
      }
    })();
  }, [notify]);
  const loadBoards = useCallback(async (showLoading = true) => {
    if (showLoading) setLoading(true);
    setFailure("");
    try {
      setBoards(await boardApi.list());
    } catch (cause) {
      setFailure(errorMessage(cause));
      throw cause;
    } finally {
      if (showLoading) setLoading(false);
    }
  }, []);
  const onProfileUpdated = useCallback((profile: ProfileDto) => {
    setSession((current) => {
      if (!current) return current;
      const user = {
        id: profile.userId,
        email: profile.email,
        username: profile.username,
        displayName: profile.displayName,
        profileImageUrl: profile.profileImageUrl,
        profileImageVersion: profile.profileImageVersion,
      };
      reconcileSessionUser(user);
      return { ...current, user };
    });
  }, []);
  const boardLoaded = useCallback(
    (board: BoardDetailDto) =>
      setBoards((previous) => previous.map((item) =>
        item.id === board.id
          ? { ...item, title: board.title, updatedAt: board.updatedAt }
          : item)),
    [],
  );
  useEffect(() => {
    setSessionExpiredHandler(() => {
      setSession(null);
      setBoards([]);
      setStartupError("Your session expired. Please sign in again.");
      window.history.replaceState(null, "", "/login");
      setPath("/login");
    });
    return () => setSessionExpiredHandler(null);
  }, []);
  useEffect(() => {
    if (!starting && !session && !["/", "/login", "/register", "/privacy", "/terms"].includes(path)) {
      window.history.replaceState(null, "", "/login");
      setPath("/login");
    }
  }, [starting, session, path]);
  useEffect(() => {
    if (session) void loadBoards().catch(() => undefined);
  }, [session, loadBoards]);
  async function signOut(all: boolean) {
    try {
      if (all) await logoutEverywhere();
      else await logout();
      setSession(null);
      setBoards([]);
      navigate("/login");
    } catch (cause) {
      notify(errorMessage(cause));
    }
  }
  const boardId = boardFromPath(path);
  const accountSection = accountSectionForPath(path);
  useEffect(() => {
    if (path === "/account") {
      window.history.replaceState(null, "", "/account/profile");
      setPath("/account/profile");
    }
  }, [path]);
  const leaveBoard = useCallback(() => navigate("/boards"), [navigate]);
  async function renameBoard(boardId: string, title: string) {
    const updated = await boardApi.rename(boardId, title);
    setBoards((current) => current.map((item) => item.id === boardId
      ? { ...item, title: updated.title, updatedAt: updated.updatedAt }
      : item));
    notify("Board renamed");
  }
  async function deleteBoard(deletedId: string) {
    await boardApi.remove(deletedId);
    setBoards((current) => current.filter((item) => item.id !== deletedId));
    if (boardId === deletedId) navigate("/boards");
    notify("Board deleted");
  }
  useEffect(() => {
    if (!session) return;
    void realtimeConnection.start();
    return () => void realtimeConnection.stop();
  }, [session?.user.id]);
  useEffect(() => {
    if (!session) return;
    return realtimeConnection.onReconnected(() => loadBoards(false));
  }, [loadBoards, session?.user.id]);
  useEffect(() => {
    if (!session) return;
    return realtimeConnection.on<UserProfileChangedEvent>(realtimeEvents.userProfileChanged, (message) => {
      if (message.userId !== session.user.id) return;
      void profileApi.get().then(onProfileUpdated).catch(() => undefined);
    });
  }, [onProfileUpdated, session?.user.id]);
  useEffect(() => {
    if (!session) return;
    const removeChanged = realtimeConnection.on<BoardSummaryChangedEvent>(
      realtimeEvents.boardSummaryChanged,
      (message) => setBoards((current) => mergeBoardSummary(current, message)),
    );
    const removeDeleted = realtimeConnection.on<BoardScopedEvent>(
      realtimeEvents.boardSummaryRemoved,
      (message) => setBoards((current) =>
        current.filter((board) => board.id !== message.boardId)),
    );
    return () => {
      removeChanged();
      removeDeleted();
    };
  }, [session?.user.id]);
  useEffect(() => {
    if (!session || !boardId) return;
    return realtimeConnection.subscribeBoard(boardId);
  }, [boardId, session?.user.id]);
  if (path === "/privacy") return <PrivacyPolicyPage />;
  if (path === "/terms") return <TermsOfServicePage />;
  if (starting)
    return <div className="wk-startup"><Wordmark /><p role="status">Restoring your session…</p></div>;
  if (!session && path === "/") return <PublicHome />;
  if (!session)
    return (
      <AuthScreen
        mode={path === "/register" ? "register" : "login"}
        error={startupError}
        navigate={navigate}
        onSuccess={(value) => {
          setSession(value);
          setStartupError("");
          navigate("/boards");
        }}
      />
    );
  if (
    path === "/" ||
    path === "/login" ||
    path === "/register" ||
    path === "/auth/callback"
  ) {
    window.history.replaceState(null, "", "/boards");
    queueMicrotask(() => setPath("/boards"));
    return <div className="wk-startup"><Wordmark /><p role="status">Opening boards…</p></div>;
  }
  return (
    <AppShell
      user={session.user}
      boards={boards}
      activeBoardId={boardId}
      navigate={navigate}
      onRenameBoard={renameBoard}
      onDeleteBoard={deleteBoard}
      signOut={() => void signOut(false)}
      signOutEverywhere={() => void signOut(true)}
      notify={notify}
    >
      {path === "/library" ? <SoonPage area="Library" /> : path === "/library/pictures" ? <SoonPage area="Pictures" /> : path === "/journal" ? <SoonPage area="Journal" /> : path === "/tasks" ? <SoonPage area="Tasks" /> : accountSection ? (
        <AccountPanel
          user={session.user}
          section={accountSection}
          navigate={navigate}
          signOut={() => void signOut(false)}
          signOutEverywhere={() => void signOut(true)}
          notify={notify}
          onProfileUpdated={onProfileUpdated}
        />
      ) : boardId ? (
        <Workspace
          key={boardId}
          id={boardId}
          titleOverride={boards.find((item) => item.id === boardId)?.title}
          currentUserId={session.user.id}
          profileIdentityVersion={`${session.user.username}:${session.user.displayName ?? ""}:${session.user.profileImageVersion ?? ""}`}
          back={leaveBoard}
          boardLoaded={boardLoaded}
          notify={notify}
        />
      ) : (
        <BoardLanding
          boards={boards}
          displayName={session.user.displayName}
          loading={loading}
          failure={failure}
          retry={() => void loadBoards().catch(() => undefined)}
          navigate={navigate}
          onRenameBoard={renameBoard}
          onDeleteBoard={deleteBoard}
          onBoardLimitReached={() => warn("Board limit reached. You can have a maximum of 5 boards.")}
          create={async (title) => {
            try {
              const board = await boardApi.create(title);
              setBoards(await boardApi.list());
              notify("Board created");
              navigate(`/boards/${board.id}`);
            } catch (cause) {
              if (cause instanceof AuthApiError && cause.code === "board_limit_reached")
                void loadBoards(false).catch(() => undefined);
              throw cause;
            }
          }}
        />
      )}
      {notice && (
        <Notice message={notice.message} tone={notice.tone} onDismiss={() => setNotice(null)} />
      )}
    </AppShell>
  );
}
