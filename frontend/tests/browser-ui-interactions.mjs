import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { browser, setup, visit, inspect, record, output } from './browser-ui-audit.mjs';
import { id, tasks, today } from './ui-audit-fixtures.mjs';

// Explicit Opera-only UI regression checks. API responses are synthetic, including
// errors and offline realtime; this is not a backend integration test.
const results = [];
const filter = process.env.WUKNA_AUDIT_FILTER;
async function check(env, name, run) {
  if (filter && !name.includes(filter)) return;
  env.page.setDefaultTimeout(4500);
  const result = { name, width: env.page.viewportSize().width };
  try {
    await run();
    const scan = await inspect(env.page);
    result.theme = scan.theme;
    assert.deepEqual(scan.contrasts, [], 'Button text contrast');
    assert.ok(scan.scrollWidth <= scan.width, 'Page overflow');
    assert.deepEqual(scan.unnamed, [], 'Accessible button names');
    assert.deepEqual(env.errors, [], 'Browser exceptions');
    assert.deepEqual(env.missing, [], 'Unhandled fixture endpoints');
    for (const icon of scan.icons) assert.ok(Math.abs(icon.offset) <= 1, `${icon.label} centering`);
    result.passed = true;
  } catch (error) {
    result.passed = false;
    result.error = error.message;
    result.snapshot = await record(env, env.page.url(), `failed-${name}-${result.width}-${results.length}`);
  }
  results.push(result);
  console.log(JSON.stringify(result.passed ? result : { ...result, snapshot: undefined }));
}
async function closeModal(page) {
  await page.keyboard.press('Escape');
  await page.locator('dialog[open]').waitFor({ state: 'hidden' });
}
async function focusContained(page) {
  const dialog = page.locator('dialog[open]').last();
  await dialog.locator('button:not(:disabled)').last().focus();
  await page.keyboard.press('Tab');
  assert.ok(await dialog.evaluate(e => e.contains(document.activeElement)), 'Dialog contains keyboard focus');
}
async function capture(env, route, label) {
  const scan = await record(env, route, label);
  assert.deepEqual(scan.contrasts, [], 'Open panel button contrast');
  assert.ok(scan.scrollWidth <= scan.width, 'Open panel page overflow');
  assert.deepEqual(scan.unnamed, [], 'Open panel accessible button names');
}

try {
  for (const theme of ['light', 'dark']) for (const width of [375, 768, 1024, 1440]) {
    const env = await setup({ theme, width, height: width === 375 ? 667 : 1024 });
    const page = env.page;
    await check(env, 'dashboard-action-hierarchy', async () => {
      await visit(env, '/home');
      assert.equal(await page.getByRole('button', { name: 'Add task', exact: true }).count(), 0);
      await page.getByRole('textbox', { name: 'Add a task for today', exact: true }).fill('Plan today');
      assert.equal(await page.locator('.wk-dashboard-quick-add').count(), 1);
      assert.equal(await page.locator('.wk-dashboard-quick-add').getByRole('button', { name: 'Add', exact: true }).count(), 1);
      if (width >= 768) {
        const toggle = page.getByRole('button', { name: /^(Collapse|Expand) sidebar$/ });
        await toggle.click();
        await page.waitForTimeout(280);
        await toggle.click();
        await page.waitForTimeout(280);
        await page.keyboard.press('Control+k');
        await page.waitForFunction(() => document.activeElement?.getAttribute('aria-label') === 'Search boards');
        assert.equal(await page.getByRole('textbox', { name: 'Search boards' }).evaluate(e => e === document.activeElement), true);
      }
    });
    await check(env, 'task-details-and-checkmarks', async () => {
      await visit(env, '/tasks');
      const geometry = await page.locator('.wk-board-task .wk-task-complete:has(svg)').first().evaluate(e => {
        const r = e.getBoundingClientRect(), s = getComputedStyle(e, '::before'), icon = e.querySelector('svg').getBoundingClientRect();
        return { box: parseFloat(s.width), icon: icon.width, x: icon.x + icon.width / 2 - r.x - parseFloat(s.left), y: icon.y + icon.height / 2 - r.y - parseFloat(s.top) };
      });
      assert.ok(geometry.icon <= geometry.box, 'Checkmark fits checkbox');
      assert.ok(Math.abs(geometry.x) <= 1 && Math.abs(geometry.y) <= 1, 'Checkmark centered inside checkbox');
      await page.getByRole('button', { name: `Open ${tasks[0].title}`, exact: true }).first().click();
      const details = page.locator('.wk-task-details');
      const rect = await details.boundingBox();
      assert.ok(rect.y >= 0 && rect.y < page.viewportSize().height, 'Details open inside viewport');
      await details.getByLabel('Date', { exact: true }).fill('');
      assert.equal(await details.getByLabel('Time', { exact: true }).isDisabled(), true);
      await details.getByRole('button', { name: 'Save changes', exact: true }).scrollIntoViewIfNeeded();
      if (width < 768) await focusContained(page);
      await capture(env, '/tasks', `details-${theme}-${width}`);
      await details.getByRole('button', { name: 'Delete', exact: true }).click();
      await capture(env, '/tasks', `delete-confirmation-${theme}-${width}`);
      await page.getByRole('alertdialog').getByRole('button', { name: 'Cancel', exact: true }).click();
      await page.getByRole('alertdialog').waitFor({ state: 'hidden' });
      await details.getByLabel('Title', { exact: true }).focus();
      await page.keyboard.press('Escape');
      await details.waitFor({ state: 'hidden' });
    });
    await check(env, 'template-editor', async () => {
      await visit(env, '/tasks/templates');
      await page.getByRole('button', { name: 'Create template', exact: true }).click();
      const dialog = page.locator('dialog[open]');
      assert.equal(await dialog.getByRole('button', { name: 'Save template', exact: true }).isDisabled(), true);
      await dialog.getByLabel('Template name').fill('A'.repeat(80));
      await dialog.getByLabel('Task 1 name').fill('A readable reusable task');
      assert.equal(await dialog.getByRole('button', { name: 'Save template', exact: true }).isEnabled(), true);
      await focusContained(page);
      await capture(env, '/tasks/templates', `template-dialog-${theme}-${width}`);
      await closeModal(page);
    });
    await check(env, 'calendar-event-editor', async () => {
      await visit(env, '/calendar');
      assert.equal(await page.locator('.wk-calendar-mobile-agenda').getByRole('button', { name: 'Add event', exact: true }).count(), 0);
      await page.getByRole('button', { name: 'New event', exact: true }).click();
      const dialog = page.locator('dialog[open]');
      assert.equal(await dialog.getByRole('button', { name: 'Create event', exact: true }).isDisabled(), true);
      await dialog.getByLabel('Title', { exact: true }).fill('A clear event title');
      await dialog.getByLabel('All day', { exact: true }).check();
      assert.equal(await dialog.getByLabel('Start time', { exact: true }).count(), 0);
      await dialog.getByLabel('All day', { exact: true }).uncheck();
      assert.equal(await dialog.getByLabel('Start time', { exact: true }).count(), 1);
      await focusContained(page);
      await dialog.getByRole('button', { name: 'Create event', exact: true }).hover();
      await capture(env, '/calendar', `calendar-dialog-${theme}-${width}`);
      await closeModal(page);
      assert.equal(await page.getByRole('button', { name: 'New event', exact: true }).evaluate(e => e === document.activeElement), true);
    });
    await check(env, 'board-action-hierarchy-and-chat', async () => {
      await visit(env, `/boards/${id(2)}?chat=1`);
      for (const name of ['New note', 'New task list', 'Connect notes', 'Board tasks']) {
        assert.equal(await page.getByRole('button', { name, exact: true }).count(), 1, `${name} appears once`);
      }
      await page.getByRole('button', { name: 'Create scheduled task', exact: true }).click();
      const form = page.getByRole('form', { name: 'Create scheduled task', exact: true });
      await form.getByLabel('Title', { exact: true }).fill('A scheduled task');
      await form.getByLabel('Start', { exact: true }).fill(`${today}T18:00`);
      await form.getByRole('button', { name: 'Post task', exact: true }).scrollIntoViewIfNeeded();
      const button = await form.getByRole('button', { name: 'Post task', exact: true }).boundingBox();
      assert.ok(button.y >= 0 && button.y + button.height <= page.viewportSize().height, 'Post task visible after form scrolling');
      await capture(env, page.url(), `scheduled-task-${theme}-${width}`);
      await form.getByLabel('Time zone', { exact: true }).fill('Invalid/Zone');
      assert.equal(await form.getByText('Choose a valid time zone.', { exact: true }).count(), 1);
      await form.getByRole('button', { name: 'Back to message', exact: true }).click();
      assert.equal(await page.getByRole('button', { name: 'Create scheduled task', exact: true }).count(), 1);
    });
    await check(env, 'quick-template-action-hierarchy', async () => {
      await visit(env, '/tasks/quick');
      const panel = page.getByRole('complementary', { name: 'Task templates', exact: true });
      assert.equal(await panel.getByRole('button', { name: 'Browse all', exact: true }).count(), 1);
      assert.equal(await panel.locator('li button').count(), 0);
    });
    await check(env, 'board-properties-and-viewer', async () => {
      await visit(env, `/boards/${id(2)}`);
      await page.getByRole('button', { name: 'Open properties for Note 1', exact: true }).click();
      await page.getByRole('button', { name: 'Close properties', exact: true }).waitFor();
      await capture(env, page.url(), `properties-${theme}-${width}`);
      await page.getByRole('button', { name: 'Close properties', exact: true }).click();
      await visit(env, `/boards/${id(3)}`);
      assert.equal(await page.getByRole('button', { name: 'New note', exact: true }).count(), 0);
      assert.equal(await page.getByRole('button', { name: 'New task list', exact: true }).count(), 0);
      await page.getByRole('button', { name: 'Share', exact: true }).click();
      await capture(env, page.url(), `viewer-share-${theme}-${width}`);
    });
    await check(env, 'live-theme-switch', async () => {
      await visit(env, '/account/preferences');
      const opposite = theme === 'light' ? 'Dark' : 'Light';
      await page.getByRole('radio', { name: opposite, exact: true }).check({ force: true });
      assert.equal(await page.locator('html').getAttribute('data-theme'), opposite.toLowerCase());
      await capture(env, '/account/preferences', `theme-switch-${theme}-${width}`);
      await page.getByRole('radio', { name: theme === 'light' ? 'Light' : 'Dark', exact: true }).check({ force: true });
    });
    await env.context.close();
  }
  for (const state of ['empty', 'error', 'loading']) for (const theme of ['light', 'dark']) for (const width of [375, 1440]) {
    const env = await setup({ state, theme, width, height: width === 375 ? 667 : 1024 });
    for (const route of ['/home', '/boards', '/tasks', '/tasks/quick', '/tasks/templates', '/calendar', '/notifications']) {
      await check(env, `${state}-${route.replaceAll('/', '_')}`, async () => {
        await visit(env, route);
        if (state === 'loading') assert.ok(await env.page.locator('[role="status"], [aria-busy="true"]').count(), 'Loading announces progress');
        if (state === 'empty' && route === '/tasks/templates') assert.equal(await env.page.getByRole('button', { name: 'Create template', exact: true }).count(), 1);
        if (state === 'error' && route !== '/notifications') assert.ok(await env.page.locator('[role="alert"]').count(), 'Failure has an alert');
        if (state === 'error') await capture(env, route, `error-${theme}-${width}-${route.replaceAll('/', '_')}`);
      });
    }
    await env.context.close();
  }
  for (const theme of ['light', 'dark']) for (const width of [375, 1440]) {
    const env = await setup({ theme, width, authenticated: false });
    for (const route of ['/', '/login', '/register', '/terms', '/privacy']) {
      await check(env, `public-${route.replaceAll('/', '_')}`, async () => {
        await env.page.goto(`http://localhost:5173${route}`);
        await env.page.evaluate(() => document.fonts.ready);
        await env.page.waitForTimeout(450);
      });
    }
    await env.context.close();
  }
  await fs.writeFile(`${output}/interactions.json`, JSON.stringify(results, null, 2));
  console.log(JSON.stringify({ total: results.length, failed: results.filter(r => !r.passed).length }));
  if (results.some(r => !r.passed)) process.exitCode = 1;
} finally {
  await browser.close();
}
