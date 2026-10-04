import assert from 'node:assert/strict';
import test from 'node:test';
import { build } from 'esbuild';

const compiled = await build({
  entryPoints: [new URL('../src/features/notifications/browserPush.ts', import.meta.url).pathname],
  bundle: true, write: false, format: 'esm', platform: 'node',
  plugins: [{ name: 'browser-push-fixture', setup(builder) {
    builder.onResolve({ filter: /^(\.\.\/\.\.\/api|\.\/notificationState)$/ }, args => ({ path: args.path, namespace: 'fixture' }));
    builder.onLoad({ filter: /.*/, namespace: 'fixture' }, args => ({ contents: args.path.endsWith('/api') ? `
      export const errorMessage = error => error.status >= 500 ? 'Wukna had a server error. Try again shortly.' : 'Could not connect to Wukna. Try again.';
      export const pushApi = {
        configuration: async () => ({ enabled: true, publicKey: 'AQ' }),
        register: async (...args) => { const f = globalThis.pushFixture; f.registrations.push(args); if (f.apiError) throw f.apiError; }
      };
    ` : `
      export const installationId = () => 'installation';
      export const bindNotificationSession = async (...args) => { const f = globalThis.pushFixture; if (f.bindingError) throw f.bindingError; f.bindings.push(args); };
      export const clearNotificationBinding = async () => true;
    ` }));
  } }],
});
const { enableBrowserPush, browserPushErrorMessage } = await import(`data:text/javascript;base64,${Buffer.from(compiled.outputFiles[0].text).toString('base64')}`);

function fixture() {
  const f = { permission: 'granted', registrations: [], bindings: [], subscriptions: 0, unsubscribed: 0 };
  const subscription = {
    toJSON: () => ({ endpoint: 'https://fcm.googleapis.com/example', keys: { p256dh: 'public', auth: 'auth' } }),
    unsubscribe: async () => { f.unsubscribed++; if (f.cleanupError) throw f.cleanupError; return true; },
  };
  const registration = { pushManager: {
    getSubscription: async () => null,
    subscribe: async () => { f.subscriptions++; if (f.pushError) throw f.pushError; return subscription; },
  } };
  const globals = {
    pushFixture: f,
    window: { isSecureContext: true, PushManager: function () {}, Notification: {} },
    navigator: { serviceWorker: { register: async () => { if (f.workerError) throw f.workerError; return registration; }, ready: Promise.resolve(registration) } },
    Notification: { requestPermission: async () => f.permission },
    localStorage: { getItem: () => f.storageBlocked ? null : 'installation' },
  };
  const originals = new Map();
  for (const [name, value] of Object.entries(globals)) {
    originals.set(name, Object.getOwnPropertyDescriptor(globalThis, name));
    Object.defineProperty(globalThis, name, { configurable: true, writable: true, value });
  }
  f.close = () => { for (const [name, original] of originals) {
    if (original) Object.defineProperty(globalThis, name, original); else delete globalThis[name];
  } };
  return f;
}

test('push service failure gives Brave setup guidance without claiming Wukna is unreachable', async () => {
  const f = fixture();
  try {
    f.pushError = new DOMException('Subscription failed - push service error', 'AbortError');
    await assert.rejects(enableBrowserPush('me'), error => {
      assert.match(browserPushErrorMessage(error), /Use Google services for push messaging/);
      assert.doesNotMatch(browserPushErrorMessage(error), /Could not connect to Wukna/);
      return true;
    });
    assert.equal(f.registrations.length, 0);
  } finally { f.close(); }
});

test('blocked permission, site storage and worker startup each retain actionable setup errors', async () => {
  for (const [change, expected] of [
    [{ permission: 'denied' }, /Notifications are blocked/],
    [{ storageBlocked: true }, /Allow site storage/],
    [{ workerError: new TypeError('worker registration failed') }, /Could not start browser notifications/],
    [{ pushError: new DOMException('blocked', 'NotAllowedError') }, /Notifications are blocked/],
  ]) {
    const f = fixture();
    try {
      Object.assign(f, change);
      await assert.rejects(enableBrowserPush('me'), error => expected.test(browserPushErrorMessage(error)));
      assert.equal(f.registrations.length, 0);
    } finally { f.close(); }
  }
});

test('API errors stay intact even when browser cleanup also fails', async () => {
  const f = fixture();
  try {
    f.apiError = Object.assign(new Error('server_error'), { status: 500 });
    f.cleanupError = new DOMException('cleanup failure', 'AbortError');
    await assert.rejects(enableBrowserPush('me'), error => {
      assert.equal(error, f.apiError);
      assert.match(browserPushErrorMessage(error), /Wukna had a server error/);
      return true;
    });
    assert.equal(f.unsubscribed, 1);
    assert.equal(f.bindings.length, 0);
  } finally { f.close(); }
});

test('session storage failure reports storage guidance and unsubscribes', async () => {
  const f = fixture();
  try {
    f.bindingError = new DOMException('storage blocked', 'SecurityError');
    await assert.rejects(enableBrowserPush('me'), error => /Allow site storage/.test(browserPushErrorMessage(error)));
    assert.equal(f.unsubscribed, 1);
  } finally { f.close(); }
});

test('successful enrollment registers browser keys and binds the current account', async () => {
  const f = fixture();
  try {
    await enableBrowserPush('me');
    assert.deepEqual(f.registrations, [['installation', 'https://fcm.googleapis.com/example', 'public', 'auth']]);
    assert.deepEqual(f.bindings, [['me', 'installation']]);
    assert.equal(f.unsubscribed, 0);
  } finally { f.close(); }
});
