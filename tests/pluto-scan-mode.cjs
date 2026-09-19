const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const source = fs.readFileSync('PlutoDetectionScript.cs', 'utf8');
const script = source.match(/Script = """\r?\n([\s\S]*?)\r?\n\s*""";/)?.[1];
assert(script, 'Could not find the Pluto detection script.');

const rect = (left, top, width, height) => ({ left, top, width, height, right: left + width, bottom: top + height });
const body = {};
const container = element(rect(0, 0, 1000, 600), { parentElement: body });
const largeVideo = element(rect(0, 0, 1000, 600), { parentElement: container, closest: () => container });
const smallVideo = element(rect(700, 400, 200, 100), { parentElement: body, closest: () => null });
const ad = element(rect(8, 8, 90, 24), {
  parentElement: container,
  innerText: 'Ad 0:30',
  attributes: { 'aria-label': 'Advertisement countdown', role: 'status' }
});
const unrelated = Array.from({ length: 100 }, (_, index) =>
  element(rect(700, 400 + index, 10, 10), { parentElement: body, innerText: 'ordinary page text' }));
container.querySelectorAll = () => [ad];

const calls = [];
const videos = [smallVideo, largeVideo];
const document = {
  body,
  querySelectorAll(selector) {
    calls.push(selector);
    if (selector === 'video') return videos;
    if (selector === '*') return [container, largeVideo, smallVideo, ad, ...unrelated];
    return [ad];
  },
  elementFromPoint() { return ad; }
};
const detector = vm.runInNewContext(`(${script})`, {
  document,
  innerWidth: 1200,
  innerHeight: 800,
  getComputedStyle: () => ({ display: 'block', visibility: 'visible', opacity: '1' }),
  Set,
  JSON
});

const focused = JSON.parse(detector('focused'));
assert.equal(focused.isAd, true);
assert.equal(focused.hasPlayer, true);
assert.equal(focused.method, 'DOM');
assert.equal(focused.width, 450);
assert.equal(focused.height, 180);
assert(!calls.includes('*'), 'Focused mode must not request every DOM element.');

calls.length = 0;
const full = JSON.parse(detector('full'));
assert.equal(full.isAd, true);
assert(calls.includes('*'), 'Full mode must preserve the whole-document scan.');

videos.length = 0;
const noPlayer = JSON.parse(detector('focused'));
assert.equal(noPlayer.isAd, false);
assert.equal(noPlayer.hasPlayer, false);
console.log('PASS: focused mode avoids whole-DOM iteration and full mode preserves it');

function element(box, options = {}) {
  const attributes = options.attributes || {};
  return {
    id: options.id || '',
    className: options.className || '',
    innerText: options.innerText || '',
    textContent: options.textContent || '',
    parentElement: options.parentElement || null,
    getBoundingClientRect: () => box,
    getAttribute: name => attributes[name] || '',
    querySelectorAll: options.querySelectorAll,
    closest: options.closest || (() => null)
  };
}
