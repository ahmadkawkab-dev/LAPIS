import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import { build } from 'esbuild';
import { JSDOM } from 'jsdom';

const require = createRequire(import.meta.url);
const code = (await build({
  stdin: { contents: "export * from './src/theme/boardVariationState'; export * from './src/theme/paletteState'; export { curatedPalettes } from './src/theme/curatedPalettes';", resolveDir: new URL('..', import.meta.url).pathname, loader: 'ts' },
  bundle: true, write: false, format: 'esm', platform: 'node',
  define: { 'import.meta.hot': 'undefined', 'import.meta.env.DEV': 'false' },
  plugins: [{ name: 'test-dependencies', setup(builder) {
    builder.onResolve({ filter: /^react$/ }, args => ({ path: pathToFileURL(require.resolve(args.path)).href, external: true }));
    builder.onResolve({ filter: /tokens\.css\?raw$/ }, () => ({ path: new URL('../src/styles/tokens.css', import.meta.url).pathname, namespace: 'raw-css' }));
    builder.onLoad({ filter: /.*/, namespace: 'raw-css' }, args => ({ contents: readFileSync(args.path, 'utf8'), loader: 'text' }));
  } }],
})).outputFiles[0].text;
const id = n => `00000000-0000-0000-0000-${n.toString(16).padStart(12, '0')}`;
const boards = [0, 6, 12, 18, 24, 30].map(n => ({ id: id(n), cardColor: null }));
let sequence = 0;
async function environment(run) {
  const dom = new JSDOM(`<!doctype html><html data-theme="light"><body><div class="workspace" data-board-id="${id(0)}"><div class="canvas" style="transform:translate(10px,20px)"><div class="sticky-note" style="background:#e4dfb5;width:200px;height:160px">Untouched idea</div></div></div></body></html>`, { url: 'http://localhost:5173' });
  const names = ['window', 'document', 'localStorage', 'MutationObserver'];
  const originals = new Map(names.map(name => [name, Object.getOwnPropertyDescriptor(globalThis, name)]));
  for (const name of names) Object.defineProperty(globalThis, name, { configurable: true, value: dom.window[name] });
  const controller = await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}#${++sequence}`);
  try { controller.initializeColorPalette(); await run(controller, dom.window); }
  finally {
    controller.disposeBoardVariations(); controller.disposeColorPalette(); dom.window.close();
    for (const [name, descriptor] of originals) {
      if (descriptor) Object.defineProperty(globalThis, name, descriptor); else delete globalThis[name];
    }
  }
}
const sheet = () => document.getElementById('wk-board-variations');
test('production board colors follow palette and appearance without changing DOM, geometry, or content', async () => {
  await environment(async c => {
    const canvas = document.querySelector('.canvas'), original = document.body.innerHTML;
    c.syncBoardVariations('account-a', boards);
    assert.equal(sheet(), null, 'original theme remains unmodified');
    c.applyColorPalette('warm-minimal');
    const slot = c.boardVariationFor(id(0));
    assert.ok(sheet().textContent.includes(c.curatedPalettes[0].variants.light[slot].canvas));
    document.documentElement.dataset.theme = 'dark';
    await new Promise(resolve => setTimeout(resolve, 0));
    assert.ok(sheet().textContent.includes(c.curatedPalettes[0].variants.dark[slot].canvas));
    c.applyColorPalette('ocean-slate');
    assert.equal(c.boardVariationFor(id(0)), slot);
    assert.equal(document.querySelector('.canvas'), canvas);
    assert.equal(document.body.innerHTML, original);
    c.applyColorPalette(null);
    assert.equal(sheet(), null);
    assert.equal(JSON.parse(localStorage.getItem(c.boardVariationStoragePrefix + 'account-a'))[id(0)], slot);
  });
});
test('refresh restores assignments and account switching isolates relationships', async () => {
  await environment(c => {
    localStorage.setItem(c.boardVariationStoragePrefix + 'account-a', JSON.stringify({ [id(0)]: 5 }));
    localStorage.setItem(c.boardVariationStoragePrefix + 'account-b', JSON.stringify({ [id(0)]: 2 }));
    c.syncBoardVariations('account-a', boards);
    assert.equal(c.boardVariationFor(id(0)), 5);
    const saved = localStorage.getItem(c.boardVariationStoragePrefix + 'account-a');
    c.syncBoardVariations('account-b', boards);
    assert.equal(c.boardVariationFor(id(0)), 2);
    c.syncBoardVariations('account-a', boards.toReversed());
    assert.equal(c.boardVariationFor(id(0)), 5);
    assert.equal(localStorage.getItem(c.boardVariationStoragePrefix + 'account-a'), saved);
    c.disposeBoardVariations();
    c.syncBoardVariations('account-a', boards);
    assert.equal(c.boardVariationFor(id(0)), 5);
  });
});
test('invalid stored IDs and slots cannot inject selectors; returning boards keep their identity', async () => {
  await environment(c => {
    localStorage.setItem(c.boardVariationStoragePrefix + 'account-a', JSON.stringify({ 'bad"]{color:red}': 1, [id(0)]: 99, [id(6)]: 4 }));
    c.syncBoardVariations('account-a', boards);
    c.applyColorPalette('calm-sage');
    assert.ok(!sheet().textContent.includes('color:red'));
    assert.equal(c.boardVariationFor(id(6)), 4);
    c.syncBoardVariations('account-a', boards.filter(b => b.id !== id(6)));
    assert.ok(!sheet().textContent.includes(id(6)));
    c.syncBoardVariations('account-a', boards);
    assert.equal(c.boardVariationFor(id(6)), 4);
  });
});
test('shared choices update the same CSS sheet; routine reconciliation avoids repainting identical rules', async () => {
  await environment(c => {
    c.syncBoardVariations('account-a', boards); c.applyColorPalette('soft-lavender');
    const current = sheet(), previous = current.textContent;
    c.syncBoardVariations('account-a', boards.toReversed());
    assert.equal(sheet(), current); assert.equal(current.textContent, previous);
    c.syncBoardVariations('account-a', boards.map(b => b.id === id(0) ? { ...b, cardColor: 'gold' } : b));
    assert.equal(c.boardVariationFor(id(0)), 4);
    assert.equal(sheet(), current);
    assert.ok(current.textContent.includes(c.curatedPalettes[2].variants.light[4].card));
    c.syncBoardVariations(null, []);
    assert.equal(sheet(), null);
  });
});
test('cross-tab changes synchronize only the current account', async () => {
  await environment((c, window) => {
    c.syncBoardVariations('account-a', boards); c.applyColorPalette('calm-sage');
    const slot = c.boardVariationFor(id(0));
    localStorage.setItem(c.boardVariationStoragePrefix + 'account-b', JSON.stringify({ [id(0)]: 3 }));
    window.dispatchEvent(new window.StorageEvent('storage', { key: c.boardVariationStoragePrefix + 'account-b' }));
    assert.equal(c.boardVariationFor(id(0)), slot);
    localStorage.setItem(c.boardVariationStoragePrefix + 'account-a', JSON.stringify({ [id(0)]: 3 }));
    window.dispatchEvent(new window.StorageEvent('storage', { key: c.boardVariationStoragePrefix + 'account-a' }));
    assert.equal(c.boardVariationFor(id(0)), 3);
  });
});
test('readable but unwritable browser storage retains in-memory account relationships', async () => {
  await environment((c, window) => {
    const original = window.Storage.prototype.setItem;
    window.Storage.prototype.setItem = () => { throw new Error('Quota exceeded'); };
    try {
      c.syncBoardVariations('account-a', [{ ...boards[0], cardColor: 'rose' }]);
      c.syncBoardVariations('account-b', boards);
      c.syncBoardVariations('account-a', [boards[0]]);
      assert.equal(c.boardVariationFor(id(0)), 5);
      c.applyColorPalette('modern-neutral'); assert.ok(sheet());
    } finally { window.Storage.prototype.setItem = original; }
  });
});
