import type { NoteDto } from "../../api";
import { boardBounds } from "./boardBounds.ts";

type SizedNote = Pick<NoteDto, "kind" | "title" | "content" | "width"> & Partial<Pick<NoteDto, "positionX" | "positionY">>;
type ChecklistText = Pick<NoteDto, "title">;

export const maxNoteWidth = 560;
export const maxNoteHeight = 640;
export const maxTaskHeight = 720;

export function clampDimension(value: number, minimum: number, maximum: number) {
  return Math.min(maximum, Math.max(minimum, Math.round(value)));
}

/** Content sets the floor; bounded text areas scroll only after the card reaches its cap. */
export function noteDimensionBounds(
  note: SizedNote,
  items: readonly ChecklistText[],
  candidateWidth = note.width,
) {
  const longestWord = Math.max(0, ...[note.title, ...items.map((item) => item.title)]
    .flatMap((text) => text.split(/\s+/).map((word) => word.length)));
  const minWidth = Math.min(360, Math.max(note.kind === 1 ? 260 : 220, 104 + longestWord * 8));
  const maxWidth = Math.min(maxNoteWidth, boardBounds.right - Math.min(note.positionX ?? 0, boardBounds.right - minWidth));
  const width = clampDimension(candidateWidth, minWidth, maxWidth);
  const heightLimit = (minimum: number, maximum: number) => Math.min(maximum,
    boardBounds.bottom - Math.min(note.positionY ?? 0, boardBounds.bottom - minimum));

  if (note.kind === 1) {
    const charactersPerLine = Math.max(12, Math.floor((width - 124) / 8));
    const rows = items.reduce((total, item) => {
      const lines = Math.min(3, Math.max(1, Math.ceil(item.title.length / charactersPerLine)));
      return total + Math.max(44, lines * 22 + 12);
    }, 0);
    const minHeight = Math.min(360, items.length ? 140 + rows : 144);
    return {
      minWidth,
      maxWidth,
      minHeight,
      maxHeight: heightLimit(minHeight, maxTaskHeight),
    };
  }

  const titleWidth = Math.max(12, Math.floor((width - 96) / 8));
  const bodyWidth = Math.max(12, Math.floor((width - 32) / 8));
  const titleLines = Math.min(3, Math.max(1, Math.ceil(note.title.length / titleWidth)));
  const bodyLines = Math.min(4, Math.max(1, note.content.split("\n")
    .reduce((total, line) => total + Math.max(1, Math.ceil(line.length / bodyWidth)), 0)));
  const minHeight = Math.max(160, 72 + titleLines * 22 + bodyLines * 24);
  return {
    minWidth,
    maxWidth,
    minHeight,
    maxHeight: heightLimit(minHeight, maxNoteHeight),
  };
}
