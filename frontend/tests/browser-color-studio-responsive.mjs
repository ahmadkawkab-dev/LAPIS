import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { browser, setup, visit, inspect, output } from './browser-ui-audit.mjs';
import { boards, notes } from './ui-audit-fixtures.mjs';

// Enrich only this process's synthetic board summary: one populated thumbnail,
// one empty thumbnail. Saved board/note data and the fixture source stay untouched.
boards[0].previewNodes = notes.filter(note => note.parentNoteId === null).map(note => ({
  id: note.id, type: note.kind, color: note.color,
  x: note.positionX, y: note.positionY, width: note.width, height: note.height,
}));
boards[0].previewConnections = [{ sourceId: notes[0].id, targetId: notes[1].id, type: 1 }];
const results = [];
async function scan(env, label) {
  const value = await inspect(env.page);
  assert.deepEqual(value.contrasts, [], label);
  assert.ok(value.scrollWidth <= value.width, label);
  assert.deepEqual(env.errors, [], label);
  assert.deepEqual(env.missing, [], label);
  results.push({ label, theme: value.theme, width: value.width, passed: true });
}
async function checkStudio(env, label) {
  const page = env.page;
  await page.getByRole('button', { name: 'Color Studio', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Color Studio', exact: true });
  await dialog.getByRole('checkbox', { name: 'Compare with original', exact: true }).check();
  await dialog.getByRole('button', { name: /^Ocean Slate/ }).click();
  await dialog.getByRole('radio', { name: 'Dark', exact: true }).check({ force: true });
  await dialog.getByRole('checkbox', { name: 'Use this palette across the app', exact: true }).check();
  assert.ok(await dialog.evaluate(element => element.scrollWidth <= element.clientWidth + 1), `${label} dialog fits horizontally`);
  const sample = dialog.getByRole('region', { name: 'Ocean Slate component preview', exact: true });
  await sample.getByRole('button', { name: 'Primary', exact: true }).click();
  await sample.getByRole('status').filter({ hasText: 'Primary action preview' }).waitFor();
  await sample.getByRole('textbox', { name: 'Sample input', exact: true }).fill('A little color');
  await scan(env, `${label}-studio`);
  await dialog.getByRole('button', { name: 'Reset to Original Theme', exact: true }).click();
  await page.keyboard.press('Escape');
  await dialog.waitFor({ state: 'hidden' });
  assert.equal(await page.locator('html').getAttribute('data-color-palette'), null);
  assert.ok(await page.getByRole('button', { name: 'Color Studio', exact: true }).evaluate(element => element === document.activeElement));
  await scan(env, `${label}-reset`);
}
try {
  for (const width of [320, 390, 430, 768, 1024, 1280, 1920]) for (const theme of ['light', 'dark']) {
    const env = await setup({ width, theme, height: width < 768 ? 812 : 1024 });
    await visit(env, '/boards');
    const colors = await env.page.locator('.wk-board-preview-nodes rect').evaluateAll(rects => rects.map(rect => rect.getAttribute('fill')));
    assert.deepEqual(colors, notes.filter(note => note.parentNoteId === null).map(note => note.color));
    assert.equal(await env.page.locator('.wk-board-preview-edges path').count(), 1);
    const tones = await env.page.locator('.wk-board-card-wrap').evaluateAll(cards => cards.map(card => card.dataset.boardTone));
    assert.notEqual(tones[0], tones[1]);
    await scan(env, 'colored-board-thumbnails');
    if (width === 390 || width === 1280) await env.page.screenshot({ path: `${output}/colored-boards-${theme}-${width}.png`, fullPage: true });
    await visit(env, '/home');
    assert.deepEqual(await env.page.locator('.wk-dashboard-board-grid button').evaluateAll(cards => cards.map(card => card.dataset.boardTone)), tones);
    await scan(env, 'consistent-home-board-accents');
    await visit(env, '/account/preferences');
    await checkStudio(env, `width-${width}`);
    await env.context.close();
  }
  for (const width of [375, 1440]) for (const theme of ['light', 'dark']) for (const scale of [1.25, 1.5, 2]) {
    const env = await setup({ width, theme, height: width < 768 ? 812 : 1024 });
    await visit(env, '/account/preferences');
    await env.page.evaluate(scale => { document.documentElement.style.fontSize = `${100 * scale}%`; }, scale);
    await checkStudio(env, `text-scale-${scale}`);
    await env.context.close();
  }
  await fs.writeFile(`${output}/responsive-browser-results.json`, JSON.stringify(results, null, 2));
  console.log(JSON.stringify({ cases: results.length, failed: 0 }));
} catch (error) {
  await fs.writeFile(`${output}/responsive-browser-failure.txt`, error.stack);
  throw error;
} finally { await browser.close(); }
