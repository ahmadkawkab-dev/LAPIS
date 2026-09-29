import assert from 'node:assert/strict';
import test from 'node:test';
import { boardBounds, boardBoundaryGutter, clampBoardCamera, clampBoardPosition, constrainBoardGeometry } from '../src/features/boards/boardBounds.ts';
import { createEdgeAutoPan, panCamera, beginWorldDrag, worldDragPosition } from '../src/features/boards/boardNavigation.ts';
import { screenToWorld, worldToScreen, zoomAtPoint } from '../src/features/boards/boardZoom.ts';
import { noteDimensionBounds, clampDimension } from '../src/features/boards/noteDimensions.ts';

const size = { width: 1000, height: 800 };
const zooms = [.25, .5, 1, 1.5, 1.75];
const close = (a, b) => assert.ok(Math.abs(a - b) < 1e-8, `${a} != ${b}`);

test('board is generously sized and finite, retaining the existing negative-coordinate origin', () => {
  assert.equal(boardBounds.right - boardBounds.left, 12000);
  assert.equal(boardBounds.bottom - boardBounds.top, 8000);
  assert.equal(boardBounds.left + boardBounds.right, 0);
  assert.equal(boardBounds.top + boardBounds.bottom, 0);
});

test('notes and tasks stop with their full rectangles inside all four edges at every zoom', () => {
  for (const kind of [0, 1]) for (const zoom of zooms) {
    const note = Object.freeze({ kind, positionX: -500, positionY: -1200, width: 300, height: 220 });
    const camera = { x: 125, y: 90, zoom };
    const anchor = beginWorldDrag({ x: note.positionX, y: note.positionY }, { x: -480, y: -1184 });
    for (const x of [-1e6, 1e6]) for (const y of [-1e6, 1e6]) {
      const point = clampBoardPosition(worldDragPosition(anchor, screenToWorld({ x, y }, camera)), note);
      assert.equal(point.x, x < 0 ? boardBounds.left : boardBounds.right - note.width);
      assert.equal(point.y, y < 0 ? boardBounds.top : boardBounds.bottom - note.height);
    }
    assert.deepEqual(clampBoardPosition({ x: -800, y: -1500 }, note), { x: -800, y: -1500 });
    assert.equal(note.positionX, -500);
  }
});

test('creation, legacy geometry, and oversized cards are contained without mutating input', () => {
  const legacy = Object.freeze({ positionX: 20000, positionY: -12000, width: 280, height: 220 });
  assert.deepEqual(constrainBoardGeometry(legacy), { positionX: 5720, positionY: -4000, width: 280, height: 220 });
  assert.deepEqual(constrainBoardGeometry({ ...legacy, width: 20000, height: 12000 }),
    { positionX: -6000, positionY: -4000, width: 12000, height: 8000 });
  assert.equal(legacy.positionX, 20000);
});

test('drag and inspector resize limits stop at the right/bottom edge while keeping content floors', () => {
  for (const kind of [0, 1]) {
    const note = { kind, title: 'Plan', content: '', positionX: 5700, positionY: 3780, width: 300, height: 220 };
    const bounds = noteDimensionBounds(note, [], 10000);
    assert.equal(bounds.maxWidth, 300);
    assert.equal(bounds.maxHeight, 220);
    const resized = constrainBoardGeometry({ ...note, width: clampDimension(10000, bounds.minWidth, bounds.maxWidth),
      height: clampDimension(10000, bounds.minHeight, bounds.maxHeight) });
    assert.equal(resized.positionX + resized.width, boardBounds.right);
    assert.equal(resized.positionY + resized.height, boardBounds.bottom);
    const grown = constrainBoardGeometry({ ...note, height: noteDimensionBounds(note, Array(100).fill({ title: 'Task' })).minHeight });
    assert.ok(grown.positionY + grown.height <= boardBounds.bottom);
  }
});

test('all pan paths and zoom changes clamp the screen camera with a small visible boundary gutter', () => {
  for (const zoom of zooms) for (const x of [-1e9, 1e9]) for (const y of [-1e9, 1e9]) {
    const camera = clampBoardCamera(panCamera({ x: 0, y: 0, zoom }, { x, y }), size);
    const edge = worldToScreen({ x: x > 0 ? boardBounds.left : boardBounds.right,
      y: y > 0 ? boardBounds.top : boardBounds.bottom }, camera);
    close(edge.x, x > 0 ? boardBoundaryGutter : size.width - boardBoundaryGutter);
    close(edge.y, y > 0 ? boardBoundaryGutter : size.height - boardBoundaryGutter);
    assert.deepEqual(clampBoardCamera(camera, size), camera);
    for (const nextZoom of zooms) {
      const next = clampBoardCamera(zoomAtPoint(camera, nextZoom, { x: 400, y: 300 }), size);
      assert.deepEqual(clampBoardCamera(next, size), next);
    }
  }
});

test('a board smaller than the viewport centers each short axis instead of panning into empty space', () => {
  const small = { left: -100, top: -50, right: 100, bottom: 50 };
  assert.deepEqual(clampBoardCamera({ x: -1e6, y: 1e6, zoom: 1 }, size, small), { x: 500, y: 400, zoom: 1 });
  const narrow = { ...small, top: -4000, bottom: 4000 };
  const camera = clampBoardCamera({ x: 10000, y: -10000, zoom: 1 }, size, narrow);
  assert.equal(camera.x, 500);
  assert.equal(camera.y, size.height - boardBoundaryGutter - narrow.bottom);
});

function autoPanRig(camera) {
  let time = 0, id = 0, previews = 0;
  const frames = new Map();
  const auto = createEdgeAutoPan({
    getViewport: () => ({ left: 0, top: 0, right: size.width, bottom: size.height }),
    constrainVelocity: (speed) => {
      const next = clampBoardCamera(panCamera(camera, speed), size);
      return { x: next.x === camera.x ? 0 : speed.x, y: next.y === camera.y ? 0 : speed.y };
    },
    panBy: (delta) => {
      const before = camera;
      camera = clampBoardCamera(panCamera(camera, delta), size);
      return { x: camera.x - before.x, y: camera.y - before.y };
    },
    scheduler: { request: (cb) => { frames.set(++id, cb); return id; }, cancel: (id) => frames.delete(id), now: () => time },
  });
  return { auto, frames, get camera() { return camera; }, get previews() { return previews; },
    start: (point) => auto.start(point, () => previews++),
    step: () => { time += 16; const callbacks = [...frames.values()]; frames.clear(); callbacks.forEach((cb) => cb(time)); },
  };
}

test('stationary edge auto-pan reaches every board edge, stops its loop, and reverses immediately', () => {
  for (const zoom of zooms) for (const x of [-1e9, 1e9]) for (const y of [-1e9, 1e9]) {
    const limit = clampBoardCamera({ x, y, zoom }, size);
    const r = autoPanRig({ ...limit, x: limit.x + (x > 0 ? -10 : 10), y: limit.y + (y > 0 ? -10 : 10) });
    const point = { x: x > 0 ? 0 : size.width, y: y > 0 ? 0 : size.height };
    r.start(point);
    for (let i = 0; i < 20; i++) r.step();
    assert.deepEqual(r.camera, limit);
    assert.equal(r.frames.size, 0);
    assert.ok(r.previews > 0);
    r.auto.update({ x: size.width - point.x, y: size.height - point.y });
    r.step();
    assert.notDeepEqual(r.camera, limit);
    r.auto.stop();
  }
});

test('edge auto-pan continues on the unblocked axis and never banks blocked-axis movement', () => {
  const corner = clampBoardCamera({ x: 1e9, y: 1e9, zoom: 1 }, size);
  const r = autoPanRig({ ...corner, y: corner.y - 100 });
  r.start({ x: 0, y: 0 });
  r.step();
  assert.equal(r.camera.x, corner.x);
  assert.ok(r.camera.y > corner.y - 100);
  for (let i = 0; i < 200; i++) r.step();
  assert.deepEqual(r.camera, corner);
  assert.equal(r.frames.size, 0);
  r.auto.update({ x: size.width, y: size.height / 2 });
  r.step();
  assert.ok(r.camera.x < corner.x);
  assert.equal(r.camera.y, corner.y);
  r.auto.stop();
});
