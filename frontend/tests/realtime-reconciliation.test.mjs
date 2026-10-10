import assert from 'node:assert/strict';
import test from 'node:test';
import {
  mergeBoardSummary,
  mergePresenceSnapshot,
  mergeVersionedNote,
  rejoinThenReconcile,
  removeVersionedNote,
  shouldAcceptGeometryPreview,
  shouldClearGeometryPreview,
  shouldEndGeometryPreview,
} from '../src/realtime/reconcile.ts';
import { isDragGesture } from '../src/features/boards/gesture.ts';
import {
  applyEditingStarted,
  applyEditingStopped,
  editingUserIds,
} from '../src/realtime/editing.ts';
import {
  applyCursorMoved,
  applyCursorStopped,
  shouldAcceptCursorMoved,
} from '../src/realtime/cursors.ts';
import { CursorRenderStore } from '../src/realtime/cursorRenderStore.ts';

const note = (version, title = `v${version}`) => ({ id: 'note-1', version, title });

test('note reconciliation ignores duplicate, stale, and pre-tombstone events', () => {
  const current = [note(8)];
  assert.equal(mergeVersionedNote(current, note(8, 'duplicate')), current);
  assert.equal(mergeVersionedNote(current, note(7, 'stale')), current);
  assert.equal(mergeVersionedNote(current, note(9, 'deleted'), 9), current);

  const newer = mergeVersionedNote(current, note(9, 'authoritative'));
  assert.equal(newer[0].title, 'authoritative');
  assert.equal(newer[0].version, 9);
});

test('delete reconciliation is idempotent and cannot remove a newer entity version', () => {
  const current = [note(9)];
  assert.equal(removeVersionedNote(current, 'note-1', 8), current);
  assert.deepEqual(removeVersionedNote(current, 'note-1', 9), []);
  assert.deepEqual(removeVersionedNote([], 'note-1', 9), []);
});

test('board summaries ignore older events and replace equal/newer snapshots', () => {
  const old = { id: 'board-1', updatedAt: '2026-01-02T00:00:00Z', title: 'Current' };
  const stale = { ...old, updatedAt: '2026-01-01T00:00:00Z', title: 'Stale' };
  assert.equal(mergeBoardSummary([old], stale)[0].title, 'Current');
  assert.equal(mergeBoardSummary([old], { ...old, title: 'Equal snapshot' })[0].title,
    'Equal snapshot');
});

test('board summaries preserve newer shared color revisions when timestamps are equal', () => {
  const current = { id: 'board-1', title: 'Current', updatedAt: '2026-01-02T00:00:00Z', cardColor: 'blue', cardColorVersion: 3 };
  const stale = { ...current, cardColor: 'sage', cardColorVersion: 2, noteCount: 5 };
  const merged = mergeBoardSummary([current], stale)[0];
  assert.equal(merged.cardColor, 'blue');
  assert.equal(merged.cardColorVersion, 3);
  assert.equal(merged.noteCount, 5);
  assert.equal(mergeBoardSummary([current], { ...current, cardColor: null, cardColorVersion: 4 })[0].cardColor, null);
});

test('presence replaces state only with a newer authoritative board revision', () => {
  const current = { boardId: 'board-1', revision: 4, viewers: [] };
  const stale = { boardId: 'board-1', revision: 3, viewers: [{ userId: 'user-1', connectionCount: 1 }] };
  const duplicate = { ...current, viewers: [{ userId: 'user-2', connectionCount: 1 }] };
  const newer = { boardId: 'board-1', revision: 5, viewers: [{ userId: 'user-1', connectionCount: 2 }] };
  assert.equal(mergePresenceSnapshot(current, stale), current);
  assert.equal(mergePresenceSnapshot(current, duplicate), current);
  assert.equal(mergePresenceSnapshot(current, newer), newer);
  assert.equal(mergePresenceSnapshot(null, stale), stale);
});

test('cursor rendering notifies only cursor subscribers for changed snapshots', () => {
  const store = new CursorRenderStore();
  let updates = 0;
  const unsubscribe = store.subscribe(() => { updates++; });
  const original = store.getSnapshot();
  store.set(original);
  assert.equal(updates, 0);
  const moved = { connection: { x: 12, y: 18, sequence: 1 } };
  store.set(moved);
  assert.equal(store.getSnapshot(), moved);
  assert.equal(updates, 1);
  store.set(moved);
  assert.equal(updates, 1);
  unsubscribe();
  store.set({});
  assert.equal(updates, 1);
});

test('reconnect rejoins groups before refreshing every authoritative scope', async () => {
  const order = [];
  const healthy = await rejoinThenReconcile(
    async () => { order.push('rejoin'); },
    [
      async () => { order.push('boards'); },
      async () => { order.push('active-board'); },
    ],
  );
  assert.equal(healthy, true);
  assert.equal(order[0], 'rejoin');
  assert.deepEqual(new Set(order.slice(1)), new Set(['boards', 'active-board']));
});

test('failed authoritative refresh keeps realtime unhealthy for retry', async () => {
  const healthy = await rejoinThenReconcile(
    async () => {},
    [async () => { throw new Error('offline'); }],
  );
  assert.equal(healthy, false);
});

test('failed group rejoin still runs authoritative refresh before retrying', async () => {
  let refreshed = false;
  const healthy = await rejoinThenReconcile(
    async () => { throw new Error('join rejected'); },
    [async () => { refreshed = true; }],
  );
  assert.equal(refreshed, true);
  assert.equal(healthy, false);
});

test('ephemeral geometry rejects old versions, sequences, and ended previews', () => {
  const preview = {
    boardId: 'board-1', noteId: 'note-1', userId: 'user-1', connectionId: 'connection-1',
    operation: 0, x: 10, y: 20, width: null, height: null,
    baseVersion: 8, sequence: 4, sentAt: '2026-01-01T00:00:00Z',
  };
  assert.equal(shouldAcceptGeometryPreview(undefined, preview, 9), false);
  assert.equal(shouldAcceptGeometryPreview(undefined, preview, 8, 4), false);
  assert.equal(shouldAcceptGeometryPreview(undefined, preview, 8, 3), true);
  assert.equal(shouldAcceptGeometryPreview(preview, { ...preview, sequence: 3 }, 8), false);
  assert.equal(shouldAcceptGeometryPreview(preview, { ...preview, sequence: 5 }, 8), true);
});

test('preview end cleanup only clears the matching sender at an equal or newer sequence', () => {
  const preview = {
    boardId: 'board-1', noteId: 'note-1', userId: 'user-1', connectionId: 'connection-1',
    operation: 1, x: null, y: null, width: 300, height: 200,
    baseVersion: 8, sequence: 7, sentAt: '2026-01-01T00:00:00Z',
  };
  assert.equal(shouldEndGeometryPreview(preview,
    { boardId: 'board-1', noteId: 'note-1', userId: 'user-1', connectionId: 'other', sequence: 8 }), false);
  assert.equal(shouldEndGeometryPreview(preview,
    { boardId: 'board-1', noteId: 'note-1', userId: 'user-1', connectionId: 'connection-1', sequence: 6 }), false);
  assert.equal(shouldEndGeometryPreview(preview,
    { boardId: 'board-1', noteId: 'note-1', userId: 'user-1', connectionId: 'connection-1', sequence: 7 }), true);
});

test('only a strictly newer durable note clears an active geometry preview', () => {
  assert.equal(shouldClearGeometryPreview(undefined, 8), true);
  assert.equal(shouldClearGeometryPreview(8, 8), false);
  assert.equal(shouldClearGeometryPreview(8, 7), false);
  assert.equal(shouldClearGeometryPreview(8, 9), true);
});

test('click jitter stays a click while actual note movement is classified as a drag', () => {
  assert.equal(isDragGesture(100, 100, 102, 102), false);
  assert.equal(isDragGesture(100, 100, 106, 100), true);
});

test('editing leases reject stale events, expire by connection, and aggregate by user', () => {
  const first = {
    boardId: 'board-1', noteId: 'note-1', userId: 'user-1', connectionId: 'tab-1',
    sequence: 2, expiresAt: '2026-01-01T00:00:07Z',
  };
  let state = applyEditingStarted({}, first);
  assert.equal(applyEditingStarted(state, { ...first, sequence: 1 }), state);
  state = applyEditingStarted(state, { ...first, connectionId: 'tab-2', sequence: 3 });
  state = applyEditingStarted(state, {
    ...first, userId: 'user-2', connectionId: 'tab-3', sequence: 4,
  });
  assert.deepEqual(editingUserIds(state['note-1'], 'local-user').sort(), ['user-1', 'user-2']);

  const staleStop = applyEditingStopped(state, {
    boardId: 'board-1', noteId: 'note-1', userId: 'user-1', connectionId: 'tab-1', sequence: 1,
  });
  assert.equal(staleStop, state);
  state = applyEditingStopped(state, {
    boardId: 'board-1', noteId: 'note-1', userId: 'user-1', connectionId: 'tab-1', sequence: 2,
  });
  assert.deepEqual(editingUserIds(state['note-1'], 'user-2'), ['user-1']);
});

test('cursor leases remain connection-scoped and reject stale move or stop events', () => {
  const first = {
    boardId: 'board-1', userId: 'user-1', connectionId: 'device-1',
    x: 120, y: 80, sequence: 2, expiresAt: '2026-01-01T00:00:01.500Z',
  };
  let state = applyCursorMoved({}, first);
  assert.equal(shouldAcceptCursorMoved(undefined, first, 2), false);
  assert.equal(shouldAcceptCursorMoved(undefined, { ...first, sequence: 3 }, 2), true);
  assert.equal(applyCursorMoved(state, { ...first, x: 10, sequence: 1 }), state);
  state = applyCursorMoved(state, {
    ...first, connectionId: 'device-2', x: 220, sequence: 1,
  });
  assert.equal(Object.keys(state).length, 2);

  assert.equal(applyCursorStopped(state, {
    boardId: 'board-1', userId: 'user-1', connectionId: 'device-1', sequence: 1,
  }), state);
  state = applyCursorStopped(state, {
    boardId: 'board-1', userId: 'user-1', connectionId: 'device-1', sequence: 2,
  });
  assert.deepEqual(Object.keys(state), ['device-2']);
});
