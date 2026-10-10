import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { browser, setup, visit, inspect, output } from './browser-ui-audit.mjs';
import { id } from './ui-audit-fixtures.mjs';
import { curatedPalettes } from '../src/theme/curatedPalettes.ts';

// Uses the existing disposable Opera profile and synthetic API fixtures only.
const results = [];
const storageKey = 'wukna.palette.v1';
const routes = ['/home', '/boards', '/tasks', '/tasks/quick', '/tasks/templates', '/calendar', '/notifications', '/account/profile', '/account/preferences', `/boards/${id(2)}?chat=1`];
async function geometry(page) {
  return page.evaluate(() => Object.fromEntries(['.wk-shell-main', '.wk-sidebar', '.wk-feature-stage', '.board-header', '.canvas', '.canvas-toolbar', '.wk-calendar-header', '.wk-dashboard-header'].map(selector => {
    const element = document.querySelector(selector), rect = element?.getBoundingClientRect();
    return [selector, rect ? { x: rect.x, y: rect.y, width: rect.width, height: rect.height } : null];
  })));
}
async function noteAppearance(page) {
  await page.locator('.sticky-note').first().waitFor();
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  return page.locator('.sticky-note').evaluateAll(notes => notes.map(note => ({ color: note.style.backgroundColor, width: note.style.width, height: note.style.height })));
}
async function scan(env, label) {
  const snapshot = await inspect(env.page);
  assert.deepEqual(snapshot.contrasts, [], label);
  assert.ok(snapshot.scrollWidth <= snapshot.width, label);
  assert.deepEqual(snapshot.unnamed, [], label);
  assert.deepEqual(env.errors, [], label);
  assert.deepEqual(env.missing, [], label);
  results.push({ label, width: snapshot.width, theme: snapshot.theme, passed: true });
}
async function studio(page) {
  await page.getByRole('button', { name: 'Color Studio', exact: true }).click();
  return page.getByRole('dialog', { name: 'Color Studio', exact: true });
}
async function visitReady(env, route) {
  await visit(env, route);
  await env.page.waitForFunction(() => !document.querySelector('.wk-loading, [data-tour-loading="true"]'));
  if (route.includes('?chat=1')) await noteAppearance(env.page);
}
async function close(page) {
  await page.keyboard.press('Escape');
  await page.locator('dialog[open]').waitFor({ state: 'hidden' });
}
try {
  for (const width of [375, 1440]) for (const theme of ['light', 'dark']) {
    const env = await setup({ width, theme, height: width === 375 ? 812 : 1024 });
    const page = env.page;
    const baseline = new Map();
    let originalNotes;
    for (const route of routes) {
      await visitReady(env, route);
      baseline.set(route, await geometry(page));
      await scan(env, `original-${route}`);
      if (route.includes('?chat=1')) originalNotes = await noteAppearance(page);
      if (route === '/boards' || route === '/home') await page.screenshot({ path: `${output}/original-${route.slice(1)}-${theme}-${width}.png` });
    }
    await visit(env, '/account/preferences');
    const original = await page.evaluate(() => ({ primary: getComputedStyle(document.documentElement).getPropertyValue('--primary'), inline: document.documentElement.style.cssText, theme: localStorage.getItem('wukna.theme.v1') }));
    let dialog = await studio(page);
    await page.evaluate(() => { window.palettePageMarker = 'no-reload'; });
    await dialog.getByRole('checkbox', { name: 'Compare with original', exact: true }).check();
    for (const palette of curatedPalettes) {
      await dialog.getByRole('button', { name: new RegExp(`^${palette.name}`) }).click();
      for (const sampleMode of ['light', 'dark']) {
        await dialog.getByRole('radio', { name: sampleMode === 'light' ? 'Light' : 'Dark', exact: true }).check({ force: true });
        const sample = dialog.getByRole('region', { name: `${palette.name} component preview`, exact: true });
        assert.equal(await sample.evaluate(e => getComputedStyle(e).getPropertyValue('--primary').trim()), palette[sampleMode]['--primary']);
        assert.equal(await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--primary')), original.primary);
        assert.equal(await page.evaluate(() => window.palettePageMarker), 'no-reload');
        if (width === 1440) {
          for (const name of ['Primary', 'Secondary', 'Quiet', 'Delete']) {
            await sample.getByRole('button', { name, exact: true }).hover();
            await scan(env, `${palette.id}-${sampleMode}-${name}-hover`);
          }
          await sample.getByRole('button', { name: 'Primary', exact: true }).focus();
          await page.keyboard.press('Tab');
          assert.ok(await sample.getByRole('button', { name: 'Secondary', exact: true }).evaluate(e => e.matches(':focus-visible') && parseFloat(getComputedStyle(e).outlineWidth) >= 2));
          await scan(env, `${palette.id}-${sampleMode}-keyboard-focus`);
        }
        await scan(env, `${palette.id}-${sampleMode}-scoped-comparison`);
      }
    }
    await page.screenshot({ path: `${output}/comparison-${theme}-${width}.png` });
    await dialog.getByRole('checkbox', { name: 'Compare with original', exact: true }).uncheck();
    await dialog.getByRole('checkbox', { name: 'Use this palette across the app', exact: true }).check();
    await close(page);
    for (const palette of curatedPalettes) {
      await visit(env, '/account/preferences');
      dialog = await studio(page);
      await dialog.getByRole('button', { name: new RegExp(`^${palette.name}`) }).click();
      assert.equal(await page.locator('html').getAttribute('data-color-palette'), palette.id);
      await close(page);
      for (const route of routes) {
        await visitReady(env, route);
        assert.equal(await page.locator('html').getAttribute('data-color-palette'), palette.id);
        assert.equal(await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--primary').trim()), palette[theme]['--primary']);
        const current = await geometry(page), previous = baseline.get(route);
        for (const [selector, rect] of Object.entries(current)) if (rect && previous[selector]) {
          for (const key of ['x', 'y', 'width', 'height']) assert.ok(Math.abs(rect[key] - previous[selector][key]) <= 1, `${palette.id} ${route} ${selector} ${key} geometry`);
        }
        if (route.includes('?chat=1')) {
          const colors = await noteAppearance(page);
          assert.equal(colors.length, 6);
          assert.equal(colors[0].color, 'rgb(238, 232, 219)');
          assert.deepEqual(colors, originalNotes, `${palette.id} preserves actual note pigments and geometry`);
        }
        await scan(env, `${palette.id}-${route}`);
        if (route === '/home' && palette.id === 'soft-lavender') await page.screenshot({ path: `${output}/app-${palette.id}-${theme}-${width}.png` });
      }
    }
    await visit(env, '/account/preferences');
    const other = theme === 'light' ? 'Dark' : 'Light';
    await page.getByRole('radio', { name: other, exact: true }).check({ force: true });
    await page.waitForFunction(expected => document.documentElement.style.getPropertyValue('--primary') === expected, curatedPalettes.at(-1)[other.toLowerCase()]['--primary']);
    await scan(env, 'existing-theme-control-with-palette');
    await page.getByRole('radio', { name: theme === 'light' ? 'Light' : 'Dark', exact: true }).check({ force: true });
    dialog = await studio(page);
    await dialog.getByRole('button', { name: 'Reset to Original Theme', exact: true }).click();
    await close(page);
    assert.equal(await page.evaluate(key => localStorage.getItem(key), storageKey), null);
    assert.equal(await page.evaluate(() => localStorage.getItem('wukna.theme.v1')), original.theme);
    assert.equal(await page.evaluate(() => document.documentElement.style.cssText), original.inline);
    assert.equal(await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--primary')), original.primary);
    await scan(env, 'reset-exact-original');
    await env.context.close();
    console.log(JSON.stringify({ width, theme, passed: true }));
  }
  {
    const env = await setup({ width: 1440 });
    await env.context.route('**/src/theme/coordinatedPalettes.ts*', async route => {
      const response = await route.fetch();
      const body = (await response.text()).replaceAll('#4e5752', '#ffffff');
      await route.fulfill({ response, body });
    });
    await visit(env, '/account/preferences');
    const dialog = await studio(env.page);
    // Exercise the visible warning with an intentionally unsafe fixture candidate.
    await dialog.getByRole('button', { name: /^Ocean Slate/ }).click();
    await dialog.getByRole('button', { name: /^Warm Minimal/ }).click();
    await dialog.getByRole('alert').filter({ hasText: 'This candidate needs contrast adjustments' }).waitFor();
    assert.equal(await env.page.locator('html').getAttribute('data-color-palette'), null);
    await dialog.getByRole('checkbox', { name: 'Use this palette across the app', exact: true }).check();
    await dialog.getByRole('radio', { name: 'Dark', exact: true }).check({ force: true });
    await dialog.getByRole('alert').filter({ hasText: 'The active palette needs contrast adjustments in light mode' }).waitFor();
    await close(env.page);
    await env.page.getByRole('alert').filter({ hasText: 'This palette needs contrast adjustments' }).waitFor();
    const reset = await studio(env.page);
    await reset.getByRole('button', { name: 'Reset to Original Theme', exact: true }).click();
    await close(env.page);
    await scan(env, 'unsafe-candidate-warns-in-samples-and-active-app');
    await env.context.close();
  }
  for (const width of [375, 1440]) {
    const env = await setup({ width });
    await env.context.addInitScript(key => localStorage.setItem(key, 'ocean-slate'), storageKey);
    await env.page.goto(`${process.env.WUKNA_PRODUCTION_PREVIEW || 'http://127.0.0.1:4174'}/account/preferences`);
    await env.page.getByRole('heading', { name: 'Appearance', exact: true }).waitFor();
    const dialog = await studio(env.page);
    assert.equal(await env.page.locator('html').getAttribute('data-color-palette'), 'ocean-slate');
    await dialog.getByRole('button', { name: /^Calm Sage/ }).click();
    assert.equal(await env.page.evaluate(() => document.documentElement.style.getPropertyValue('--primary')), curatedPalettes[1].light['--primary']);
    await scan(env, 'production-color-studio-switches');
    await dialog.getByRole('button', { name: 'Reset to Original Theme', exact: true }).click();
    await close(env.page);
    assert.equal(await env.page.locator('html').getAttribute('data-color-palette'), null);
    assert.equal(await env.page.evaluate(() => document.documentElement.style.getPropertyValue('--primary')), '');
    await scan(env, 'production-color-studio-resets');
    await env.context.close();
  }
  await fs.writeFile(`${output}/palette-browser-results.json`, JSON.stringify(results, null, 2));
  console.log(JSON.stringify({ cases: results.length, failed: 0 }));
} catch (error) {
  await fs.writeFile(`${output}/palette-browser-failure.txt`, error.stack);
  throw error;
} finally { await browser.close(); }
