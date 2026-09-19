using System.Text.Json;

var savedAt = new DateTimeOffset(2026, 9, 19, 12, 34, 56, TimeSpan.Zero);
var browserState = new YoutubeQueueBrowserState(
    new YoutubeCurrentVideoState("AAAAAAAAAAA", "Current title", 42.5),
    [
        new YoutubeQueuedVideoState("AAAAAAAAAAA", "Current title", 601.25),
        new YoutubeQueuedVideoState("BBBBBBBBBBB", "Next title", 777)
    ],
    ["CCCCCCCCCCC", "DDDDDDDDDDD"]);

var json = YoutubeQueueStateSerializer.Serialize(
    browserState,
    "https://www.youtube.com/@MeidasTouch",
    300,
    savedAt);
using var document = JsonDocument.Parse(json);
var root = document.RootElement;

Equal(1, root.GetProperty("version").GetInt32());
Equal(savedAt, root.GetProperty("savedAtUtc").GetDateTimeOffset());
Equal("https://www.youtube.com/@MeidasTouch", root.GetProperty("channelUrl").GetString());
Equal(300, root.GetProperty("minimumDurationSeconds").GetInt32());
Equal("AAAAAAAAAAA", root.GetProperty("currentVideo").GetProperty("id").GetString());
Equal("Current title", root.GetProperty("currentVideo").GetProperty("title").GetString());
Equal(42.5, root.GetProperty("currentVideo").GetProperty("playbackPositionSeconds").GetDouble());
Equal(2, root.GetProperty("queuedVideos").GetArrayLength());
Equal(601.25, root.GetProperty("queuedVideos")[0].GetProperty("durationSeconds").GetDouble());
Equal("DDDDDDDDDDD", root.GetProperty("completedOrSkippedVideoIds")[1].GetString());
Equal(true, YoutubeQueueStateSerializer.ShouldSave(null));
Equal(false, YoutubeQueueStateSerializer.ShouldSave("AAAAAAAAAAA"));
Console.WriteLine("PASS: versioned automatic queue state schema and single-video exclusion");

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}, got {actual}");
}
