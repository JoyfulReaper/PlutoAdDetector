using System.Text.Json;

internal sealed record YoutubeCurrentVideoState(
    string Id,
    string Title,
    double? PlaybackPositionSeconds);

internal sealed record YoutubeQueuedVideoState(
    string Id,
    string Title,
    double DurationSeconds);

internal sealed record YoutubeQueueBrowserState(
    YoutubeCurrentVideoState? CurrentVideo,
    YoutubeQueuedVideoState[] QueuedVideos,
    string[] CompletedOrSkippedVideoIds);

internal sealed record YoutubeQueueStateFile(
    int Version,
    DateTimeOffset SavedAtUtc,
    string ChannelUrl,
    int MinimumDurationSeconds,
    YoutubeCurrentVideoState? CurrentVideo,
    YoutubeQueuedVideoState[] QueuedVideos,
    string[] CompletedOrSkippedVideoIds);

internal static class YoutubeQueueStateSerializer
{
    internal const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    internal static bool ShouldSave(string? youtubeVideoId) => youtubeVideoId is null;

    internal static string Serialize(
        YoutubeQueueBrowserState browserState,
        string channelUrl,
        int minimumDurationSeconds,
        DateTimeOffset savedAtUtc)
    {
        var state = new YoutubeQueueStateFile(
            CurrentVersion,
            savedAtUtc,
            channelUrl,
            minimumDurationSeconds,
            browserState.CurrentVideo,
            browserState.QueuedVideos,
            browserState.CompletedOrSkippedVideoIds);
        return JsonSerializer.Serialize(state, Options);
    }
}
