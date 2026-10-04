import { useState } from 'react';
import type { ConnectionDto, ConnectionSide, NoteDto } from '../../../api';
import { Button } from '../../../components/ui/Button';
import { Dialog } from '../../../components/ui/Dialog';

type Endpoints = { sourceNoteId: string; targetNoteId: string; sourceHandle: ConnectionSide; targetHandle: ConnectionSide };

export function ConnectionEditor({ edge, notes, save, close }: {
  edge: ConnectionDto; notes: NoteDto[]; save: (endpoints: Endpoints) => Promise<boolean>; close: () => void;
}) {
  const [value, setValue] = useState<Endpoints>({ sourceNoteId: edge.sourceNoteId, targetNoteId: edge.targetNoteId,
    sourceHandle: edge.sourceHandle ?? 'right', targetHandle: edge.targetHandle ?? 'left' });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  async function submit(event: React.FormEvent) {
    event.preventDefault();
    if (busy) return;
    setBusy(true); setError('');
    try {
      if (await save(value)) close();
      else setError('Could not update the connection. Check the cards and try again.');
    } finally { setBusy(false); }
  }
  return <Dialog title="Edit connection" busy={busy} onClose={close}>
    <p>Choose the cards and the side of each card where the connection attaches.</p>
    <form onSubmit={(event) => void submit(event)} className="connection-editor-form">
      {(['source', 'target'] as const).map((endpoint) => <fieldset disabled={busy} key={endpoint}>
        <legend>{endpoint === 'source' ? 'From' : 'To'}</legend>
        <label>Card<select value={value[`${endpoint}NoteId`]} required onChange={(event) => setValue({ ...value, [`${endpoint}NoteId`]: event.target.value })}>
          {notes.map((note) => <option key={note.id} value={note.id}>{note.title || 'Untitled note'}</option>)}
        </select></label>
        <label>Side<select value={value[`${endpoint}Handle`]} onChange={(event) => setValue({ ...value, [`${endpoint}Handle`]: event.target.value })}>
          {(['top', 'right', 'bottom', 'left'] as const).map((side) => <option key={side} value={side}>{side[0].toUpperCase() + side.slice(1)}</option>)}
        </select></label>
      </fieldset>)}
      {error && <p className="wk-alert" role="alert">{error}</p>}
      <div className="wk-dialog-actions"><Button variant="quiet" disabled={busy} onClick={close}>Cancel</Button>
        <Button type="submit" loading={busy} loadingLabel="Saving…">Save connection</Button></div>
    </form>
  </Dialog>;
}
