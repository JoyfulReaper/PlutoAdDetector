const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const source = fs.readFileSync('AdTrackingShortcut.cs', 'utf8');
const script = source.match(/Script = """\r?\n([\s\S]*?)\r?\n\s*""";/)?.[1];
assert(script, 'Could not find the ad-tracking shortcut script.');

let listener;
let messageListener;
let toggles = 0;
let trainings = 0;
let resets = 0;
let visualToggles = 0;
const context = {
  addEventListener(type, callback, capture) {
    if (type === 'keydown') { assert.equal(capture, true); listener = callback; }
    if (type === 'message') messageListener = callback;
  },
  requestAdTrackingToggle() { toggles++; },
  requestDetectorReset() { resets++; },
  requestVisualMatchingToggle() { visualToggles++; },
  requestVisualSignatureTraining() { trainings++; }
};
context.globalThis = context;
context.top = context;
vm.runInNewContext(script, context);
assert(listener, 'The P shortcut listener was not installed.');

const press = overrides => {
  let prevented = false;
  listener({ code: 'KeyP', repeat: false, ctrlKey: false, altKey: false, metaKey: false,
    preventDefault() { prevented = true; }, ...overrides });
  return prevented;
};

assert.equal(press({}), true);
assert.equal(toggles, 1);
assert.equal(press({ code: 'KeyT' }), true);
assert.equal(trainings, 1);
assert.equal(press({ code: 'KeyX' }), true);
assert.equal(resets, 1);
assert.equal(press({ code: 'KeyV' }), true);
assert.equal(visualToggles, 1);
assert.equal(press({ code: 'KeyN' }), false);
assert.equal(press({ repeat: true }), false);
assert.equal(press({ ctrlKey: true }), false);
assert.equal(toggles, 1);

let iframeListener;
const messages = [];
const iframeContext = {
  addEventListener(type, callback) { if (type === 'keydown') iframeListener = callback; },
  requestAdTrackingToggle() {},
  top: { postMessage: (message, target) => messages.push({ message, target }) }
};
iframeContext.globalThis = iframeContext;
vm.runInNewContext(script, iframeContext);
let iframePrevented = false;
iframeListener({ code: 'KeyN', key: 'n', repeat: false, ctrlKey: false, altKey: false, metaKey: false,
  preventDefault() { iframePrevented = true; } });
iframeListener({ code: 'KeyH', key: 'h', repeat: false, ctrlKey: false, altKey: false, metaKey: false,
  preventDefault() { iframePrevented = true; } });
iframeListener({ code: 'Slash', key: '?', repeat: false, ctrlKey: false, altKey: false, metaKey: false,
  preventDefault() {} });
iframeListener({ code: 'KeyR', key: 'r', repeat: false, ctrlKey: false, altKey: false, metaKey: false,
  preventDefault() {} });
iframeListener({ code: 'KeyT', key: 't', repeat: false, ctrlKey: false, altKey: false, metaKey: false,
  preventDefault() {} });
iframeListener({ code: 'KeyX', key: 'x', repeat: false, ctrlKey: false, altKey: false, metaKey: false,
  preventDefault() {} });
iframeListener({ code: 'KeyV', key: 'v', repeat: false, ctrlKey: false, altKey: false, metaKey: false,
  preventDefault() {} });
assert.equal(iframePrevented, true);
assert.deepEqual(messages, [
  { message: 'pluto-ad-detector:skip-youtube-video', target: '*' },
  { message: 'pluto-ad-detector:show-youtube-help', target: '*' },
  { message: 'pluto-ad-detector:show-youtube-help', target: '*' },
  { message: 'pluto-ad-detector:reload-youtube-queue', target: '*' },
  { message: 'pluto-ad-detector:train-visual-signature', target: '*' },
  { message: 'pluto-ad-detector:force-source-reset', target: '*' },
  { message: 'pluto-ad-detector:toggle-visual-matching', target: '*' }
]);
messageListener({ data: 'pluto-ad-detector:train-visual-signature' });
assert.equal(trainings, 2);
messageListener({ data: 'pluto-ad-detector:force-source-reset' });
assert.equal(resets, 2);
messageListener({ data: 'pluto-ad-detector:toggle-visual-matching' });
assert.equal(visualToggles, 2);

vm.runInNewContext(script, context);
assert.equal(context.__plutoAdTrackingShortcutInstalled, true);
console.log('PASS: browser-wide P/T/V/X actions and iframe N/H/?/R/T/V/X forwarding');
