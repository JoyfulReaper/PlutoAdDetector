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

const string firstId = "AAAAAAAAAAA";
const string secondId = "BBBBBBBBBBB";
const string shortId = "CCCCCCCCCCC";
const string lockupId = "DDDDDDDDDDD";
var videosHtml = $$"""
    <script>
      var ytInitialData = {
        "contents": [
          { "videoRenderer": { "videoId": "{{firstId}}", "title": { "runs": [{ "text": "First " }, { "text": "Video" }] } } },
          { "gridVideoRenderer": { "videoId": "{{secondId}}", "title": { "simpleText": "Second Video" } } },
          { "richItemRenderer": { "content": { "lockupViewModel": {
            "contentId": "{{lockupId}}",
            "contentType": "LOCKUP_CONTENT_TYPE_VIDEO",
            "metadata": { "lockupMetadataViewModel": { "title": { "content": "Current Lockup Video" } } }
          } } } },
          { "reelItemRenderer": { "videoId": "{{shortId}}", "headline": { "simpleText": "Short" } } },
          { "lockupViewModel": { "contentId": "{{shortId}}", "contentType": "LOCKUP_CONTENT_TYPE_SHORT", "metadata": {} } },
          { "videoRenderer": { "videoId": "{{firstId}}", "title": { "simpleText": "Duplicate" } } }
        ]
      };
    </script>
    """;
var pageVideos = YoutubeFeed.VideosFromHtml(videosHtml);
Equal(firstId, pageVideos[0].Id);
Equal("First Video", pageVideos[0].Title);
Equal(secondId, pageVideos[1].Id);
Equal("Second Video", pageVideos[1].Title);
Equal(lockupId, pageVideos[2].Id);
Equal("Current Lockup Video", pageVideos[2].Title);
if (pageVideos.Length != 3) throw new Exception("Shorts and duplicate IDs should be excluded.");

var rssXml = $$"""
    <feed xmlns="http://www.w3.org/2005/Atom" xmlns:yt="http://www.youtube.com/xml/schemas/2015">
      <entry><title>RSS First</title><yt:videoId>{{firstId}}</yt:videoId></entry>
      <entry><title>RSS Second</title><yt:videoId>{{secondId}}</yt:videoId></entry>
    </feed>
    """;

var rssHandler = new FakeHandler(request =>
    request.RequestUri!.AbsolutePath.Contains("feeds/videos.xml")
        ? FakeHandler.Text(rssXml)
        : throw new Exception("RSS success must not fetch the videos page."));
var rss = await YoutubeFeed.FetchAsync("https://www.youtube.com/@MeidasTouch", new HttpClient(rssHandler), CancellationToken.None);
Equal("RSS", rss.Source);
Equal(firstId, rss.Uploads[0].Id);

foreach (var rssResponse in new[]
{
    new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { ReasonPhrase = "Not Found" },
    FakeHandler.Text("not XML")
})
{
    var fallbackHandler = new FakeHandler(request =>
        request.RequestUri!.AbsolutePath.Contains("feeds/videos.xml")
            ? rssResponse
            : FakeHandler.Text(videosHtml));
    var fallback = await YoutubeFeed.FetchAsync("https://www.youtube.com/@MeidasTouch", new HttpClient(fallbackHandler), CancellationToken.None);
    Equal("channel-page fallback", fallback.Source);
    Equal(firstId, fallback.Uploads[0].Id);
    Equal(secondId, fallback.Uploads[1].Id);
    if (!fallbackHandler.Urls.Last().EndsWith("/@MeidasTouch/videos"))
        throw new Exception("Fallback did not request the channel /videos page.");
}

try
{
    var failedHandler = new FakeHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
    await YoutubeFeed.FetchAsync("https://www.youtube.com/@MeidasTouch", new HttpClient(failedHandler), CancellationToken.None);
    throw new Exception("Both failed discovery methods should throw.");
}
catch (HttpRequestException exception) when (exception.Message.Contains("YouTube discovery failed"))
{
}

Console.WriteLine("PASS: channel IDs, embedded video order/title parsing, RSS preference, page fallback, malformed RSS, and total failure");

internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    internal List<string> Urls { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Urls.Add(request.RequestUri!.AbsoluteUri);
        return Task.FromResult(response(request));
    }

    internal static HttpResponseMessage Text(string value) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(value)
    };
}
