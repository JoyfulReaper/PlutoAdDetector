const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const source = fs.readFileSync('AdTrackingShortcut.cs', 'utf8');
const script = source.match(/Script = """\r?\n([\s\S]*?)\r?\n\s*""";/)?.[1];
assert(script, 'Could not find the ad-tracking shortcut script.');

let listener;
let toggles = 0;
const context = {
  addEventListener(type, callback, capture) {
    assert.equal(type, 'keydown');
    assert.equal(capture, true);
    listener = callback;
  },
  requestAdTrackingToggle() { toggles++; }
};
context.globalThis = context;
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
assert.equal(press({ code: 'KeyN' }), false);
assert.equal(press({ repeat: true }), false);
assert.equal(press({ ctrlKey: true }), false);
assert.equal(toggles, 1);

vm.runInNewContext(script, context);
assert.equal(context.__plutoAdTrackingShortcutInstalled, true);
console.log('PASS: browser-wide P shortcut requests one tracking toggle and ignores repeats/modifiers');
