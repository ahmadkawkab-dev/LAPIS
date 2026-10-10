import assert from 'node:assert/strict';
import test from 'node:test';
import { assignBoardVariations, boardVariationCount } from '../src/theme/boardVariations.ts';
import { curatedPalettes, checkPaletteAndBoardContrast } from '../src/theme/curatedPalettes.ts';

const id = index => `00000000-0000-0000-0000-${index.toString(16).padStart(12, '0')}`;
const boards = Array.from({ length: 18 }, (_, index) => ({ id: id(index) }));
test('automatic variants fill unused slots deterministically despite ID hash collisions', () => {
  const colliding = [0, 6, 12, 18, 24, 30].map(index => ({ id: id(index) }));
  const assigned = assignBoardVariations(colliding);
  assert.equal(new Set(Object.values(assigned)).size, boardVariationCount);
  assert.deepEqual(assignBoardVariations(colliding.toReversed()), assigned);
});
test('relationships survive rerenders, refreshed persistence, palette switches, and new boards', () => {
  let assignments = assignBoardVariations(boards.slice(0, 4));
  const initial = structuredClone(assignments);
  assignments = assignBoardVariations(boards.slice(0, 5).toReversed(), JSON.parse(JSON.stringify(assignments)));
  for (const board of boards.slice(0, 4)) assert.equal(assignments[board.id], initial[board.id]);
  assert.equal(new Set(Object.values(assignments)).size, 5);
  for (const palette of curatedPalettes) for (const mode of ['light', 'dark']) {
    assert.deepEqual(assignBoardVariations(boards.slice(0, 5), assignments), assignments);
    for (const variant of Object.values(assignments)) assert.ok(palette.variants[mode][variant]);
  }
});
test('larger collections reuse least-used variants evenly and keep existing relationships', () => {
  const assignments = assignBoardVariations(boards);
  const counts = Array.from({ length: boardVariationCount }, (_, slot) => Object.values(assignments).filter(value => value === slot).length);
  assert.deepEqual(counts, [3, 3, 3, 3, 3, 3]);
  assert.deepEqual(assignBoardVariations(boards.toReversed(), assignments), assignments);
});
test('explicit shared choices keep priority and reserve slots for automatic boards', () => {
  const chosen = [{ id: id(0), cardColor: 'gold' }, ...boards.slice(1, 6)];
  const assignments = assignBoardVariations(chosen, { [id(0)]: 1 });
  assert.equal(assignments[id(0)], 4);
  assert.equal(new Set(Object.values(assignments)).size, 6);
  const sameChoice = assignBoardVariations([{ id: id(0), cardColor: 'blue' }, { id: id(1), cardColor: 'blue' }]);
  assert.equal(sameChoice[id(0)], 1); assert.equal(sameChoice[id(1)], 1);
});
test('invalid IDs and stored variants cannot become CSS selectors or out-of-range slots', () => {
  const assigned = assignBoardVariations([{ id: 'bad"]{color:red}' }, boards[0], boards[0], boards[1]], { [id(0)]: 99, [id(1)]: -1 });
  assert.deepEqual(Object.keys(assigned).sort(), [id(0), id(1)]);
  assert.ok(Object.values(assigned).every(value => value >= 0 && value < 6));
});
for (const palette of curatedPalettes) for (const mode of ['light', 'dark']) {
  test(`${palette.name} ${mode}: three families, six distinct board identities, AA color roles`, () => {
    assert.equal(new Set(palette.families.map(family => family.name)).size, 3);
    assert.equal(new Set(palette.families.map(family => family[mode])).size, 3);
    assert.equal(new Set(palette.variants[mode].map(variant => variant.card)).size, 6);
    assert.equal(new Set(palette.variants[mode].map(variant => variant.canvas)).size, 6);
    const failures = checkPaletteAndBoardContrast(palette, mode).filter(check => !check.passes);
    assert.deepEqual(failures, [], failures.map(check => `${check.label} ${check.ratio.toFixed(2)}`).join(', '));
    assert.match(palette[mode]['--success'], /^#/);
    assert.notEqual(palette[mode]['--success'], palette[mode]['--primary'], 'success remains a functional green');
  });
}
