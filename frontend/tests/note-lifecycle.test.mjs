import assert from 'node:assert/strict';
import test from 'node:test';
import { EditorStateController } from '../src/features/boards/editor/editorStateController.ts';
import { createEditorDraftStore } from '../src/features/boards/editor/draftStore.ts';

class MemoryStorage {
  values = new Map();
  get length() { return this.values.size; }
  getItem(key) { return this.values.get(key) ?? null; }
  setItem(key, value) { this.values.set(key, value); }
  removeItem(key) { this.values.delete(key); }
  key(index) { return [...this.values.keys()][index] ?? null; }
}
const fresh = (kind = 0) => ({
  id: 'local:draft-1', boardId: 'board-1', kind, parentNoteId: null,
  title: '', content: '', positionX: 100, positionY: 120,
  width: 280, height: 220, color: '#EEE8DB', zIndex: 1,
  version: 0, isCompleted: false, createdAt: '2026-09-28T00:00:00Z',
});
const setup = (storage = new MemoryStorage()) => new EditorStateController('user-1', 'board-1', createEditorDraftStore(storage));
const server = (note) => ({ ...note, id: 'server-1', version: 10 });

test('new card is editable and selected before any persistence', async () => {
  const editor = setup();
  const note = fresh();
  editor.createNote(note);
  assert.equal(editor.getNavigation().selectedNoteId, note.id);
  assert.equal(editor.getNavigation().editingNoteId, note.id);
  assert.equal(editor.getCreations()[note.id].status, 'draft');
  editor.updateDraft(note, { title: '  Plan  ' });
  let posts = 0;
  const saved = await editor.commitCreation(note.id, async (value) => { posts++; assert.equal(value.title, 'Plan'); return server(value); });
  assert.equal(posts, 1);
  assert.equal(saved.title, 'Plan');
  assert.deepEqual(editor.getCreations(), {});
  assert.equal(editor.getNavigation().selectedNoteId, saved.id);
});

test('cancel/exit an empty creation removes draft, focus state and recovery storage without POST', async () => {
  const storage = new MemoryStorage();
  const editor = setup(storage);
  const note = fresh();
  editor.createNote(note);
  editor.updateDraft(note, { title: '   ', content: '\n ' });
  let posts = 0;
  assert.equal(await editor.commitCreation(note.id, async (value) => { posts++; return server(value); }), null);
  assert.equal(posts, 0);
  assert.deepEqual(editor.getCreations(), {});
  assert.equal(editor.getNavigation().selectedNoteId, null);
  assert.equal(editor.getNavigation().editingNoteId, null);
  assert.equal(storage.length, 0);
});

test('blank title with meaningful body becomes a persisted, editable Untitled note', async () => {
  const editor = setup();
  const note = fresh();
  editor.createNote(note);
  editor.updateDraft(note, { content: 'Keep this idea' });
  const saved = await editor.commitCreation(note.id, async (value) => server(value));
  assert.equal(saved.title, 'Untitled');
  assert.equal(saved.content, 'Keep this idea');
  editor.startEditing(saved.id);
  editor.updateDraft(saved, { title: 'Named later' });
  assert.equal(editor.getDraft(saved.id).title, 'Named later');
  editor.reconcileSaved({ ...saved, title: 'Named later', version: 11 });
  assert.equal(editor.getDraft(saved.id), null);
  editor.startEditing(saved.id);
  assert.equal(editor.getNavigation().editingNoteId, saved.id);
});

test('simultaneous blur/Enter finishes share exactly one pending POST', async () => {
  const editor = setup();
  const note = fresh();
  editor.createNote(note);
  editor.updateDraft(note, { title: 'One note' });
  let resolve;
  let posts = 0;
  const save = (value) => { posts++; return new Promise((done) => { resolve = () => done(server(value)); }); };
  const first = editor.commitCreation(note.id, save);
  const second = editor.commitCreation(note.id, save);
  assert.equal(first, second);
  await Promise.resolve();
  assert.equal(posts, 1);
  assert.equal(editor.getCreations()[note.id].status, 'saving');
  resolve();
  await Promise.all([first, second]);
  assert.equal(await editor.commitCreation(note.id, save), null);
  assert.equal(posts, 1);
});

test('failed creation keeps content and geometry, survives reload, and can retry', async () => {
  const storage = new MemoryStorage();
  const editor = setup(storage);
  const note = fresh();
  editor.createNote(note);
  editor.updateDraft(note, { content: 'Recover me' });
  editor.updateCreation(note.id, { positionX: 350, width: 320 });
  await assert.rejects(editor.commitCreation(note.id, async () => { throw new Error('offline'); }), /offline/);
  assert.equal(editor.getCreations()[note.id].status, 'failed');
  assert.equal(editor.getCreations()[note.id].note.content, 'Recover me');
  const recovered = setup(storage);
  assert.equal(recovered.getCreations()[note.id].status, 'draft');
  assert.equal(recovered.getCreations()[note.id].note.positionX, 350);
  recovered.hydrate([]);
  assert.ok(recovered.getCreations()[note.id]);
  const saved = await recovered.commitCreation(note.id, async (value) => server(value));
  assert.equal(saved.width, 320);
  assert.equal(saved.content, 'Recover me');
});

test('task title and item title drafts are independent; completion and resize geometry stay intact', async () => {
  const editor = setup();
  const task = { ...fresh(1), id: 'task', title: 'Launch', version: 4 };
  const item = { ...fresh(2), id: 'item', title: 'Write copy', parentNoteId: 'task', version: 5, isCompleted: true };
  editor.updateDraft(task, { title: 'Launch day' });
  editor.updateDraft(item, { title: 'Review copy' });
  assert.equal(editor.getDraft(task.id).title, 'Launch day');
  assert.equal(editor.getDraft(item.id).title, 'Review copy');
  editor.reconcileSaved({ ...item, title: 'Review copy', version: 6 });
  assert.equal(editor.getDraft(item.id), null);
  assert.equal(editor.getDraft(task.id).title, 'Launch day');
  assert.equal(item.isCompleted, true);
  assert.equal(task.width, 280);
});

test('adding the first task item can persist a blank parent with a valid fallback title', async () => {
  const editor = setup();
  const note = fresh(1);
  editor.createNote(note);
  const saved = await editor.commitCreation(note.id, async (value) => server(value), true);
  assert.equal(saved.title, 'Untitled');
  assert.equal(saved.kind, 1);
});


test('a pending creation is immutable and failure releases its edit lock', async () => {
  const editor = setup();
  const note = fresh();
  editor.createNote(note);
  editor.updateCreation(note.id, { content: 'Saved snapshot' });
  let reject;
  const pending = editor.commitCreation(note.id, () => new Promise((_, fail) => { reject = fail; }));
  await Promise.resolve();
  editor.updateCreation(note.id, { content: 'Changed during POST' });
  assert.equal(editor.getCreations()[note.id].note.content, 'Saved snapshot');
  reject(new Error('network failure'));
  await assert.rejects(pending, /network failure/);
  editor.updateCreation(note.id, { title: 'Retry title' });
  assert.equal(editor.getCreations()[note.id].status, 'draft');
  assert.equal(editor.getCreations()[note.id].note.title, 'Retry title');
});


test('reentrant store subscribers cannot start a duplicate creation request', async () => {
  const editor = setup();
  const note = fresh();
  editor.createNote(note);
  editor.updateCreation(note.id, { title: 'Exactly once' });
  let posts = 0, reentrant;
  const save = async (value) => { posts++; return server(value); };
  editor.subscribe(() => {
    if (editor.getCreations()[note.id]?.status === 'saving') reentrant = editor.commitCreation(note.id, save);
  });
  const request = editor.commitCreation(note.id, save);
  assert.equal(reentrant, request);
  await request;
  assert.equal(posts, 1);
});
