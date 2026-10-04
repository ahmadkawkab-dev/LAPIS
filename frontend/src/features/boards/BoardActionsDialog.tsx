import { useState, type FormEvent } from "react";
import type { BoardListItemDto } from "../../api";
import { errorMessage } from "../../api";
import { Button } from "../../components/ui/Button";
import { Dialog } from "../../components/ui/Dialog";
import { Field } from "../../components/ui/Field";

export function BoardActionsDialog({ board, onClose, onRenameBoard, onDeleteBoard }: {
  board: BoardListItemDto;
  onClose: () => void;
  onRenameBoard: (id: string, title: string) => Promise<void>;
  onDeleteBoard: (id: string) => Promise<void>;
}) {
  const [stage, setStage] = useState<"menu" | "rename" | "delete">("menu");
  const [title, setTitle] = useState(board.title);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
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
    <Dialog busy={busy} title={stage === "menu" ? "Board actions" : stage === "rename" ? "Rename board" : "Delete board"}
      urgent={stage === "delete"} onClose={() => { if (!busy) onClose(); }}>
      {stage === "menu" && (
        <div className="wk-rename-board">
          <p>Choose an action for <strong>{board.title}</strong>.</p>
          <div className="wk-dialog-actions">
            <Button onClick={() => setStage("rename")}>Rename board</Button>
            <Button variant="danger" onClick={() => setStage("delete")}>Delete board</Button>
            <Button variant="quiet" onClick={onClose}>Cancel</Button>
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
