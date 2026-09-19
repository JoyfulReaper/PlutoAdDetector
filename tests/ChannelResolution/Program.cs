const string id = "UC9r9HYFxEQOBXSopFS61ZWg";
static void Equal(string? expected, string? actual)
{
    if (expected != actual) throw new Exception($"Expected {expected ?? "null"}, got {actual ?? "null"}");
}
Equal(id, YoutubeFeed.DirectChannelId("https://www.youtube.com/@MeidasTouch"));
Equal(id, YoutubeFeed.DirectChannelId("https://www.youtube.com/@meidastouch/"));
Equal(id, YoutubeFeed.DirectChannelId($"https://www.youtube.com/channel/{id}/"));
Equal(null, YoutubeFeed.DirectChannelId("https://www.youtube.com/@OtherChannel"));
Equal(null, YoutubeFeed.DirectChannelId($"https://www.youtube.com/channel/{id}extra"));
foreach (var html in new[]
{
    $"{{\"channelId\":\"{id}\"}}",
    $"{{\"externalId\" : \"{id}\"}}",
    $"<link href='https://www.youtube.com/channel/{id}' rel='canonical'>",
    $"<link rel=\"canonical\" href=\"https://www.youtube.com/channel/{id}\">",
    $"https://www.youtube.com/feeds/videos.xml?channel_id={id}&amp;alt=rss",
    $"{{\\\"externalId\\\":\\\"{id}\\\"}}",
    $"{{\\x22externalId\\x22: \\x22{id}\\x22}}",
    $"{{\\u0022channelId\\u0022: \\u0022{id}\\u0022}}",
    $"{{&quot;channelId&quot;:&quot;{id}&quot;}}"
}) Equal(id, YoutubeFeed.ChannelIdFromHtml(html));
Equal(id, YoutubeFeed.ChannelIdFromHtml($"{{\"channelId\":\"UCaaaaaaaaaaaaaaaaaaaaaa\"}}<link rel='canonical' href='/channel/{id}'>"));
Equal(null, YoutubeFeed.ChannelIdFromHtml("<html>Consent or unavailable</html>"));
Equal(null, YoutubeFeed.ChannelIdFromHtml($"{{\"externalId\":\"{id}extra\"}}"));
Console.WriteLine("PASS: default/direct IDs, all HTML patterns, escaping, canonical precedence, and missing/invalid IDs");
