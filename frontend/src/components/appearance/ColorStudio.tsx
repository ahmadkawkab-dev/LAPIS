import { useState } from 'react';
import { Check, Palette, RotateCcw, TriangleAlert } from 'lucide-react';
import { Button } from '../ui/Button';
import { Dialog } from '../ui/Dialog';
import { ComponentPreview } from './PaletteComponentPreview';
import { checkPaletteAndBoardContrast, curatedPalettes, findColorPalette, type PaletteMode } from '../../theme/curatedPalettes';
import { applyColorPalette, originalPreviewTokens, useAppliedPaletteId, usePaletteAppMode } from '../../theme/paletteState';
import './color-studio.css';

export default function ColorStudio() {
  const appliedId = useAppliedPaletteId();
  const appMode = usePaletteAppMode();
  const [open, setOpen] = useState(false);
  const [selectedId, setSelectedId] = useState(appliedId ?? curatedPalettes[0].id);
  const [mode, setMode] = useState<PaletteMode>(appMode);
  const [compare, setCompare] = useState(false);
  const selected = findColorPalette(appliedId ?? selectedId) ?? curatedPalettes[0];
  const applied = findColorPalette(appliedId);
  const checks = checkPaletteAndBoardContrast(selected, mode);
  const failures = checks.filter(check => !check.passes);
  const appFailures = applied ? checkPaletteAndBoardContrast(applied, appMode).filter(check => !check.passes) : [];
  function choose(id: string) {
    setSelectedId(id);
    if (appliedId) applyColorPalette(id);
  }
  function reset() {
    setSelectedId(selected.id);
    applyColorPalette(null);
    setMode(appMode);
  }
  function useAcrossApp(enabled: boolean) {
    setSelectedId(selected.id);
    applyColorPalette(enabled ? selected.id : null);
  }
  return <div className="wk-palette-entry">
    <div><Button variant="secondary" size="compact" aria-haspopup="dialog" onClick={() => setOpen(true)}><Palette size={16} aria-hidden="true" /> Color Studio</Button>
      </div>
    <p role={appFailures.length ? 'alert' : undefined}>{appFailures.length ? 'This palette needs contrast adjustments. Reset or choose another palette.' : applied ? `${applied.name} is your color theme on this browser.` : 'Choose a color theme for this browser.'}</p>
    {open && <Dialog title="Color Studio" onClose={() => setOpen(false)} className="wk-palette-dialog">
      <p className="wk-palette-intro">Find a palette that feels like your workspace. Compare appearances, then use your favorite across the app.</p>
      {(failures.length > 0 || appFailures.length > 0) && <div className="wk-palette-warning" role="alert"><TriangleAlert size={17} aria-hidden="true" /><p>{appFailures.length ? `The active palette needs contrast adjustments in ${appMode} mode.` : `This candidate needs contrast adjustments in ${mode} mode before everyday use.`}</p></div>}
      <div className="wk-palette-controls">
        <fieldset><legend>Sample appearance</legend><div className="wk-palette-mode-options">
          {(['light', 'dark'] as const).map(value => <label key={value}><input type="radio" name="wk-palette-preview-mode" value={value}
            checked={mode === value} onChange={() => setMode(value)} /><span>{value === 'light' ? 'Light' : 'Dark'}</span></label>)}
        </div></fieldset>
        <label className="wk-palette-compare"><input type="checkbox" checked={compare} onChange={event => setCompare(event.target.checked)} /> Compare with original</label>
      </div>
      <div className="wk-palette-footer">
        <div><label className="wk-palette-apply"><input type="checkbox" checked={!!appliedId} onChange={event => useAcrossApp(event.target.checked)} /> Use this palette across the app</label>
          <p>Applies on this browser. App uses {appMode} appearance; samples are independent.</p>
          <p role="status">{applied ? `Active: ${applied.name}. Changes apply instantly.` : 'The original Wukna theme is active.'}</p></div>
        <Button variant="secondary" size="compact" onClick={reset}><RotateCcw size={15} aria-hidden="true" /> Reset to Original Theme</Button>
      </div>
      <div className="wk-palette-layout">
        <div className="wk-palette-library" role="group" aria-label="Color palettes">
          {curatedPalettes.map(candidate => <button key={candidate.id} className="wk-palette-card" aria-pressed={selected.id === candidate.id}
            onClick={() => choose(candidate.id)}>
            <span className="wk-palette-swatches" aria-hidden="true">{(['--palette-dominant', '--palette-secondary', '--palette-accent'] as const).map((token, index) =>
              <i key={token} style={{ backgroundColor: candidate[mode][token], flex: [6, 3, 1][index] }} />)}</span>
            <span className="wk-palette-card-heading"><strong>{candidate.name}</strong><span className="wk-palette-selected-mark" aria-hidden="true">{selected.id === candidate.id && <Check size={15} />}</span></span>
            <span className="wk-palette-card-description">{candidate.description}</span>
          </button>)}
        </div>
        <div className={`wk-palette-previews${compare ? ' wk-palette-previews--compare' : ''}`}>
          {compare && <ComponentPreview title="Original Wukna" values={originalPreviewTokens(mode)} mode={mode} />}
          <ComponentPreview title={selected.name} values={selected[mode]} mode={mode} palette={selected} />
        </div>
      </div>
      <details className="wk-palette-contrast">
        <summary>{failures.length ? `${failures.length} sample contrast checks need attention` : `${checks.length} sample contrast checks pass`}</summary>
        <p>Text pairs require 4.5:1; focus and control boundaries require 3:1. These checks help compare candidates; they are not a complete accessibility certification.</p>
        <ul>{checks.map(check => <li key={check.label}><span>{check.label}</span><strong>{check.ratio.toFixed(2)}:1 {check.passes ? 'Pass' : 'Needs attention'}</strong></li>)}</ul>
      </details>
    </Dialog>}
  </div>;
}
