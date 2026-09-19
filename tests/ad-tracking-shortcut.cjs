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
const context = {
  addEventListener(type, callback, capture) {
    if (type === 'keydown') { assert.equal(capture, true); listener = callback; }
    if (type === 'message') messageListener = callback;
  },
  requestAdTrackingToggle() { toggles++; },
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
assert.equal(iframePrevented, true);
assert.deepEqual(messages, [
  { message: 'pluto-ad-detector:skip-youtube-video', target: '*' },
  { message: 'pluto-ad-detector:show-youtube-help', target: '*' },
  { message: 'pluto-ad-detector:show-youtube-help', target: '*' },
  { message: 'pluto-ad-detector:reload-youtube-queue', target: '*' },
  { message: 'pluto-ad-detector:train-visual-signature', target: '*' }
]);
messageListener({ data: 'pluto-ad-detector:train-visual-signature' });
assert.equal(trainings, 2);

vm.runInNewContext(script, context);
assert.equal(context.__plutoAdTrackingShortcutInstalled, true);
console.log('PASS: browser-wide P/T actions and iframe N/H/?/R/T forwarding');
