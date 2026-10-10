import { assignBoardVariations, validBoardId, validVariant, type BoardAssignments, type VariationBoard } from './boardVariations';
import { findColorPalette } from './curatedPalettes';
import { appliedPaletteId, resolvedPaletteMode, subscribePalette } from './paletteState';

export const boardVariationStoragePrefix = 'wukna.board-variations.v1:';
let account: string | null = null;
let boards: readonly VariationBoard[] = [];
let history: BoardAssignments = Object.create(null);
let assignments: BoardAssignments = Object.create(null);
let sheet: HTMLStyleElement | null = null;
let unsubscribe: (() => void) | null = null;
const memory = new Map<string, BoardAssignments>();
function readHistory(id: string): BoardAssignments {
  try {
    const saved = localStorage.getItem(boardVariationStoragePrefix + id);
    if (saved === null) return memory.get(id) ?? Object.create(null);
    const parsed: unknown = JSON.parse(saved);
    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed))
      return Object.fromEntries(Object.entries(parsed).slice(0, 2048).filter(([key, value]) => validBoardId(key) && validVariant(value)));
  } catch { /* Keep assignments stable in memory when storage is blocked. */ }
  return memory.get(id) ?? Object.create(null);
}
function paint() {
  const palette = findColorPalette(appliedPaletteId());
  if (!palette || !account) { sheet?.remove(); sheet = null; return; }
  const mode = resolvedPaletteMode();
  const css = Object.entries(assignments).map(([id, index]) => {
    const variant = palette.variants[mode][index];
    const selector = `[data-board-id="${id}"]`;
    return `${selector}{--board-variation-card:${variant.card};--board-variation-canvas:${variant.canvas};--board-variation-chrome:${variant.chrome};--board-variation-action:${variant.action};--board-variation-indicator:${variant.indicator};}
      .workspace${selector}{--surface:${variant.chrome};--canvas:${variant.canvas};--primary:${variant.action};--selected-foreground:${variant.action};--focus:${variant.action};--graph-edge-selected:${variant.action};}`;
  }).join('\n');
  if (!sheet) { sheet = document.createElement('style'); sheet.id = 'wk-board-variations'; document.head.append(sheet); }
  // CSS updates repaint colors without remounting the canvas or touching its state.
  if (sheet.textContent !== css) sheet.textContent = css;
}
function reconcile() {
  if (!account) { paint(); return; }
  assignments = assignBoardVariations(boards, history);
  history = { ...history, ...assignments };
  // Keep active relationships first and retain bounded history for returning boards.
  history = Object.fromEntries([...Object.entries(assignments), ...Object.entries(history).filter(([id]) => !(id in assignments))].slice(0, 2048));
  memory.set(account, history);
  const serialized = JSON.stringify(history);
  try {
    if (localStorage.getItem(boardVariationStoragePrefix + account) !== serialized)
      localStorage.setItem(boardVariationStoragePrefix + account, serialized);
  } catch { /* Current-session choices remain stable. */ }
  paint();
}
function onStorage(event: StorageEvent) {
  if (account && (event.key === null || event.key === boardVariationStoragePrefix + account)) {
    history = readHistory(account); reconcile();
  }
}
export function initializeBoardVariations() {
  if (unsubscribe) return;
  unsubscribe = subscribePalette(paint);
  window.addEventListener('storage', onStorage);
}
export function syncBoardVariations(accountId: string | null, current: readonly VariationBoard[]) {
  initializeBoardVariations();
  if (account !== accountId) { account = accountId; history = account ? readHistory(account) : Object.create(null); }
  boards = current;
  reconcile();
}
export const boardVariationFor = (boardId: string) => assignments[boardId];
export function disposeBoardVariations() {
  unsubscribe?.(); unsubscribe = null;
  window.removeEventListener('storage', onStorage);
  sheet?.remove(); sheet = null;
  account = null; boards = []; history = Object.create(null); assignments = Object.create(null);
  memory.clear();
}
if (import.meta.hot) import.meta.hot.dispose(disposeBoardVariations);
