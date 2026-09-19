const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const source = fs.readFileSync('LocalYoutubePlayerHost.cs', 'utf8');
const script = source.match(/<script>([\s\S]*?)<\/script>/)[1]
  .replace('{{candidatesJson}}', JSON.stringify(['short', 'boundary', 'error', 'long']));
let instance;
const logs = [];
const context = { window: {}, console: { log: value => logs.push(value) },
  setTimeout: callback => setImmediate(callback),
  YT: { PlayerState: { ENDED: 0, PLAYING: 1 }, Player: class {
    constructor(id, options) { instance = this; this.events = options.events; this.id = ''; this.playing = false; }
    mute() {} unMute() {}
    loadVideoById(id) { this.id = id; this.playing = true; if (id === 'error') this.events.onError({ data: 150 }); }
    cueVideoById(id) { this.id = id; this.playing = false; }
    getVideoData() { return { video_id: this.id }; }
    getDuration() { return { short: 299, boundary: 300, long: 600 }[this.id] || 0; }
    pauseVideo() { this.playing = false; }
    playVideo() { this.playing = true; }
  } } };
context.window.location = { origin: 'http://127.0.0.1:1234' };
vm.runInNewContext(script, context);
context.window.onYouTubeIframeAPIReady();
instance.events.onReady();
const controls = context.window.youtubePlayerControls;
controls.play();
controls.pause(); // Ad ends while duration checks are still running.
async function test() {
  for (let i = 0; i < 100 && !logs.some(x => x.includes('eligible videos')); i++)
    await new Promise(setImmediate);
  assert(logs.some(x => x.includes('2 eligible videos')));
  assert.equal(instance.id, 'boundary');
  assert.equal(instance.playing, false);
  controls.play();
  assert.equal(instance.playing, true);
  instance.events.onStateChange({ data: 0 });
  assert.equal(instance.id, 'long');
  assert.equal(instance.playing, true);
  controls.pause();
  assert.equal(instance.playing, false);
  controls.play();
  assert.equal(instance.id, 'long');
  instance.events.onStateChange({ data: 0 });
  assert.match(controls.getError(), /exhausted/);
  assert.equal(controls.play().success, false);
  console.log('PASS: duration boundary, errors, ordering, pause during preparation, resume, advance, exhaustion');
}
test().catch(error => { console.error(error); process.exitCode = 1; });
