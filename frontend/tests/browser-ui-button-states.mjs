import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { browser, setup, visit, inspect, record, output } from './browser-ui-audit.mjs';

const results = [];
try {
  for (const theme of ['light', 'dark']) for (const width of [375, 1440]) {
    const env = await setup({ theme, width, height: width === 375 ? 667 : 1024 });
    let submitted = false;
    await env.context.route('**/api/calendar/events', async route => {
      if (route.request().method() !== 'POST') return route.fallback();
      submitted = true;
      await new Promise(resolve => setTimeout(resolve, 800));
      await route.fulfill({ status: 503, json: { code: 'service_unavailable' } });
    });
    await visit(env, '/calendar');
    await env.page.getByRole('button', { name: 'New event', exact: true }).click();
    const dialog = env.page.locator('dialog[open]');
    const submit = dialog.locator('button[type="submit"]');
    assert.equal(await submit.isDisabled(), true);
    await dialog.getByLabel('Title', { exact: true }).fill('Synthetic event for UI verification');
    await dialog.getByLabel('All day', { exact: true }).check();
    const before = await submit.boundingBox();
    await submit.click();
    await dialog.locator('button[aria-busy="true"]').waitFor();
    assert.equal(await submit.isDisabled(), true);
    const busy = await submit.boundingBox();
    assert.ok(Math.abs(before.width - busy.width) <= 1 && Math.abs(before.height - busy.height) <= 1, 'Loading preserves button dimensions');
    assert.deepEqual((await inspect(env.page)).contrasts, [], 'Loading controls remain readable');
    await env.page.keyboard.press('Escape');
    assert.equal(await dialog.count(), 1, 'Busy dialog keeps the pending operation visible');
    await dialog.getByRole('alert').waitFor();
    assert.equal(submitted, true);
    assert.equal(await submit.isEnabled(), true, 'Failed operation can be retried');
    const scan = await record(env, '/calendar', `button-error-${theme}-${width}`);
    assert.deepEqual(scan.contrasts, []);
    assert.ok(scan.scrollWidth <= width);
    assert.deepEqual(env.errors, []);
    assert.deepEqual(env.missing, []);
    results.push({ theme, width, disabled: true, loading: true, failure: true, passed: true });
    await env.context.close();
  }
  await fs.writeFile(`${output}/button-states.json`, JSON.stringify(results, null, 2));
  console.log(JSON.stringify(results));
} finally { await browser.close(); }
