import assert from 'node:assert/strict';
import test from 'node:test';
import { build } from 'esbuild';

const stubs = {
  'react': `export const memo = fn => fn;
    export const useRef = initial => { const f = globalThis.chatAttentionFixture; return { current: f.refIndex++ === 0 ? f.list : initial }; };
    export const useEffect = fn => globalThis.chatAttentionFixture.effects.push(fn);
    export const useLayoutEffect = fn => globalThis.chatAttentionFixture.layout.push(fn);
    export const useState = initial => [typeof initial === 'function' ? initial() : initial, () => {}];
    export const useId = () => 'id';`,
  'react/jsx-runtime': `export const jsx = () => null; export const jsxs = jsx; export const Fragment = Symbol('fragment');`,
  'lucide-react': `export const Bell = 'icon', ArrowDown = 'icon', CalendarClock = 'icon', ImagePlus = 'icon', RefreshCw = 'icon', Send = 'icon', Settings = 'icon', X = 'icon';`,
  '../../components/ui/Avatar': `export const Avatar = () => null;`,
  '../../components/ui/Button': `export const Button = () => null, IconButton = Button;`,
  './ChatControls': `export const ChatControls = () => null;`,
  './ChatAttachmentView': `export const ChatAttachmentView = () => null;`,
  './ChatScheduledTaskCard': `export const ChatScheduledTaskCard = () => null;`,
  './ChatScheduledTaskComposer': `export const ChatScheduledTaskComposer = () => null;`,
  './chatApi': `export const mentionMembers = async () => [];`,
  '../notifications/BoardNotificationPreferences': `export const BoardNotificationPreferences = () => null;`,
  '../notifications/notificationState': `export const setChatNotificationView = value => globalThis.chatAttentionFixture.views.push(value);`,
};
const compiled = await build({
  entryPoints: [new URL('../src/features/chat/ChatPanel.tsx', import.meta.url).pathname],
  bundle: true, write: false, format: 'esm', platform: 'node',
  plugins: [{ name: 'chat-attention-fixture', setup(builder) {
    builder.onResolve({ filter: /.*/ }, args => args.path in stubs ? { path: args.path, namespace: 'fixture' } : undefined);
    builder.onLoad({ filter: /.*/, namespace: 'fixture' }, args => ({ contents: stubs[args.path] }));
  } }],
});
const { ChatPanel } = await import(`data:text/javascript;base64,${Buffer.from(compiled.outputFiles[0].text).toString('base64')}`);

test('a visible unfocused chat preserves unread messages, then acknowledges them on focus', () => {
  const listeners = new Map(), originals = new Map();
  const f = { refIndex: 0, focused: false, effects: [], layout: [], views: [], reads: [], viewing: [],
    list: { scrollHeight: 1000, clientHeight: 500, scrollTop: 500 },
  };
  const globals = { chatAttentionFixture: f,
    window: { addEventListener: (name, fn) => listeners.set(`window:${name}`, fn), removeEventListener: name => listeners.delete(`window:${name}`) },
    document: { visibilityState: 'visible', hasFocus: () => f.focused,
      addEventListener: (name, fn) => listeners.set(`document:${name}`, fn), removeEventListener: name => listeners.delete(`document:${name}`) },
  };
  for (const [name, value] of Object.entries(globals)) {
    originals.set(name, Object.getOwnPropertyDescriptor(globalThis, name));
    Object.defineProperty(globalThis, name, { configurable: true, writable: true, value });
  }
  const snapshot = { revoked: false, status: 'connected', loaded: true, joined: {}, messages: [
    { id: 'message', sender: { userId: 'peer' }, sequence: '1', cursor: 'cursor', boardId: 'board' }],
    pending: [], typing: [], draft: '', cooldownUntil: 0, unseen: 0, lastReadSequence: '0', hasOlder: false,
  };
  const controller = { boardId: 'board', userId: 'me', getSnapshot: () => snapshot,
    isMuted: () => false, canSend: () => true, stopTyping() {},
    setViewingLatest: (...args) => f.viewing.push(args), markReadThrough: value => f.reads.push(value.sequence),
  };
  let cleanups = [];
  try {
    ChatPanel({ controller, snapshot, boardTitle: 'Board', onClose() {}, modal: true,
      scrollPosition: { current: { top: 500, latest: true, initialized: true } } });
    cleanups = f.effects.map(fn => fn()); f.layout[0]();
    assert.deepEqual(f.reads, []);
    assert.equal(f.views.at(-1).visible, false);
    assert.deepEqual(f.viewing.at(-1), [false, false], 'blur does not pin the history window');
    f.focused = true; listeners.get('window:focus')();
    assert.deepEqual(f.reads, ['1']);
    assert.equal(f.views.at(-1).visible, true);
    f.focused = false; listeners.get('window:blur')();
    snapshot.messages.push({ ...snapshot.messages[0], id: 'next', sequence: '2' }); f.layout[0]();
    assert.deepEqual(f.reads, ['1'], 'new messages in an unfocused window stay unread');
    f.focused = true; document.visibilityState = 'hidden'; listeners.get('document:visibilitychange')();
    assert.equal(f.views.at(-1).visible, false);
    assert.deepEqual(f.reads, ['1']);
  } finally {
    for (const cleanup of cleanups) cleanup?.();
    for (const [name, original] of originals) {
      if (original) Object.defineProperty(globalThis, name, original); else delete globalThis[name];
    }
  }
});
