import assert from 'node:assert/strict';
import test from 'node:test';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import { build } from 'esbuild';
import { JSDOM } from 'jsdom';
import React, { act } from 'react';
import { createRoot } from 'react-dom/client';

const require = createRequire(import.meta.url);
const compiled = await build({
  stdin: { contents: "export { AppShell } from './src/components/navigation/AppShell';", resolveDir: new URL('..', import.meta.url).pathname, loader: 'tsx' },
  bundle: true, write: false, format: 'esm', platform: 'node', jsx: 'automatic', loader: { '.css': 'empty' },
  plugins: [{ name: 'shell-dom-dependencies', setup(builder) {
    builder.onResolve({ filter: /^(react(?:-dom)?(?:\/.*)?|lucide-react)$/ }, args => ({ path: pathToFileURL(require.resolve(args.path)).href, external: true }));
    builder.onResolve({ filter: /\/api$/ }, () => ({ path: 'api', namespace: 'test' }));
    builder.onLoad({ filter: /.*/, namespace: 'test' }, () => ({ contents: 'export const errorMessage = error => error.message; export const onboardingApi = {}; export class AuthApiError extends Error { constructor(code) { super(code); this.code = code; } }' }));
  } }],
});
const { AppShell } = await import(`data:text/javascript;base64,${Buffer.from(compiled.outputFiles[0].text).toString('base64')}`);

test('tablet navigation, manual collapse and search shortcut preserve accessible controls without changing board data', async () => {
  const dom = new JSDOM('<!doctype html><body><div id="root"></div></body>', { url: 'http://localhost/home', pretendToBeVisual: true });
  const names = ['window', 'document', 'HTMLElement', 'Element', 'Event', 'KeyboardEvent', 'requestAnimationFrame', 'cancelAnimationFrame', 'IS_REACT_ACT_ENVIRONMENT'];
  const originals = new Map(names.map(name => [name, Object.getOwnPropertyDescriptor(globalThis, name)]));
  for (const name of names) Object.defineProperty(globalThis, name, { configurable: true, writable: true, value: name === 'IS_REACT_ACT_ENVIRONMENT' ? true : dom.window[name] });
  const tablet = new dom.window.EventTarget();
  tablet.matches = true;
  tablet.addListener = tablet.addEventListener;
  tablet.removeListener = tablet.removeEventListener;
  dom.window.matchMedia = () => tablet;
  dom.window.scrollTo = () => {};
  const root = createRoot(document.getElementById('root'));
  const mutations = [];
  const mutate = async (...args) => { mutations.push(args); };
  const settle = () => new Promise(resolve => setTimeout(resolve, 35));
  const toggle = () => document.querySelector('.wk-sidebar-header button[aria-expanded]');
  try {
    await act(async () => { root.render(React.createElement(AppShell, {
      user: { id: 'test-user', username: 'test-user', email: 'test@example.test' },
      boards: [], activeBoardId: null, navigate: () => {}, onCreateBoard: mutate, onRenameBoard: mutate,
      onDeleteBoard: mutate, signOut: () => {}, signOutEverywhere: () => {}, notify: () => {}, children: React.createElement('p', null, 'Workspace'),
    })); await settle(); });
    assert.equal(toggle().getAttribute('aria-expanded'), 'false');
    assert.equal(toggle().getAttribute('aria-label'), 'Expand sidebar');
    assert.ok(document.querySelector('button[aria-label="New board"]'), 'the collapsed creation action keeps its name');
    await act(async () => { toggle().click(); await settle(); });
    assert.equal(toggle().getAttribute('aria-expanded'), 'true');
    await act(async () => { toggle().click(); await settle(); });
    assert.equal(toggle().getAttribute('aria-expanded'), 'false');
    await act(async () => { dom.window.dispatchEvent(new dom.window.KeyboardEvent('keydown', { key: 'k', ctrlKey: true, bubbles: true })); await settle(); });
    assert.equal(toggle().getAttribute('aria-expanded'), 'true');
    assert.equal(document.activeElement.getAttribute('aria-label'), 'Search boards');
    await act(async () => { tablet.matches = false; tablet.dispatchEvent(new dom.window.Event('change')); await settle(); });
    assert.equal(toggle().getAttribute('aria-expanded'), 'true');
    await act(async () => { tablet.matches = true; tablet.dispatchEvent(new dom.window.Event('change')); await settle(); });
    assert.equal(toggle().getAttribute('aria-expanded'), 'false');
    assert.deepEqual(mutations, []);
  } finally {
    await act(async () => root.unmount());
    dom.window.close();
    for (const [name, descriptor] of originals) { if (descriptor) Object.defineProperty(globalThis, name, descriptor); else delete globalThis[name]; }
  }
});
