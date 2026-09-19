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
const outsideFocusedRegionAd = element(rect(700, 50, 120, 24), {
  parentElement: container,
  innerText: 'Advertisement 0:30',
  attributes: { role: 'status' }
});
const enormousWrapper = element(rect(0, 0, 1200, 800), {
  parentElement: body,
  innerText: 'Advertisement'
});
let hiddenVisibilityOptions;
const hiddenByAncestorAd = element(rect(8, 8, 90, 24), {
  parentElement: container,
  innerText: 'Advertisement',
  checkVisibility: options => {
    hiddenVisibilityOptions = options;
    return false;
  }
});
const unrelated = Array.from({ length: 100 }, (_, index) =>
  element(rect(700, 400 + index, 10, 10), { parentElement: body, innerText: 'ordinary page text' }));
let semanticCandidates = [ad];
let allCandidates = [container, largeVideo, smallVideo, ad, ...unrelated];
let hitElement = ad;
container.querySelectorAll = () => semanticCandidates;

const calls = [];
const videos = [smallVideo, largeVideo];
const document = {
  body,
  documentElement: {},
  querySelectorAll(selector) {
    calls.push(selector);
    if (selector === 'video') return videos;
    if (selector === '*') return allCandidates;
    return semanticCandidates;
  },
  elementFromPoint() { return hitElement; }
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
assert.equal(full.width, 450, 'Full mode must retain player-region diagnostic crop coordinates.');
assert.equal(full.height, 180, 'Full mode must retain player-region diagnostic crop coordinates.');

semanticCandidates = [outsideFocusedRegionAd];
allCandidates = [container, largeVideo, smallVideo, outsideFocusedRegionAd, ...unrelated];
hitElement = largeVideo;
calls.length = 0;
const focusedOutside = JSON.parse(detector('focused'));
assert.equal(focusedOutside.isAd, false, 'Focused mode must ignore candidates outside the player upper-left region.');
const fullOutside = JSON.parse(detector('full'));
assert.equal(fullOutside.isAd, true, 'Full mode must inspect candidates across the visible viewport.');

semanticCandidates = [];
allCandidates = [enormousWrapper];
const fullWrapperOnly = JSON.parse(detector('full'));
assert.equal(fullWrapperOnly.isAd, false, 'Full mode must ignore enormous application-wide wrappers.');

semanticCandidates = [hiddenByAncestorAd];
allCandidates = [hiddenByAncestorAd];
const focusedHiddenAncestor = JSON.parse(detector('focused'));
const fullHiddenAncestor = JSON.parse(detector('full'));
assert.equal(focusedHiddenAncestor.isAd, false, 'Focused mode must ignore an indicator hidden by an ancestor.');
assert.equal(fullHiddenAncestor.isAd, false, 'Full mode must ignore an indicator hidden by an ancestor.');
assert.equal(hiddenVisibilityOptions.checkOpacity, true);
assert.equal(hiddenVisibilityOptions.opacityProperty, true);
assert.equal(hiddenVisibilityOptions.checkVisibilityCSS, true);
assert.equal(hiddenVisibilityOptions.visibilityProperty, true);
assert.equal(hiddenVisibilityOptions.contentVisibilityAuto, true);

videos.length = 0;
const noPlayer = JSON.parse(detector('focused'));
assert.equal(noPlayer.isAd, false);
assert.equal(noPlayer.hasPlayer, false);
console.log('PASS: scan bounds, enormous-wrapper filtering, and effective visibility checks');

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
    checkVisibility: options.checkVisibility,
    querySelectorAll: options.querySelectorAll,
    closest: options.closest || (() => null)
  };
}
