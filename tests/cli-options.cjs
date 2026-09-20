const { spawnSync } = require('node:child_process');
const assert = require('node:assert/strict');
const run = args => spawnSync('dotnet', ['bin/Release/net10.0/PlutoAdDetector.dll', ...args], { encoding: 'utf8', timeout: 10000 });
const video = 'https://www.youtube.com/watch?v=M7lc1UVf-VE';
const channel = 'https://www.youtube.com/@MeidasTouch';
for (const url of [video, 'https://youtu.be/M7lc1UVf-VE?t=10', 'https://www.youtube.com/embed/M7lc1UVf-VE', 'https://www.youtube.com/shorts/M7lc1UVf-VE']) {
  assert.equal(run(['--youtube-url', url, '--help']).status, 0);
}
assert.equal(run(['--help']).status, 0);
assert.match(run(['--help']).stderr, /--scan-mode focused\|full/);
assert.equal(run(['--scan-mode', 'focused', '--help']).status, 0);
assert.equal(run(['--scan-mode', 'full', '--help']).status, 0);
assert.equal(run(['--resume', '--help']).status, 0);
assert.match(run(['--help']).stderr, /--resume/);
assert.equal(run(['--no-visual', '--help']).status, 0);
assert.match(run(['--help']).stderr, /--no-visual/);
assert.equal(run(['--url', 'https://example.com/stream', '--help']).status, 0);
assert.equal(run(['--channel-url', channel, '--help']).status, 0);
for (const args of [
  ['--youtube-url', video, '--channel-url', channel],
  ['--channel-url', channel, '--youtube-url', video],
  ['--youtube-url', 'https://example.com/watch?v=M7lc1UVf-VE'],
  ['--youtube-url', 'https://youtube.com/watch?v=bad'],
  ['--youtube-url'],
  ['--scan-mode', 'wide'],
  ['--scan-mode'],
  ['--url', 'ftp://example.com/stream'],
  ['--url', '/relative/stream'],
  ['--channel-url', 'https://www.youtube.com/@MeidasTouch/videos'],
  ['--channel-url', 'https://www.youtube.com/channel/not-a-channel-id']
]) assert.equal(run([...args, ...(args.length === 1 ? [] : ['--help'])]).status, 2);
console.log('PASS: source URL compatibility/validation, scan modes, YouTube URL formats, missing values, and explicit-channel conflicts');
