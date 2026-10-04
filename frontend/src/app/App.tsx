import { lazy, Suspense, useCallback, useEffect, useRef, useState } from "react";
import {
  currentSession,
  exchangeGoogleCode,
  logout,
  logoutEverywhere,
  bootstrapSession,
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
const AccountPanel = lazy(() => import("../features/account/AccountPanel").then((module) => ({ default: module.AccountPanel })));
import { accountSectionForPath } from "../features/account/accountRoute";
import { Wordmark } from "../components/brand/Wordmark";
import { AppShell } from "../components/navigation/AppShell";
import { Button } from "../components/ui/Button";
import { LoadingSkeleton } from "../components/ui/LoadingSkeleton";
import { Notice } from "../components/ui/Notice";
import { BoardLanding } from "../features/boards/BoardLanding";
import { DashboardPage } from "../features/dashboard/DashboardPage";
const TasksPage = lazy(() => import("../features/tasks/TasksPage").then((module) => ({ default: module.TasksPage })));
const TemplatesPage = lazy(() => import("../features/tasks/TemplatesPage").then((module) => ({ default: module.TemplatesPage })));
const CalendarPage = lazy(() => import("../features/calendar/CalendarPage").then((module) => ({ default: module.CalendarPage })));
import { rememberNotificationReturnPath, takeNotificationReturnPath } from "../features/notifications/notificationRoutes";
import { NotificationRuntime } from "../features/notifications/NotificationRuntime";
import { clearBrowserNotificationSession } from "../features/notifications/browserPush";
const NotificationsPage = lazy(() => import("../features/notifications/NotificationsPage").then((module) => ({ default: module.NotificationsPage })));
import { SoonPage } from "../features/future/PreviewUI";
const Workspace = lazy(() => import("../features/boards/BoardWorkspace").then((module) => ({ default: module.Workspace })));
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
const taskFromPath = (path: string) =>
  /^\/tasks\/([0-9a-f-]{36})$/i.exec(path)?.[1] ?? null;

export default function App() {
  const [path, setPath] = useState(window.location.pathname),
    [session, setSession] = useState<AuthSession | null>(currentSession()),
    [starting, setStarting] = useState(true),
    [startupError, setStartupError] = useState(""),
    [startupBlocked, setStartupBlocked] = useState(false),
    [notice, setNotice] = useState<{ message: string; tone: "default" | "warning" } | null>(null),
    [boards, setBoards] = useState<BoardListItemDto[]>([]),
    [loading, setLoading] = useState(false),
    [failure, setFailure] = useState("");
  const started = useRef(false);
  const returnPath = useRef<string | null>(null);
  const notify = useCallback((message: string) => setNotice({ message, tone: "default" }), []);
  const warn = useCallback((message: string) => setNotice({ message, tone: "warning" }), []);
  const navigate = useCallback((next: string) => {
    window.history.pushState(null, "", next);
    setPath(new URL(next, window.location.origin).pathname);
    window.dispatchEvent(new Event("wukna:navigation"));
  }, []);
  useEffect(() => {
    const pop = () => setPath(window.location.pathname);
    window.addEventListener("popstate", pop);
    return () => window.removeEventListener("popstate", pop);
  }, []);
  const initializeSession = useCallback(async () => {
    setStarting(true);
    setStartupBlocked(false);
    setStartupError("");
    try {
      let callbackError = "";
      let linked = false;
      if (window.location.pathname === "/auth/callback") {
        const params = new URLSearchParams(window.location.search),
          code = params.get("code"),
          error = params.get("error");
        linked = params.get("linked") === "google";
        window.history.replaceState(null, "", "/home");
        setPath("/home");
        if (error) {
          callbackError = errorMessage(new AuthApiError(error));
          setStartupError(callbackError);
        }
        if (code) {
          setSession(await exchangeGoogleCode(code));
          navigate(takeNotificationReturnPath() ?? "/home");
          return;
        }
      }
      const restored = await bootstrapSession();
      setSession(restored);
      if (restored && window.location.pathname === "/home") { const target = takeNotificationReturnPath(); if (target) navigate(target); }
      if (restored && callbackError) notify(callbackError);
      else if (restored && linked) notify("Google account linked");
    } catch (cause) {
      setStartupBlocked(true);
      setStartupError(errorMessage(cause));
    } finally {
      setStarting(false);
    }
  }, [notify, navigate]);
  useEffect(() => {
    if (started.current) return;
    started.current = true;
    void initializeSession();
  }, [initializeSession]);
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
      const target = window.location.pathname + window.location.search;
      returnPath.current = rememberNotificationReturnPath(target);
      window.history.replaceState(null, "", "/login");
      setPath("/login");
    });
    return () => setSessionExpiredHandler(null);
  }, []);
  useEffect(() => {
    if (!starting && !startupBlocked && !session && !["/", "/login", "/register", "/privacy", "/terms"].includes(path)) {
      const target = window.location.pathname + window.location.search;
      returnPath.current = rememberNotificationReturnPath(target);
      window.history.replaceState(null, "", "/login");
      setPath("/login");
    }
  }, [starting, startupBlocked, session, path]);
  useEffect(() => {
    if (!starting && !startupBlocked && session) void loadBoards().catch(() => undefined);
  }, [starting, startupBlocked, session, loadBoards]);
  async function signOut(all: boolean) {
    try {
      await clearBrowserNotificationSession(true, session?.user.id).catch(() => undefined);
      if (all) await logoutEverywhere();
      else await logout();
      takeNotificationReturnPath(); returnPath.current = null;
      setSession(null);
      setBoards([]);
      navigate("/login");
    } catch (cause) {
      notify(errorMessage(cause));
    }
  }
  const boardId = boardFromPath(path);
  const taskId = taskFromPath(path);
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
  async function createBoard(title: string) {
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
  }
  useEffect(() => {
    if (starting || startupBlocked || !session) return;
    void realtimeConnection.start();
    return () => void realtimeConnection.stop();
  }, [starting, startupBlocked, session?.user.id]);
  useEffect(() => {
    if (starting || startupBlocked || !session) return;
    return realtimeConnection.onReconnected(() => loadBoards(false));
  }, [starting, startupBlocked, loadBoards, session?.user.id]);
  useEffect(() => {
    if (starting || startupBlocked || !session) return;
    return realtimeConnection.on<UserProfileChangedEvent>(realtimeEvents.userProfileChanged, (message) => {
      if (message.userId !== session.user.id) return;
      void profileApi.get().then(onProfileUpdated).catch(() => undefined);
    });
  }, [starting, startupBlocked, onProfileUpdated, session?.user.id]);
  useEffect(() => {
    if (starting || startupBlocked || !session) return;
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
  }, [starting, startupBlocked, session?.user.id]);
  useEffect(() => {
    if (starting || startupBlocked || !session || !boardId) return;
    return realtimeConnection.subscribeBoard(boardId);
  }, [starting, startupBlocked, boardId, session?.user.id]);
  if (path === "/privacy") return <PrivacyPolicyPage />;
  if (path === "/terms") return <TermsOfServicePage />;
  if (starting)
    return <div className="wk-startup"><Wordmark /><p role="status">Restoring your session…</p></div>;
  if (startupBlocked)
    return <div className="wk-startup"><Wordmark /><h1>Could not restore your session</h1>
      <p role="alert">{startupError}</p>
      <Button onClick={() => void initializeSession()}>Try again</Button>
    </div>;
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
          navigate(returnPath.current ?? takeNotificationReturnPath() ?? "/home"); returnPath.current = null; takeNotificationReturnPath();
        }}
      />
    );
  if (
    path === "/" ||
    path === "/login" ||
    path === "/register" ||
    path === "/auth/callback"
  ) {
    window.history.replaceState(null, "", "/home");
    queueMicrotask(() => setPath("/home"));
    return <div className="wk-startup"><Wordmark /><p role="status">Opening your workspace…</p></div>;
  }
  return (
    <AppShell
      user={session.user}
      boards={boards}
      activeBoardId={boardId}
      navigate={navigate}
      onCreateBoard={createBoard}
      onRenameBoard={renameBoard}
      onDeleteBoard={deleteBoard}
      signOut={() => void signOut(false)}
      signOutEverywhere={() => void signOut(true)}
      notify={notify}
    >
      <div className="wk-feedback-stack">
        <NotificationRuntime key={session.user.id} userId={session.user.id} navigate={navigate} />
        {notice && <Notice message={notice.message} tone={notice.tone} onDismiss={() => setNotice(null)} />}
      </div>
      <Suspense fallback={<LoadingSkeleton label="Opening page" layout={path === "/calendar" ? "calendar" : path === "/tasks" ? "week" : "list"} />}>
      {path === "/home" ? <DashboardPage user={session.user} boards={boards} boardsLoading={loading} boardsError={failure}
        retryBoards={() => void loadBoards().catch(() => undefined)} navigate={navigate} notify={notify} />
      : path === "/library" ? <SoonPage area="Library" /> : path === "/library/pictures" ? <SoonPage area="Pictures" /> : path === "/journal" ? <SoonPage area="Journal" /> : path === "/tasks/templates" ? <TemplatesPage notify={notify} navigate={navigate} />
      : path === "/notifications" ? <NotificationsPage navigate={navigate} notify={notify} />
      : path === "/tasks" || path === "/tasks/quick" || taskId ? <TasksPage key={path} notify={notify} openTaskId={taskId}
        initialView={path === "/tasks/quick" ? "inbox" : "week"}
        onCloseLinked={() => navigate("/tasks")} navigate={navigate} /> : path === "/calendar" ? <CalendarPage notify={notify} navigate={navigate} /> : accountSection ? (
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
          loading={loading}
          failure={failure}
          retry={() => void loadBoards().catch(() => undefined)}
          navigate={navigate}
          onRenameBoard={renameBoard}
          onDeleteBoard={deleteBoard}
          onBoardLimitReached={() => warn("Board limit reached. You can have a maximum of 5 boards.")}
          create={createBoard}
        />
      )}
      </Suspense>
    </AppShell>
  );
}
