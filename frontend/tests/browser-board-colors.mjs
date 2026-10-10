import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { browser, setup, visit, inspect, output } from './browser-ui-audit.mjs';
import { boards } from './ui-audit-fixtures.mjs';
import { boardCardTones, boardCardTone } from '../src/features/boards/boardCardTone.ts';
import { curatedPalettes } from '../src/theme/curatedPalettes.ts';

const results = [];
async function scan(env, label) {
  const snapshot = await inspect(env.page);
  assert.deepEqual(snapshot.contrasts, [], label);
  assert.ok(snapshot.scrollWidth <= snapshot.width, label);
  assert.deepEqual(env.errors, [], label);
  assert.deepEqual(env.missing, [], label);
  results.push({ label, theme: snapshot.theme, width: snapshot.width, passed: true });
}
async function colorFixture(env) {
  for (const board of boards) { board.cardColor = null; board.cardColorVersion = 0; }
  let outcome = 'success';
  const writes = [];
  await env.context.route('**/api/boards/*/appearance', async route => {
    const board = boards.find(board => route.request().url().includes(board.id));
    const request = route.request().postDataJSON();
    writes.push({ ...request, boardId: board.id });
    await new Promise(resolve => setTimeout(resolve, 120));
    if (outcome === 'error') return route.fulfill({ status: 503, json: { error: 'service_unavailable' } });
    if (outcome === 'conflict') {
      board.cardColor = 'sage'; board.cardColorVersion++;
      return route.fulfill({ status: 409, json: { error: 'board_appearance_conflict' } });
    }
    assert.equal(request.expectedVersion, board.cardColorVersion);
    if (board.cardColor !== request.cardColor) {
      board.cardColor = request.cardColor; board.cardColorVersion++;
    }
    return route.fulfill({ json: board });
  });
  return { writes, setOutcome(value) { outcome = value; } };
}
async function openActions(page, board) {
  await page.getByRole('button', { name: `Actions for ${board.title}`, exact: true }).click();
  return page.getByRole('dialog', { name: board.role === 1 ? 'Board actions' : 'Board appearance', exact: true });
}
async function close(page) {
  await page.keyboard.press('Escape');
  await page.locator('dialog[open]').waitFor({ state: 'hidden' });
}
async function selectColor(dialog, name) {
  // Click the visible choice and await the save, including when its resolved
  // color matches the current automatic accent and the card does not repaint.
  const choice = dialog.locator(`input[type="radio"][value="${name.toLowerCase()}"]`);
  await choice.locator('..').click();
  await dialog.page().waitForFunction(() => !document.querySelector('.wk-board-color-control')?.disabled);
}
try {
  for (const width of [375, 1440]) for (const theme of ['light', 'dark']) for (const palette of curatedPalettes) {
    const env = await setup({ width, theme, height: width === 375 ? 812 : 1024 });
    const fixture = await colorFixture(env);
    await env.context.addInitScript(id => localStorage.setItem('wukna.palette.v1', id), palette.id);
    await visit(env, '/boards');
    assert.equal(await env.page.getByRole('button', { name: `Actions for ${boards[1].title}`, exact: true }).count(), 0, 'viewers have no board color mutation control');
    const card = env.page.locator('.wk-board-card-wrap').first();
    const originalRect = await card.evaluate(element => { const rect = element.getBoundingClientRect(); return { width: rect.width, height: rect.height }; });
    const dialog = await openActions(env.page, boards[0]);
    for (const tone of boardCardTones) {
      await selectColor(dialog, tone.name);
      await dialog.getByRole('button', { name: 'Done', exact: true }).waitFor({ state: 'visible' });
      await env.page.waitForFunction(tone => document.querySelector('.wk-board-card-wrap').dataset.boardTone === tone, tone.id);
      await scan(env, `${palette.id}-${tone.id}-picker`);
    }
    await close(env.page);
    assert.deepEqual(await card.evaluate(element => { const rect = element.getBoundingClientRect(); return { width: rect.width, height: rect.height }; }), originalRect, 'card color choices preserve card dimensions');
    assert.equal(fixture.writes.at(-1).cardColor, 'rose');
    assert.equal(await env.page.evaluate(() => localStorage.getItem('wukna.board-colors.v1')), null);
    await visit(env, '/home');
    assert.equal(await env.page.locator('.wk-dashboard-board-grid button').first().getAttribute('data-board-tone'), 'rose');
    await scan(env, `${palette.id}-shared-color-on-home-after-navigation`);
    await visit(env, '/boards');
    assert.equal(await card.getAttribute('data-board-tone'), 'rose', 'shared choice survives reload');
    const reset = await openActions(env.page, boards[0]);
    await reset.getByRole('button', { name: 'Use automatic color', exact: true }).click();
    await env.page.waitForFunction(tone => document.querySelector('.wk-board-card-wrap').dataset.boardTone === tone, boardCardTone(boards[0].id));
    await close(env.page);
    assert.equal(fixture.writes.at(-1).cardColor, null);
    await scan(env, `${palette.id}-shared-color-reset`);
    await env.context.close();
  }
  {
    const env = await setup({ width: 375 });
    const fixture = await colorFixture(env);
    await visit(env, '/boards');
    let dialog = await openActions(env.page, boards[0]);
    fixture.setOutcome('error');
    await selectColor(dialog, 'Blue');
    await dialog.getByRole('alert').waitFor();
    assert.equal(await env.page.locator('.wk-board-card-wrap').first().getAttribute('data-board-tone'), boardCardTone(boards[0].id), 'failed saving keeps the previous card color');
    fixture.setOutcome('conflict');
    await selectColor(dialog, 'Rose');
    await dialog.getByRole('alert').filter({ hasText: 'The latest choice is shown' }).waitFor();
    assert.ok(await dialog.getByRole('radio', { name: 'Sage', exact: true }).isChecked());
    fixture.setOutcome('success');
    await selectColor(dialog, 'Gold');
    await env.page.waitForFunction(() => document.querySelector('.wk-board-card-wrap').dataset.boardTone === 'gold');
    await close(env.page);
    await scan(env, 'color-save-failure-conflict-and-retry');
    await env.page.setViewportSize({ width: 1440, height: 1024 });
    await env.page.locator('.wk-board-link').first().click({ button: 'right' });
    dialog = env.page.getByRole('dialog', { name: 'Board actions', exact: true });
    await selectColor(dialog, 'Blue');
    await env.page.waitForFunction(() => document.querySelector('.wk-board-link').dataset.boardTone === 'blue');
    await selectColor(dialog, 'Rose');
    await env.page.waitForFunction(() => document.querySelector('.wk-board-link').dataset.boardTone === 'rose');
    assert.ok(await dialog.getByRole('radio', { name: 'Rose', exact: true }).isChecked());
    await close(env.page);
    await scan(env, 'sidebar-menu-uses-current-shared-color-and-version');
    boards[1].canEdit = true;
    await visit(env, '/boards');
    dialog = await openActions(env.page, boards[1]);
    assert.equal(await dialog.getByRole('button', { name: 'Rename board', exact: true }).count(), 0);
    assert.equal(await dialog.getByRole('button', { name: 'Delete board', exact: true }).count(), 0);
    await selectColor(dialog, 'Clay');
    await env.page.waitForFunction(() => document.querySelectorAll('.wk-board-card-wrap')[1].dataset.boardTone === 'clay');
    await close(env.page);
    await scan(env, 'editor-can-change-color-without-owner-management-actions');
    boards[1].canEdit = false;
    await env.context.close();
  }
  for (const width of [320, 768, 1920]) for (const theme of ['light', 'dark']) for (const fontSize of ['100%', '200%']) {
    const env = await setup({ width, theme, height: 1024 });
    await colorFixture(env);
    await visit(env, '/boards');
    await env.page.evaluate(size => { document.documentElement.style.fontSize = size; }, fontSize);
    const dialog = await openActions(env.page, boards[0]);
    await scan(env, `shared-picker-${width}-${theme}-text-${fontSize}`);
    assert.ok(await dialog.evaluate(element => element.scrollWidth <= element.clientWidth), 'picker fits the dialog');
    assert.ok(await dialog.getByRole('heading', { level: 2 }).evaluate(element =>
      element.getBoundingClientRect().height <= parseFloat(getComputedStyle(element).lineHeight) * 3), 'dialog title remains readable with enlarged text');
    const choices = await dialog.locator('.wk-board-color-options span').evaluateAll(elements => elements.map(element => { const rect = element.getBoundingClientRect(); return { top: rect.top, height: rect.height }; }));
    for (const choice of choices) for (const sibling of choices.filter(sibling => Math.abs(sibling.top - choice.top) < 1))
      assert.ok(Math.abs(sibling.height - choice.height) < 1, 'choices in each row align');
    assert.ok(choices.every(choice => choice.height < 180), 'large text wraps without squeezing words into vertical columns');
    await env.context.close();
  }
  await fs.writeFile(`${output}/board-color-browser-results.json`, JSON.stringify(results, null, 2));
  console.log(JSON.stringify({ cases: results.length, failed: 0 }));
} catch (error) {
  await fs.writeFile(`${output}/board-color-browser-failure.txt`, error.stack);
  throw error;
} finally { await browser.close(); }
