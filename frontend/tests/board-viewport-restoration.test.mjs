import assert from 'node:assert/strict';
import test from 'node:test';
import { createBoardViewportState, readBoardViewport, viewportStorageKey, viewportSaveDelay } from '../src/features/boards/boardViewportState.ts';
import { clampBoardCamera } from '../src/features/boards/boardBounds.ts';
import { fitBoardContent } from '../src/features/boards/boardZoom.ts';

const size = { width: 1000, height: 800 };
const content = { left: -500, top: -1200, right: 300, bottom: -500 };
function preferences() {
  const entries = new Map(), writes = [];
  return { entries, writes, getItem: (key) => entries.get(key) ?? null,
    setItem: (key, value) => { entries.set(key, value); writes.push({ key, value }); } };
}
function state(storage, boardId = 'board-a', userId = 'user-a', extra = {}) {
  let id = 0;
  const timers = new Map();
  const viewport = createBoardViewportState({ storage, boardId, userId, getSize: () => size, getContentBounds: () => content,
    schedule: (cb) => { timers.set(++id, cb); return id; }, cancel: (id) => timers.delete(id), ...extra });
  return { ...viewport, timers, settle: () => { const callbacks = [...timers.values()]; timers.clear(); callbacks.forEach((cb) => cb()); } };
}

test('first visit uses fit-to-content and does not write a preference during initialization', () => {
  const storage = preferences(), view = state(storage);
  view.initialize();
  assert.deepEqual(view.getCamera(), clampBoardCamera(fitBoardContent(content, size), size));
  view.flush();
  assert.equal(storage.writes.length, 0);
  const empty = state(storage, 'empty', 'user-a', { getContentBounds: () => null });
  empty.initialize();
  assert.deepEqual(empty.getCamera(), { x: 0, y: 0, zoom: 1 });
});

test('camera preferences debounce a burst of navigation and store only the latest x/y/zoom', () => {
  assert.equal(viewportSaveDelay, 300);
  const storage = preferences(), view = state(storage);
  view.initialize();
  for (let i = 0; i < 200; i++) view.set({ x: i, y: -i, zoom: .8 });
  assert.equal(storage.writes.length, 0);
  assert.equal(view.timers.size, 1);
  view.settle();
  assert.equal(storage.writes.length, 1);
  assert.deepEqual(JSON.parse(storage.writes[0].value), { x: 199, y: -199, zoom: .8 });
  view.set(view.getCamera());
  view.revalidate();
  assert.equal(view.timers.size, 0);
});

test('refresh/unmount flushes a pending save and a fresh viewport restores that exact location and zoom', () => {
  const storage = preferences(), before = state(storage);
  before.initialize();
  before.set({ x: -3200.25, y: 1750.5, zoom: .67 });
  before.flush(); // Same lifecycle used by pagehide, hidden visibility, and hook cleanup.
  assert.equal(before.timers.size, 0);
  const refreshed = state(storage, 'board-a', 'user-a', { getContentBounds: () => ({ left: 0, top: 0, right: 100, bottom: 100 }) });
  refreshed.initialize();
  assert.deepEqual(refreshed.getCamera(), before.getCamera());
  refreshed.set({ x: -1500, y: 1800, zoom: 1.25 });
  refreshed.initialize(); // A reconnect/ready transition must never re-fit or restore the old preference.
  assert.deepEqual(refreshed.getCamera(), { x: -1500, y: 1800, zoom: 1.25 });
  refreshed.flush();
});

test('two boards and two users keep independent viewports when reopened', () => {
  const storage = preferences();
  const cases = [['board-a', 'user-a', { x: 1100, y: -700, zoom: .5 }],
    ['board-b', 'user-a', { x: -2700, y: 950, zoom: 1.5 }],
    ['board-a', 'user-b', { x: 450, y: 100, zoom: 1 }]];
  for (const [board, user, camera] of cases) { const view = state(storage, board, user); view.initialize(); view.set(camera); view.flush(); }
  assert.equal(storage.entries.size, 3);
  for (const [board, user, camera] of cases) { const view = state(storage, board, user); view.initialize(); assert.deepEqual(view.getCamera(), camera); }
  assert.notEqual(viewportStorageKey('a:b', 'c'), viewportStorageKey('a', 'b:c'));
});

test('restoration clamps saved translation and zoom after board dimensions or viewport size change', () => {
  const storage = preferences(), key = viewportStorageKey('user-a', 'board-a');
  const smallBounds = { left: -1000, top: -500, right: 1000, bottom: 500 };
  for (const camera of [{ x: -99999, y: 99999, zoom: 100 }, { x: 99999, y: -99999, zoom: .01 }]) {
    storage.setItem(key, JSON.stringify(camera));
    const view = state(storage, 'board-a', 'user-a', { bounds: smallBounds });
    view.initialize();
    assert.deepEqual(view.getCamera(), clampBoardCamera(camera, size, smallBounds));
  }
  let currentSize = size;
  const view = state(storage, 'board-a', 'user-a', { getSize: () => currentSize });
  view.initialize();
  view.set({ x: -99999, y: -99999, zoom: 1 });
  currentSize = { width: 2000, height: 1400 };
  view.revalidate();
  assert.deepEqual(view.getCamera(), clampBoardCamera({ x: -99999, y: -99999, zoom: 1 }, currentSize));
  view.flush();
});

test('corrupt, nonfinite, nonnumeric, and unsupported preferences safely use the initial view', () => {
  const storage = preferences(), key = viewportStorageKey('user-a', 'board-a');
  for (const invalid of ['{', 'null', '{}', '[]', '{"x":1,"y":2}', '{"x":"1","y":2,"zoom":1}',
    '{"x":1e999,"y":2,"zoom":1}', '{"x":1,"y":2,"zoom":0}', '{"x":1,"y":2,"zoom":-1}']) {
    storage.setItem(key, invalid);
    assert.equal(readBoardViewport(storage, key), null);
    const view = state(storage); view.initialize();
    assert.deepEqual(view.getCamera(), clampBoardCamera(fitBoardContent(content, size), size));
  }
});

test('blocked storage leaves navigation and cleanup usable', () => {
  const storage = { getItem: () => { throw Error('blocked'); }, setItem: () => { throw Error('full'); } };
  for (const preferenceStorage of [storage, null]) {
    const view = state(preferenceStorage); view.initialize();
    view.set({ x: 1200, y: -800, zoom: 1 });
    assert.doesNotThrow(() => view.flush());
    assert.deepEqual(view.getCamera(), { x: 1200, y: -800, zoom: 1 });
    assert.equal(view.timers.size, 0);
  }
});
