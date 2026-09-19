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
tab opens a loopback-hosted local page that embeds the configurable video through
the YouTube IFrame Player API. When an ad starts, Pluto is muted and the embedded
player is brought forward and resumed. When the ad ends, it is paused and Pluto
is brought forward and unmuted. YouTube IFrame API errors are logged after
playback starts.

Use `dotnet run -- --headless` to hide Chromium. Run `dotnet run -- --help` for
polling, confirmation, URL, YouTube URL, and capture options.

The test video can be changed with `--youtube-url <url>`.
