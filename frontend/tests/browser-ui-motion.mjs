import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { browser, setup, visit, inspect, output } from './browser-ui-audit.mjs';

const results = [];
try {
  for (const theme of ['light', 'dark']) for (const width of [768, 1440]) for (const reducedMotion of ['reduce', 'no-preference']) {
    const env = await setup({ theme, width, reducedMotion });
    await visit(env, '/home');
    const sample = await env.page.evaluate(() => new Promise(resolve => {
      const shell = document.querySelector('.wk-app-shell');
      const sidebar = document.querySelector('.wk-sidebar');
      const main = document.querySelector('.wk-shell-main');
      const toggle = document.querySelector('.wk-sidebar-header button[aria-expanded]');
      const initialMainWidth = main.getBoundingClientRect().width;
      const duration = getComputedStyle(innerWidth <= 1100 ? sidebar : shell).transitionDuration;
      const frames = [], start = performance.now();
      toggle.click();
      function frame(time) {
        frames.push({ elapsed: time - start, sidebar: sidebar.getBoundingClientRect().width, main: main.getBoundingClientRect().width, scrollWidth: document.documentElement.scrollWidth });
        if (time - start < 400) requestAnimationFrame(frame);
        else resolve({ initialMainWidth, duration, frames });
      }
      requestAnimationFrame(frame);
    }));
    const scan = await inspect(env.page);
    assert.deepEqual(scan.contrasts, []);
    assert.ok(sample.frames.every(f => f.scrollWidth <= width), 'Animation does not overflow');
    if (reducedMotion === 'reduce') assert.equal(sample.duration, '0s');
    else assert.ok(new Set(sample.frames.map(f => Math.round(f.sidebar))).size > 2, 'Sidebar animates through intermediate widths');
    if (width === 768) assert.ok(sample.frames.every(f => Math.abs(f.main - sample.initialMainWidth) < 1), 'Tablet drawer preserves content width');
    for (const icon of scan.icons) assert.ok(Math.abs(icon.offset) <= 1, `${icon.label} alignment`);
    await env.page.keyboard.press('Control+k');
    assert.equal(await env.page.getByRole('textbox', { name: 'Search boards', exact: true }).evaluate(e => e === document.activeElement), true);
    const gaps = sample.frames.slice(1).map((f, i) => f.elapsed - sample.frames[i].elapsed);
    results.push({ theme, width, reducedMotion, duration: sample.duration, frames: sample.frames.length, maxFrameGapMs: +Math.max(...gaps).toFixed(2), passed: true });
    await env.context.close();
  }
  await fs.writeFile(`${output}/motion.json`, JSON.stringify(results, null, 2));
  console.log(JSON.stringify(results));
} finally { await browser.close(); }
