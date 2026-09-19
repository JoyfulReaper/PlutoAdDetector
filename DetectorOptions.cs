using System.Globalization;

internal enum PlutoScanMode
{
    Focused,
    Full
}

internal sealed record DetectorOptions(
    string SourceUrl,
    string ChannelUrl,
    string? YoutubeVideoId,
    int MinimumDurationSeconds,
    bool Headless,
    TimeSpan PollInterval,
    int ConfirmationSamples,
    string CaptureDirectory,
    TimeSpan CaptureInterval,
    PlutoScanMode ScanMode,
    bool Resume,
    bool ShowHelp)
{
    private const string DefaultSourceUrl = "https://pluto.tv/live-tv";

    internal const string Usage = """
        PlutoAdDetector
          --headless                 Run Chromium without a visible window (headed is the default)
          --url <url>                Source/streaming page URL (default: https://pluto.tv/live-tv)
          --channel-url <url>        YouTube channel (default: https://www.youtube.com/@MeidasTouch)
          --youtube-url <url>        Single video; bypass RSS and duration filtering (exclusive with --channel-url)
          --min-duration-seconds <n> Minimum duration (default: 300)
          --poll-ms <milliseconds>   Detection interval (default: 500)
          --confirm <count>          Consecutive samples required for a transition (default: 2)
          --scan-mode focused|full  DOM scan scope (default: focused; full scans the visible viewport)
          --resume                   Restore automatic queue state from youtube-queue.json
          --captures <directory>     Visual fallback directory (default: captures)
          --capture-seconds <count>  Seconds between fallback crops (default: 30)
          --help                     Show this help on standard error
        """;

    internal static DetectorOptions Parse(string[] args)
    {
        var sourceUrl = DefaultSourceUrl;
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
        var resume = false;
        var showHelp = false;

        for (var index = 0; index < args.Length; index++)
        {
            string NextValue(string option)
            {
                if (++index >= args.Length)
                    throw new ArgumentException($"Missing value for {option}.");

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
                    sourceUrl = NextValue("--url");
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
                case "--resume":
                    resume = true;
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

        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var sourceUri) ||
            sourceUri.Scheme is not ("https" or "http"))
            throw new ArgumentException("--url must be an absolute HTTP or HTTPS URL.");

        if (!Uri.TryCreate(channelUrl, UriKind.Absolute, out var channelUri) || !IsYoutubeChannelUri(channelUri))
            throw new ArgumentException("--channel-url must be a YouTube HTTPS channel or @handle URL.");
        if (channelExplicit && youtubeUrl is not null)
            throw new ArgumentException("--youtube-url and --channel-url cannot both be supplied.");

        var youtubeVideoId = youtubeUrl is null ? null : ParseYoutubeVideoId(youtubeUrl);
        return new DetectorOptions(
            sourceUrl,
            channelUrl,
            youtubeVideoId,
            minimumDurationSeconds,
            headless,
            TimeSpan.FromMilliseconds(pollMilliseconds),
            confirmationSamples,
            captureDirectory,
            TimeSpan.FromSeconds(captureSeconds),
            scanMode,
            resume,
            showHelp);
    }

    private static bool IsYoutubeChannelUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host is not ("youtube.com" or "www.youtube.com" or "m.youtube.com"))
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 1)
            return segments[0] is { Length: > 1 } handle && handle[0] == '@';

        return segments.Length == 2 &&
            segments[0] == "channel" &&
            segments[1] is { Length: 24 } channelId &&
            channelId.StartsWith("UC", StringComparison.Ordinal) &&
            channelId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
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
            throw new ArgumentException($"{option} must be a positive integer.");

        return result;
    }
}
