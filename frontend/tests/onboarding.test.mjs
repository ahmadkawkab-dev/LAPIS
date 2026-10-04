import assert from 'node:assert/strict';
import test from 'node:test';
import { TourController, tourStep, placeCoach, TOUR_LENGTH, TOUR_VERSION } from '../src/features/onboarding/tour.ts';

function fixture(status = 'NotStarted', version = 0) {
  const saves = [], events = [];
  const api = { get: async () => ({ status, version }), save: async value => { saves.push(value); return value; } };
  return { controller: new TourController(api, (name, step) => events.push({ name, step })), saves, events, api };
}
const settle = () => new Promise(resolve => setImmediate(resolve));

test('first eligible user starts a short tour; completed/skipped users and future versions stay closed', async () => {
  const first = fixture(); await first.controller.load();
  assert.equal(first.controller.getSnapshot().step, 0);
  assert.deepEqual(first.events.map(value => value.name), ['tour_started', 'tour_step_viewed']);
  for (const [status, version] of [['Completed', 1], ['Skipped', 1], ['Completed', 2], ['NotStarted', 2]]) {
    const { controller } = fixture(status, version); await controller.load();
    assert.equal(controller.getSnapshot().step, null);
  }
  const revised = fixture('Completed', 0); await revised.controller.load();
  assert.equal(revised.controller.getSnapshot().step, 0);
});

test('skip and finish close immediately, persist only outcome/version, and can both replay', async () => {
  for (const outcome of ['Skipped', 'Completed']) {
    const { controller, saves } = fixture(); await controller.load();
    controller.close(outcome);
    assert.equal(controller.getSnapshot().step, null);
    await settle();
    assert.deepEqual(saves, [{ status: outcome, version: TOUR_VERSION }]);
    assert.equal(controller.getSnapshot().pending, null);
    controller.start(); assert.equal(controller.getSnapshot().step, 0);
  }
});

test('Back/Next stay within five steps, and interaction does not write workspace data', async () => {
  const { controller, saves } = fixture(); await controller.load();
  controller.move(-1); assert.equal(controller.getSnapshot().step, 0);
  for (let i = 1; i < TOUR_LENGTH; i++) { controller.move(1); assert.equal(controller.getSnapshot().step, i); }
  controller.move(1); assert.equal(controller.getSnapshot().step, TOUR_LENGTH - 1);
  controller.move(-1); assert.equal(controller.getSnapshot().step, TOUR_LENGTH - 2);
  assert.deepEqual(saves, []);
});

test('late preferences cannot reopen a skipped tour and failed saving has a retry path', async () => {
  let resolveRead;
  let attempts = 0;
  const controller = new TourController({ get: () => new Promise(resolve => { resolveRead = resolve; }), save: async value => {
    if (++attempts === 1) throw new Error('offline'); return value;
  } });
  const read = controller.load(); controller.start(); controller.close('Skipped');
  resolveRead({ status: 'NotStarted', version: 0 }); await read; await settle();
  assert.equal(controller.getSnapshot().step, null);
  assert.equal(controller.getSnapshot().failure, true);
  assert.deepEqual(controller.getSnapshot().pending, { status: 'Skipped', version: 1 });
  await controller.retry();
  assert.equal(controller.getSnapshot().failure, false);
  assert.equal(controller.getSnapshot().pending, null);
});

test('the five steps explain outer workspace areas with mobile menu fallbacks', () => {
  const definitions = Array.from({ length: TOUR_LENGTH }, (_, index) => tourStep(index));
  assert.deepEqual(definitions.map(step => step.title), ['Home', 'Boards', 'Calendar', 'Quick tasks', 'Templates']);
  assert.deepEqual(definitions.map(step => step.targets), [['home'], ['boards', 'more'], ['calendar'], ['quick-tasks', 'more'], ['templates', 'more']]);
  assert.match(definitions[0].text, /activity/);
  assert.match(definitions[1].text, /notes/);
  assert.match(definitions[1].text, /collaborate in real time/);
  assert.match(definitions[1].text, /board chat/);
});

test('coach positions remain inside 375/768/1024/1440px viewports and support all four sides', () => {
  for (const width of [375, 768, 1024, 1440]) {
    const viewport = { width, height: 800 }, card = { width: Math.min(352, width - 24), height: 310 };
    for (const target of [null, { left: 8, top: 8, width: 48, height: 44 },
      { left: width - 60, top: 700, width: 44, height: 44 }, { left: 0, top: 744, width, height: 56 }]) {
      const result = placeCoach(target, card, viewport);
      assert.ok(result.left >= 12 && result.left + card.width <= width - 12);
      assert.ok(result.top >= 12 && result.top + card.height <= viewport.height - 12);
    }
  }
  const card = { width: 100, height: 100 }, viewport = { width: 1024, height: 600 };
  assert.equal(placeCoach({ left: 10, top: 20, width: 100, height: 30 }, card, viewport).left, 126); // right
  assert.equal(placeCoach({ left: 900, top: 20, width: 100, height: 30 }, card, viewport).left, 784); // left
  assert.equal(placeCoach({ left: 12, top: 20, width: 1000, height: 30 }, card, viewport).top, 66); // bottom
  assert.equal(placeCoach({ left: 12, top: 400, width: 1000, height: 180 }, card, viewport).top, 284); // top
  assert.equal(placeCoach({ left: 0, top: 744, width: 768, height: 56 }, { width: 352, height: 310 }, { width: 768, height: 800 }).top, 12);
});
