import assert from 'node:assert/strict';
import test from 'node:test';
import {
  clientPointToBoard,
  minimalRevealDelta,
} from '../src/features/boards/boardViewport.ts';

const viewport = { left: 100, top: 50, right: 900, bottom: 650 };

test('visible nodes do not move the board viewport', () => {
  assert.deepEqual(
    minimalRevealDelta(viewport, { left: 220, top: 140, right: 420, bottom: 300 }),
    { left: 0, top: 0 },
  );
});

test('reveal movement is minimal when the dock clips a node at the right edge', () => {
  assert.deepEqual(
    minimalRevealDelta(viewport, { left: 780, top: 140, right: 940, bottom: 300 }),
    { left: 64, top: 0 },
  );
});

test('reveal can correct left and vertical clipping without recentering', () => {
  assert.deepEqual(
    minimalRevealDelta(viewport, { left: 105, top: 35, right: 265, bottom: 145 }),
    { left: -19, top: -39 },
  );
});

test('pointer coordinates remain board-relative with independent camera translation', () => {
  assert.deepEqual(
    clientPointToBoard(viewport, { x: -260, y: -120, zoom: 1 }, { x: 350, y: 250 }),
    { x: 510, y: 320 },
  );
});
