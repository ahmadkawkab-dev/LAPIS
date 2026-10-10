import { useState, type FormEvent } from "react";
import type { BoardListItemDto } from "../../api";
import { errorMessage } from "../../api";
import { Button } from "../../components/ui/Button";
import { Dialog } from "../../components/ui/Dialog";
import { Field } from "../../components/ui/Field";
import { BoardCardColorControl } from './BoardCardColorControl';
import type { BoardCardTone } from './boardCardTone';

export function BoardActionsDialog({ board, onClose, onRenameBoard, onDeleteBoard, onColorBoard }: {
  board: BoardListItemDto;
  onClose: () => void;
  onRenameBoard: (id: string, title: string) => Promise<void>;
  onDeleteBoard: (id: string) => Promise<void>;
  onColorBoard: (id: string, color: BoardCardTone | null, version: number) => Promise<void>;
}) {
  const [stage, setStage] = useState<"menu" | "rename" | "delete">("menu");
  const [title, setTitle] = useState(board.title);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const isOwner = board.role === 1;
  async function submitRename(event: FormEvent) {
    event.preventDefault();
    if (!title.trim()) return;
    setBusy(true);
    setError("");
    try {
      await onRenameBoard(board.id, title.trim());
      onClose();
    } catch (cause) {
      setError(errorMessage(cause));
    } finally {
      setBusy(false);
    }
  }
  async function deleteBoard() {
    setBusy(true);
    setError("");
    try {
      await onDeleteBoard(board.id);
      onClose();
    } catch (cause) {
      setError(errorMessage(cause));
    } finally {
      setBusy(false);
    }
  }
  return (
    <Dialog className="wk-board-actions-dialog" busy={busy} title={stage === "menu" ? isOwner ? "Board actions" : "Board appearance" : stage === "rename" ? "Rename board" : "Delete board"}
      urgent={stage === "delete"} onClose={() => { if (!busy) onClose(); }}>
      {stage === "menu" && (
        <div className="wk-rename-board">
          <p>Choose an action for <strong>{board.title}</strong>.</p>
          <BoardCardColorControl board={board} busy={busy} onBusyChange={setBusy} onChange={onColorBoard} />
          <div className="wk-dialog-actions">
            {isOwner && <Button disabled={busy} onClick={() => setStage("rename")}>Rename board</Button>}
            {isOwner && <Button disabled={busy} variant="danger" onClick={() => setStage("delete")}>Delete board</Button>}
            <Button variant="quiet" disabled={busy} onClick={onClose}>Done</Button>
          </div>
        </div>
      )}
      {stage === "rename" && (
        <form className="wk-rename-board" onSubmit={(event) => void submitRename(event)}>
          <Field label="Board name" value={title} maxLength={200} autoFocus
            onChange={(event) => setTitle(event.target.value)} />
          {error && <p className="wk-alert" role="alert">{error}</p>}
          <div className="wk-dialog-actions">
            <Button type="submit" loading={busy} disabled={!title.trim() || title.trim() === board.title}>
              Save name
            </Button>
            <Button variant="quiet" onClick={onClose} disabled={busy}>Cancel</Button>
          </div>
        </form>
      )}
      {stage === "delete" && (
        <div className="wk-rename-board">
          <p>Delete <strong>{board.title}</strong> and all its notes, tasks, and connections? This cannot be undone.</p>
          {error && <p className="wk-alert" role="alert">{error}</p>}
          <div className="wk-dialog-actions">
            <Button variant="danger" loading={busy} onClick={() => void deleteBoard()}>
              Delete board
            </Button>
            <Button variant="quiet" onClick={onClose} disabled={busy}>Cancel</Button>
          </div>
        </div>
      )}
    </Dialog>
  );
}
