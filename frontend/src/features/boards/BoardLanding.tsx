import { useId, useRef, useState, type FormEvent, type MouseEvent } from "react";
import {
  CheckCircle2,
  MoreHorizontal,
  Plus,
  Search,
  Users,
} from "lucide-react";
import type { BoardListItemDto } from "../../api";
import { AuthApiError, errorMessage } from "../../api";
import { BrandMark } from "../../components/brand/BrandMark";
import { Button } from "../../components/ui/Button";
import { BoardActionsDialog } from "./BoardActionsDialog";
import { BoardPreview } from "./BoardPreview";

const MAX_OWNED_BOARDS = 5;
const LIMIT_WARNING_COOLDOWN_MS = 10_000;

const relativeTimeFormatter = new Intl.RelativeTimeFormat(undefined, {
  numeric: "auto",
});

function plural(value: number, singular: string, pluralForm = `${singular}s`) {
  return `${value} ${value === 1 ? singular : pluralForm}`;
}

function relativeTime(value: string) {
  const date = new Date(value);
  const elapsedSeconds = Math.round((date.getTime() - Date.now()) / 1000);
  const ranges = [
    [60, "second"],
    [60, "minute"],
    [24, "hour"],
    [7, "day"],
    [4.345, "week"],
    [12, "month"],
    [Number.POSITIVE_INFINITY, "year"],
  ] as const;
  let duration = elapsedSeconds;

  for (const [range, unit] of ranges) {
    if (Math.abs(duration) < range) {
      return relativeTimeFormatter.format(Math.round(duration), unit);
    }
    duration /= range;
  }

  return date.toLocaleDateString();
}

function BoardCard({
  board,
  navigate,
  onRenameBoard,
  onDeleteBoard,
}: {
  board: BoardListItemDto;
  navigate: (path: string) => void;
  onRenameBoard: (id: string, title: string) => Promise<void>;
  onDeleteBoard: (id: string) => Promise<void>;
}) {
  const [actionsOpen, setActionsOpen] = useState(false);
  const titleId = useId();
  const detailsId = useId();
  const roleId = useId();
  const isOwner = board.role === 1;
  const role = isOwner ? "Owner" : board.canEdit ? "Editor" : "Viewer";
  const counts = [
    plural(board.noteCount, "note"),
    plural(board.taskListCount, "task list"),
  ];

  return (
    <div className="wk-board-card-wrap">
    <a
      href={`/boards/${board.id}`}
      className="wk-board-card"
      aria-labelledby={titleId}
      aria-describedby={`${detailsId} ${roleId}`}
      onClick={(event: MouseEvent<HTMLAnchorElement>) => {
        if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
        event.preventDefault();
        navigate(`/boards/${board.id}`);
      }}
    >
      <BoardPreview
        nodes={board.previewNodes}
        connections={board.previewConnections}
      />
      <div className="wk-board-card-heading">
        <h3 id={titleId}>{board.title}</h3>
      </div>
      <div className="wk-board-card-counts" id={detailsId}>
        <span>{counts.join(" · ")}</span>
        {board.taskItemCount > 0 ? (
          <span className="wk-board-card-progress">
            <CheckCircle2 size={14} aria-hidden="true" />
            {board.completedTaskItemCount}/{board.taskItemCount} tasks
          </span>
        ) : null}
      </div>
      <div className="wk-board-card-footer">
        <span className="wk-board-role" id={roleId}>{role}</span>
        <span className="wk-board-card-members"><Users size={14} aria-hidden="true" />{plural(board.memberCount, "member")}</span>
        <span className="wk-board-card-activity">
          <time dateTime={board.updatedAt} title={new Date(board.updatedAt).toLocaleString()}>
            Updated {relativeTime(board.updatedAt)}
          </time>
        </span>
      </div>
    </a>
    {isOwner && (
      <div className="wk-board-card-actions">
        <Button variant="quiet" size="compact" onClick={() => setActionsOpen(true)}
          aria-label={`Actions for ${board.title}`}><MoreHorizontal size={16} aria-hidden="true" /> Manage board</Button>
      </div>
    )}
    {actionsOpen && (
      <BoardActionsDialog board={board} onClose={() => setActionsOpen(false)}
        onRenameBoard={onRenameBoard} onDeleteBoard={onDeleteBoard} />
    )}
    </div>
  );
}

function BoardGrid({
  boards,
  navigate,
  onRenameBoard,
  onDeleteBoard,
}: {
  boards: BoardListItemDto[];
  navigate: (path: string) => void;
  onRenameBoard: (id: string, title: string) => Promise<void>;
  onDeleteBoard: (id: string) => Promise<void>;
}) {
  return (
    <div className="wk-board-grid">
      {boards.map((board) => (
        <BoardCard key={board.id} board={board} navigate={navigate}
          onRenameBoard={onRenameBoard} onDeleteBoard={onDeleteBoard} />
      ))}
    </div>
  );
}

function SkeletonCards() {
  return (
    <div className="wk-board-grid" aria-hidden="true">
      {[0, 1, 2].map((item) => (
        <div className="wk-board-card wk-board-card--skeleton" key={item}>
          <div className="wk-board-preview" />
          <span className="wk-skeleton-line wk-skeleton-line--title" />
          <span className="wk-skeleton-line" />
          <span className="wk-skeleton-line wk-skeleton-line--short" />
        </div>
      ))}
    </div>
  );
}

export function BoardLanding({
  boards,
  loading,
  failure,
  retry,
  navigate,
  create,
  onBoardLimitReached,
  onRenameBoard,
  onDeleteBoard,
}: {
  boards: BoardListItemDto[];
  loading: boolean;
  failure: string;
  retry: () => void;
  navigate: (path: string) => void;
  create: (title: string) => Promise<void>;
  onBoardLimitReached: () => void;
  onRenameBoard: (id: string, title: string) => Promise<void>;
  onDeleteBoard: (id: string) => Promise<void>;
}) {
  const [query, setQuery] = useState("");
  const [creating, setCreating] = useState(false);
  const [title, setTitle] = useState("");
  const [createError, setCreateError] = useState("");
  const [busy, setBusy] = useState(false);
  const createTrigger = useRef<HTMLButtonElement>(null);
  const lastLimitWarningAt = useRef(0);
  const normalizedQuery = query.trim().toLocaleLowerCase();
  const shown = normalizedQuery
    ? boards.filter((board) =>
        board.title.toLocaleLowerCase().includes(normalizedQuery),
      )
    : boards;
  const owned = shown.filter((board) => board.role === 1);
  const shared = shown.filter((board) => board.role !== 1);
  const atBoardLimit = boards.filter((board) => board.role === 1).length >= MAX_OWNED_BOARDS;

  function warnAboutLimit() {
    const now = Date.now();
    if (now - lastLimitWarningAt.current < LIMIT_WARNING_COOLDOWN_MS) return;
    lastLimitWarningAt.current = now;
    onBoardLimitReached();
  }

  function openCreateForm() {
    if (atBoardLimit) {
      warnAboutLimit();
      return;
    }
    setCreating(true);
  }

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (atBoardLimit) {
      setCreating(false);
      warnAboutLimit();
      return;
    }
    const nextTitle = title.trim();
    if (busy || !nextTitle) return;
    setBusy(true);
    setCreateError("");
    try {
      await create(nextTitle);
      setTitle("");
      setCreating(false);
    } catch (cause) {
      if (cause instanceof AuthApiError && cause.code === "board_limit_reached") {
        setCreating(false);
        setCreateError("");
        warnAboutLimit();
      } else {
        setCreateError(errorMessage(cause));
      }
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="wk-board-landing" aria-labelledby="wk-boards-title" aria-busy={loading}>
      <div className="wk-board-landing-inner">
        <header className="wk-board-landing-header">
          <div>
            <span className="wk-board-landing-eyebrow">Your workspace</span>
            <h1 id="wk-boards-title">Boards</h1>
            <p>A space for your ideas, notes and shared work.</p>
          </div>
          <Button ref={createTrigger} onClick={openCreateForm} aria-disabled={atBoardLimit} aria-expanded={creating} aria-controls="wk-create-board">
            <Plus size={18} aria-hidden="true" />
            New board
          </Button>
        </header>

        {creating ? (
          <form id="wk-create-board" className="wk-create-board" onSubmit={submit}>
            <div>
              <label htmlFor="new-board-title">Name your new board</label>
              <input
                id="new-board-title"
                autoFocus
                value={title}
                maxLength={200}
                placeholder="For example, Autumn campaign"
                aria-invalid={createError ? "true" : undefined}
                aria-describedby={createError ? "new-board-error" : undefined} disabled={busy} required
                onChange={(event) => setTitle(event.target.value)}
              />
              {createError ? <p id="new-board-error" role="alert">{createError}</p> : null}
            </div>
            <Button type="submit" loading={busy} disabled={!title.trim()} aria-disabled={atBoardLimit}>
              Create board
            </Button>
            <Button
              variant="quiet" disabled={busy}
              onClick={() => {
                setCreating(false);
                setCreateError("");
                createTrigger.current?.focus();
              }}
            >
              Cancel
            </Button>
          </form>
        ) : null}

        <div className="wk-board-library-toolbar"><div className="wk-board-search">
          <Search size={16} aria-hidden="true" />
          <input
            type="search"
            value={query}
            aria-label="Search your boards"
            placeholder="Search your boards…"
            onChange={(event) => setQuery(event.target.value)}
          />
        </div>
          {!loading && !failure && <span className="wk-board-library-summary" role="status">{plural(shown.length, "board")}{normalizedQuery ? " found" : " in your workspace"}</span>}
        </div>

        <div className="wk-board-landing-body">
        {loading ? (
          <div role="status" aria-live="polite">
            <span className="wk-sr-only">Loading boards…</span>
            <SkeletonCards />
          </div>
        ) : failure ? (
          <div className="wk-board-state wk-board-state--error" role="alert">
            <h2>Could not load your boards.</h2>
            <p>{failure}</p>
            <Button variant="secondary" onClick={retry}>Try again</Button>
          </div>
        ) : normalizedQuery && shown.length === 0 ? (
          <div className="wk-board-state">
            <Search size={24} aria-hidden="true" />
            <h2>No boards match “{query.trim()}”.</h2>
            <p>Try a different title or clear your search.</p>
            <Button variant="quiet" onClick={() => setQuery("")}>Clear search</Button>
          </div>
        ) : (
          <div className="wk-board-groups">
            <section className="wk-board-group" aria-labelledby="owned-boards-heading">
              <div className="wk-board-section-heading">
                <div className="wk-board-section-title">
                  <h2 id="owned-boards-heading">Your boards</h2>
                  <span aria-label={`${owned.length} ${owned.length === 1 ? "board" : "boards"}`}>{owned.length}</span>
                </div>
                <p>The boards you create and manage.</p>
              </div>
              {owned.length > 0 ? (
                <BoardGrid boards={owned} navigate={navigate}
                  onRenameBoard={onRenameBoard} onDeleteBoard={onDeleteBoard} />
              ) : (
                <div className="wk-board-state wk-board-state--primary">
                  <BrandMark />
                  <h3>{normalizedQuery ? "No matching boards of your own" : "Your Wukna starts here"}</h3>
                  <p>{normalizedQuery ? "Try another title to find one of your boards." : "Create your first board and begin gathering ideas."}</p>
                  {!normalizedQuery && <Button onClick={openCreateForm} aria-disabled={atBoardLimit}>
                    <Plus size={18} aria-hidden="true" />
                    Create your first board
                  </Button>}
                </div>
              )}
            </section>

            <section className="wk-board-group" aria-labelledby="shared-boards-heading">
              <div className="wk-board-section-heading">
                <div className="wk-board-section-title">
                  <h2 id="shared-boards-heading">Shared with you</h2>
                  <span aria-label={`${shared.length} shared ${shared.length === 1 ? "board" : "boards"}`}>{shared.length}</span>
                </div>
                <p>Work together in spaces shared with you.</p>
              </div>
              {shared.length > 0 ? (
                <BoardGrid boards={shared} navigate={navigate}
                  onRenameBoard={onRenameBoard} onDeleteBoard={onDeleteBoard} />
              ) : (
                <p className="wk-board-secondary-empty">{normalizedQuery ? "No shared boards match this search." : "Nothing has been shared with you yet."}</p>
              )}
            </section>
          </div>
        )}
        </div>
      </div>
    </section>
  );
}
