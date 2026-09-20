const assert = require('node:assert/strict');
const fs = require('node:fs');

const source = fs.readFileSync('Program.cs', 'utf8');

const resetHandling = source.indexOf('if (Interlocked.Exchange(ref detectorResetRequests, 0) > 0)');
const runtimeCompletion = source.indexOf('if (visualRuntimeCapture?.IsCompleted is true)');
assert(resetHandling >= 0 && resetHandling < runtimeCompletion);
const resetBlock = source.slice(resetHandling, runtimeCompletion);
for (const required of [
  'visualGeneration++',
  'visualTrainingCancellation?.Cancel()',
  'visualBlockedState = null',
  'pendingVisualMatch = null',
  'publishedState = null',
  'pendingState = null',
  'pendingCount = 0',
  'visualMatcher.ResetProgressions()',
  'youtubePlaybackExpected = false',
  'PauseYoutubeAsync(youtubePage)',
  'sourcePage.BringToFrontAsync()',
  'SetSourceMutedAsync(sourcePage, muted: false)',
  'recoveryBaseline.Begin()',
  'manual reset: source restored',
  'detector waiting for clean baseline'
]) assert(resetBlock.includes(required), `X reset block missing ${required}`);

const runtimeSection = source.slice(runtimeCompletion, source.indexOf('if (visualTraining?.IsCompleted is true)'));
assert(runtimeSection.includes('completedCaptureGeneration != visualGeneration'));
assert(runtimeSection.includes('Volatile.Read(ref detectorResetRequests) > 0'));
const trainingSection = source.slice(
  source.indexOf('if (visualTraining?.IsCompleted is true)'),
  source.indexOf('if (Interlocked.Exchange(ref visualTrainingRequests, 0) > 0)'));
assert(trainingSection.includes('completedTrainingGeneration != visualGeneration'));
assert(trainingSection.includes('visualTrainingCancellation?.IsCancellationRequested'));

const captureCompletion = source.indexOf('pendingVisualMatch ??= match;');
const toggleHandling = source.indexOf('var toggleCount = Interlocked.Exchange(ref trackingToggleRequests, 0);');
const pendingHandling = source.indexOf('if (pendingVisualMatch is not null &&');
const domSample = source.indexOf('var sample = await DetectAsync(sourcePage, options.ScanMode);');
const timeoutHandling = source.indexOf('if (VisualBlockPolicy.ShouldTimeout(');
assert(captureCompletion >= 0 && captureCompletion < toggleHandling);
assert(toggleHandling < pendingHandling);
assert(pendingHandling < domSample);
assert(domSample < timeoutHandling);
const pendingSection = source.slice(pendingHandling, source.indexOf('if (Interlocked.Exchange(ref queueRestoreRequests, 0) > 0)'));
assert(pendingSection.includes('Volatile.Read(ref trackingToggleRequests) == 0'));
assert(pendingSection.includes('Volatile.Read(ref detectorResetRequests) == 0'));

const recoveryHandling = source.indexOf('if (recoveryBaseline.IsAwaiting)', domSample);
const ordinaryDomHandling = source.indexOf('everFoundSemanticIndicator |= sample.IsAd;', domSample);
assert(domSample < recoveryHandling && recoveryHandling < ordinaryDomHandling);
const postDetectionGuard = source.slice(domSample, recoveryHandling);
assert(postDetectionGuard.includes('Volatile.Read(ref trackingToggleRequests) > 0'));
assert(postDetectionGuard.includes('Volatile.Read(ref detectorResetRequests) > 0'));
const recoveryBlock = source.slice(recoveryHandling, ordinaryDomHandling);
assert(recoveryBlock.includes('recoveryBaseline.Observe(sample.IsAd, options.ConfirmationSamples)'));
assert(recoveryBlock.includes('detector re-armed'));
assert(recoveryBlock.includes('continue;'));

const scheduleStart = source.indexOf('if (learnedVisualSignatures.Count > 0 &&');
const scheduleEnd = source.indexOf('visualRuntimeCapture = VisualFrameSampler.CaptureAsync(sourcePage);', scheduleStart);
const schedule = source.slice(scheduleStart, scheduleEnd);
assert(schedule.includes('publishedState is not true'));
assert(schedule.includes('visualBlockedState is null'));
assert(schedule.includes('pendingVisualMatch is null'));

const pauseStart = source.indexOf('if (trackingPaused)');
const pauseEnd = source.indexOf('Console.WriteLine("ad tracking paused");', pauseStart);
const pause = source.slice(pauseStart, pauseEnd);
assert(pause.includes('visualBlockedState = null;'));
assert(pause.includes('pendingVisualMatch = null;'));
assert(pause.includes('PauseYoutubeAsync(youtubePage)'));
assert(pause.includes('sourcePage.BringToFrontAsync()'));
assert(pause.includes('SetSourceMutedAsync(sourcePage, muted: false)'));

const visualStart = source.slice(
  source.indexOf('if (start.Started)'),
  source.indexOf('if (Interlocked.Exchange(ref queueRestoreRequests, 0) > 0)'));
assert(visualStart.includes('SetSourceMutedAsync(sourcePage, muted: true)'));
assert(visualStart.includes('youtubePage.BringToFrontAsync()'));
assert(visualStart.includes('youtubePlaybackExpected = true'));
assert(visualStart.includes('ResumeYoutubeAsync(youtubePage)'));

const domStart = source.indexOf('if (sample.IsAd)');
const domEnd = source.indexOf('else if (publishedState is true)', domStart);
const domStartBlock = source.slice(domStart, domEnd);
const switchGuard = domStartBlock.indexOf('if (requiresDomStartSwitch)');
assert(switchGuard >= 0);
for (const operation of [
  'SetSourceMutedAsync(sourcePage, muted: true)',
  'youtubePage.BringToFrontAsync()',
  'ResumeYoutubeAsync(youtubePage)'
]) {
  assert(domStartBlock.indexOf(operation) > switchGuard);
  assert.equal(domStartBlock.indexOf(operation), domStartBlock.lastIndexOf(operation));
}

const domEndBlock = source.slice(domEnd, timeoutHandling);
assert(domEndBlock.includes('youtubePlaybackExpected = false'));
assert(domEndBlock.includes('PauseYoutubeAsync(youtubePage)'));
assert(domEndBlock.includes('sourcePage.BringToFrontAsync()'));
assert(domEndBlock.includes('SetSourceMutedAsync(sourcePage, muted: false)'));

const timeoutEnd = source.indexOf('if (!everFoundSemanticIndicator', timeoutHandling);
const timeoutBlock = source.slice(timeoutHandling, timeoutEnd);
assert(timeoutBlock.includes('youtubePlaybackExpected = false'));
assert(timeoutBlock.includes('PauseYoutubeAsync(youtubePage)'));
assert(timeoutBlock.includes('sourcePage.BringToFrontAsync()'));
assert(timeoutBlock.includes('SetSourceMutedAsync(sourcePage, muted: false)'));
assert(timeoutBlock.includes('pendingState = null'));
assert(timeoutBlock.includes('pendingCount = 0'));

console.log('PASS: visual block ordering plus X reset, stale-work guards, clean-baseline hold, and seamless handoff');
