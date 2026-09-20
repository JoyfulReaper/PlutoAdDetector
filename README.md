# PlutoAdDetector

A .NET 10 proof of concept that watches a streaming page for Pluto's visible ad
indicator and uses ad breaks to switch to a local YouTube IFrame player.

> Yes, this is AI-assisted slop. Unfortunately, it works.

Pluto TV is the default source. When an ad starts, the app mutes the source,
brings YouTube forward, and resumes it. When the ad ends, it pauses YouTube,
returns to the source, and unmutes it. Detection is debounced; normal state
changes look like:

```text
ad started [DOM]
ad ended [DOM]
blocked segment started [VISUAL:<signature name>]
blocked segment ended [VISUAL:timeout]
ad tracking paused
ad tracking resumed
```

## Run it

Requirements: .NET 10 and either installed Google Chrome or Playwright Chromium.

```powershell
dotnet restore
dotnet build
pwsh .\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet run
```

The browser is headed by default. Installed Google Chrome is preferred; the
Playwright browser is the fallback. The app uses a dedicated persistent profile
at `browser-profile/`, never your normal Chrome profile. Headed mode uses the real
window size and remains available for normal interaction.

If Pluto or another source does not fill the display and Chrome's normal browser
controls remain visible, press `F11` to toggle Chrome's browser fullscreen as a
workaround. This is a Chrome shortcut, not a PlutoAdDetector command; press `F11`
again to leave fullscreen.

Useful examples:

```powershell
# Restore the last compatible automatic queue
dotnet run -- --resume

# Use another channel and a 10-minute minimum
dotnet run -- --channel-url "https://www.youtube.com/@SomeChannel" --min-duration-seconds 600

# Use exactly one video instead of channel discovery
dotnet run -- --youtube-url "https://www.youtube.com/watch?v=M7lc1UVf-VE"

# Open another streaming service for manual experimentation
dotnet run -- --url "https://example.com/stream"

# Debug with the original whole-document detector
dotnet run -- --scan-mode full

# Start with automatic learned visual sampling disabled
dotnet run -- --no-visual
```

## YouTube modes

### Automatic channel queue (default)

The default channel is `https://www.youtube.com/@MeidasTouch`. No YouTube API key
is required.

- Discovery tries the channel's public RSS feed first. It also reads the channel
  `/videos` page for live/upcoming metadata. If RSS fails or cannot be parsed, the
  `/videos` page and its embedded `ytInitialData` become the discovery fallback.
- Custom `--channel-url` values may be an `@handle` or `/channel/UC...` URL.
- Automatic mode rejects current live streams, scheduled/upcoming streams,
  unfinished premieres, and videos identified as live-stream recordings. A
  completed premiere without live/upcoming markers may remain eligible.
- A separate muted IFrame probes duration. Videos shorter than
  `--min-duration-seconds` (default: 300), unavailable videos, and metadata
  timeouts are skipped and logged.
- The queue keeps at most 20 videos. The current video is preserved across
  refreshes; duplicates and IDs completed or skipped during this run are omitted.
- Discovery runs at startup unless a valid `--resume` snapshot is loaded. Later
  refreshes happen only when five or fewer videos remain. Every attempt, including
  a failure, starts a five-minute cooldown. A failed discovery retains the queue.
- When a queued video ends, the next video becomes current. It plays immediately
  only if YouTube is currently supposed to be playing.

### Single-video override

`--youtube-url` uses only the explicitly selected video. Watch, `youtu.be`, embed,
Shorts, and live URLs with a valid 11-character video ID are accepted.
The video must allow playback in an embedded YouTube player; videos with embedding
disabled by the owner will not work with the local hosted player page.

Single-video mode bypasses channel/RSS discovery, automatic live/upcoming checks,
duration filtering, queue advancement, manual skipping, and queue save/restore.
The chosen video pauses and resumes across detected ad breaks without being
reloaded. An explicitly chosen live or short video is therefore allowed.

Do not explicitly combine `--youtube-url` with `--channel-url`; that is a CLI
error.

## Queue save and restore

In automatic mode, the app caches a queue snapshot approximately every 15 seconds.
A normal exit or Ctrl+C first requests a fresh snapshot, then falls back to the
last cached state if the browser or player page has already closed. The result is
saved best-effort to `youtube-queue.json`. Version 1 stores:

- save time, channel URL, and minimum-duration setting;
- current video ID/title and playback position when available;
- the ordered queue with titles, IDs, and durations; and
- completed/skipped IDs for the current run.

Use `--resume` to load the file at startup. The saved channel and minimum duration
are compatibility checks only; they never override current CLI options. A restore
is ignored and logged when the file is missing, invalid, over seven days old,
version-incompatible, or mismatched with the current channel/minimum duration.
A successful startup restore skips initial discovery and resumes normal low-queue
refresh behavior afterward.

Single-video mode does not read or write this file. Save and restore failures are
logged and do not stop shutdown or normal application startup.

## Learned visual training

Press `T` while the source tab has focus to record about six seconds from the
largest visible source `<video>`. Training samples the decoded video directly,
normalizes each frame in memory, and stores an ordered sequence of compact
perceptual fingerprints in `visual-signatures.json`. It does not save training
screenshots, and DOM overlays above the video are intentionally excluded. Only
one training session runs at a time.

These signatures are not an ML model and do not classify arbitrary concepts.
They are best suited to recurring identical or visually similar promos, bumpers,
repeated ads, PSAs, station cards, and similar repeated segments. If the segment
changes materially, it may need to be taught again.

When signatures exist, automatic matching samples decoded source-video frames
directly at a modest rate through the same normalization and fingerprinting path
used for training. The matcher requires several ordered similar frames and
tolerates sampling jitter, skipped reference frames, and isolated mismatches. A
confirmed match mutes and hides the source, brings YouTube forward, and resumes
it. The visual block then waits about 30 seconds for Pluto's normal DOM ad
detector. If the DOM ad is confirmed, it takes over seamlessly without repeating
the mute, tab, or playback operations. If no DOM ad follows, the visual timeout
pauses YouTube, restores the source, and unmutes it.

Set the `PLUTO_VISUAL_DEBUG` environment variable to `1` for concise near-match
diagnostics containing the global and expected-forward closest reference frames,
Hamming distances, threshold, progression/miss counts, and the reason alignment
advanced, stalled, restarted, or reset. Normal output does not include these
per-sample diagnostics.

Press `V` to toggle automatic runtime visual sampling for the current run, or use
`--no-visual` to start with it disabled. DOM ad detection continues normally,
and `T` training remains available because it is an explicit action. Disabling
automatic matching does not delete learned signatures or interrupt an already
active visual block; that block still ends through DOM handoff or its timeout.

Automatic sampling can occasionally cause mild screen flashing or display
disturbance on some Chrome/GPU/video-composition configurations. It does not
necessarily happen on every 500 ms sample, and severity varies by system. This is
a proof-of-concept limitation; press `V` or start with `--no-visual` if it is
distracting. Ordinary DOM detection remains available.

Older incompatible signature versions are skipped and must be retrained. If
browser security, cross-origin media, or DRM prevents canvas pixel access,
learned visual sampling logs one error and disables itself for that run; it does
not fall back to continuous screenshots.

## Keyboard shortcuts

| Key | Action |
| --- | --- |
| `N` | Skip the current automatic-queue video, mark it completed for this run, and select the next video. Unavailable in single-video mode. |
| `P` | Toggle ad tracking. Pausing also pauses YouTube, foregrounds/unmutes the source, and suppresses switching while the detector loop stays alive. Resuming clears debounce state and samples fresh. |
| `R` | Reload `youtube-queue.json` in automatic mode. Allowed only while tracking is paused or YouTube is foregrounded. The restored video keeps the player's current playing/paused state. |
| `T` | Teach a visual signature from a short sequence of the source player's full bounds. Available while the source tab has focus. |
| `V` | Toggle automatic learned visual sampling and matching. DOM detection and explicit `T` training remain available. |
| `X` | Force the source back immediately, unmute it, pause YouTube, and reset transient detector state. Detection resumes only after the configured number of consecutive clean DOM samples. |
| `H` or `?` | Briefly show keyboard help over the local YouTube page. |

`N`, `R`, `V`, `X`, and help work from the local player page, including when
focus is inside the YouTube iframe. `P`, `V`, and `X` work from either browser
tab, while `T` is source-tab specific. Queue refreshes continue while ad tracking
is paused; pausing or resetting the detector does not clear or reorder the queue.

`P`, `V`, and `X` intentionally have different scopes: `P` pauses or resumes
all tracking, `V` toggles only automatic learned visual matching, and `X` is an
immediate recovery action when the current result is wrong. `X` clears transient
DOM and visual state, pauses YouTube, restores/unmutes the source, and waits for a
clean baseline before re-arming. It does not delete learned visual signatures or
the YouTube queue.

## Detection and source switching

The default `focused` scan finds the largest visible video and inspects targeted
text, ARIA/title/role/test-ID candidates plus a bounded hit-test grid in the
player's upper-left region. It does not iterate over every DOM element every poll.

`--scan-mode full` enumerates the whole DOM and evaluates visible candidates
across the viewport, while ignoring enormous page-wide wrappers. It is intended
as a debugging fallback. Both modes return the same `[DOM]` detection result
shape and use the same debounce settings (500 ms polling and two confirming
samples by default).

`--url` accepts any absolute HTTP or HTTPS source/streaming page. Muting and tab
switching remain available for experimentation, but the detector profile is
Pluto-specific. A non-Pluto URL produces a warning; this project does not claim
automatic compatibility with other services.

Until a semantic ad indicator has been observed, the app periodically saves the
largest player's upper-left crop under `captures/`. These images are diagnostics
for DOM-detector troubleshooting. They are not used by learned visual matching,
which samples decoded `<video>` frames directly.

## CLI options

| Option | Behavior |
| --- | --- |
| `--headless` | Hide Chromium. Headed is the default. |
| `--url <url>` | Source page; absolute HTTP/HTTPS only. Default: `https://pluto.tv/live-tv`. |
| `--channel-url <url>` | Automatic queue channel. Default: MeidasTouch. |
| `--youtube-url <url>` | Single-video override; mutually exclusive with an explicitly supplied `--channel-url`. |
| `--min-duration-seconds <n>` | Automatic queue minimum. Default: `300`. |
| `--resume` | Restore a compatible automatic queue from `youtube-queue.json`. |
| `--no-visual` | Start with automatic learned visual sampling disabled. Press `V` to enable it for the current run. |
| `--scan-mode focused\|full` | DOM scan scope. Default: `focused`. |
| `--poll-ms <n>` | Detector polling interval. Default: `500`. |
| `--confirm <n>` | Consecutive samples required for a transition. Default: `2`. |
| `--captures <directory>` | Diagnostic crop directory. Default: `captures`. |
| `--capture-seconds <n>` | Seconds between diagnostic crops. Default: `30`. |
| `--help`, `-h` | Print CLI help. |

## Current limitations

- Pluto can change its DOM at any time; focused and full scans are heuristic.
- Learned visual matching can false-positive or false-negative. It recognizes
  trained recurring sequences rather than arbitrary concepts, and changed promos
  may require retraining. DOM/accessibility detection remains authoritative when
  a normal Pluto ad indicator appears.
- Protected, cross-origin, or DRM-controlled media may prevent direct video-pixel
  sampling and disable learned visual matching for that run.
- Automatic visual sampling can cause occasional mild flashing or display
  disturbance depending on Chrome, the GPU, and video composition. It is not
  necessarily tied visibly to every sample. `V` or `--no-visual` disables that
  sampling without disabling DOM detection.
- YouTube videos can fail because embedding is disabled, the video is unavailable,
  or the IFrame API rejects the playback client. Those errors are logged.
- Live/upcoming filtering depends on metadata exposed by YouTube's channel page.
- Queue state is local, versioned, and intentionally limited to automatic mode.
- Other streaming sites are experimental: source muting and tab switching may
  work, but the built-in DOM detector profile is Pluto-specific.
