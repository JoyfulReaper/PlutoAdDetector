const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const item = (id, published = 1) => ({ id, title: `Title ${id}`, published: new Date(published * 1000).toISOString() });
const candidates = [item('short', 10), { ...item('live', 9), automaticSkipReason: 'currently live stream' },
  item('boundary', 8), item('error', 7), item('long', 6)];
const script = fs.readFileSync('LocalYoutubePlayerHost.cs', 'utf8').match(/<script>([\s\S]*?)<\/script>/)[1]
  .replace('{{candidatesJson}}', JSON.stringify(candidates)).replace('{{minimumDurationSeconds}}', '300')
  .replace('{{JsonSerializer.Serialize(singleVideoId)}}', 'null')
  .replace('{{restoredQueueStateJson}}', 'null');
const players = {};
const logs = [];
const keyListeners = [];
const messageListeners = [];
let restoreRequests = 0;
const helpChanges = [];
const helpOverlay = {
  classList: { add: value => helpChanges.push(`add:${value}`), remove: value => helpChanges.push(`remove:${value}`) },
  setAttribute: (name, value) => helpChanges.push(`${name}:${value}`)
};
const context = { window: { location: { origin: 'http://127.0.0.1:1234' }, requestYoutubeQueueRestore: () => { restoreRequests++; }, addEventListener: (type, listener) => { if (type === 'keydown') keyListeners.push(listener); if (type === 'message') messageListeners.push(listener); } },
  document: { getElementById: id => id === 'keyboard-help' ? helpOverlay : null },
  console: { log: value => logs.push(value) }, setTimeout: callback => setImmediate(callback), clearTimeout: () => {},
  YT: { PlayerState: { ENDED: 0, PLAYING: 1, PAUSED: 2, BUFFERING: 3 }, Player: class {
    constructor(id, options) { players[id] = this; this.events = options.events; this.id = ''; this.playing = false; this.loads = 0; this.loadedIds = []; }
    mute() {} unMute() {}
    loadVideoById(request) { const { id, position } = videoRequest(request); this.loads++; this.loadedIds.push(id); this.metadataReads = 0; this.id = id; this.position = position; this.playing = true; if (id === 'error') this.events.onError({ data: 150 }); }
    cueVideoById(request) { const { id, position } = videoRequest(request); this.loads++; this.id = id; this.position = position; this.playing = false; }
    getVideoData() { return this.metadataReads++ === 0 ? undefined : { video_id: this.id }; }
    getDuration() { return { short: 299, boundary: 300, error: 0 }[this.id] ?? 600; }
    getCurrentTime() { return this.position ?? 0; }
    getPlayerState() { return this.playing ? 1 : 2; }
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
  let helpPrevented = false;
  keyListeners[0]({ code: 'KeyH', key: 'h', repeat: false, ctrlKey: false, altKey: false, metaKey: false,
    preventDefault: () => { helpPrevented = true; } });
  await new Promise(setImmediate);
  assert.equal(helpPrevented, true);
  assert.deepEqual(helpChanges, ['add:visible', 'aria-hidden:false', 'remove:visible', 'aria-hidden:true']);
  keyListeners[0]({ code: 'Slash', key: '?', repeat: false, ctrlKey: false, altKey: false, metaKey: false,
    preventDefault() {} });
  await new Promise(setImmediate);
  assert.equal(helpChanges.filter(x => x === 'add:visible').length, 2);
  messageListeners[0]({ data: 'pluto-ad-detector:show-youtube-help' });
  await new Promise(setImmediate);
  assert.equal(helpChanges.filter(x => x === 'add:visible').length, 3);
  keyListeners[0]({ code: 'KeyR', key: 'r', repeat: false, ctrlKey: false, altKey: false, metaKey: false,
    preventDefault() {} });
  messageListeners[0]({ data: 'pluto-ad-detector:reload-youtube-queue' });
  assert.equal(restoreRequests, 2);
  players.player.position = 17.5;
  const saved = controls.getQueueState();
  assert.deepEqual(JSON.parse(JSON.stringify(saved.currentVideo)), {
    id: 'new1', title: 'Title new1', playbackPositionSeconds: 17.5
  });
  assert.equal(saved.queuedVideos[0].id, 'new1');
  assert.equal(saved.queuedVideos[0].durationSeconds, 600);
  assert.deepEqual([...saved.completedOrSkippedVideoIds], ['boundary', 'new0']);
  const restoredState = {
    currentVideo: { id: 'restore0001', title: 'Restored current title', playbackPositionSeconds: 42.5 },
    queuedVideos: [
      { id: 'restore0001', title: 'Restored current', durationSeconds: 700 },
      { id: 'restore0002', title: 'Restored next', durationSeconds: 800 }
    ],
    completedOrSkippedVideoIds: ['restore0003']
  };
  controls.pause();
  assert.equal(controls.restoreQueue(restoredState).success, true);
  assert.equal(players.player.id, 'restore0001');
  assert.equal(controls.getQueue().videos[0].title, 'Restored current title');
  assert.equal(players.player.position, 42.5);
  assert.equal(players.player.playing, false);
  assert.deepEqual(JSON.parse(JSON.stringify(controls.getQueue().videos.map(video => video.id))), ['restore0001', 'restore0002']);
  controls.play();
  assert.equal(controls.restoreQueue({ ...restoredState,
    currentVideo: { ...restoredState.currentVideo, playbackPositionSeconds: 84 } }).success, true);
  assert.equal(players.player.position, 84);
  assert.equal(players.player.playing, true);
  const beforeIframeSkip = controls.getQueue().currentId;
  messageListeners[0]({ data: 'pluto-ad-detector:skip-youtube-video' });
  assert.notEqual(controls.getQueue().currentId, beforeIframeSkip);
  const finalVideoState = {
    currentVideo: { id: 'finalvideo1', title: 'Final video', playbackPositionSeconds: 10 },
    queuedVideos: [{ id: 'finalvideo1', title: 'Final video', durationSeconds: 700 }],
    completedOrSkippedVideoIds: []
  };
  controls.restoreQueue(finalVideoState);
  controls.play();
  assert.equal(players.player.playing, true);
  assert.equal(controls.skip().success, true);
  assert.equal(controls.getQueue().currentId, null);
  assert.equal(players.player.playing, false);
  console.log('PASS: queue behavior, N/R shortcuts, H/? help overlay, restore position/order, and playback intent');
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
  assert.equal(single.restoreQueue(restoredState).success, false);
  singleKeys[0]({ code: 'KeyN', repeat: false, ctrlKey: false, altKey: false, metaKey: false, preventDefault() {} });
  assert.equal(single.getQueue().currentId, 'short');
  assert.equal(players.player.loads, singleLoads);
  assert(logs.filter(x => x.includes('manual skipping is unavailable in single-video mode')).length >= 2);
  console.log('PASS: single-video override bypasses probing/refresh/skip and preserves pause/resume and video selection');
}
test().catch(error => { console.error(error); process.exitCode = 1; });

function videoRequest(request) {
  return typeof request === 'string'
    ? { id: request, position: 0 }
    : { id: request.videoId, position: request.startSeconds || 0 };
}
