import assert from 'node:assert/strict';
import test from 'node:test';
import { notificationSounds, notificationSoundVolume } from '../src/features/notifications/notificationSounds.ts';
import { NotificationAudioPlayer } from '../src/features/notifications/notificationAudio.ts';

const settings = { soundsMuted: false, soundVolume: 0.5,
  chatSoundEnabled: true, taskReminderSoundEnabled: true, scheduledTaskPostedSoundEnabled: true,
  boardInvitationSoundEnabled: true, taskCompletedSoundEnabled: true };

test('global sound mute and zero volume silence all categories without changing visual preferences', () => {
  for (const { key } of notificationSounds) {
    assert.equal(notificationSoundVolume({ ...settings, soundsMuted: true, inAppEnabled: true }, key), 0);
    assert.equal(notificationSoundVolume({ ...settings, soundVolume: 0 }, key), 0);
    assert.equal(notificationSoundVolume(settings, key), 0.5);
  }
});

test('individual sound choices stay independent and invalid volumes cannot reach playback', () => {
  const oneMuted = { ...settings, chatSoundEnabled: false };
  assert.equal(notificationSoundVolume(oneMuted, 'chatSoundEnabled'), 0);
  assert.equal(notificationSoundVolume(oneMuted, 'taskReminderSoundEnabled'), 0.5);
  assert.equal(notificationSoundVolume({ ...settings, soundVolume: Number.NaN }, 'chatSoundEnabled'), 0);
  assert.equal(notificationSoundVolume({ ...settings, soundVolume: 2 }, 'chatSoundEnabled'), 1);
  assert.equal(notificationSoundVolume({ ...settings, soundVolume: -1 }, 'chatSoundEnabled'), 0);
});

function audioFixture(fetchClip = async () => new Response(new ArrayBuffer(4))) {
  const sources = [], gains = [], requests = [];
  const context = { state: 'suspended', destination: {}, resumes: 0, decoded: 0,
    async resume() { this.resumes++; this.state = 'running'; },
    async decodeAudioData() { this.decoded++; return { decoded: true }; },
    createBufferSource() { const source = { starts: 0, stops: 0, connect() {}, disconnect() {},
      start() { this.starts++; }, stop() { this.stops++; } }; sources.push(source); return source; },
    createGain() { const gain = { gain: { value: 0 }, connect() {}, disconnect() {} }; gains.push(gain); return gain; },
  };
  const player = new NotificationAudioPlayer(() => context, async path => { requests.push(path); return fetchClip(path); });
  return { player, context, sources, gains, requests };
}

test('trusted-interaction unlock permits later realtime playback of the supplied clips', async () => {
  const f = audioFixture();
  assert.equal(f.player.ready, false);
  assert.equal(await f.player.play('chatSoundEnabled', 0.5), 'blocked');
  assert.equal(f.requests.length, 0);
  assert.equal(await f.player.unlock(), true);
  assert.equal(f.player.ready, true);
  for (const { key, path } of notificationSounds) {
    assert.equal(await f.player.play(key, 0.35), 'played');
    assert.equal(f.requests.at(-1), path);
    assert.equal(f.sources.at(-1).starts, 1);
    assert.equal(f.gains.at(-1).gain.value, 0.35);
  }
  assert.equal(f.context.resumes, 1);
  assert.equal(await f.player.play('chatSoundEnabled', 0.5), 'played');
  assert.equal(f.context.decoded, 5, 'decoded clips are reused');
  f.player.stop();
  assert.equal(f.sources.at(-1).stops, 1);
});

test('mute or teardown cancels playback while a clip is loading', async () => {
  let resolve;
  const f = audioFixture(() => new Promise(done => { resolve = done; }));
  await f.player.unlock();
  const pending = f.player.play('taskReminderSoundEnabled', 0.5);
  f.player.stop(); resolve(new Response(new ArrayBuffer(4)));
  assert.equal(await pending, 'cancelled');
  assert.equal(f.sources.length, 0);
  assert.equal(await f.player.play('chatSoundEnabled', 0), 'cancelled');
  assert.equal(f.requests.length, 1, 'muted sounds do not fetch clips');
});

test('an unavailable clip reports a load failure and can retry after recovery', async () => {
  let fail = true;
  const f = audioFixture(async () => fail ? new Response(null, { status: 404 }) : new Response(new ArrayBuffer(4)));
  await f.player.unlock();
  assert.equal(await f.player.play('boardInvitationSoundEnabled', 0.5), 'failed');
  fail = false;
  assert.equal(await f.player.play('boardInvitationSoundEnabled', 0.5), 'played');
  assert.equal(f.requests.length, 2);
  f.player.stop();
});
