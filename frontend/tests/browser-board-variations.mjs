import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { browser, setup, visit, inspect, output } from './browser-ui-audit.mjs';
import { id } from './ui-audit-fixtures.mjs';
import { curatedPalettes } from '../src/theme/curatedPalettes.ts';

const results = [];
async function scan(env, label) {
  const state = await inspect(env.page);
  assert.deepEqual(state.contrasts, [], label);
  if (state.scrollWidth > state.width) {
    await env.page.screenshot({ path: `${output}/layout-failure.png`, fullPage: true });
  }
  assert.ok(state.scrollWidth <= state.width, JSON.stringify({ label, ...state }));
  assert.deepEqual(env.errors, [], label);
  assert.deepEqual(env.missing, [], label);
  results.push({ label, width: state.width, theme: state.theme, passed: true });
}
async function boardColors(page, selector = '.wk-board-card-wrap') {
  return page.locator(selector).evaluateAll(elements => Object.fromEntries(elements.map(element => [element.dataset.boardId, getComputedStyle(element).getPropertyValue('--board-variation-card').trim()])));
}
try {
  if (!process.env.WUKNA_LAYOUT_ONLY) for (const theme of ['light', 'dark']) for (const palette of curatedPalettes) {
    const env = await setup({ width: 390, theme });
    await env.context.addInitScript(id => localStorage.setItem('wukna.palette.v1', id), palette.id);
    await visit(env, '/boards');
    const original = await boardColors(env.page);
    assert.equal(new Set(Object.values(original)).size, 2, 'automatic board cards have different identities');
    await scan(env, `${palette.id}-${theme}-mobile-boards`);
    if (palette.id === 'ocean-slate') await env.page.screenshot({ path: `${output}/boards-${theme}-390.png`, fullPage: true });
    await visit(env, '/home');
    assert.deepEqual(await boardColors(env.page, '.wk-dashboard-board-grid button'), original);
    await visit(env, '/boards');
    assert.deepEqual(await boardColors(env.page), original, 'refresh retains board identities');
    await visit(env, `/boards/${id(2)}`);
    await env.page.locator('.sticky-note').first().waitFor();
    const paint = await env.page.locator('.workspace').evaluate(element => ({ canvas: getComputedStyle(element).getPropertyValue('--canvas').trim(), surface: getComputedStyle(element).getPropertyValue('--surface').trim() }));
    assert.ok(palette.variants[theme].some(v => v.canvas === paint.canvas && v.chrome === paint.surface));
    await scan(env, `${palette.id}-${theme}-mobile-canvas`);
    if (palette.id === 'ocean-slate') await env.page.screenshot({ path: `${output}/canvas-${theme}-390.png` });
    await visit(env, '/calendar');
    const date = env.page.locator('.wk-calendar-day--today .wk-calendar-date');
    const marker = await date.boundingBox(), target = await date.locator('..').boundingBox();
    assert.equal(marker.width, 24); assert.equal(marker.height, 24);
    assert.ok(target.height >= 44 && target.width >= 44, 'small marker keeps a generous date tap target');
    await date.click();
    assert.equal(await date.locator('..').getAttribute('aria-pressed'), 'true');
    await scan(env, `${palette.id}-${theme}-small-current-date`);
    if (palette.id === 'ocean-slate') await env.page.screenshot({ path: `${output}/calendar-${theme}-390.png`, fullPage: true });
    await env.context.close();
  }
  for (const theme of ['light', 'dark']) {
    for (const width of [320, 430, 767]) {
      const env = await setup({ width, theme });
      await visit(env, '/calendar');
      const date = env.page.locator('.wk-calendar-day--today .wk-calendar-date');
      const marker = await date.boundingBox(), target = await date.locator('..').boundingBox();
      const cell = await date.locator('../..').boundingBox();
      assert.equal(marker.width, 24); assert.equal(marker.height, 24);
      assert.ok(target.height >= 44 && target.width >= 24);
      assert.ok(Math.abs(marker.x + marker.width / 2 - cell.x - cell.width / 2) <= 1, 'current date stays centered at narrow and landscape mobile widths');
      await scan(env, `${theme}-calendar-marker-${width}`);
      if (width === 320 || width === 767) {
        await env.page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
        await env.page.getByRole('button', { name: 'Today', exact: true }).click();
        const scaledMarker = await date.boundingBox();
        const scaledTarget = await date.locator('..').boundingBox();
        assert.ok(scaledMarker.width <= 48 && scaledMarker.width <= scaledTarget.width);
        assert.ok(Math.abs(scaledMarker.width - scaledMarker.height) <= 1, 'enlarged date marker remains a circle inside its cell');
        await scan(env, `${theme}-calendar-enlarged-${width}`);
        // Palette-aware labels also need room when text is enlarged.
        await env.context.addInitScript(() => localStorage.setItem('wukna.palette.v1', 'warm-minimal'));
        await visit(env, '/boards');
        await env.page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
        await env.page.getByRole('button', { name: /^Actions for A collaborative board/ }).click();
        const choices = await env.page.locator('.wk-board-color-options span').first().boundingBox();
        assert.ok(choices.width >= 160, 'long color labels have room to wrap with enlarged text');
        await scan(env, `${theme}-palette-picker-enlarged-${width}`);
      }
      await env.context.close();
    }
    const env = await setup({ width: 1440, theme });
    await env.context.addInitScript(() => localStorage.setItem('wukna.palette.v1', 'calm-sage'));
    await visit(env, '/boards');
    assert.equal(await env.page.locator('.wk-sidebar-search kbd').count(), 0);
    await env.page.getByRole('button', { name: 'Collapse sidebar', exact: true }).click();
    const brand = await env.page.getByRole('button', { name: 'Wukna home', exact: true }).boundingBox();
    const toggle = await env.page.getByRole('button', { name: 'Expand sidebar', exact: true }).boundingBox();
    assert.ok(toggle.y - brand.y - brand.height >= 12, 'logo and toggle have separate visual space');
    assert.ok(Math.abs(brand.x + brand.width / 2 - toggle.x - toggle.width / 2) <= 1);
    await env.page.getByRole('button', { name: 'Expand sidebar', exact: true }).focus();
    await scan(env, `${theme}-sidebar-spacing-and-focus`);
    await env.page.screenshot({ path: `${output}/sidebar-${theme}-1440.png` });
    await env.page.keyboard.press('Control+k');
    const search = env.page.getByRole('textbox', { name: 'Search boards', exact: true });
    await search.waitFor({ state: 'visible' });
    assert.ok(await search.evaluate(element => element === document.activeElement), 'shortcut remains functional without its hint');
    // A palette changed through a second tab updates the existing canvas in place.
    await visit(env, `/boards/${id(2)}`);
    await env.page.locator('.sticky-note').first().waitFor();
    const before = await env.page.locator('.board-world').evaluate(element => {
      window.boardWorldForPaletteTest = element;
      return { style: element.style.cssText, notes: [...element.querySelectorAll('.sticky-note')].map(note => ({ style: note.style.cssText, text: note.textContent })) };
    });
    const second = await env.context.newPage();
    await second.goto('http://localhost:5173/account/preferences');
    await second.getByRole('button', { name: 'Color Studio', exact: true }).click();
    await second.getByRole('button', { name: /^Ocean Slate/ }).click();
    await env.page.waitForFunction(() => document.documentElement.dataset.colorPalette === 'ocean-slate');
    const after = await env.page.locator('.board-world').evaluate(element => ({ same: element === window.boardWorldForPaletteTest, style: element.style.cssText, notes: [...element.querySelectorAll('.sticky-note')].map(note => ({ style: note.style.cssText, text: note.textContent })) }));
    assert.ok(after.same, 'palette changes do not remount the board world');
    assert.deepEqual({ style: after.style, notes: after.notes }, before, 'palette changes preserve note geometry, pigments, and content');
    await scan(env, `${theme}-cross-tab-canvas-preservation`);
    await env.context.close();
  }
  await fs.writeFile(`${output}/board-variation-browser-results.json`, JSON.stringify(results, null, 2));
  console.log(JSON.stringify({ cases: results.length, failed: 0 }));
} finally { await browser.close(); }
