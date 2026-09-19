using System.Xml.Linq;
using System.Text.RegularExpressions;
using System.Net;

internal sealed record YoutubeUpload(string Id, string Title, DateTimeOffset Published);

internal static class YoutubeFeed
{
    internal const string DefaultChannelId = "UC9r9HYFxEQOBXSopFS61ZWg";
    private const string IdPattern = "UC[A-Za-z0-9_-]{22}";

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
        // YouTube may embed JSON inside escaped JavaScript strings or HTML attributes.
        var normalized = WebUtility.HtmlDecode(html);
        for (var pass = 0; pass < 2; pass++)
        {
            normalized = Regex.Replace(normalized, @"\\(?:u00|x)([0-9a-fA-F]{2})",
                match => ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString());
            normalized = normalized.Replace(@"\/", "/").Replace("\\\"", "\"");
        }

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

    internal static async Task<YoutubeUpload[]> FetchAsync(string channelUrl, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var channelId = DirectChannelId(channelUrl);
        if (channelId is null)
        {
            Console.Error.WriteLine($"youtube channel: resolving {channelUrl}");
            var html = await client.GetStringAsync(channelUrl, cancellationToken);
            channelId = ChannelIdFromHtml(html);
            if (channelId is null) throw new HttpRequestException($"Could not resolve YouTube channel ID from {channelUrl} (canonical URL, externalId, channel_id, channelId).");
        }
        var rssUrl = $"https://www.youtube.com/feeds/videos.xml?channel_id={channelId}";
        Console.Error.WriteLine($"youtube channel: resolved {channelId} from {channelUrl}");
        Console.Error.WriteLine($"youtube RSS: fetching {rssUrl}");
        using var response = await client.GetAsync(rssUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"YouTube RSS request failed: {(int)response.StatusCode} {response.ReasonPhrase}; URL: {rssUrl}", null, response.StatusCode);
        var xml = await response.Content.ReadAsStringAsync(cancellationToken);
        var feed = XDocument.Parse(xml);
        XNamespace atom = "http://www.w3.org/2005/Atom";
        XNamespace yt = "http://www.youtube.com/xml/schemas/2015";
        var ids = feed.Root!.Elements(atom + "entry")
            .OrderByDescending(entry => (DateTimeOffset?)entry.Element(atom + "published") ?? DateTimeOffset.MinValue)
            .Select(entry => new YoutubeUpload((string?)entry.Element(yt + "videoId") ?? "",
                Regex.Replace((string?)entry.Element(atom + "title") ?? "Untitled", @"\s+", " "),
                (DateTimeOffset?)entry.Element(atom + "published") ?? DateTimeOffset.MinValue))
            .Where(video => video.Id.Length == 11 && video.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            .DistinctBy(video => video.Id).ToArray();
        Console.Error.WriteLine($"youtube RSS: {ids.Length} recent uploads from {channelUrl}");
        return ids;
    }
}
