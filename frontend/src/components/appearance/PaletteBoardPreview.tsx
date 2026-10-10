import type { CSSProperties } from 'react';
import type { CoordinatedPalette } from '../../theme/coordinatedPalettes';
import type { PaletteMode } from '../../theme/curatedPalettes';
import { noteAppearanceStyle, notePigments } from '../../features/boards/noteAppearance';

export function PaletteBoardPreview({ palette, mode }: { palette: CoordinatedPalette; mode: PaletteMode }) {
  const variants = palette.variants[mode];
  return <div className="wk-palette-board-examples">
    <h4>One palette, six board identities</h4>
    <div className="wk-palette-board-grid">
      {variants.map((variant, index) => <article key={index} className="wk-palette-board-example"
        style={{ '--sample-board': variant.card, '--sample-chrome': variant.chrome, '--sample-action': variant.action } as CSSProperties}>
        <span aria-hidden="true" className="wk-palette-board-example-mark" />
        <strong>Board {String.fromCharCode(65 + index)}</strong><p>{variant.name}</p>
      </article>)}
    </div>
    <section className="wk-palette-canvas-example" aria-label="Sample board canvas"
      style={{ '--sample-chrome': variants[0].chrome, '--sample-canvas': variants[0].canvas } as CSSProperties}>
      <header><strong>A place for ideas</strong><span>{variants[0].name}</span></header>
      <div>{notePigments.map(note => <article key={note.name} style={noteAppearanceStyle(note.value)}><strong>{note.name}</strong><p>A colorful thought</p></article>)}</div>
    </section>
  </div>;
}
