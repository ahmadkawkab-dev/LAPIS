import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/styles/tokens.css', import.meta.url), 'utf8');
const luminance = (hex) => {
  const channels = hex.slice(1).match(/../g).map((part) => parseInt(part, 16) / 255)
    .map((channel) => channel <= .04045 ? channel / 12.92 : ((channel + .055) / 1.055) ** 2.4);
  return channels[0] * .2126 + channels[1] * .7152 + channels[2] * .0722;
};
const ratio = (a, b) => {
  const values = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (values[0] + .05) / (values[1] + .05);
};

for (const [theme, block] of [['light', source.split(":root[data-theme='dark']")[0]],
  ['dark', source.split(":root[data-theme='dark']")[1].split('}')[0]]]) {
  const tokens = Object.fromEntries([...block.matchAll(/--([\w-]+):\s*(#[\da-f]{6});/gi)].map((match) => [match[1], match[2]]));
  test(`${theme} semantic text and interactive boundaries retain contrast`, () => {
    for (const background of ['canvas', 'surface', 'secondary', 'selected']) {
      for (const foreground of ['foreground', 'muted-foreground']) {
        assert.ok(ratio(tokens[foreground], tokens[background]) >= 4.5, `${foreground} on ${background}`);
      }
    }
    assert.ok(ratio(tokens['primary-foreground'], tokens.primary) >= 4.5, 'primary button text');
    for (const [foreground, background] of [['destructive', 'status-danger-surface'], ['warning', 'status-warning-surface'],
      ['success', 'status-success-surface'], ['status-info', 'status-info-surface']]) {
      assert.ok(ratio(tokens[foreground], tokens[background]) >= 4.5, `${foreground} status text`);
    }
    for (const background of ['surface', 'canvas']) {
      assert.ok(ratio(tokens['control-border'], tokens[background]) >= 3, `control border on ${background}`);
      assert.ok(ratio(tokens.focus, tokens[background]) >= 3, `focus on ${background}`);
    }
  });
}
