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

var restored = YoutubeQueueStateLoader.Parse(
    json,
    "https://www.youtube.com/@meidastouch/",
    300,
    savedAt.AddHours(1));
Equal(YoutubeQueueRestoreStatus.Succeeded, restored.Status);
Equal("AAAAAAAAAAA", restored.BrowserState?.CurrentVideo?.Id);
Equal(42.5, restored.BrowserState?.CurrentVideo?.PlaybackPositionSeconds);
Equal("BBBBBBBBBBB", restored.BrowserState?.QueuedVideos[1].Id);

var channelMismatch = YoutubeQueueStateLoader.Parse(
    json, "https://www.youtube.com/@OtherChannel", 300, savedAt.AddHours(1));
Equal(YoutubeQueueRestoreStatus.Skipped, channelMismatch.Status);
Equal(true, channelMismatch.IsWarning);
var durationMismatch = YoutubeQueueStateLoader.Parse(
    json, "https://www.youtube.com/@MeidasTouch", 600, savedAt.AddHours(1));
Equal(YoutubeQueueRestoreStatus.Skipped, durationMismatch.Status);
var stale = YoutubeQueueStateLoader.Parse(
    json, "https://www.youtube.com/@MeidasTouch", 300, savedAt.AddDays(8));
Equal(YoutubeQueueRestoreStatus.Skipped, stale.Status);
Equal(true, stale.Reason.Contains("stale", StringComparison.Ordinal));

var unsupportedJson = json.Replace("\"version\": 1", "\"version\": 99", StringComparison.Ordinal);
Equal(YoutubeQueueRestoreStatus.Skipped, YoutubeQueueStateLoader.Parse(
    unsupportedJson, "https://www.youtube.com/@MeidasTouch", 300, savedAt.AddHours(1)).Status);
Equal(YoutubeQueueRestoreStatus.Failed, YoutubeQueueStateLoader.Parse(
    "{not json", "https://www.youtube.com/@MeidasTouch", 300, savedAt.AddHours(1)).Status);
var overlapJson = json.Replace(
    "\"CCCCCCCCCCC\",",
    "\"AAAAAAAAAAA\",",
    StringComparison.Ordinal);
Equal(YoutubeQueueRestoreStatus.Failed, YoutubeQueueStateLoader.Parse(
    overlapJson, "https://www.youtube.com/@MeidasTouch", 300, savedAt.AddHours(1)).Status);
var belowMinimumJson = json.Replace(
    "\"durationSeconds\": 601.25",
    "\"durationSeconds\": 299",
    StringComparison.Ordinal);
Equal(YoutubeQueueRestoreStatus.Failed, YoutubeQueueStateLoader.Parse(
    belowMinimumJson, "https://www.youtube.com/@MeidasTouch", 300, savedAt.AddHours(1)).Status);
var missingPath = Path.Combine(Path.GetTempPath(), $"pluto-queue-missing-{Guid.NewGuid():N}.json");
Equal(YoutubeQueueRestoreStatus.Skipped, YoutubeQueueStateLoader.Load(
    missingPath, "https://www.youtube.com/@MeidasTouch", 300, savedAt.AddHours(1)).Status);
Equal(false, YoutubeQueueRestoreAccess.CanReload(trackingPaused: false, youtubeForegrounded: false));
Equal(true, YoutubeQueueRestoreAccess.CanReload(trackingPaused: true, youtubeForegrounded: false));
Equal(true, YoutubeQueueRestoreAccess.CanReload(trackingPaused: false, youtubeForegrounded: true));
Console.WriteLine("PASS: versioned queue schema, compatible restore, duration/integrity rejection, staleness, and single-video exclusion");

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}, got {actual}");
}
