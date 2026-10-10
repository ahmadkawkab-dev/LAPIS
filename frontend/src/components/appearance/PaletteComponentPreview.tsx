import { useState, type CSSProperties } from 'react';
import { CheckCircle2, Info, Palette, TriangleAlert } from 'lucide-react';
import { Button } from '../ui/Button';
import { noteAppearanceStyle, notePigments } from '../../features/boards/noteAppearance';
import type { PaletteMode, PaletteTokens } from '../../theme/curatedPalettes';
import type { CoordinatedPalette } from '../../theme/coordinatedPalettes';
import { PaletteBoardPreview } from './PaletteBoardPreview';

export function ComponentPreview({ title, values, mode, palette }: { title: string; values: PaletteTokens; mode: PaletteMode; palette?: CoordinatedPalette }) {
  const [feedback, setFeedback] = useState('Try hover, focus, and the sample controls.');
  return <section className="wk-palette-sample" aria-label={`${title} component preview`} data-preview-mode={mode}
    style={{ ...values, colorScheme: mode } as CSSProperties}>
    <header><span className="wk-palette-sample-mark"><Palette size={16} aria-hidden="true" /></span><div><h3>{title}</h3><p>{mode === 'light' ? 'Light' : 'Dark'} appearance</p></div></header>
    <div className="wk-palette-sample-body">
      {palette && <div className="wk-palette-family-guide" aria-label="Palette color roles">
        <div className="wk-palette-family-strip" aria-hidden="true">{['dominant', 'secondary', 'accent'].map(role => <i key={role} style={{ background: values[`--palette-${role}`] }} />)}</div>
        <div className="wk-palette-families">{palette.families.map((family, index) => <div key={family.name}>
          <strong>{[60, 30, 10][index]}% · {['Dominant', 'Secondary', 'Accent'][index]}</strong><p>{family.name}</p>
          <span>{['Workspace and canvas', 'Sidebar and supporting surfaces', 'Actions, focus, and emphasis'][index]}</span>
        </div>)}</div>
      </div>}
      <div className="wk-palette-sample-card"><strong>A little room to think</strong><p>Readable text, comfortable surfaces, and one clear next step.</p>
        <div className="wk-palette-sample-badges">
          <span className="wk-palette-badge wk-palette-badge--success"><CheckCircle2 size={13} aria-hidden="true" /> Ready</span>
          <span className="wk-palette-badge wk-palette-badge--info"><Info size={13} aria-hidden="true" /> Shared</span>
          <span className="wk-palette-badge wk-palette-badge--warning"><TriangleAlert size={13} aria-hidden="true" /> Due soon</span>
        </div>
      </div>
      <div className="wk-palette-sample-actions">
        <Button size="compact" onClick={() => setFeedback('Primary action preview.')}>Primary</Button>
        <Button variant="secondary" size="compact" onClick={() => setFeedback('Secondary action preview.')}>Secondary</Button>
        <Button variant="quiet" size="compact" onClick={() => setFeedback('Quiet action preview.')}>Quiet</Button>
        <Button variant="danger" size="compact" onClick={() => setFeedback('Destructive action preview; nothing was deleted.')}>Delete</Button>
        <Button size="compact" disabled>Disabled</Button>
        <Button size="compact" loading loadingLabel="Saving…">Save</Button>
      </div>
      <label className="wk-palette-sample-field">Sample input<input placeholder="Write a thought…" /></label>
      <label className="wk-palette-sample-field">Sample select<select defaultValue="today"><option value="today">Today</option><option value="tomorrow">Tomorrow</option></select></label>
      <label className="wk-palette-sample-check"><input type="checkbox" defaultChecked /> Keep a little breathing room</label>
      <p className="wk-palette-sample-feedback" role="status">{feedback}</p>
      <div className="wk-palette-sample-notes" aria-label="Original board note colors">
        {notePigments.map(note => <div key={note.name} style={noteAppearanceStyle(note.value)}><strong>{note.name}</strong><span>A small idea</span></div>)}
      </div>
      <p className="wk-palette-sample-caption">Note pigments stay exactly as they are.</p>
      {palette && <>
        <nav className="wk-palette-navigation-example" aria-label={`${title} sample navigation`}>
          <strong>Your workspace</strong><Button variant="quiet" size="compact" onClick={() => setFeedback('Home navigation preview.')}>Home</Button>
          <Button variant="secondary" size="compact" aria-current="page" onClick={() => setFeedback('Boards navigation preview.')}>Boards</Button>
        </nav>
        <section className="wk-palette-dialog-example" aria-label="Sample dialog"><h4>A shared idea</h4><p>Clear surfaces and a single next step.</p>
          <Button size="compact" onClick={() => setFeedback('Sample dialog completed.')}>Done</Button></section>
        <PaletteBoardPreview palette={palette} mode={mode} />
      </>}
    </div>
  </section>;
}
