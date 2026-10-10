import assert from 'node:assert/strict';
import test from 'node:test';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import { build } from 'esbuild';
import { JSDOM } from 'jsdom';
import React, { act } from 'react';
import { createRoot } from 'react-dom/client';
import { TourController } from '../src/features/onboarding/tour.ts';
const require = createRequire(import.meta.url);
const compiled = await build({
  stdin: { contents: `export { default as WuknaTour } from './src/features/onboarding/WuknaTour';
    export { OnboardingProvider } from './src/features/onboarding/OnboardingProvider';
    export { MobileHeader, MobileNav } from './src/components/navigation/MobileNav';
    export { Sidebar } from './src/components/navigation/Sidebar';`, resolveDir: new URL('..', import.meta.url).pathname, loader: 'tsx' },
  bundle: true, write: false, format: 'esm', platform: 'node', jsx: 'automatic', loader: { '.css': 'empty' },
  plugins: [{ name: 'dom-test-dependencies', setup(builder) {
    builder.onResolve({ filter: /^(react(?:-dom)?(?:\/.*)?|lucide-react)$/ }, args => ({ path: pathToFileURL(require.resolve(args.path)).href, external: true }));
    builder.onResolve({ filter: /\/api$/ }, () => ({ path: 'api', namespace: 'test' }));
    builder.onLoad({ filter: /.*/, namespace: 'test' }, () => ({ contents: `
      export const onboardingApi = { get: () => globalThis.tourDomApi.get(), save: value => globalThis.tourDomApi.save(value) };
      export const errorMessage = error => error.message;
      export class AuthApiError extends Error { constructor(code) { super(code); this.code = code; } }
    ` }));
  } }],
});
const { WuknaTour, OnboardingProvider, MobileHeader, MobileNav, Sidebar } = await import(`data:text/javascript;base64,${Buffer.from(compiled.outputFiles[0].text).toString('base64')}`);
const settle = () => new Promise(resolve => setTimeout(resolve, 35));
async function withDom(run) {
  const dom = new JSDOM('<!doctype html><body><button data-tour="replay">Replay</button><main id="wk-main-content" tabindex="-1"><nav data-tour="navigation"><button id="real-action" data-tour="home">Home</button></nav></main><div id="root"></div></body>', { url: 'http://localhost/home', pretendToBeVisual: true });
  const names = ['window', 'document', 'HTMLElement', 'Element', 'MutationObserver', 'Event', 'CustomEvent', 'KeyboardEvent', 'requestAnimationFrame', 'cancelAnimationFrame', 'ResizeObserver', 'innerWidth', 'innerHeight', 'IS_REACT_ACT_ENVIRONMENT'];
  const original = new Map(names.map(name => [name, Object.getOwnPropertyDescriptor(globalThis, name)]));
  for (const name of names) {
    let value = dom.window[name];
    if (name === 'ResizeObserver') value = class { observe() {} unobserve() {} disconnect() {} };
    if (name === 'innerWidth') value = 1024;
    if (name === 'innerHeight') value = 800;
    if (name === 'IS_REACT_ACT_ENVIRONMENT') value = true;
    Object.defineProperty(globalThis, name, { configurable: true, writable: true, value });
  }
  dom.window.HTMLElement.prototype.getBoundingClientRect = function () {
    if (this.style.display === 'none' || this.hidden) return { left: 0, top: 0, right: 0, bottom: 0, width: 0, height: 0 };
    const size = this.classList.contains('wk-tour-card') ? { width: Math.min(352, innerWidth - 24), height: 310 } : { width: 100, height: 44 };
    const top = Number(this.dataset.testTop ?? 20);
    return { left: 12, top, right: 12 + size.width, bottom: top + size.height, ...size };
  };
  dom.window.HTMLElement.prototype.getClientRects = function () { return this.hidden ? [] : [this.getBoundingClientRect()]; };
  dom.window.HTMLElement.prototype.scrollIntoView = function () {};
  dom.window.HTMLDialogElement.prototype.showModal = function () { this.setAttribute('open', ''); };
  dom.window.HTMLDialogElement.prototype.close = function () { this.removeAttribute('open'); };
  const root = createRoot(document.getElementById('root'));
  const render = async element => { await act(async () => { root.render(element); await settle(); }); await act(settle); };
  const click = async button => { assert.ok(button); await act(async () => { button.click(); await settle(); }); };
  try { await run({ dom, root, render, click }); }
  finally { await act(async () => root.unmount()); dom.window.close(); for (const [name, descriptor] of original) { if (descriptor) Object.defineProperty(globalThis, name, descriptor); else delete globalThis[name]; } delete globalThis.tourDomApi; }
}
function button(text) { return Array.from(document.querySelectorAll('.wk-tour-card button')).find(item => item.textContent.includes(text)); }
function setupController() {
  const saves = [];
  const controller = new TourController({ get: async () => ({ status: 'NotStarted', version: 0 }), save: async value => { saves.push(value); return value; } });
  return { controller, saves };
}
const user = { id: 'user', username: 'tour-user', email: 'tour@example.test', displayName: null, profileImageUrl: null, profileImageVersion: null };
test('desktop steps highlight only the five outer links without opening an existing board or writing workspace data', async () => withDom(async ({ render, click }) => {
  document.querySelector('[data-tour="navigation"]').remove();
  const { controller, saves } = setupController();
  const navigations = [], mutations = [];
  const boardId = '00000000-0000-4000-8000-000000000001';
  window.history.replaceState(null, '', `/boards/${boardId}`);
  const sidebar = React.createElement(Sidebar, { user, boards: [{ id: boardId, title: 'Existing board', role: 1, canEdit: true }], activeBoardId: boardId,
    collapsed: false, onToggle: () => {}, navigate: path => navigations.push(path), onOpenAccount: () => {},
    onCreateBoard: async () => mutations.push('create'), onRenameBoard: async () => mutations.push('rename'), onDeleteBoard: async () => mutations.push('delete') });
  await render(sidebar);
  const targets = ['home', 'boards', 'calendar', 'quick-tasks', 'templates'];
  for (const [index, name] of targets.entries()) document.querySelector(`[data-tour="${name}"]`).dataset.testTop = 100 + index * 50;
  controller.start();
  await render(React.createElement(React.Fragment, null, sidebar, React.createElement(WuknaTour, { controller, path: window.location.pathname, ready: true })));
  for (const [index, title] of ['Home', 'Boards', 'Calendar', 'Quick tasks', 'Templates'].entries()) {
    assert.equal(document.querySelector('.wk-tour-card h2').textContent, title);
    assert.equal(document.querySelector('.wk-tour-spotlight > rect:last-child').getAttribute('y'), String(94 + index * 50));
    assert.equal(document.querySelectorAll('.wk-tour-card button').length, 3);
    assert.equal(document.querySelector('.wk-tour-hint'), null);
    assert.deepEqual(navigations, []);
    assert.deepEqual(mutations, []);
    assert.deepEqual(saves, []);
    assert.equal(window.location.pathname, `/boards/${boardId}`);
    if (index < 4) await click(button('Next'));
  }
  await click(button('Finish'));
  assert.deepEqual(saves, [{ status: 'Completed', version: 1 }]);
}));
test('mobile outer tour points hidden sections to More and resumes the same step after its menu closes', async () => withDom(async ({ render, click }) => {
  document.querySelector('[data-tour="navigation"]').remove();
  globalThis.innerWidth = 375;
  const { controller, saves } = setupController();
  const navigations = [];
  const nav = React.createElement(MobileNav, { navigate: path => navigations.push(path), onBoards: () => navigations.push('/boards') });
  await render(nav);
  for (const element of document.querySelectorAll('[data-tour="home"], [data-tour="calendar"], [data-tour="more"]')) element.dataset.testTop = '744';
  controller.start();
  await render(React.createElement(React.Fragment, null, nav, React.createElement(WuknaTour, { controller, path: '/home', ready: true })));
  assert.equal(document.querySelector('.wk-tour-card h2').textContent, 'Home');
  await click(button('Next'));
  assert.equal(document.querySelector('.wk-tour-card h2').textContent, 'Boards');
  assert.equal(document.querySelector('.wk-tour-hint').textContent, 'Find Boards in the More menu.');
  assert.equal(document.querySelector('.wk-tour-card').style.top, '12px');
  await click(document.querySelector('[data-tour="more"]'));
  assert.ok(document.querySelector('dialog[open]'));
  assert.equal(document.querySelector('.wk-tour-card'), null);
  await click(document.querySelector('button[aria-label="Close More"]')); await act(settle);
  assert.equal(document.querySelector('.wk-tour-card h2').textContent, 'Boards');
  for (const title of ['Calendar', 'Quick tasks', 'Templates']) {
    await click(button('Next'));
    assert.equal(document.querySelector('.wk-tour-card h2').textContent, title);
    const hint = document.querySelector('.wk-tour-hint');
    if (title === 'Calendar') assert.equal(hint, null);
    else assert.equal(hint.textContent, `Find ${title} in the More menu.`);
  }
  assert.deepEqual(navigations, []);
  await click(button('Finish'));
  assert.deepEqual(saves, [{ status: 'Completed', version: 1 }]);
}));
test('React tour supports Back/Next, normal navigation focus, Escape and focus restoration without an exercise button', async () => withDom(async ({ render, click }) => {
  const { controller, saves } = setupController();
  const replay = document.querySelector('[data-tour="replay"]'); replay.focus(); controller.start();
  await render(React.createElement(WuknaTour, { controller, path: '/home', ready: true }));
  assert.equal(document.querySelector('[role="dialog"]').getAttribute('aria-modal'), 'false');
  assert.equal(document.activeElement.tagName, 'H2');
  assert.equal(button('Back').disabled, true);
  assert.equal(button('Try the highlighted control'), undefined);
  document.getElementById('real-action').focus();
  assert.equal(document.activeElement.id, 'real-action');
  const tab = new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true }); document.activeElement.dispatchEvent(tab);
  assert.equal(tab.defaultPrevented, false);
  await click(button('Next')); assert.match(document.querySelector('.wk-tour-meta').textContent, /2 of 5/);
  await click(button('Back')); assert.match(document.querySelector('.wk-tour-meta').textContent, /1 of 5/);
  await act(async () => { window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true })); await settle(); });
  assert.equal(document.querySelector('.wk-tour-layer'), null);
  assert.equal(document.activeElement, replay);
  assert.deepEqual(saves, [{ status: 'Skipped', version: 1 }]);
  let actions = 0; document.getElementById('real-action').onclick = () => actions++;
  await click(document.getElementById('real-action')); assert.equal(actions, 1);
}));
test('missing targets remain dismissible and Finish saves Completed', async () => withDom(async ({ render, click }) => {
  const { controller, saves } = setupController(); controller.start();
  document.querySelector('[data-tour="navigation"]').remove();
  await render(React.createElement(WuknaTour, { controller, path: '/boards', ready: true }));
  assert.equal(document.querySelector('.wk-tour-spotlight'), null);
  assert.match(document.querySelector('.wk-tour-hint').textContent, /main navigation/);
  for (let step = 0; step < 4; step++) await click(button('Next'));
  await click(button('Finish'));
  assert.equal(document.querySelector('.wk-tour-card'), null);
  assert.deepEqual(saves, [{ status: 'Completed', version: 1 }]);
  assert.equal(document.activeElement.dataset.tour, 'replay');
}));
test('dialogs and loading UI suspend coach marks without closing or advancing', async () => withDom(async ({ render }) => {
  const { controller } = setupController(); controller.start();
  const props = { controller, path: '/home', ready: true };
  await render(React.createElement(WuknaTour, props));
  const dialog = document.createElement('dialog');
  await act(async () => { dialog.setAttribute('open', ''); document.body.append(dialog); await settle(); });
  assert.equal(document.querySelector('.wk-tour-card'), null);
  await act(async () => { window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })); await settle(); });
  assert.equal(controller.getSnapshot().step, 0);
  await act(async () => { dialog.remove(); await settle(); }); await act(settle);
  assert.ok(document.querySelector('.wk-tour-card'));
  await render(React.createElement(WuknaTour, { ...props, ready: false }));
  assert.equal(document.querySelector('.wk-tour-card'), null);
}));
test('help beside the mobile avatar replays completed and skipped tours without resetting other preferences', async () => {
  for (const status of ['Completed', 'Skipped']) await withDom(async ({ render, click }) => {
    const saves = [];
    globalThis.tourDomApi = { get: async () => ({ status, version: 1 }), save: async value => { saves.push(value); return value; } };
    await render(React.createElement(OnboardingProvider, { ready: true, path: '/home' }, React.createElement(MobileHeader, { user, onHome: () => {}, onOpenAccount: () => {} })));
    assert.equal(document.querySelector('.wk-tour-card'), null);
    const replay = document.querySelector('button[aria-label="Replay onboarding tour"]');
    assert.equal(replay.title, 'Take the Wukna tour');
    await click(replay); await act(settle);
    assert.match(document.querySelector('.wk-tour-meta').textContent, /1 of 5/);
    assert.equal(saves.length, 0);
    await click(button('Skip tour')); assert.deepEqual(saves, [{ status: 'Skipped', version: 1 }]);
  });
});
