import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { browser, setup, visit, inspect, record, output } from './browser-ui-audit.mjs';
import { tasks } from './ui-audit-fixtures.mjs';

const results = [];
async function verify(env, label) {
  const scan = await inspect(env.page);
  assert.deepEqual(scan.contrasts, [], label);
  assert.ok(scan.scrollWidth <= scan.width, label);
  assert.deepEqual(scan.unnamed, [], label);
  assert.deepEqual(env.errors, [], label);
  assert.deepEqual(env.missing, [], label);
  results.push({ label, theme: scan.theme, width: scan.width, passed: true });
}
try {
  for (const theme of ['light', 'dark']) for (const width of [375, 768, 1024, 1440]) {
    const env = await setup({ theme, width });
    for (const route of ['/journal', '/library', '/library/pictures']) {
      await visit(env, route);
      await verify(env, route);
    }
    if (width === 375 || width === 1440) for (const route of ['/tasks', '/calendar']) {
      await visit(env, route);
      if (route === '/calendar' && width === 1440) await env.page.getByRole('button', { name: 'Agenda', exact: true }).click();
      const trigger = route === '/tasks'
        ? env.page.getByRole('button', { name: `Open ${tasks[0].title}`, exact: true }).first()
        : width === 375 ? env.page.locator('.wk-calendar-mobile-agenda li button').last() : env.page.locator('.wk-calendar-agenda-item').last();
      await trigger.press('Shift+F10');
      const dialog = env.page.locator('dialog[open]');
      await dialog.getByRole('button', { name: 'Delete', exact: true }).click();
      await env.page.getByRole('alertdialog').waitFor();
      const scan = await record(env, route, `menu-confirmation-${theme}-${width}-${route.slice(1)}`);
      assert.deepEqual(scan.contrasts, []);
      await dialog.getByRole('button', { name: 'Back', exact: true }).click();
      await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
      await dialog.waitFor({ state: 'hidden' });
      await verify(env, `${route}-keyboard-actions`);
    }
    if (width === 375) {
      await visit(env, '/home');
      await env.page.getByRole('button', { name: /^More/ }).click();
      await env.page.getByRole('dialog', { name: 'More', exact: true }).waitFor();
      await verify(env, 'mobile-more-navigation');
      await env.page.keyboard.press('Escape');
      await env.page.getByRole('dialog', { name: 'More', exact: true }).waitFor({ state: 'hidden' });
    }
    await env.context.close();
  }
  await fs.writeFile(`${output}/additional-routes.json`, JSON.stringify(results, null, 2));
  console.log(JSON.stringify({ checked: results.length, failed: 0 }));
} finally { await browser.close(); }
