import assert from 'node:assert/strict';
import test from 'node:test';
import { curatedPalettes, checkPaletteContrast, paletteContrast } from '../src/theme/curatedPalettes.ts';
import { notePigments, noteForeground } from '../src/features/boards/noteAppearance.ts';

for (const palette of curatedPalettes) for (const mode of ['light', 'dark']) {
  test(`${palette.name} ${mode} retains readable semantic text and control boundaries`, () => {
    const failures = checkPaletteContrast(palette[mode]).filter(check => !check.passes);
    assert.deepEqual(failures, [], failures.map(check => `${check.label}: ${check.ratio.toFixed(2)}`).join(', '));
    for (const name of Object.keys(palette[mode])) assert.doesNotMatch(name, /note|pigment|collaborator|space|radius|font|size|height|width|motion|layer/);
    for (const note of notePigments) assert.ok(paletteContrast(noteForeground(note.value), note.value) >= 4.5);
  });
}
test('contrast feedback reports an unreadable candidate', () => {
  const unsafe = { ...curatedPalettes[0].light, '--muted-foreground': '#FFFFFF' };
  assert.ok(checkPaletteContrast(unsafe).some(check => !check.passes && check.label.includes('muted-foreground')));
});
