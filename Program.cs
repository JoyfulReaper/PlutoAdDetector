using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;

DetectorOptions options;
try
{
    options = DetectorOptions.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine(DetectorOptions.Usage);
    return 2;
}

if (options.ShowHelp)
{
    Console.Error.WriteLine(DetectorOptions.Usage);
    return 0;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

try
{
    await RunAsync(options, shutdown.Token);
    return 0;
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    return 0;
}
catch (PlaywrightException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static async Task RunAsync(DetectorOptions options, CancellationToken cancellationToken)
{
    Directory.CreateDirectory(options.CaptureDirectory);

    using var playwright = await Playwright.CreateAsync();
    var chromeExecutable = FindInstalledGoogleChrome();
    await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
    {
        Headless = options.Headless,
        ExecutablePath = chromeExecutable,
        Args = ["--autoplay-policy=no-user-gesture-required"]
    });
    Console.Error.WriteLine(chromeExecutable is null
        ? "browser: Playwright Chromium"
        : $"browser: Google Chrome ({chromeExecutable})");

    var context = await browser.NewContextAsync(new BrowserNewContextOptions
    {
        ViewportSize = options.Headless
            ? new ViewportSize { Width = 1440, Height = 900 }
            : ViewportSize.NoViewport,
        Locale = "en-US"
    });
    var plutoPage = await context.NewPageAsync();
    await plutoPage.GotoAsync(options.Url, new PageGotoOptions
    {
        WaitUntil = WaitUntilState.DOMContentLoaded,
        Timeout = 90_000
    });

    var youtubePage = await context.NewPageAsync();
    await youtubePage.GotoAsync(options.YoutubeUrl, new PageGotoOptions
    {
        WaitUntil = WaitUntilState.DOMContentLoaded,
        Timeout = 90_000
    });
    await PauseYoutubeAsync(youtubePage);
    await plutoPage.BringToFrontAsync();

    bool? publishedState = null;
    bool? pendingState = null;
    var activeDetectionMethod = "DOM";
    var pendingCount = 0;
    var everFoundSemanticIndicator = false;
    var nextCaptureAt = DateTimeOffset.UtcNow;
    var youtubePlaybackStarted = false;
    string? loggedYoutubeError = null;
    var nextYoutubeHealthCheckAt = DateTimeOffset.MinValue;

    while (!cancellationToken.IsCancellationRequested)
    {
        var sample = await DetectAsync(plutoPage);
        everFoundSemanticIndicator |= sample.IsAd;

        if (pendingState == sample.IsAd)
        {
            pendingCount++;
        }
        else
        {
            pendingState = sample.IsAd;
            pendingCount = 1;
        }

        if (pendingCount >= options.ConfirmationSamples && publishedState != sample.IsAd)
        {
            // A non-ad page load is baseline state, not an "ad ended" transition.
            if (sample.IsAd)
            {
                activeDetectionMethod = sample.Method;
                await SetPlutoMutedAsync(plutoPage, muted: true);
                await youtubePage.BringToFrontAsync();
                youtubePlaybackStarted = await ResumeYoutubeAsync(youtubePage);
                loggedYoutubeError = null;
                nextYoutubeHealthCheckAt = DateTimeOffset.UtcNow;
                Console.WriteLine($"ad started [{activeDetectionMethod}]");
                publishedState = true;
            }
            else if (publishedState is true)
            {
                await PauseYoutubeAsync(youtubePage);
                youtubePlaybackStarted = false;
                loggedYoutubeError = null;
                await plutoPage.BringToFrontAsync();
                await SetPlutoMutedAsync(plutoPage, muted: false);
                Console.WriteLine($"ad ended [{sample.Method}]");
                publishedState = false;
            }
        }

        if (!everFoundSemanticIndicator &&
            !sample.IsAd &&
            sample.HasPlayer &&
            DateTimeOffset.UtcNow >= nextCaptureAt)
        {
            await SaveDiagnosticCropAsync(plutoPage, sample, options.CaptureDirectory);
            nextCaptureAt = DateTimeOffset.UtcNow.Add(options.CaptureInterval);
        }

        if (youtubePlaybackStarted && DateTimeOffset.UtcNow >= nextYoutubeHealthCheckAt)
        {
            var youtubeError = await DetectYoutubeErrorAsync(youtubePage);
            if (youtubeError is not null && youtubeError != loggedYoutubeError)
            {
                Console.Error.WriteLine($"youtube player error: {youtubeError}");
            }

            loggedYoutubeError = youtubeError;
            nextYoutubeHealthCheckAt = DateTimeOffset.UtcNow.AddSeconds(2);
        }

        await Task.Delay(options.PollInterval, cancellationToken);
    }
}

static string? FindInstalledGoogleChrome()
{
    string[] candidates =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Google", "Chrome", "Application", "chrome.exe")
    ];

    return candidates.FirstOrDefault(File.Exists);
}

static async Task SetPlutoMutedAsync(IPage page, bool muted)
{
    await page.EvaluateAsync(
        "muted => document.querySelectorAll('video').forEach(video => video.muted = muted)",
        muted);
}

static async Task PauseYoutubeAsync(IPage page)
{
    var json = await page.EvaluateAsync<string>(
        """
        () => {
          const video = document.querySelector('video');
          if (!video) {
            return JSON.stringify({ success: false, error: 'YouTube video element was not found.' });
          }

          try {
            video.pause();
            return JSON.stringify({
              success: video.paused,
              error: video.paused ? null : 'The video did not enter the paused state.'
            });
          } catch (error) {
            return JSON.stringify({
              success: false,
              error: `${error?.name || 'Error'}: ${error?.message || String(error)}`
            });
          }
        }
        """);
    var result = JsonSerializer.Deserialize<YoutubeControlResult>(json, JsonOptions.Instance);
    Console.Error.WriteLine(result?.Success is true
        ? "youtube paused"
        : $"youtube pause failed: {result?.Error ?? "Unknown error."}");
}

static async Task<bool> ResumeYoutubeAsync(IPage page)
{
    var json = await page.EvaluateAsync<string>(
        """
        async () => {
          const video = document.querySelector('video');
          if (!video) {
            return JSON.stringify({ success: false, error: 'YouTube video element was not found.' });
          }

          video.muted = false;
          try {
            await video.play();
            return JSON.stringify({
              success: !video.paused,
              error: !video.paused ? null : 'The video remained paused after play() completed.'
            });
          } catch (error) {
            return JSON.stringify({
              success: false,
              error: `${error?.name || 'Error'}: ${error?.message || String(error)}`
            });
          }
        }
        """);
    var result = JsonSerializer.Deserialize<YoutubeControlResult>(json, JsonOptions.Instance);
    Console.Error.WriteLine(result?.Success is true
        ? "youtube resumed"
        : $"youtube resume failed: {result?.Error ?? "Unknown error."}");
    return result?.Success is true;
}

static async Task<string?> DetectYoutubeErrorAsync(IPage page)
{
    return await page.EvaluateAsync<string?>(
        """
        () => {
          const video = document.querySelector('video');
          if (video?.error) {
            const names = {
              1: 'MEDIA_ERR_ABORTED',
              2: 'MEDIA_ERR_NETWORK',
              3: 'MEDIA_ERR_DECODE',
              4: 'MEDIA_ERR_SRC_NOT_SUPPORTED'
            };
            const name = names[video.error.code] || `MEDIA_ERR_${video.error.code}`;
            return `${name}: ${video.error.message || 'No media error message was provided.'}`;
          }

          const selectors = [
            '.ytp-error-content-wrap-reason',
            '.ytp-error-content-wrap-subreason',
            '.ytp-error-content-wrap',
            '.ytp-error'
          ];
          for (const selector of selectors) {
            const element = document.querySelector(selector);
            if (!element) continue;
            const style = getComputedStyle(element);
            const rect = element.getBoundingClientRect();
            const visible = style.display !== 'none' && style.visibility !== 'hidden' &&
                            Number(style.opacity || 1) > 0 && rect.width > 0 && rect.height > 0;
            const message = (element.innerText || element.textContent || '').trim().replace(/\s+/g, ' ');
            if (visible && message) return message;
          }

          const player = document.querySelector('#movie_player');
          return player?.classList.contains('ytp-error')
            ? 'YouTube player entered an error state.'
            : null;
        }
        """);
}

static async Task<DetectionSample> DetectAsync(IPage page)
{
    const string script = """
        () => {
          const visible = (element, rect) => {
            if (!rect || rect.width < 1 || rect.height < 1) return false;
            const style = getComputedStyle(element);
            return style.display !== 'none' && style.visibility !== 'hidden' &&
                   Number(style.opacity || 1) > 0;
          };

          const videos = [...document.querySelectorAll('video')]
            .map(element => ({ element, rect: element.getBoundingClientRect() }))
            .filter(item => visible(item.element, item.rect));
          const player = videos.sort((a, b) =>
            (b.rect.width * b.rect.height) - (a.rect.width * a.rect.height))[0];

          if (!player) {
            return JSON.stringify({ isAd: false, method: 'DOM', hasPlayer: false, x: 0, y: 0, width: 0, height: 0 });
          }

          const p = player.rect;
          const region = {
            left: Math.max(0, p.left),
            top: Math.max(0, p.top),
            right: Math.min(innerWidth, p.left + Math.min(p.width * 0.45, 640)),
            bottom: Math.min(innerHeight, p.top + Math.min(p.height * 0.30, 260))
          };
          const intersects = rect => rect.right > region.left && rect.left < region.right &&
                                     rect.bottom > region.top && rect.top < region.bottom;
          const adWords = /\b(ad|ads|advertisement|commercial break|sponsored)\b/i;
          const adIdentity = /(^|[-_])(ad|ads|advert|advertisement)([-_]|$)|adbadge|adindicator|adcountdown/i;

          let isAd = false;
          for (const element of document.querySelectorAll('*')) {
            const rect = element.getBoundingClientRect();
            if (!intersects(rect) || !visible(element, rect)) continue;
            if (rect.width > (region.right - region.left) * 1.5 ||
                rect.height > (region.bottom - region.top) * 1.5) continue;

            const text = (element.innerText || element.textContent || '').trim().replace(/\s+/g, ' ');
            const aria = element.getAttribute('aria-label') || '';
            const title = element.getAttribute('title') || '';
            const role = element.getAttribute('role') || '';
            const testId = element.getAttribute('data-testid') || '';
            const identity = `${element.id} ${element.className || ''} ${testId}`;
            const conciseText = text.length <= 160 ? text : '';

            if (adWords.test(`${conciseText} ${aria} ${title}`) ||
                (adIdentity.test(identity) && !/load|download/i.test(identity)) ||
                (/status|timer/i.test(role) && adWords.test(`${aria} ${conciseText}`))) {
              isAd = true;
              break;
            }
          }

          return JSON.stringify({
            isAd,
            method: 'DOM',
            hasPlayer: true,
            x: Math.max(0, region.left),
            y: Math.max(0, region.top),
            width: Math.max(1, region.right - region.left),
            height: Math.max(1, region.bottom - region.top)
          });
        }
        """;

    var json = await page.EvaluateAsync<string>(script);
    return JsonSerializer.Deserialize<DetectionSample>(json, JsonOptions.Instance)
        ?? new DetectionSample(false, "DOM", false, 0, 0, 0, 0);
}

static async Task SaveDiagnosticCropAsync(IPage page, DetectionSample sample, string captureDirectory)
{
    try
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var path = Path.Combine(captureDirectory, $"player-upper-left-{timestamp}.png");
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = path,
            Clip = new Clip
            {
                X = sample.X,
                Y = sample.Y,
                Width = sample.Width,
                Height = sample.Height
            }
        });
    }
    catch (PlaywrightException)
    {
        // Captures are a best-effort visual fallback and must not stop detection.
    }
}

internal sealed record DetectionSample(
    bool IsAd,
    string Method,
    bool HasPlayer,
    float X,
    float Y,
    float Width,
    float Height);

internal sealed record YoutubeControlResult(bool Success, string? Error);

internal static class JsonOptions
{
    internal static readonly JsonSerializerOptions Instance = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

internal sealed record DetectorOptions(
    string Url,
    string YoutubeUrl,
    bool Headless,
    TimeSpan PollInterval,
    int ConfirmationSamples,
    string CaptureDirectory,
    TimeSpan CaptureInterval,
    bool ShowHelp)
{
    private const string DefaultUrl = "https://pluto.tv/live-tv";
    private const string DefaultYoutubeUrl = "https://www.youtube.com/watch?v=M7lc1UVf-VE";

    internal const string Usage = """
        PlutoAdDetector
          --headless                 Run Chromium without a visible window (headed is the default)
          --url <url>                Pluto URL (default: https://pluto.tv/live-tv)
          --youtube-url <url>        Test YouTube video URL
          --poll-ms <milliseconds>   Detection interval (default: 500)
          --confirm <count>          Consecutive samples required for a transition (default: 2)
          --captures <directory>     Visual fallback directory (default: captures)
          --capture-seconds <count>  Seconds between fallback crops (default: 30)
          --help                     Show this help on standard error
        """;

    internal static DetectorOptions Parse(string[] args)
    {
        var url = DefaultUrl;
        var youtubeUrl = DefaultYoutubeUrl;
        var headless = false;
        var pollMilliseconds = 500;
        var confirmationSamples = 2;
        var captureDirectory = Path.GetFullPath("captures");
        var captureSeconds = 30;
        var showHelp = false;

        for (var index = 0; index < args.Length; index++)
        {
            string NextValue(string option)
            {
                if (++index >= args.Length)
                {
                    throw new ArgumentException($"Missing value for {option}.");
                }

                return args[index];
            }

            switch (args[index])
            {
                case "--headless":
                    headless = true;
                    break;
                case "--url":
                    url = NextValue("--url");
                    break;
                case "--youtube-url":
                    youtubeUrl = NextValue("--youtube-url");
                    break;
                case "--poll-ms":
                    pollMilliseconds = ParsePositiveInt(NextValue("--poll-ms"), "--poll-ms");
                    break;
                case "--confirm":
                    confirmationSamples = ParsePositiveInt(NextValue("--confirm"), "--confirm");
                    break;
                case "--captures":
                    captureDirectory = Path.GetFullPath(NextValue("--captures"));
                    break;
                case "--capture-seconds":
                    captureSeconds = ParsePositiveInt(NextValue("--capture-seconds"), "--capture-seconds");
                    break;
                case "--help" or "-h":
                    showHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {args[index]}");
            }
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("--url must be an absolute HTTP or HTTPS URL.");
        }

        if (!Uri.TryCreate(youtubeUrl, UriKind.Absolute, out var youtubeUri) ||
            (youtubeUri.Scheme != Uri.UriSchemeHttps && youtubeUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("--youtube-url must be an absolute HTTP or HTTPS URL.");
        }

        return new DetectorOptions(
            url,
            youtubeUrl,
            headless,
            TimeSpan.FromMilliseconds(pollMilliseconds),
            confirmationSamples,
            captureDirectory,
            TimeSpan.FromSeconds(captureSeconds),
            showHelp);
    }

    private static int ParsePositiveInt(string value, string option)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result <= 0)
        {
            throw new ArgumentException($"{option} must be a positive integer.");
        }

        return result;
    }
}
