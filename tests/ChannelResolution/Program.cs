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
const string liveId = "EEEEEEEEEEE";
const string upcomingId = "FFFFFFFFFFF";
const string streamedId = "GGGGGGGGGGG";
const string premiereId = "HHHHHHHHHHH";
const string titleLiveId = "IIIIIIIIIII";
const string lockupUpcomingId = "JJJJJJJJJJJ";
const string lockupLiveId = "KKKKKKKKKKK";
const string premiereBadgeId = "LLLLLLLLLLL";
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
          { "videoRenderer": { "videoId": "{{liveId}}", "title": { "simpleText": "Live Video" },
            "thumbnailOverlays": [{ "thumbnailOverlayTimeStatusRenderer": { "style": "LIVE", "text": { "simpleText": "LIVE" } } }] } },
          { "videoRenderer": { "videoId": "{{upcomingId}}", "title": { "simpleText": "Upcoming Video" },
            "upcomingEventData": { "startTime": "123" } } },
          { "videoRenderer": { "videoId": "{{streamedId}}", "title": { "simpleText": "Stream Recording" },
            "publishedTimeText": { "simpleText": "Streamed 2 hours ago" } } },
          { "videoRenderer": { "videoId": "{{premiereId}}", "title": { "simpleText": "Completed Premiere" },
            "publishedTimeText": { "simpleText": "Premiered 2 hours ago" } } },
          { "videoRenderer": { "videoId": "{{titleLiveId}}", "title": { "simpleText": "LIVE appears only in this normal title" } } },
          { "lockupViewModel": { "contentId": "{{lockupUpcomingId}}", "contentType": "LOCKUP_CONTENT_TYPE_VIDEO",
            "metadata": { "lockupMetadataViewModel": { "title": { "content": "Future Premiere" },
              "metadata": { "contentMetadataViewModel": { "metadataRows": [{ "metadataParts": [{ "text": { "content": "Premieres in 2 hours" } }] }] } } } } } },
          { "lockupViewModel": { "contentId": "{{lockupLiveId}}", "contentType": "LOCKUP_CONTENT_TYPE_VIDEO",
            "contentImage": { "thumbnailViewModel": { "overlays": [{ "thumbnailBottomOverlayViewModel": {
              "badges": [{ "thumbnailBadgeViewModel": { "text": "LIVE", "badgeStyle": "THUMBNAIL_OVERLAY_BADGE_STYLE_LIVE" } }]
            } }] } }, "metadata": { "lockupMetadataViewModel": { "title": { "content": "Lockup Live" } } } } },
          { "videoRenderer": { "videoId": "{{premiereBadgeId}}", "title": { "simpleText": "Premiere Badge" },
            "badges": [{ "metadataBadgeRenderer": { "label": "PREMIERE" } }] } },
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
if (pageVideos.Length != 11) throw new Exception("Shorts and duplicate IDs should be excluded.");
if (pageVideos.Single(video => video.Id == liveId).AutomaticSkipReason != "currently live stream") throw new Exception("LIVE renderer not classified.");
if (pageVideos.Single(video => video.Id == upcomingId).AutomaticSkipReason != "upcoming/scheduled live stream") throw new Exception("Upcoming renderer not classified.");
if (pageVideos.Single(video => video.Id == streamedId).AutomaticSkipReason != "live stream recording") throw new Exception("Stream recording not classified.");
if (pageVideos.Single(video => video.Id == premiereId).AutomaticSkipReason is not null) throw new Exception("Completed premiere should be allowed.");
if (pageVideos.Single(video => video.Id == titleLiveId).AutomaticSkipReason is not null) throw new Exception("LIVE in title should not be classified.");
if (pageVideos.Single(video => video.Id == lockupUpcomingId).AutomaticSkipReason != "upcoming/scheduled premiere") throw new Exception("Upcoming lockup premiere not classified.");
if (pageVideos.Single(video => video.Id == lockupLiveId).AutomaticSkipReason != "currently live stream") throw new Exception("Live lockup not classified.");
if (pageVideos.Single(video => video.Id == premiereBadgeId).AutomaticSkipReason != "premiere not yet completed") throw new Exception("Premiere badge not classified.");

var rssXml = $$"""
    <feed xmlns="http://www.w3.org/2005/Atom" xmlns:yt="http://www.youtube.com/xml/schemas/2015">
      <entry><title>RSS First</title><yt:videoId>{{firstId}}</yt:videoId></entry>
      <entry><title>RSS Live</title><yt:videoId>{{liveId}}</yt:videoId></entry>
      <entry><title>RSS Second</title><yt:videoId>{{secondId}}</yt:videoId></entry>
    </feed>
    """;

var rssHandler = new FakeHandler(request =>
    request.RequestUri!.AbsolutePath.Contains("feeds/videos.xml")
        ? FakeHandler.Text(rssXml)
        : FakeHandler.Text(videosHtml));
var rss = await YoutubeFeed.FetchAsync("https://www.youtube.com/@MeidasTouch", new HttpClient(rssHandler), CancellationToken.None);
Equal("RSS", rss.Source);
Equal(firstId, rss.Uploads[0].Id);
Equal(liveId, rss.Uploads[1].Id);
Equal("currently live stream", rss.Uploads[1].AutomaticSkipReason);
Equal(secondId, rss.Uploads[2].Id);
if (!rssHandler.Urls[0].Contains("feeds/videos.xml") || !rssHandler.Urls[1].EndsWith("/@MeidasTouch/videos"))
    throw new Exception("RSS must remain first and channel metadata enrichment second.");

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

Console.WriteLine("PASS: channel IDs, video order/titles, live/upcoming/stream/premiere classification, RSS enrichment, fallback, and total failure");

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
