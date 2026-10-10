import { useSyncExternalStore } from 'react';
import { curatedPalettes, findColorPalette, type PaletteMode, type PaletteTokens } from './curatedPalettes';
import originalThemeSource from '../styles/tokens.css?raw';

export const paletteStorageKey = 'wukna.palette.v1';
const listeners = new Set<() => void>();
const originalInlineTokens = new Map<keyof PaletteTokens, { value: string; priority: string }>();
let appliedId: string | null = null;
let observer: MutationObserver | null = null;
let initialized = false;
const notify = () => listeners.forEach(listener => listener());
export const subscribePalette = (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener); }; };
export const resolvedPaletteMode = (): PaletteMode => document.documentElement.dataset.theme === 'dark' ? 'dark' : 'light';
/** Read the unchanged stylesheet, so comparison never copies the default palette. */
export function originalPreviewTokens(mode: PaletteMode) {
  const blocks = [originalThemeSource.match(/:root\s*\{([^}]+)\}/)?.[1] ?? ''];
  if (mode === 'dark') blocks.push(originalThemeSource.match(/:root\[data-theme=['"]dark['"]\]\s*\{([^}]+)\}/)?.[1] ?? '');
  const allowed = new Set(Object.keys(curatedPalettes[0].light));
  return Object.fromEntries(blocks.flatMap(block => [...block.matchAll(/(--[\w-]+)\s*:\s*([^;]+);/g)]
    .filter(match => allowed.has(match[1])).map(match => [match[1], match[2].trim()])));
}
function savedPalette() {
  try { return findColorPalette(localStorage.getItem(paletteStorageKey))?.id ?? null; }
  catch { return null; }
}
function updateTokens() {
  const root = document.documentElement;
  const palette = findColorPalette(appliedId);
  root.classList.add('wk-theme-changing');
  for (const [name, original] of originalInlineTokens) {
    const value = palette?.[resolvedPaletteMode()][name];
    if (value) root.style.setProperty(name, value);
    else if (original.value) root.style.setProperty(name, original.value, original.priority);
    else root.style.removeProperty(name);
  }
  if (palette) root.dataset.colorPalette = palette.id;
  else delete root.dataset.colorPalette;
  // Resolve paired token changes together, retaining the app's existing motion.
  void root.offsetHeight;
  root.classList.remove('wk-theme-changing');
  notify();
}
function onStorage(event: StorageEvent) {
  if (event.key !== paletteStorageKey && event.key !== null) return;
  appliedId = savedPalette();
  updateTokens();
}
export function initializeColorPalette() {
  if (initialized) return;
  initialized = true;
  const root = document.documentElement;
  for (const name of Object.keys(curatedPalettes[0].light) as (keyof PaletteTokens)[]) {
    originalInlineTokens.set(name, { value: root.style.getPropertyValue(name), priority: root.style.getPropertyPriority(name) });
  }
  appliedId = savedPalette();
  if (appliedId) updateTokens();
  observer = new MutationObserver(() => { if (appliedId) updateTokens(); else notify(); });
  observer.observe(root, { attributes: true, attributeFilter: ['data-theme'] });
  window.addEventListener('storage', onStorage);
}
export function applyColorPalette(id: string | null) {
  if (id !== null && !findColorPalette(id)) return;
  initializeColorPalette();
  appliedId = id;
  try {
    if (id) localStorage.setItem(paletteStorageKey, id);
    else localStorage.removeItem(paletteStorageKey);
  } catch { /* Color selection still works until this tab reloads. */ }
  updateTokens();
}
export const appliedPaletteId = () => appliedId;
export function useAppliedPaletteId() {
  return useSyncExternalStore(subscribePalette, appliedPaletteId);
}
export function usePaletteAppMode() {
  return useSyncExternalStore(subscribePalette, resolvedPaletteMode);
}
export function disposeColorPalette() {
  observer?.disconnect();
  window.removeEventListener('storage', onStorage);
  appliedId = null;
  updateTokens();
  originalInlineTokens.clear();
  initialized = false;
  listeners.clear();
}
if (import.meta.hot) import.meta.hot.dispose(disposeColorPalette);
