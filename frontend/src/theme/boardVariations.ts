import { boardCardTones, type BoardCardTone } from '../features/boards/boardCardTone.ts';

export const boardVariationCount = 6;
export const validBoardId = (id: string) => /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i.test(id);
export type VariationBoard = { id: string; cardColor?: BoardCardTone | null };
export type BoardAssignments = Record<string, number>;
export const validVariant = (value: unknown): value is number => Number.isInteger(value) && Number(value) >= 0 && Number(value) < boardVariationCount;
function hash(id: string) {
  let result = 0;
  for (const character of id) result = (result * 31 + character.charCodeAt(0)) >>> 0;
  return result;
}
/** Existing relationships win; new automatic boards take unused, then least-used slots. */
export function assignBoardVariations(boards: readonly VariationBoard[], previous: BoardAssignments = {}): BoardAssignments {
  const assignments: BoardAssignments = Object.create(null);
  const counts = Array.from({ length: boardVariationCount }, () => 0);
  const unique = [...new Map(boards.filter(board => validBoardId(board.id)).map(board => [board.id, board])).values()].sort((a, b) => a.id.localeCompare(b.id));
  for (const board of unique) {
    const explicit = boardCardTones.findIndex(tone => tone.id === board.cardColor);
    const variant = explicit >= 0 ? explicit : previous[board.id];
    if (validVariant(variant)) { assignments[board.id] = variant; counts[variant]++; }
  }
  for (const board of unique) {
    if (board.id in assignments) continue;
    const start = hash(board.id) % boardVariationCount;
    const available = Array.from({ length: boardVariationCount }, (_, index) => (start + index) % boardVariationCount);
    const variant = available.find(index => counts[index] === 0)
      ?? available.find(index => counts[index] === Math.min(...counts))!;
    assignments[board.id] = variant;
    counts[variant]++;
  }
  return assignments;
}
