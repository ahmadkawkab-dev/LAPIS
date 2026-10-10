import { useId, useState } from 'react';
import { AuthApiError, errorMessage, type BoardListItemDto } from '../../api';
import { Button } from '../../components/ui/Button';
import { boardCardTone, boardCardTones, type BoardCardTone } from './boardCardTone';
import { findColorPalette } from '../../theme/curatedPalettes';
import { useAppliedPaletteId, usePaletteAppMode } from '../../theme/paletteState';
import { boardVariationFor } from '../../theme/boardVariationState';

export function BoardCardColorControl({ board, busy, onBusyChange, onChange }: {
  board: BoardListItemDto; busy: boolean; onBusyChange: (value: boolean) => void;
  onChange: (id: string, color: BoardCardTone | null, version: number) => Promise<void>;
}) {
  const [pendingTone, setPendingTone] = useState<BoardCardTone | null | undefined>(undefined);
  const tone = pendingTone === undefined ? board.cardColor : pendingTone;
  const palette = findColorPalette(useAppliedPaletteId());
  const mode = usePaletteAppMode();
  const automatic = palette?.variants[mode][boardVariationFor(board.id)]?.name
    ?? boardCardTones.find(candidate => candidate.id === boardCardTone(board.id))!.name;
  const [error, setError] = useState('');
  const name = useId();
  async function change(value: BoardCardTone | null) {
    setPendingTone(value);
    onBusyChange(true);
    setError('');
    try { await onChange(board.id, value, board.cardColorVersion ?? 0); }
    catch (cause) {
      setError(cause instanceof AuthApiError && cause.code === 'board_appearance_conflict'
        ? 'Another member changed this color. The latest choice is shown; choose again.'
        : errorMessage(cause));
    } finally { setPendingTone(undefined); onBusyChange(false); }
  }
  return <fieldset className="wk-board-color-control" disabled={busy || !board.canEdit}>
    <legend>Board color</legend>
    <p>{palette ? 'Shared choice; colors follow each member’s palette. Note colors stay unchanged.' : 'Shared with board members. Brightness adapts to each member’s theme; note colors stay unchanged.'}</p>
    <div className="wk-board-color-options">
      {boardCardTones.map((candidate, index) => <label key={candidate.id} data-board-tone={candidate.id}>
        <input type="radio" name={name} value={candidate.id} checked={tone === candidate.id}
          onChange={() => void change(candidate.id)} />
        <span><i aria-hidden="true" style={palette ? { background: palette.variants[mode][index].card, border: '1px solid var(--control-border)' } : undefined} />{palette?.variants[mode][index].name ?? candidate.name}</span>
      </label>)}
    </div>
    <Button variant="quiet" size="compact" disabled={busy || !board.cardColor || !board.canEdit} onClick={() => void change(null)}>Use automatic color</Button>
    <p className="wk-board-color-status" role={error ? 'alert' : 'status'}>{error || (busy ? 'Saving color…' : board.cardColor ? 'Color choices apply to this board, Home, and Boards.' : `Automatic color: ${automatic}. Choose an accent to customize.`)}</p>
  </fieldset>;
}
