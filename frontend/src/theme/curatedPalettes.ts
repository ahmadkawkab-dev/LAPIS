/** Curated color themes. No note pigments, dimensions, or typography. */
export type PaletteMode = 'light' | 'dark';
export type PaletteTokens = Record<`--${string}`, string>;
export type ColorPalette = {
  id: string; name: string; description: string;
  light: PaletteTokens; dark: PaletteTokens;
};

import { coordinatedPalettes, type CoordinatedPalette } from './coordinatedPalettes.ts';
export const curatedPalettes = coordinatedPalettes;

export function findColorPalette(id: string | null) {
  return curatedPalettes.find(candidate => candidate.id === id) ?? null;
}
export { paletteContrast } from './colorContrast.ts';
import { paletteContrast } from './colorContrast.ts';
export type ContrastCheck = { label: string; ratio: number; minimum: number; passes: boolean };
/** Conservative token-pair checks, including disabled labels and interactive edges. */
export function checkPaletteContrast(values: PaletteTokens): ContrastCheck[] {
  const checks: ContrastCheck[] = [];
  const check = (label: string, foreground: keyof PaletteTokens, background: keyof PaletteTokens, minimum = 4.5) => {
    const ratio = paletteContrast(values[foreground], values[background]);
    checks.push({ label, ratio, minimum, passes: Number.isFinite(ratio) && ratio >= minimum });
  };
  for (const background of ['background', 'surface', 'secondary', 'selected', 'hover', 'board-card-alt-surface']) {
    for (const foreground of ['foreground', 'muted-foreground']) check(`${foreground} on ${background}`, `--${foreground}`, `--${background}`);
  }
  check('Primary button label', '--primary-foreground', '--primary');
  check('Selected label', '--selected-foreground', '--selected');
  for (const [foreground, background] of [['destructive', 'status-danger-surface'], ['success', 'status-success-surface'], ['warning', 'status-warning-surface'], ['status-info', 'status-info-surface']]) {
    check(`${foreground} message`, `--${foreground}`, `--${background}`);
  }
  for (const background of ['background', 'surface', 'secondary']) {
    for (const foreground of ['primary', 'destructive']) check(`${foreground} action on ${background}`, `--${foreground}`, `--${background}`);
    check(`Control boundary on ${background}`, '--control-border', `--${background}`, 3);
    check(`Focus on ${background}`, '--focus', `--${background}`, 3);
  }
  for (const background of ['--palette-sidebar', '--palette-supporting'] as const) {
    if (!values[background]) continue;
    for (const foreground of ['--foreground', '--muted-foreground', '--primary', '--destructive'] as const)
      check(`${foreground} on ${background}`, foreground, background);
    for (const foreground of ['--focus', '--control-border'] as const) check(`${foreground} on ${background}`, foreground, background, 3);
  }
  return checks;
}
export function checkPaletteAndBoardContrast(palette: CoordinatedPalette, mode: PaletteMode): ContrastCheck[] {
  const values = palette[mode];
  const checks = checkPaletteContrast(values);
  palette.variants[mode].forEach((variant, index) => {
    const add = (label: string, foreground: string, background: string, minimum = 4.5) => {
      const ratio = paletteContrast(foreground, background);
      checks.push({ label: `Board ${String.fromCharCode(65 + index)}: ${label}`, ratio, minimum, passes: Number.isFinite(ratio) && ratio >= minimum });
    };
    for (const [name, surface] of [['card', variant.card], ['canvas', variant.canvas], ['chrome', variant.chrome]]) {
      add(`text on ${name}`, values['--foreground'], surface);
      add(`metadata on ${name}`, values['--muted-foreground'], surface);
      add(`action on ${name}`, variant.action, surface);
      add(`focus on ${name}`, variant.action, surface, 3);
      add(`control on ${name}`, values['--control-border'], surface, 3);
      add(`destructive action on ${name}`, values['--destructive'], surface);
    }
    add('primary button label', values['--primary-foreground'], variant.action);
  });
  return checks;
}
