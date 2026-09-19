const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const source = fs.readFileSync('SourceMuteScript.cs', 'utf8');
const script = source.match(/Script = """\r?\n([\s\S]*?)\r?\n\s*""";/)?.[1];
assert(script, 'Could not find the source-muting script.');

const currentVideo = video();
const videos = [currentVideo];
const observers = [];
const document = {
  documentElement: {},
  querySelectorAll(selector) {
    assert.equal(selector, 'video');
    return videos;
  }
};
const context = {
  document,
  globalThis: null,
  MutationObserver: class {
    constructor(callback) {
      this.callback = callback;
      this.disconnected = false;
      observers.push(this);
    }
    observe(target, options) {
      assert.equal(target, document.documentElement);
      assert.equal(options.childList, true);
      assert.equal(options.subtree, true);
    }
    disconnect() { this.disconnected = true; }
  }
};
context.globalThis = context;
const setSourceMuted = vm.runInNewContext(`(${script})`, context);

setSourceMuted(true);
assert.equal(currentVideo.muted, true);
assert.equal(observers.length, 1);

const addedVideo = video();
const nestedVideo = video();
const wrapper = element([nestedVideo]);
observers[0].callback([{ addedNodes: [addedVideo, wrapper, { nodeType: 3 }] }]);
assert.equal(addedVideo.muted, true);
assert.equal(nestedVideo.muted, true);

setSourceMuted(true);
assert.equal(observers.length, 1, 'Repeated mute calls must reuse the observer.');

videos.push(addedVideo, nestedVideo);
setSourceMuted(false);
assert.equal(observers[0].disconnected, true);
assert(videos.every(item => item.muted === false));

addedVideo.muted = false;
observers[0].callback([{ addedNodes: [addedVideo] }]);
assert.equal(addedVideo.muted, false, 'A late callback must not remute after tracking resumes.');

console.log('PASS: source video replacements remain muted and unmute disconnects the observer');

function video() {
  return {
    nodeType: 1,
    muted: false,
    matches: selector => selector === 'video',
    querySelectorAll: () => []
  };
}

function element(descendantVideos) {
  return {
    nodeType: 1,
    matches: () => false,
    querySelectorAll: selector => selector === 'video' ? descendantVideos : []
  };
}
