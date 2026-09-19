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
than an emulated viewport. A second tab preloads a test YouTube video. When an ad
starts, Pluto is muted and YouTube is brought forward and resumed. When the ad
ends, YouTube is paused and Pluto is brought forward and unmuted.

Use `dotnet run -- --headless` to hide Chromium. Run `dotnet run -- --help` for
polling, confirmation, URL, YouTube URL, and capture options.

The test video can be changed with `--youtube-url <url>`.
