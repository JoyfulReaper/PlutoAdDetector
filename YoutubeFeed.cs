using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

internal sealed record YoutubeUpload(string Id, string Title, string? AutomaticSkipReason = null);
internal sealed record YoutubeDiscovery(YoutubeUpload[] Uploads, string Source);

internal static class YoutubeFeed
{
    internal const string DefaultChannelId = "UC9r9HYFxEQOBXSopFS61ZWg";
    private const string IdPattern = "UC[A-Za-z0-9_-]{22}";

    internal static async Task<YoutubeDiscovery> FetchAsync(
        string channelUrl,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/151 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return await FetchAsync(channelUrl, client, cancellationToken);
    }

    internal static async Task<YoutubeDiscovery> FetchAsync(
        string channelUrl,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        Exception? rssFailure = null;
        var channelId = DirectChannelId(channelUrl);

        if (channelId is null)
        {
            try
            {
                Console.Error.WriteLine($"youtube channel: resolving {channelUrl}");
                var channelHtml = await GetRequiredTextAsync(client, channelUrl, cancellationToken);
                channelId = ChannelIdFromHtml(channelHtml)
                    ?? throw new InvalidDataException(
                        $"Could not resolve YouTube channel ID from {channelUrl} " +
                        "(canonical URL, externalId, channel_id, channelId).");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                rssFailure = exception;
                Console.Error.WriteLine($"youtube RSS unavailable: channel resolution failed: {exception.Message}");
            }
        }

        if (channelId is not null)
        {
            var rssUrl = $"https://www.youtube.com/feeds/videos.xml?channel_id={channelId}";
            Console.Error.WriteLine($"youtube channel: resolved {channelId} from {channelUrl}");
            Console.Error.WriteLine($"youtube RSS: fetching {rssUrl}");
            try
            {
                var xml = await GetRequiredTextAsync(client, rssUrl, cancellationToken);
                var uploads = ParseRss(xml);
                if (uploads.Length == 0)
                    throw new InvalidDataException("RSS contained no valid video entries.");
                uploads = await AddChannelPageStateAsync(
                    uploads,
                    channelUrl,
                    client,
                    cancellationToken);
                Console.Error.WriteLine($"youtube discovery: RSS ({uploads.Length} uploads)");
                return new YoutubeDiscovery(uploads, "RSS");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                rssFailure = exception;
                Console.Error.WriteLine($"youtube RSS failed; trying channel page: {exception.Message}");
            }
        }

        var videosUrl = VideosUrl(channelUrl);
        Console.Error.WriteLine($"youtube channel page: fetching {videosUrl}");
        try
        {
            var html = await GetRequiredTextAsync(client, videosUrl, cancellationToken);
            var uploads = VideosFromHtml(html);
            if (uploads.Length == 0)
                throw new InvalidDataException("Channel videos page contained no videoRenderer entries.");
            Console.Error.WriteLine($"youtube discovery: channel-page fallback ({uploads.Length} uploads)");
            return new YoutubeDiscovery(uploads, "channel-page fallback");
        }
        catch (Exception pageFailure) when (pageFailure is not OperationCanceledException)
        {
            throw new HttpRequestException(
                $"YouTube discovery failed. RSS: {rssFailure?.Message ?? "unavailable"}; " +
                $"channel page {videosUrl}: {pageFailure.Message}",
                new AggregateException(rssFailure ?? new InvalidOperationException("RSS unavailable."), pageFailure));
        }
    }

    internal static string? DirectChannelId(string channelUrl)
    {
        var uri = new Uri(channelUrl);
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Equals("/@MeidasTouch", StringComparison.OrdinalIgnoreCase))
            return DefaultChannelId;
        var match = Regex.Match(path, "^/channel/(" + IdPattern + ")$", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    internal static string? ChannelIdFromHtml(string html)
    {
        var normalized = NormalizeEmbeddedText(html);

        // Prefer the canonical channel URL over IDs belonging to recommended channels.
        foreach (Match tag in Regex.Matches(normalized, @"<link\b[^>]*>", RegexOptions.IgnoreCase))
        {
            if (!Regex.IsMatch(tag.Value, "rel\\s*=\\s*[\"']canonical[\"']", RegexOptions.IgnoreCase)) continue;
            var canonical = Regex.Match(tag.Value, "/channel/(" + IdPattern + ")(?=[\"'/?&#])");
            if (canonical.Success) return canonical.Groups[1].Value;
        }

        string[] patterns =
        [
            "[\"']externalId[\"']\\s*:\\s*[\"'](" + IdPattern + ")[\"']",
            "channel_id=(" + IdPattern + ")(?=[^A-Za-z0-9_-]|$)",
            "[\"']channelId[\"']\\s*:\\s*[\"'](" + IdPattern + ")[\"']"
        ];
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(normalized, pattern);
            if (match.Success) return match.Groups[1].Value;
        }
        return null;
    }

    internal static YoutubeUpload[] VideosFromHtml(string html)
    {
        var json = InitialDataJson(html)
            ?? throw new InvalidDataException("ytInitialData JSON was not found on the channel videos page.");
        using var document = JsonDocument.Parse(json);
        var uploads = new List<YoutubeUpload>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Visit(document.RootElement, uploads, seen);
        return uploads.ToArray();
    }

    private static async Task<YoutubeUpload[]> AddChannelPageStateAsync(
        YoutubeUpload[] rssUploads,
        string channelUrl,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var videosUrl = VideosUrl(channelUrl);
        Console.Error.WriteLine($"youtube channel metadata: fetching {videosUrl}");
        try
        {
            var html = await GetRequiredTextAsync(client, videosUrl, cancellationToken);
            var pageUploads = VideosFromHtml(html);
            var byId = pageUploads.ToDictionary(video => video.Id, StringComparer.Ordinal);
            return rssUploads.Select(video => byId.TryGetValue(video.Id, out var pageVideo)
                    ? video with { AutomaticSkipReason = pageVideo.AutomaticSkipReason }
                    : video)
                .ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // RSS remains usable when optional state enrichment is unavailable.
            Console.Error.WriteLine($"youtube channel metadata unavailable; using RSS candidates: {exception.Message}");
            return rssUploads;
        }
    }

    private static YoutubeUpload[] ParseRss(string xml)
    {
        var feed = XDocument.Parse(xml);
        XNamespace atom = "http://www.w3.org/2005/Atom";
        XNamespace yt = "http://www.youtube.com/xml/schemas/2015";
        return feed.Root!.Elements(atom + "entry")
            .Select(entry => new YoutubeUpload(
                (string?)entry.Element(yt + "videoId") ?? "",
                Regex.Replace((string?)entry.Element(atom + "title") ?? "Untitled", @"\s+", " ").Trim()))
            .Where(video => IsVideoId(video.Id))
            .DistinctBy(video => video.Id)
            .ToArray();
    }

    private static async Task<string> GetRequiredTextAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"{(int)response.StatusCode} {response.ReasonPhrase}; URL: {url}",
                null,
                response.StatusCode);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string VideosUrl(string channelUrl)
    {
        var source = new Uri(channelUrl);
        var builder = new UriBuilder(source)
        {
            Path = source.AbsolutePath.TrimEnd('/') + "/videos",
            Query = "",
            Fragment = ""
        };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static string NormalizeEmbeddedText(string html)
    {
        var normalized = WebUtility.HtmlDecode(html);
        for (var pass = 0; pass < 2; pass++)
        {
            normalized = Regex.Replace(normalized, @"\\(?:u00|x)([0-9a-fA-F]{2})",
                match => ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString());
            normalized = normalized.Replace(@"\/", "/").Replace("\\\"", "\"");
        }
        return normalized;
    }

    private static string? InitialDataJson(string html)
    {
        string[] markers =
        [
            "var ytInitialData =",
            "window[\"ytInitialData\"] =",
            "ytInitialData =",
            "\"ytInitialData\":"
        ];
        foreach (var marker in markers)
        {
            var searchAt = 0;
            while ((searchAt = html.IndexOf(marker, searchAt, StringComparison.Ordinal)) >= 0)
            {
                var open = html.IndexOf('{', searchAt + marker.Length);
                if (open < 0) break;
                var candidate = BalancedObject(html, open);
                if (candidate is not null)
                {
                    try
                    {
                        using var _ = JsonDocument.Parse(candidate);
                        return candidate;
                    }
                    catch (JsonException)
                    {
                    }
                }
                searchAt += marker.Length;
            }
        }
        return null;
    }

    private static string? BalancedObject(string text, int start)
    {
        var depth = 0;
        var quoted = false;
        var escaped = false;
        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') quoted = false;
                continue;
            }
            if (character == '"') quoted = true;
            else if (character == '{') depth++;
            else if (character == '}' && --depth == 0) return text[start..(index + 1)];
        }
        return null;
    }

    private static void Visit(
        JsonElement element,
        List<YoutubeUpload> uploads,
        HashSet<string> seen)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) Visit(child, uploads, seen);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name is "videoRenderer" or "gridVideoRenderer")
            {
                AddRenderer(property.Value, uploads, seen);
            }
            else if (property.Name == "lockupViewModel")
            {
                AddLockup(property.Value, uploads, seen);
            }
            else
            {
                Visit(property.Value, uploads, seen);
            }
        }
    }

    private static void AddRenderer(
        JsonElement renderer,
        List<YoutubeUpload> uploads,
        HashSet<string> seen)
    {
        if (!renderer.TryGetProperty("videoId", out var idElement)) return;
        var id = idElement.GetString() ?? "";
        if (!IsVideoId(id) || !seen.Add(id)) return;

        var title = "Untitled";
        if (renderer.TryGetProperty("title", out var titleElement))
        {
            if (titleElement.TryGetProperty("simpleText", out var simple))
                title = simple.GetString() ?? title;
            else if (titleElement.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
                title = string.Concat(runs.EnumerateArray()
                    .Select(run => run.TryGetProperty("text", out var text) ? text.GetString() : null));
        }
        title = Regex.Replace(title, @"\s+", " ").Trim();
        uploads.Add(new YoutubeUpload(
            id,
            title.Length == 0 ? "Untitled" : title,
            AutomaticSkipReason(renderer)));
    }

    private static void AddLockup(
        JsonElement lockup,
        List<YoutubeUpload> uploads,
        HashSet<string> seen)
    {
        if (!lockup.TryGetProperty("contentType", out var type) ||
            type.GetString() != "LOCKUP_CONTENT_TYPE_VIDEO" ||
            !lockup.TryGetProperty("contentId", out var idElement)) return;
        var id = idElement.GetString() ?? "";
        if (!IsVideoId(id) || !seen.Add(id)) return;

        var title = "Untitled";
        if (lockup.TryGetProperty("metadata", out var metadata) &&
            metadata.TryGetProperty("lockupMetadataViewModel", out var viewModel) &&
            viewModel.TryGetProperty("title", out var titleElement) &&
            titleElement.TryGetProperty("content", out var content))
        {
            title = content.GetString() ?? title;
        }
        title = Regex.Replace(title, @"\s+", " ").Trim();
        uploads.Add(new YoutubeUpload(
            id,
            title.Length == 0 ? "Untitled" : title,
            AutomaticSkipReason(lockup)));
    }

    private static string? AutomaticSkipReason(JsonElement renderer)
    {
        var statusText = new List<string>();
        var currentlyLive = false;
        var upcoming = false;
        CollectStatus(renderer, "", statusText, ref currentlyLive, ref upcoming);
        var status = string.Join(" ", statusText).ToLowerInvariant();

        var premiere = Regex.IsMatch(status, @"\bpremiere(?:s|d)?\b");
        var completedPremiere = Regex.IsMatch(status, @"\bpremiered\b.*\bago\b");
        if (upcoming || Regex.IsMatch(status,
                @"\b(upcoming|scheduled for|waiting for|premieres? (?:in|on|at))\b"))
            return premiere ? "upcoming/scheduled premiere" : "upcoming/scheduled live stream";
        if (currentlyLive || Regex.IsMatch(status,
                @"\b(live|live now|currently live|watching now|watching)\b"))
            return premiere ? "premiere currently live" : "currently live stream";
        if (Regex.IsMatch(status, @"\bstreamed\b"))
            return "live stream recording";
        if (premiere && !completedPremiere)
            return "premiere not yet completed";

        // "Premiered ... ago" has become a normal completed video and is allowed.
        return null;
    }

    private static void CollectStatus(
        JsonElement element,
        string path,
        List<string> statusText,
        ref bool currentlyLive,
        ref bool upcoming)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                CollectStatus(child, path, statusText, ref currentlyLive, ref upcoming);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;

        foreach (var property in element.EnumerateObject())
        {
            var name = property.Name.ToLowerInvariant();
            var childPath = path.Length == 0 ? name : $"{path}.{name}";
            if (name == "upcomingeventdata") upcoming = true;
            if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                if (property.Value.GetBoolean() && name is "isupcoming" or "upcoming") upcoming = true;
                if (property.Value.GetBoolean() && name is "islive" or "islivenow" or "live") currentlyLive = true;
            }
            else if (property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString() ?? "";
                if (name is "style" or "badgestyle")
                {
                    if (value.Contains("UPCOMING", StringComparison.OrdinalIgnoreCase)) upcoming = true;
                    if (value.Contains("LIVE", StringComparison.OrdinalIgnoreCase)) currentlyLive = true;
                }
                if (childPath.Contains("badge", StringComparison.Ordinal) ||
                    childPath.Contains("thumbnailoverlay", StringComparison.Ordinal) ||
                    childPath.Contains("viewcounttext", StringComparison.Ordinal) ||
                    childPath.Contains("publishedtimetext", StringComparison.Ordinal) ||
                    childPath.Contains("upcomingeventdata", StringComparison.Ordinal) ||
                    childPath.Contains("metadatarows", StringComparison.Ordinal))
                {
                    statusText.Add(value);
                }
            }
            CollectStatus(property.Value, childPath, statusText, ref currentlyLive, ref upcoming);
        }
    }

    private static bool IsVideoId(string value) =>
        value.Length == 11 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
