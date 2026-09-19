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
const string duplicateMetadataId = "MMMMMMMMMMM";
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
          { "videoRenderer": { "videoId": "{{duplicateMetadataId}}", "title": { "simpleText": "Brief" } } },
          { "lockupViewModel": { "contentId": "{{duplicateMetadataId}}", "contentType": "LOCKUP_CONTENT_TYPE_VIDEO",
            "contentImage": { "thumbnailViewModel": { "overlays": [{ "thumbnailBottomOverlayViewModel": {
              "badges": [{ "thumbnailBadgeViewModel": { "text": "LIVE", "badgeStyle": "THUMBNAIL_OVERLAY_BADGE_STYLE_LIVE" } }]
            } }] } }, "metadata": { "lockupMetadataViewModel": { "title": { "content": "Richer Duplicate Title" } } } } },
          { "videoRenderer": { "videoId": "{{firstId}}", "title": { "simpleText": "Duplicate" } } },
          { "videoRenderer": { "videoId": "{{liveId}}", "title": { "simpleText": "Longer normal duplicate title" } } }
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
if (pageVideos.Length != 12) throw new Exception("Shorts should be excluded and duplicate IDs should be merged.");
if (pageVideos.Single(video => video.Id == liveId).AutomaticSkipReason != "currently live stream") throw new Exception("LIVE renderer not classified.");
if (pageVideos.Single(video => video.Id == upcomingId).AutomaticSkipReason != "upcoming/scheduled live stream") throw new Exception("Upcoming renderer not classified.");
if (pageVideos.Single(video => video.Id == streamedId).AutomaticSkipReason != "live stream recording") throw new Exception("Stream recording not classified.");
if (pageVideos.Single(video => video.Id == premiereId).AutomaticSkipReason is not null) throw new Exception("Completed premiere should be allowed.");
if (pageVideos.Single(video => video.Id == titleLiveId).AutomaticSkipReason is not null) throw new Exception("LIVE in title should not be classified.");
if (pageVideos.Single(video => video.Id == lockupUpcomingId).AutomaticSkipReason != "upcoming/scheduled premiere") throw new Exception("Upcoming lockup premiere not classified.");
if (pageVideos.Single(video => video.Id == lockupLiveId).AutomaticSkipReason != "currently live stream") throw new Exception("Live lockup not classified.");
if (pageVideos.Single(video => video.Id == premiereBadgeId).AutomaticSkipReason != "premiere not yet completed") throw new Exception("Premiere badge not classified.");
var mergedDuplicate = pageVideos.Single(video => video.Id == duplicateMetadataId);
Equal("Richer Duplicate Title", mergedDuplicate.Title);
Equal("currently live stream", mergedDuplicate.AutomaticSkipReason);
Equal(duplicateMetadataId, pageVideos[^1].Id);

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

var rssTimeoutHandler = new FakeHandler((request, _) =>
    request.RequestUri!.AbsolutePath.Contains("feeds/videos.xml")
        ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("simulated RSS timeout"))
        : Task.FromResult(FakeHandler.Text(videosHtml)));
var timeoutFallback = await YoutubeFeed.FetchAsync(
    "https://www.youtube.com/@MeidasTouch",
    new HttpClient(rssTimeoutHandler),
    CancellationToken.None);
Equal("channel-page fallback", timeoutFallback.Source);
if (rssTimeoutHandler.Urls.Count != 2 || !rssTimeoutHandler.Urls[1].EndsWith("/@MeidasTouch/videos"))
    throw new Exception("An RSS timeout should fall back to the channel videos page.");

var metadataTimeoutHandler = new FakeHandler((request, _) =>
    request.RequestUri!.AbsolutePath.Contains("feeds/videos.xml")
        ? Task.FromResult(FakeHandler.Text(rssXml))
        : Task.FromException<HttpResponseMessage>(new TaskCanceledException("simulated metadata timeout")));
var rssWithoutMetadata = await YoutubeFeed.FetchAsync(
    "https://www.youtube.com/@MeidasTouch",
    new HttpClient(metadataTimeoutHandler),
    CancellationToken.None);
Equal("RSS", rssWithoutMetadata.Source);
if (rssWithoutMetadata.Uploads.Length != 3)
    throw new Exception("A metadata timeout should retain all RSS uploads.");

var resolutionTimeoutHandler = new FakeHandler((request, _) =>
    request.RequestUri!.AbsolutePath.EndsWith("/videos")
        ? Task.FromResult(FakeHandler.Text(videosHtml))
        : Task.FromException<HttpResponseMessage>(new TaskCanceledException("simulated channel resolution timeout")));
var resolutionTimeoutFallback = await YoutubeFeed.FetchAsync(
    "https://www.youtube.com/@OtherChannel",
    new HttpClient(resolutionTimeoutHandler),
    CancellationToken.None);
Equal("channel-page fallback", resolutionTimeoutFallback.Source);

try
{
    var finalTimeoutHandler = new FakeHandler((request, _) =>
        request.RequestUri!.AbsolutePath.Contains("feeds/videos.xml")
            ? Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound))
            : Task.FromException<HttpResponseMessage>(new TaskCanceledException("simulated fallback timeout")));
    await YoutubeFeed.FetchAsync(
        "https://www.youtube.com/@MeidasTouch",
        new HttpClient(finalTimeoutHandler),
        CancellationToken.None);
    throw new Exception("A final channel-page timeout should be reported as a discovery failure.");
}
catch (HttpRequestException exception) when (exception.Message.Contains("simulated fallback timeout"))
{
}

using (var callerCancellation = new CancellationTokenSource())
{
    callerCancellation.Cancel();
    try
    {
        await YoutubeFeed.FetchAsync(
            "https://www.youtube.com/@MeidasTouch",
            new HttpClient(new FakeHandler(_ => FakeHandler.Text(rssXml))),
            callerCancellation.Token);
        throw new Exception("Caller cancellation should propagate.");
    }
    catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
    {
    }
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

Console.WriteLine("PASS: channel IDs, video order/titles, live filtering, RSS enrichment/fallback, timeout handling, caller cancellation, and total failure");

internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _response;

    internal FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        : this((request, _) => Task.FromResult(response(request)))
    {
    }

    internal FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
    {
        _response = response;
    }

    internal List<string> Urls { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Urls.Add(request.RequestUri!.AbsoluteUri);
        return _response(request, cancellationToken);
    }

    internal static HttpResponseMessage Text(string value) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(value)
    };
}
