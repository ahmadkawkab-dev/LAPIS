import type { ColorPalette, PaletteMode, PaletteTokens } from './curatedPalettes';
import { paletteContrast } from './colorContrast.ts';

type Family = { name: string; light: string; dark: string };
export type BoardVariation = { name: string; card: string; canvas: string; chrome: string; action: string; indicator: string };
export type CoordinatedPalette = ColorPalette & {
  families: readonly [Family, Family, Family];
  variants: Record<PaletteMode, readonly BoardVariation[]>;
};
/** Tonal derivation only; no random colors or component-specific pigments. */
export function mixPaletteColor(first: string, second: string, weight: number) {
  const channels = (hex: string) => hex.slice(1).match(/../g)!.map(part => parseInt(part, 16));
  const a = channels(first), b = channels(second);
  return `#${a.map((value, index) => Math.round(value * weight + b[index] * (1 - weight)).toString(16).padStart(2, '0')).join('')}`;
}
function readableAction(color: string, surfaces: readonly string[], mode: PaletteMode) {
  for (let step = 0; step <= 100; step++) {
    const candidate = mixPaletteColor(mode === 'light' ? '#000000' : '#ffffff', color, step / 100);
    if (surfaces.every(surface => paletteContrast(candidate, surface) >= 4.5)) return candidate;
  }
  return mode === 'light' ? '#000000' : '#ffffff';
}
function colors(dominant: string, supporting: string, accent: string, mode: PaletteMode): PaletteTokens {
  const dark = mode === 'dark';
  const surface = mixPaletteColor('#ffffff', dominant, dark ? .025 : .45);
  const secondary = mixPaletteColor(supporting, surface, dark ? .08 : .09);
  const selected = mixPaletteColor(supporting, surface, dark ? .13 : .14);
  const sidebar = mixPaletteColor(supporting, surface, dark ? .12 : .16);
  const action = readableAction(accent, [dominant, surface, secondary, selected, sidebar], mode);
  return {
    '--background': dominant, '--canvas': dominant, '--surface': surface,
    '--foreground': dark ? '#f1f3ee' : '#252b27', '--muted-foreground': dark ? '#c3cac0' : '#4e5752',
    '--primary': action, '--primary-foreground': dark ? '#17201b' : '#ffffff',
    '--secondary': secondary, '--selected': selected, '--selected-foreground': action,
    '--hover': secondary, '--border': mixPaletteColor(supporting, surface, dark ? .23 : .22),
    '--control-border': dark ? '#96a295' : '#70776f', '--focus': action,
    '--destructive': dark ? '#ffc4be' : '#8f302d', '--success': dark ? '#afd0ae' : '#316442',
    '--warning': dark ? '#e7be75' : '#80571d', '--status-info': dark ? '#9cc8f0' : '#315f8d',
    '--status-info-surface': dark ? '#223544' : '#e6eef5',
    '--status-success-surface': dark ? '#253727' : '#e2ede2',
    '--status-warning-surface': dark ? '#3b3020' : '#f3e8d2',
    '--status-danger-surface': dark ? '#432d2a' : '#f8e9e7',
    '--scrim': `${dominant}B3`, '--graph-edge': dark ? '#96a295' : '#70776f',
    '--graph-edge-selected': action, '--graph-edge-draft': dark ? '#c3cac0' : '#4e5752',
    '--board-card-alt-surface': secondary,
    '--shadow-card': dark ? '0 2px 8px #00000018' : '0 1px 2px #252b270A',
    '--shadow-panel': dark ? '0 12px 40px #00000030' : '0 4px 16px #252b2712',
    '--shadow-selected': dark ? '0 10px 30px #00000040' : '0 10px 30px #252b2724',
    '--palette-sidebar': sidebar, '--palette-supporting': secondary,
    '--palette-dominant': dominant, '--palette-secondary': sidebar, '--palette-accent': action,
  };
}
function variations(values: PaletteTokens, supporting: string, accent: string, names: readonly string[], mode: PaletteMode) {
  const blends = [supporting, accent, mixPaletteColor(supporting, accent, .7), mixPaletteColor(supporting, accent, .3), supporting, accent];
  return blends.map((base, index): BoardVariation => {
    const card = mixPaletteColor(base, values['--surface'], [.22, .18, .14, .24, .1, .085][index]);
    const chrome = mixPaletteColor(base, values['--surface'], [.1, .08, .065, .11, .045, .035][index]);
    const canvas = mixPaletteColor(base, values['--background'], [.06, .045, .035, .05, .025, .015][index]);
    const complementary = index % 2 === 0 ? accent : supporting;
    const action = readableAction(complementary, [card, chrome, canvas, values['--selected'], values['--secondary']], mode);
    return { name: names[index], card, chrome, canvas, action, indicator: action };
  });
}
function palette(id: string, name: string, families: readonly [Family, Family, Family]): CoordinatedPalette {
  const light = colors(...families.map(family => family.light) as [string, string, string], 'light');
  const dark = colors(...families.map(family => family.dark) as [string, string, string], 'dark');
  const names = [`${families[1].name} / ${families[2].name}`, `${families[2].name} / ${families[1].name}`, `${families[0].name} / ${families[2].name}`, `${families[1].name} blend`, `${families[1].name} wash`, `${families[2].name} wash`];
  return { id, name, description: families.map(family => family.name).join(' · '), families, light, dark,
    variants: { light: variations(light, families[1].light, families[2].light, names, 'light'), dark: variations(dark, families[1].dark, families[2].dark, names, 'dark') } };
}

// Independently curated light/dark keys, not an inverted light palette.
export const coordinatedPalettes: readonly CoordinatedPalette[] = [
  palette('warm-minimal', 'Warm Minimal', [
    { name: 'Warm ivory', light: '#f7f3ec', dark: '#211f1b' }, { name: 'Muted sage', light: '#4e6b58', dark: '#b0c8b4' }, { name: 'Soft terracotta', light: '#8a4935', dark: '#e7b09c' },
  ]),
  palette('calm-sage', 'Calm Sage', [
    { name: 'Cream', light: '#f5f4e9', dark: '#202219' }, { name: 'Sage green', light: '#4c6c50', dark: '#b2c9a3' }, { name: 'Golden amber', light: '#765419', dark: '#e4c47b' },
  ]),
  palette('soft-lavender', 'Soft Lavender', [
    { name: 'Porcelain', light: '#f5f3f7', dark: '#211e26' }, { name: 'Dusty lavender', light: '#69527b', dark: '#c6b4da' }, { name: 'Muted teal', light: '#246765', dark: '#98cfca' },
  ]),
  palette('ocean-slate', 'Ocean Slate', [
    { name: 'Mist gray', light: '#f1f5f6', dark: '#1b2328' }, { name: 'Ocean blue', light: '#386981', dark: '#a0c8dc' }, { name: 'Warm coral', light: '#98483c', dark: '#efb0a3' },
  ]),
  palette('earthy-terracotta', 'Earthy Terracotta', [
    { name: 'Warm sand', light: '#f7f0e5', dark: '#262018' }, { name: 'Olive green', light: '#5d6642', dark: '#c2c99e' }, { name: 'Clay orange', light: '#93502e', dark: '#e9b28a' },
  ]),
  palette('modern-neutral', 'Modern Neutral', [
    { name: 'Soft stone', light: '#f3f3ef', dark: '#202322' }, { name: 'Slate blue', light: '#456577', dark: '#b0c4d2' }, { name: 'Muted copper', light: '#874c35', dark: '#dbb39e' },
  ]),
];
export const findCoordinatedPalette = (id: string | null) => coordinatedPalettes.find(palette => palette.id === id) ?? null;
