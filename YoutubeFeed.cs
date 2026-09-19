using System.Xml.Linq;
using System.Text.RegularExpressions;

internal sealed record YoutubeUpload(string Id, string Title, DateTimeOffset Published);

internal static class YoutubeFeed
{
    internal static async Task<YoutubeUpload[]> FetchAsync(string channelUrl, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var uri = new Uri(channelUrl);
        var channelId = uri.AbsolutePath.StartsWith("/channel/", StringComparison.Ordinal)
            ? uri.AbsolutePath.Split('/').Last() : "";
        if (!Regex.IsMatch(channelId, "^UC[\\w-]{22}$"))
        {
            var html = await client.GetStringAsync(channelUrl, cancellationToken);
            channelId = Regex.Match(html, "channel_id=(UC[\\w-]{22})").Groups[1].Value;
            if (channelId.Length == 0) throw new HttpRequestException("Channel page did not advertise a public RSS feed.");
        }
        var xml = await client.GetStringAsync($"https://www.youtube.com/feeds/videos.xml?channel_id={channelId}", cancellationToken);
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
