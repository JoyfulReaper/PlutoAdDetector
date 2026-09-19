using System.Xml.Linq;

internal static class YoutubeFeed
{
    internal const string Url = "https://www.youtube.com/feeds/videos.xml?channel_id=UC9r9HYFxEQOBXSopFS61ZWg";

    internal static async Task<string[]> FetchAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var xml = await client.GetStringAsync(Url, cancellationToken);
        var feed = XDocument.Parse(xml);
        XNamespace atom = "http://www.w3.org/2005/Atom";
        XNamespace yt = "http://www.youtube.com/xml/schemas/2015";
        var ids = feed.Root!.Elements(atom + "entry")
            .OrderByDescending(entry => (DateTimeOffset?)entry.Element(atom + "published") ?? DateTimeOffset.MinValue)
            .Select(entry => (string?)entry.Element(yt + "videoId"))
            .OfType<string>()
            .Where(id => id.Length == 11 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            .Distinct().ToArray();
        Console.Error.WriteLine($"youtube RSS: {ids.Length} recent MeidasTouch uploads");
        return ids;
    }
}
