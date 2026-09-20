const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const source = fs.readFileSync('VisualFrameSampler.cs', 'utf8');
const script = source.match(/DirectVideoSampleScript = """\r?\n([\s\S]*?)\r?\n\s*""";/)?.[1];
assert(script, 'Could not find the direct video sample script.');
assert(!source.includes('ScreenshotAsync'), 'Normal visual sampling must not use Playwright screenshots.');

const rect = (left, top, width, height) =>
  ({ left, top, width, height, right: left + width, bottom: top + height });
const video = (box, width, height, readyState = 4) => ({
  readyState,
  videoWidth: width,
  videoHeight: height,
  getBoundingClientRect: () => box,
  getAttribute: () => '',
  checkVisibility: () => true
});

const smallVideo = video(rect(700, 400, 200, 100), 640, 360);
const largeVideo = video(rect(-100, -50, 1400, 900), 1920, 1080);
const videos = [smallVideo, largeVideo];
let drawnVideo;
let pixelReadError;

class FakeCanvas {
  getContext() {
    return {
      drawImage(candidate, x, y, width, height) {
        drawnVideo = candidate;
        assert.deepEqual([x, y, width, height], [0, 0, 9, 8]);
      },
      getImageData() {
        if (pixelReadError) throw pixelReadError;
        const data = new Uint8ClampedArray(9 * 8 * 4);
        for (let pixel = 0; pixel < 72; pixel++) {
          data[pixel * 4] = pixel;
          data[(pixel * 4) + 1] = pixel;
          data[(pixel * 4) + 2] = pixel;
          data[(pixel * 4) + 3] = 255;
        }
        return { data };
      }
    };
  }
}

const document = {
  querySelectorAll: selector => selector === 'video' ? videos : [],
  createElement: () => new FakeCanvas()
};
const sample = vm.runInNewContext(`(${script})`, {
  document,
  innerWidth: 1200,
  innerHeight: 800,
  getComputedStyle: () => ({ display: 'block', visibility: 'visible', opacity: '1' }),
  OffscreenCanvas: FakeCanvas,
  Uint8ClampedArray,
  JSON,
  Number,
  Math
});

const success = JSON.parse(sample());
assert.equal(success.status, 'ok');
assert.equal(drawnVideo, largeVideo, 'The largest visible video must be sampled.');
assert.deepEqual(success.playerBounds, { x: 0, y: 0, width: 1200, height: 800 });
assert.equal(success.luminance.length, 72);
assert.deepEqual(success.luminance.slice(0, 4), [0, 1, 2, 3]);

pixelReadError = Object.assign(new Error('canvas is tainted'), { name: 'SecurityError' });
const blocked = JSON.parse(sample());
assert.equal(blocked.status, 'unsupported');
assert.equal(blocked.errorName, 'SecurityError');
assert.equal(blocked.error, 'canvas is tainted');

pixelReadError = null;
largeVideo.readyState = 1;
const unavailable = JSON.parse(sample());
assert.equal(unavailable.status, 'unavailable');

console.log('PASS: direct largest-video sampling, normalized pixels, clipped bounds, and SecurityError reporting');
