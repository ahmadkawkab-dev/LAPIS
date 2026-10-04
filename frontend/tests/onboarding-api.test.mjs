import assert from 'node:assert/strict';
import test from 'node:test';
import { build } from 'esbuild';
const compiled = await build({ entryPoints: [new URL('../src/api.ts', import.meta.url).pathname], bundle: true, write: false, format: 'esm', platform: 'node',
  plugins: [{ name: 'tour-test-auth', setup(builder) {
    builder.onResolve({ filter: /^\.\/auth$/ }, () => ({ path: 'auth', namespace: 'test' }));
    builder.onLoad({ filter: /.*/, namespace: 'test' }, () => ({ contents: `
      export class AuthApiError extends Error { constructor(code, status) { super(code); this.code = code; this.status = status; } }
      export const apiFetch = (...args) => globalThis.tourApiFetch(...args);` }));
  } }],
});
const { onboardingApi } = await import(`data:text/javascript;base64,${Buffer.from(compiled.outputFiles[0].text).toString('base64')}`);
test('onboarding uses authenticated transport with cancellation and sends only outcome/version', async () => {
  const calls = [], abort = new AbortController();
  globalThis.tourApiFetch = async (path, init) => { calls.push({ path, init }); return Response.json({ status: 'Completed', version: 1 }); };
  try {
    await onboardingApi.get(abort.signal);
    await onboardingApi.save({ status: 'Completed', version: 1 }, abort.signal);
    assert.equal(calls[0].path, '/api/profile/onboarding'); assert.equal(calls[0].init.method, 'GET');
    assert.equal(calls[1].init.method, 'PUT'); assert.equal(calls[1].init.signal, abort.signal);
    assert.equal(calls[0].init.signal, abort.signal);
    assert.deepEqual(JSON.parse(calls[1].init.body), { status: 'Completed', version: 1 });
    globalThis.tourApiFetch = async () => Response.json({ code: 'onboarding_version_changed' }, { status: 409 });
    await assert.rejects(onboardingApi.save({ status: 'Skipped', version: 1 }), error => error.status === 409);
  } finally { delete globalThis.tourApiFetch; }
});
