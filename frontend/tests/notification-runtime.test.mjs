import assert from 'node:assert/strict';
import test from 'node:test';
import { build } from 'esbuild';

const stubs = {
  'react': `export const useEffect = fn => globalThis.notificationRuntimeFixture.effects.push(fn);
    export const useState = value => [value, () => {}];`,
  'react/jsx-runtime': `export const jsx = () => null; export const jsxs = jsx; export const Fragment = Symbol('fragment');`,
  '../../api': `export const notificationApi = {
    preferences: async () => ({ settings: globalThis.notificationRuntimeFixture.settings }),
    unreadCount: async () => ({ unreadCount: 1 }), chatUnread: async () => [],
    get: async () => globalThis.notificationRuntimeFixture.item,
    boardPreferences: async () => globalThis.notificationRuntimeFixture.boardSettings };
    export const pushApi = { presence: async request => { globalThis.notificationRuntimeFixture.presence.push(request); } };`,
  '../../realtime/connection': `export const realtimeConnection = {
    on: (name, fn) => { globalThis.notificationRuntimeFixture.events.set(name, fn); return () => {}; },
    onReconnected: () => () => {} };`,
  '../../components/ui/Button': `export const Button = () => null;`,
  './notificationView': `export const notificationPath = () => '/notifications';`,
  './notificationState': `export const bindNotificationSession = async () => {};
    export const installationId = () => 'installation';
    export const chatNotificationView = () => globalThis.notificationRuntimeFixture.chat;
    export const claimNotification = async () => { const f = globalThis.notificationRuntimeFixture; const claim = !f.claimed; f.claimed = true; return claim; };
    export const setNotificationUnread = () => {}; export const setChatUnreadCounts = () => {};
    export const notificationSoundKey = () => 'chatSoundEnabled';`,
  './notificationAudio': `export const notificationAudio = {
    get ready() { return globalThis.notificationRuntimeFixture.audioReady; },
    unlock: async () => { globalThis.notificationRuntimeFixture.unlocks++; globalThis.notificationRuntimeFixture.audioReady = true; return true; },
    play: async (key, volume) => { globalThis.notificationRuntimeFixture.plays.push({ key, volume }); return 'played'; },
    stop: () => { globalThis.notificationRuntimeFixture.stops++; } };`,
  './browserPush': `export const clearBrowserNotificationSession = async () => {};`,
};
const compiled = await build({
  entryPoints: [new URL('../src/features/notifications/NotificationRuntime.tsx', import.meta.url).pathname],
  bundle: true, write: false, format: 'esm', platform: 'node',
  plugins: [{ name: 'notification-runtime-fixture', setup(builder) {
    builder.onResolve({ filter: /.*/ }, args => args.path in stubs ? { path: args.path, namespace: 'fixture' } : undefined);
    builder.onLoad({ filter: /.*/, namespace: 'fixture' }, args => ({ contents: stubs[args.path] }));
  } }],
});
const { NotificationRuntime } = await import(`data:text/javascript;base64,${Buffer.from(compiled.outputFiles[0].text).toString('base64')}`);
const settle = () => new Promise(resolve => setImmediate(resolve));

function fixture() {
  const events = new Map(), listeners = new Map(), effects = [];
  const f = { events, listeners, effects, stops: 0, plays: [], unlocks: 0, workerUpdates: 0, audioReady: true, claimed: false, chat: null, presence: [], focused: true,
    settings: { inAppEnabled: true, chatNotificationsEnabled: true, soundsMuted: false, soundVolume: 0.5, chatSoundEnabled: true },
    boardSettings: { effectiveMode: 'allActivity', soundsMuted: false },
    item: { id: 'notice', revision: 1, type: 'chatActivity', activityKind: 'message', boardId: null,
      updatedAt: new Date().toISOString(), isUnread: true },
  };
  const originals = new Map();
  const add = (target, name, fn) => { listeners.set(`${target}:${name}`, fn); };
  const remove = (target, name) => { listeners.delete(`${target}:${name}`); };
  const globals = {
    notificationRuntimeFixture: f,
    window: { addEventListener: (name, fn) => add('window', name, fn), removeEventListener: name => remove('window', name), setTimeout: () => 0 },
    document: { visibilityState: 'visible', hasFocus: () => f.focused,
      addEventListener: (name, fn) => add('document', name, fn), removeEventListener: name => remove('document', name) },
    navigator: { serviceWorker: { getRegistration: async () => ({ update: async () => { f.workerUpdates++; } }),
      addEventListener: (name, fn) => add('worker', name, fn), removeEventListener: name => remove('worker', name) } },
    setInterval: fn => { f.poll = fn; return 0; }, clearInterval: () => {},
  };
  for (const [name, value] of Object.entries(globals)) {
    originals.set(name, Object.getOwnPropertyDescriptor(globalThis, name));
    Object.defineProperty(globalThis, name, { configurable: true, writable: true, value });
  }
  NotificationRuntime({ userId: 'me', navigate() {} });
  const cleanup = effects[0]();
  f.receive = userId => events.get('NotificationChanged')({ userId: userId ?? 'me', notification: f.item });
  f.close = () => {
    cleanup();
    for (const [name, original] of originals) {
      if (original) Object.defineProperty(globalThis, name, original); else delete globalThis[name];
    }
  };
  return f;
}

test('routine count reconciliation does not cut off realtime audio; changed preferences do', async () => {
  const f = fixture();
  try {
    await settle();
    assert.equal(f.workerUpdates, 1, 'an enrolled worker is refreshed without another subscription');
    f.listeners.get('window:pointerup')({ isTrusted: true }); await settle();
    assert.equal(f.unlocks, 1);
    await f.receive();
    assert.deepEqual(f.plays, [{ key: 'chatSoundEnabled', volume: 0.5 }]);
    const stops = f.stops;
    f.poll(); await settle();
    assert.equal(f.stops, stops, 'polling unchanged preferences must allow the clip to finish');
    f.settings = { ...f.settings, soundsMuted: true };
    f.poll(); await settle();
    assert.equal(f.stops, stops + 1, 'new mute settings stop an active clip');
    await f.receive();
    assert.equal(f.plays.length, 1, 'duplicate delivery stays quiet');
  } finally { f.close(); }
});

test('global and board mute preserve alerts without playing audio, and a visible latest chat stays quiet', async () => {
  for (const mode of ['global', 'board', 'visible-chat', 'other-user']) {
    const f = fixture();
    try {
      if (mode === 'global') f.settings.soundsMuted = true;
      if (mode === 'board') { f.item.boardId = 'board'; f.boardSettings.soundsMuted = true; }
      if (mode === 'visible-chat') { f.item.boardId = 'board'; f.chat = { boardId: 'board', visible: true, latest: true }; }
      await settle(); await f.receive(mode === 'other-user' ? 'someone-else' : 'me');
      assert.equal(f.plays.length, 0, mode);
    } finally { f.close(); }
  }
});

test('unfocused windows and background tabs play unlocked clips while releasing push suppression', async () => {
  const f = fixture();
  try {
    await settle();
    assert.equal(f.presence.at(-1).visible, true);
    f.item.boardId = 'board';
    f.chat = { boardId: 'board', visible: true, latest: true };
    const stops = f.stops;
    f.focused = false; f.listeners.get('window:blur')();
    await settle();
    assert.equal(f.presence.at(-1).visible, false);
    assert.equal(f.presence.at(-1).chatVisible, false);
    await f.receive(); assert.equal(f.plays.length, 1);
    await f.receive(); assert.equal(f.plays.length, 1, 'duplicate events do not repeat the background clip');
    f.focused = true; f.listeners.get('window:focus')(); await settle();
    assert.equal(f.presence.at(-1).visible, true);
    assert.equal(f.presence.at(-1).chatVisible, true);
    await f.receive(); assert.equal(f.plays.length, 1, 'the same focused chat stays quiet');
    document.visibilityState = 'hidden'; f.listeners.get('document:visibilitychange')(); await settle();
    assert.equal(f.presence.at(-1).visible, false);
    f.item.revision++; f.claimed = false;
    await f.receive(); assert.equal(f.plays.length, 2);
    assert.equal(f.stops, stops, 'blur and hiding do not cut off clips');
  } finally { f.close(); }
});

test('background mute, blocked audio, read activity and board notification modes leave push fallback available', async () => {
  for (const mode of ['global', 'board-sound', 'category-sound', 'zero-volume', 'blocked', 'read', 'muted-board', 'mentions-only']) {
    const f = fixture();
    try {
      f.focused = false; f.item.boardId = 'board';
      if (mode === 'global') f.settings.soundsMuted = true;
      if (mode === 'board-sound') f.boardSettings.soundsMuted = true;
      if (mode === 'category-sound') f.settings.chatSoundEnabled = false;
      if (mode === 'zero-volume') f.settings.soundVolume = 0;
      if (mode === 'blocked') f.audioReady = false;
      if (mode === 'read') f.item.isUnread = false;
      if (mode === 'muted-board') f.boardSettings.effectiveMode = 'muted';
      if (mode === 'mentions-only') f.boardSettings.effectiveMode = 'mentionsAndReplies';
      await settle(); await f.receive();
      assert.equal(f.plays.length, 0, mode);
      assert.equal(f.claimed, false, `${mode}: leave the delivery claim to the worker`);
    } finally { f.close(); }
  }
});

test('a muted duplicate push lets the claimed clip finish while push-first delivery remains quiet in the page', async () => {
  const f = fixture();
  try {
    await settle(); f.focused = false;
    await f.receive();
    const stops = f.stops;
    f.listeners.get('worker:message')({ data: { type: 'wukna:push-shown', userId: 'me', notificationId: 'notice', alreadyHandled: true } });
    await settle();
    assert.equal(f.stops, stops, 'the silent OS notification must not truncate custom audio');
    await f.receive(); assert.equal(f.plays.length, 1);
    f.listeners.get('worker:message')({ data: { type: 'wukna:push-shown', userId: 'me', notificationId: 'notice', alreadyHandled: false } });
    await settle();
    assert.equal(f.stops, stops + 1, 'push-first audio takes precedence');
    await f.receive(); assert.equal(f.plays.length, 1);
  } finally { f.close(); }
});

test('a push that arrived first prevents background page audio from sounding twice', async () => {
  const f = fixture();
  try {
    f.focused = false; f.claimed = true;
    await settle(); await f.receive();
    assert.equal(f.plays.length, 0, 'the existing worker claim prevents a second sound');
  } finally { f.close(); }
});
