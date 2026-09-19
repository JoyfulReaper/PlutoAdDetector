# PlutoAdDetector

A .NET 10 proof of concept that opens Pluto TV in Chromium and watches the
upper-left portion of the video player for a visible ad indicator. It prefers
visible DOM text and accessibility-related attributes (`aria-label`, `title`,
`role`, and test IDs). Until such an indicator has been observed, it periodically
saves an upper-left player crop under `captures/` for later visual-detector work.

Standard output contains only state changes:

```text
ad started
ad ended
```

## Setup and run

```powershell
dotnet restore
dotnet build
pwsh .\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet run
```

Chromium is headed by default. Use `dotnet run -- --headless` to hide it. Run
`dotnet run -- --help` for polling, confirmation, URL, and capture options.

This proof of concept intentionally does not implement YouTube switching.
