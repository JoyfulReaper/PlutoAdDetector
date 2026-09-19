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
    var browserProfileDirectory = Path.GetFullPath("browser-profile");
    Directory.CreateDirectory(browserProfileDirectory);
    Console.Error.WriteLine($"pluto scan mode: {options.ScanMode.ToString().ToLowerInvariant()}");
    var automaticQueueMode = YoutubeQueueRefreshPolicy.IsAutomaticMode(options.YoutubeVideoId);
    YoutubeUpload[] candidates;
    try
    {
        candidates = automaticQueueMode
            ? (await YoutubeFeed.FetchAsync(options.ChannelUrl, cancellationToken)).Uploads
            : [];
    }
    catch (Exception exception) when (exception is HttpRequestException or System.Xml.XmlException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
    {
        Console.Error.WriteLine($"youtube discovery failed: {exception.Message}");
        candidates = [];
    }
    var initialDiscoveryCompletedAt = DateTimeOffset.UtcNow;
    await using var youtubePlayerHost = LocalYoutubePlayerHost.Start(candidates, options.MinimumDurationSeconds, options.YoutubeVideoId);

    using var playwright = await Playwright.CreateAsync();
    var chromeExecutable = FindInstalledGoogleChrome();
    await using var context = await playwright.Chromium.LaunchPersistentContextAsync(
        browserProfileDirectory,
        new BrowserTypeLaunchPersistentContextOptions
    {
        Headless = options.Headless,
        ExecutablePath = chromeExecutable,
        ChromiumSandbox = true,
        Args = ["--autoplay-policy=no-user-gesture-required"],
        ViewportSize = options.Headless
            ? new ViewportSize { Width = 1440, Height = 900 }
            : ViewportSize.NoViewport,
        Locale = "en-US"
    });
    Console.Error.WriteLine(chromeExecutable is null
        ? "browser: Playwright Chromium"
        : $"browser: Google Chrome ({chromeExecutable})");
    Console.Error.WriteLine($"browser profile: {browserProfileDirectory}");

    var trackingToggleRequests = 0;
    await context.ExposeFunctionAsync("requestAdTrackingToggle", () =>
    {
        Interlocked.Increment(ref trackingToggleRequests);
    });
    await context.AddInitScriptAsync(script: AdTrackingShortcut.Script);

    var plutoPage = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
    await plutoPage.GotoAsync(options.Url, new PageGotoOptions
    {
        WaitUntil = WaitUntilState.DOMContentLoaded,
        Timeout = 90_000
    });

    var youtubePage = await context.NewPageAsync();
    youtubePage.Console += (_, message) =>
    {
        if (message.Text.StartsWith("youtube queue:", StringComparison.Ordinal))
            Console.Error.WriteLine(message.Text);
    };
    await youtubePage.GotoAsync(youtubePlayerHost.Url.AbsoluteUri, new PageGotoOptions
    {
        WaitUntil = WaitUntilState.DOMContentLoaded,
        Timeout = 90_000
    });
    await youtubePage.WaitForFunctionAsync(
        "() => window.youtubePlayerControls?.isReady() === true",
        null,
        new PageWaitForFunctionOptions { Timeout = 60_000 });
    await PauseYoutubeAsync(youtubePage);
    await plutoPage.BringToFrontAsync();
    await using var youtubeQueueStateSaver = new YoutubeQueueStateSaver(
        youtubePage,
        Path.GetFullPath("youtube-queue.json"),
        options.ChannelUrl,
        options.MinimumDurationSeconds,
        options.YoutubeVideoId);

    bool? publishedState = null;
    bool? pendingState = null;
    var activeDetectionMethod = "DOM";
    var pendingCount = 0;
    var everFoundSemanticIndicator = false;
    var nextCaptureAt = DateTimeOffset.UtcNow;
    var youtubePlaybackStarted = false;
    string? loggedYoutubeError = null;
    var nextYoutubeHealthCheckAt = DateTimeOffset.MinValue;
    var queueRefreshPolicy = new YoutubeQueueRefreshPolicy(initialDiscoveryCompletedAt);
    var nextQueueDepthCheckAt = DateTimeOffset.UtcNow;
    Task<YoutubeDiscovery>? feedRefresh = null;
    var trackingPaused = false;

    while (!cancellationToken.IsCancellationRequested)
    {
        if (feedRefresh?.IsCompleted is true)
        {
            try
            {
                var discovery = await feedRefresh;
                await youtubePage.EvaluateAsync("items => { window.youtubePlayerControls.refresh(items); }",
                    discovery.Uploads.Select(item => new
                    {
                        id = item.Id,
                        title = item.Title,
                        automaticSkipReason = item.AutomaticSkipReason
                    }).ToArray());
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine($"youtube discovery refresh failed (queue retained): {exception.Message}");
            }
            finally
            {
                feedRefresh = null;
                queueRefreshPolicy.RecordAttemptCompleted(DateTimeOffset.UtcNow);
            }
        }

        var refreshCheckTime = DateTimeOffset.UtcNow;
        if (automaticQueueMode &&
            feedRefresh is null &&
            refreshCheckTime >= nextQueueDepthCheckAt &&
            queueRefreshPolicy.CooldownElapsed(refreshCheckTime))
        {
            nextQueueDepthCheckAt = refreshCheckTime.AddSeconds(5);
            try
            {
                var remainingVideos = await GetYoutubeQueueRemainingCountAsync(youtubePage);
                if (queueRefreshPolicy.ShouldRefresh(remainingVideos, refreshCheckTime))
                {
                    Console.Error.WriteLine(
                        $"youtube discovery refresh triggered: automatic queue has {remainingVideos} remaining videos (threshold: {YoutubeQueueRefreshPolicy.RemainingVideoThreshold})");
                    // Fetch asynchronously so slow RSS requests never block ad detection.
                    feedRefresh = YoutubeFeed.FetchAsync(options.ChannelUrl, cancellationToken);
                }
            }
            catch (PlaywrightException exception)
            {
                Console.Error.WriteLine($"youtube queue depth check failed: {exception.Message}");
            }
        }

        var toggleCount = Interlocked.Exchange(ref trackingToggleRequests, 0);
        while (toggleCount-- > 0)
        {
            trackingPaused = !trackingPaused;
            publishedState = null;
            pendingState = null;
            pendingCount = 0;
            activeDetectionMethod = "DOM";

            if (trackingPaused)
            {
                await PauseYoutubeAsync(youtubePage);
                youtubePlaybackStarted = false;
                loggedYoutubeError = null;
                await plutoPage.BringToFrontAsync();
                await SetPlutoMutedAsync(plutoPage, muted: false);
                Console.WriteLine("ad tracking paused");
            }
            else
            {
                Console.WriteLine("ad tracking resumed");
            }
        }

        var sample = await DetectAsync(plutoPage, options.ScanMode);
        // If P was pressed while detection was running, normalize tracking before
        // this sample can trigger a switch. A resumed loop will take a new sample.
        if (Volatile.Read(ref trackingToggleRequests) > 0)
            continue;

        if (trackingPaused)
        {
            await Task.Delay(options.PollInterval, cancellationToken);
            continue;
        }

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

static async Task<int> GetYoutubeQueueRemainingCountAsync(IPage page)
{
    return await page.EvaluateAsync<int>(
        """
        () => {
          const queuedVideos = window.youtubePlayerControls?.getQueueState?.().queuedVideos;
          if (!Array.isArray(queuedVideos)) throw new Error('Local YouTube queue state is unavailable.');
          return queuedVideos.length;
        }
        """);
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
          const controls = window.youtubePlayerControls;
          return JSON.stringify(controls
            ? controls.pause()
            : { success: false, error: 'Local YouTube player controls are unavailable.' });
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
        () => {
          const controls = window.youtubePlayerControls;
          return JSON.stringify(controls
            ? controls.play()
            : { success: false, error: 'Local YouTube player controls are unavailable.' });
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
          const controls = window.youtubePlayerControls;
          return controls
            ? controls.getError()
            : 'Local YouTube player controls are unavailable.';
        }
        """);
}

static async Task<DetectionSample> DetectAsync(IPage page, PlutoScanMode scanMode)
{
    var mode = scanMode == PlutoScanMode.Full ? "full" : "focused";
    var json = await page.EvaluateAsync<string>(PlutoDetectionScript.Script, mode);
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

internal enum PlutoScanMode
{
    Focused,
    Full
}

internal static class JsonOptions
{
    internal static readonly JsonSerializerOptions Instance = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

internal sealed record DetectorOptions(
    string Url,
    string ChannelUrl,
    string? YoutubeVideoId,
    int MinimumDurationSeconds,
    bool Headless,
    TimeSpan PollInterval,
    int ConfirmationSamples,
    string CaptureDirectory,
    TimeSpan CaptureInterval,
    PlutoScanMode ScanMode,
    bool ShowHelp)
{
    private const string DefaultUrl = "https://pluto.tv/live-tv";

    internal const string Usage = """
        PlutoAdDetector
          --headless                 Run Chromium without a visible window (headed is the default)
          --url <url>                Pluto URL (default: https://pluto.tv/live-tv)
          --channel-url <url>        YouTube channel (default: https://www.youtube.com/@MeidasTouch)
          --youtube-url <url>        Single video; bypass RSS and duration filtering (exclusive with --channel-url)
          --min-duration-seconds <n> Minimum duration (default: 300)
          --poll-ms <milliseconds>   Detection interval (default: 500)
          --confirm <count>          Consecutive samples required for a transition (default: 2)
          --scan-mode focused|full  DOM scan scope (default: focused; full scans the whole document)
          --captures <directory>     Visual fallback directory (default: captures)
          --capture-seconds <count>  Seconds between fallback crops (default: 30)
          --help                     Show this help on standard error
        """;

    internal static DetectorOptions Parse(string[] args)
    {
        var url = DefaultUrl;
        var channelUrl = "https://www.youtube.com/@MeidasTouch";
        var channelExplicit = false;
        string? youtubeUrl = null;
        var minimumDurationSeconds = 300;
        var headless = false;
        var pollMilliseconds = 500;
        var confirmationSamples = 2;
        var captureDirectory = Path.GetFullPath("captures");
        var captureSeconds = 30;
        var scanMode = PlutoScanMode.Focused;
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
                case "--channel-url":
                    channelExplicit = true;
                    channelUrl = NextValue("--channel-url");
                    break;
                case "--youtube-url":
                    youtubeUrl = NextValue("--youtube-url");
                    break;
                case "--min-duration-seconds":
                    minimumDurationSeconds = ParsePositiveInt(NextValue("--min-duration-seconds"), "--min-duration-seconds");
                    break;
                case "--url":
                    url = NextValue("--url");
                    break;
                case "--poll-ms":
                    pollMilliseconds = ParsePositiveInt(NextValue("--poll-ms"), "--poll-ms");
                    break;
                case "--confirm":
                    confirmationSamples = ParsePositiveInt(NextValue("--confirm"), "--confirm");
                    break;
                case "--scan-mode":
                    scanMode = NextValue("--scan-mode").ToLowerInvariant() switch
                    {
                        "focused" => PlutoScanMode.Focused,
                        "full" => PlutoScanMode.Full,
                        _ => throw new ArgumentException("--scan-mode must be focused or full.")
                    };
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


        if (!Uri.TryCreate(channelUrl, UriKind.Absolute, out var channelUri) ||
            channelUri.Scheme != "https" || channelUri.Host is not ("youtube.com" or "www.youtube.com" or "m.youtube.com") ||
            !(channelUri.AbsolutePath.StartsWith("/@") || channelUri.AbsolutePath.StartsWith("/channel/")))
            throw new ArgumentException("--channel-url must be a YouTube HTTPS channel or @handle URL.");
        if (channelExplicit && youtubeUrl is not null)
            throw new ArgumentException("--youtube-url and --channel-url cannot both be supplied.");
        var youtubeVideoId = youtubeUrl is null ? null : ParseYoutubeVideoId(youtubeUrl);
        return new DetectorOptions(
            url,
            channelUrl,
            youtubeVideoId,
            minimumDurationSeconds,
            headless,
            TimeSpan.FromMilliseconds(pollMilliseconds),
            confirmationSamples,
            captureDirectory,
            TimeSpan.FromSeconds(captureSeconds),
            scanMode,
            showHelp);
    }

    private static string ParseYoutubeVideoId(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new ArgumentException("--youtube-url must be a YouTube video URL.");
        string? id = null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (uri.Host is "youtu.be" or "www.youtu.be" && segments.Length == 1)
            id = segments[0];
        else if (uri.Host is "youtube.com" or "www.youtube.com" or "m.youtube.com")
        {
            if (uri.AbsolutePath == "/watch")
                id = uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
                    .Where(parts => parts.Length == 2 && parts[0] == "v")
                    .Select(parts => Uri.UnescapeDataString(parts[1])).FirstOrDefault();
            else if (segments.Length == 2 && segments[0] is "embed" or "shorts" or "live")
                id = segments[1];
        }
        if (id is null || id.Length != 11 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException("--youtube-url must contain a valid 11-character YouTube video ID (watch, youtu.be, embed, shorts, or live URL).");
        return id;
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
