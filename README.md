# PlutoAdDetector

A .NET 10 proof of concept that opens Pluto TV in Chromium and watches the
upper-left portion of the video player for a visible ad indicator. It prefers
visible DOM text and accessibility-related attributes (`aria-label`, `title`,
`role`, and test IDs). Until such an indicator has been observed, it periodically
saves an upper-left player crop under `captures/` for later visual-detector work.

Standard output contains only state changes and the detector that caused them:

```text
ad started [DOM]
ad ended [DOM]
```

## Setup and run

```powershell
dotnet restore
dotnet build
pwsh .\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet run
```

Chromium is headed by default and uses the real browser-window dimensions rather
than an emulated viewport. When Google Chrome is installed, Playwright launches
that executable; otherwise it falls back to Playwright's bundled Chromium. It
uses a dedicated persistent profile under `browser-profile/`, so cookies and
browser state survive between runs without using or modifying the normal Chrome
profile. The headed browser remains available for normal interaction. A second
tab opens a loopback-hosted local page that embeds recent MeidasTouch uploads through
the YouTube IFrame Player API. When an ad starts, Pluto is muted and the embedded
player is brought forward and resumed. When the ad ends, it is paused and Pluto
is brought forward and unmuted. YouTube IFrame API errors are logged after
playback starts.

Use `dotnet run -- --headless` to hide Chromium. Run `dotnet run -- --help` for
polling, confirmation, Pluto URL, and capture options.

At startup, the public channel RSS feed supplies recent uploads (no API key).
If RSS returns an error or malformed content, discovery falls back to the channel's
`/videos` page and reads normal-video IDs and titles from its embedded
`ytInitialData`. Shorts-style renderers are ignored and channel-page order is
preserved. Logs identify the discovery source. If both methods fail during a
refresh, the existing queue remains intact.
The feed refreshes every five minutes without blocking Pluto detection. A separate
muted IFrame player checks durations without interrupting the current video.
The queue holds at most 20 valid videos, retaining the current video and ordering
waiting videos newest first. Duplicate IDs and videos completed during this run
are excluded. The current video and its playback position are preserved across
refreshes and ad breaks; queue state is in memory for the current run.
Press `N` while the local YouTube player page has keyboard focus to skip the
current automatic-queue video. The video is considered completed for the run,
removed from the queue, and the next video follows the current play/pause intent.
The same operation is available as `window.youtubePlayerControls.skip()`.
Press `P` in either browser tab to pause or resume ad tracking. Pausing stops ad
switching, pauses YouTube, brings Pluto forward, and unmutes it. Resuming clears
the detector's pending state and confirms the current state from fresh samples;
neither operation changes the video queue.
Automatic channel mode also uses channel-page renderer metadata to reject current
live streams, scheduled/upcoming streams, unfinished premieres, and stream
recordings before probing duration. Each rejection logs its title, ID, and reason.
Completed premieres without live/upcoming markers remain eligible. The duration
probe remains the second eligibility check. `--youtube-url` bypasses these
automatic restrictions, so an explicitly selected live URL is allowed.
Manual skipping is disabled in `--youtube-url` mode; `N` leaves the selected
video untouched and writes an explanatory queue log.
Unavailable videos and metadata timeouts are skipped. Logs include skipped titles,
durations (or “unknown”), and the queue after each successful refresh. RSS failures
retain the existing queue and retry at the next refresh.

Options: `--channel-url https://www.youtube.com/@MeidasTouch` and
`--min-duration-seconds 300` (the defaults). Channel URLs may use an @handle or
`/channel/UC...` ID. No API key is needed.
To use one video instead of the recent-upload queue:

```powershell
dotnet run -- --youtube-url "https://www.youtube.com/watch?v=M7lc1UVf-VE"
```

This override skips channel discovery, RSS refreshes, and duration filtering,
including for videos shorter than five minutes. The same video pauses and resumes
across Pluto ad breaks without reloading. It does not advance to another video
when it ends. Watch, youtu.be, embed, shorts, and live URLs are supported.
Do not explicitly supply both `--youtube-url` and `--channel-url`; this is a CLI
error even if the channel is MeidasTouch. Without either option, the default
MeidasTouch recent-video queue remains active.
