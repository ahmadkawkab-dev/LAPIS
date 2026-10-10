import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import { build } from 'esbuild';
import { JSDOM } from 'jsdom';

const require = createRequire(import.meta.url);
const source = "export * from './src/theme/paletteState'; export { curatedPalettes } from './src/theme/curatedPalettes';";
const compiled = async dev => (await build({
  stdin: { contents: source, resolveDir: new URL('..', import.meta.url).pathname, loader: 'ts' },
  bundle: true, write: false, format: 'esm', platform: 'node',
  define: { 'import.meta.env.DEV': String(dev), 'import.meta.hot': 'undefined' },
  plugins: [{ name: 'palette-test-dependencies', setup(builder) {
    builder.onResolve({ filter: /^react$/ }, args => ({ path: pathToFileURL(require.resolve(args.path)).href, external: true }));
    builder.onResolve({ filter: /tokens\.css\?raw$/ }, () => ({ path: new URL('../src/styles/tokens.css', import.meta.url).pathname, namespace: 'raw-css' }));
    builder.onLoad({ filter: /.*/, namespace: 'raw-css' }, args => ({ contents: readFileSync(args.path, 'utf8'), loader: 'text' }));
  } }],
})).outputFiles[0].text;
const [developmentCode, productionCode] = await Promise.all([compiled(true), compiled(false)]);
let sequence = 0;
async function environment(run, dev = true, saved = null) {
  const dom = new JSDOM('<!doctype html><html data-theme="light"><body></body></html>', { url: 'http://localhost:5173' });
  const globals = ['window', 'document', 'localStorage', 'MutationObserver'];
  const original = new Map(globals.map(name => [name, Object.getOwnPropertyDescriptor(globalThis, name)]));
  for (const name of globals) Object.defineProperty(globalThis, name, { configurable: true, value: dom.window[name] });
  localStorage.setItem('wukna.theme.v1', 'system');
  if (saved) localStorage.setItem('wukna.palette.v1', saved);
  const code = dev ? developmentCode : productionCode;
  const controller = await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}#${++sequence}`);
  try { await run(controller, dom.window); }
  finally {
    controller.disposeColorPalette();
    dom.window.close();
    for (const [name, descriptor] of original) {
      if (descriptor) Object.defineProperty(globalThis, name, descriptor); else delete globalThis[name];
    }
  }
}

test('app palette changes colors only, follows theme, and reset restores original inline tokens exactly', async () => {
  await environment(async controller => {
    const root = document.documentElement;
    root.style.setProperty('--background', '#123456', 'important');
    root.style.setProperty('--space-3', '17px');
    const original = root.style.cssText;
    controller.initializeColorPalette();
    for (const palette of controller.curatedPalettes) {
      controller.applyColorPalette(palette.id);
      assert.equal(root.style.getPropertyValue('--primary'), palette.light['--primary']);
      assert.equal(root.style.getPropertyValue('--space-3'), '17px');
      root.dataset.theme = 'dark';
      await new Promise(resolve => setTimeout(resolve, 0));
      assert.equal(root.style.getPropertyValue('--primary'), palette.dark['--primary']);
      root.dataset.theme = 'light';
      await new Promise(resolve => setTimeout(resolve, 0));
    }
    controller.applyColorPalette(null);
    assert.equal(root.style.cssText, original);
    assert.equal(root.dataset.theme, 'light');
    assert.equal(root.dataset.colorPalette, undefined);
    assert.equal(localStorage.getItem(controller.paletteStorageKey), null);
    assert.equal(localStorage.getItem('wukna.theme.v1'), 'system');
    assert.equal(root.classList.contains('wk-theme-changing'), false);
  });
});
test('saved color selection is isolated, valid IDs survive refresh, and invalid IDs are ignored', async () => {
  await environment(controller => {
    controller.initializeColorPalette();
    assert.equal(document.documentElement.dataset.colorPalette, 'ocean-slate');
    assert.equal(localStorage.getItem('wukna.theme.v1'), 'system');
  }, true, 'ocean-slate');
  await environment(controller => {
    controller.initializeColorPalette();
    assert.equal(document.documentElement.dataset.colorPalette, undefined);
    controller.applyColorPalette('arbitrary-css-is-not-allowed');
    assert.equal(document.documentElement.style.length, 0);
  }, true, 'not-a-palette');
});
test('color palettes restore and switch in production builds', async () => {
  await environment(controller => {
    controller.initializeColorPalette();
    assert.equal(document.documentElement.dataset.colorPalette, 'soft-lavender');
    controller.applyColorPalette('ocean-slate');
    assert.equal(document.documentElement.dataset.colorPalette, 'ocean-slate');
    assert.equal(document.documentElement.style.getPropertyValue('--primary'), controller.curatedPalettes[3].light['--primary']);
    controller.applyColorPalette(null);
    assert.equal(document.documentElement.style.length, 0);
  }, false, 'soft-lavender');
});
test('blocked browser storage does not prevent preview or reset', async () => {
  await environment((controller, window) => {
    const originals = Object.getOwnPropertyDescriptors(window.Storage.prototype);
    for (const name of ['getItem', 'setItem', 'removeItem']) window.Storage.prototype[name] = () => { throw new Error('Storage unavailable'); };
    try {
      controller.initializeColorPalette();
      controller.applyColorPalette('calm-sage');
      assert.equal(document.documentElement.dataset.colorPalette, 'calm-sage');
      controller.applyColorPalette(null);
      assert.equal(document.documentElement.style.length, 0);
    } finally { Object.defineProperties(window.Storage.prototype, originals); }
  });
});
test('comparison reads original light/dark tokens while the app is previewing a different palette', async () => {
  await environment(controller => {
    controller.initializeColorPalette();
    controller.applyColorPalette('earthy-terracotta');
    assert.equal(controller.originalPreviewTokens('light')['--primary'], '#486653');
    assert.equal(controller.originalPreviewTokens('dark')['--primary'], '#a5c0aa');
    assert.notEqual(document.documentElement.style.getPropertyValue('--primary'), '#486653');
  });
});
test('other-tab palette updates sync without changing unrelated browser preferences', async () => {
  await environment((controller, window) => {
    controller.initializeColorPalette();
    localStorage.setItem(controller.paletteStorageKey, 'soft-lavender');
    window.dispatchEvent(new window.StorageEvent('storage', { key: controller.paletteStorageKey, newValue: 'soft-lavender' }));
    assert.equal(document.documentElement.dataset.colorPalette, 'soft-lavender');
    window.dispatchEvent(new window.StorageEvent('storage', { key: 'unrelated', newValue: 'calm-sage' }));
    assert.equal(document.documentElement.dataset.colorPalette, 'soft-lavender');
    localStorage.removeItem(controller.paletteStorageKey);
    window.dispatchEvent(new window.StorageEvent('storage', { key: controller.paletteStorageKey, newValue: null }));
    assert.equal(document.documentElement.style.length, 0);
    assert.equal(localStorage.getItem('wukna.theme.v1'), 'system');
  });
});
