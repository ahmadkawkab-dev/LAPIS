export const boardCardTones = [
  { id: 'sage', name: 'Sage' }, { id: 'blue', name: 'Blue' },
  { id: 'lavender', name: 'Lavender' }, { id: 'clay', name: 'Clay' },
  { id: 'gold', name: 'Gold' }, { id: 'rose', name: 'Rose' },
] as const;
export type BoardCardTone = typeof boardCardTones[number]['id'];
/** Stable presentation only: a board keeps its accent when lists are reordered. */
export function boardCardTone(id: string): BoardCardTone {
  let hash = 0;
  for (const character of id) hash = (hash * 31 + character.charCodeAt(0)) >>> 0;
  return (['sage', 'blue', 'lavender'] as const)[hash % 3];
}
