const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const item = (id, published = 1) => ({ id, title: `Title ${id}`, published: new Date(published * 1000).toISOString() });
const candidates = [item('short', 10), { ...item('live', 9), automaticSkipReason: 'currently live stream' },
  item('boundary', 8), item('error', 7), item('long', 6)];
const script = fs.readFileSync('LocalYoutubePlayerHost.cs', 'utf8').match(/<script>([\s\S]*?)<\/script>/)[1]
  .replace('{{candidatesJson}}', JSON.stringify(candidates)).replace('{{minimumDurationSeconds}}', '300')
  .replace('{{JsonSerializer.Serialize(singleVideoId)}}', 'null');
const players = {};
const logs = [];
const keyListeners = [];
const context = { window: { location: { origin: 'http://127.0.0.1:1234' }, addEventListener: (type, listener) => { if (type === 'keydown') keyListeners.push(listener); } },
  console: { log: value => logs.push(value) }, setTimeout: callback => setImmediate(callback),
  YT: { PlayerState: { ENDED: 0, PLAYING: 1 }, Player: class {
    constructor(id, options) { players[id] = this; this.events = options.events; this.id = ''; this.playing = false; this.loads = 0; this.loadedIds = []; }
    mute() {} unMute() {}
    loadVideoById(id) { this.loads++; this.loadedIds.push(id); this.metadataReads = 0; this.id = id; this.playing = true; if (id === 'error') this.events.onError({ data: 150 }); }
    cueVideoById(id) { this.loads++; this.id = id; this.playing = false; }
    getVideoData() { return this.metadataReads++ === 0 ? undefined : { video_id: this.id }; }
    getDuration() { return { short: 299, boundary: 300, error: 0 }[this.id] ?? 600; }
    pauseVideo() { this.playing = false; }
    playVideo() { this.playing = true; }
  } } };
vm.runInNewContext(script, context);
context.window.onYouTubeIframeAPIReady();
players.player.events.onReady(); players.probe.events.onReady();
const controls = context.window.youtubePlayerControls;
async function waitRefresh(count) {
  for (let i = 0; i < 1000 && logs.filter(x => x.includes('refreshed (')).length < count; i++) await new Promise(setImmediate);
  assert.equal(logs.filter(x => x.includes('refreshed (')).length, count);
}
async function test() {
  controls.play(); controls.pause();
  await waitRefresh(1);
  assert.equal(controls.getQueue().videos.length, 2);
  assert.equal(players.player.id, 'boundary');
  assert.equal(players.player.playing, false);
  assert(logs.some(x => x.includes('Title short') && x.includes('299 seconds')));
  assert(logs.some(x => x.includes('Title live [live]') && x.includes('currently live stream')));
  assert(!players.probe.loadedIds.includes('live'));
  controls.play();
  const loads = players.player.loads;
  const additions = Array.from({length: 25}, (_, i) => item(`new${i}`, 100+i));
  controls.refresh([...additions, ...candidates, additions[0]]);
  await waitRefresh(2);
  const queue = controls.getQueue();
  assert.equal(queue.videos.length, 20);
  assert.equal(new Set(queue.videos.map(x => x.id)).size, 20);
  assert.equal(queue.currentId, 'boundary');
  assert.equal(queue.videos[1].id, 'new0');
  assert.equal(players.player.loads, loads); // Refresh did not reset position or reload.
  assert.equal(players.player.playing, true);
  assert.equal(controls.skip().success, true);
  assert.equal(players.player.id, 'new0');
  assert.equal(players.player.playing, true);
  assert(logs.some(x => x.includes('manually skipped Title boundary [boundary]')));
  assert(logs.some(x => x.includes('selected Title new0 [new0]')));
  controls.pause();
  controls.refresh([...additions, ...candidates]);
  await waitRefresh(3);
  assert.equal(players.player.playing, false);
  assert.equal(controls.getQueue().currentId, 'new0');
  assert(!controls.getQueue().videos.some(x => x.id === 'boundary'));
  let prevented = false;
  keyListeners[0]({ code: 'KeyN', repeat: false, ctrlKey: false, altKey: false, metaKey: false,
    preventDefault: () => { prevented = true; } });
  assert.equal(prevented, true);
  assert.equal(controls.getQueue().currentId, 'new1');
  assert.equal(players.player.playing, false);
  assert(controls.getQueue().videos.every(x => x.id !== 'new0'));
  console.log('PASS: duration filtering, auto/manual skip logs, deduplication, ordering, cap, current preservation, N shortcut, advancement and pause intent');
  delete players.probe;
  const singleKeys = [];
  const singleContext = { ...context, window: { location: context.window.location,
    addEventListener: (type, listener) => { if (type === 'keydown') singleKeys.push(listener); } } };
  vm.runInNewContext(script.replace('const singleVideoId = null;', 'const singleVideoId = "short";'), singleContext);
  singleContext.window.onYouTubeIframeAPIReady();
  players.player.events.onReady();
  const single = singleContext.window.youtubePlayerControls;
  assert.equal(players.probe, undefined); // No duration checks for the short override.
  assert.equal(players.player.id, 'short');
  const singleLoads = players.player.loads;
  players.player.position = 42;
  single.play(); single.pause(); single.play();
  assert.equal(players.player.loads, singleLoads);
  assert.equal(players.player.position, 42);
  assert.equal(players.player.playing, true);
  single.refresh(additions);
  players.player.events.onStateChange({ data: 0 });
  assert.equal(single.getQueue().currentId, 'short');
  assert.equal(single.getQueue().videos.length, 1);
  assert.equal(players.player.loads, singleLoads);
  assert.equal(single.skip().success, false);
  singleKeys[0]({ code: 'KeyN', repeat: false, ctrlKey: false, altKey: false, metaKey: false, preventDefault() {} });
  assert.equal(single.getQueue().currentId, 'short');
  assert.equal(players.player.loads, singleLoads);
  assert(logs.filter(x => x.includes('manual skipping is unavailable in single-video mode')).length >= 2);
  console.log('PASS: single-video override bypasses probing/refresh/skip and preserves pause/resume and video selection');
}
test().catch(error => { console.error(error); process.exitCode = 1; });
