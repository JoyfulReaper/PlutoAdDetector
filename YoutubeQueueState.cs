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

    internal static string SerializeBrowserState(YoutubeQueueBrowserState browserState) =>
        JsonSerializer.Serialize(browserState, Options);

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

internal enum YoutubeQueueRestoreStatus
{
    Succeeded,
    Skipped,
    Failed
}

internal sealed record YoutubeQueueRestoreResult(
    YoutubeQueueRestoreStatus Status,
    string Reason,
    YoutubeQueueBrowserState? BrowserState,
    bool IsWarning = false);

internal static class YoutubeQueueStateLoader
{
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromDays(7);

    internal static YoutubeQueueRestoreResult Load(
        string path,
        string channelUrl,
        int minimumDurationSeconds,
        DateTimeOffset now)
    {
        if (!File.Exists(path))
            return new(YoutubeQueueRestoreStatus.Skipped, $"{path} does not exist", null);

        try
        {
            return Parse(File.ReadAllText(path), channelUrl, minimumDurationSeconds, now);
        }
        catch (Exception exception)
        {
            return new(YoutubeQueueRestoreStatus.Failed, $"could not load {path}: {exception.Message}", null);
        }
    }

    internal static YoutubeQueueRestoreResult Parse(
        string json,
        string channelUrl,
        int minimumDurationSeconds,
        DateTimeOffset now)
    {
        YoutubeQueueStateFile? state;
        try
        {
            state = JsonSerializer.Deserialize<YoutubeQueueStateFile>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return new(YoutubeQueueRestoreStatus.Failed, $"invalid JSON: {exception.Message}", null);
        }

        if (state is null)
            return new(YoutubeQueueRestoreStatus.Failed, "JSON did not contain a queue state object", null);
        if (state.Version != YoutubeQueueStateSerializer.CurrentVersion)
            return new(YoutubeQueueRestoreStatus.Skipped,
                $"unsupported queue-state version {state.Version}; expected {YoutubeQueueStateSerializer.CurrentVersion}", null, true);
        if (state.SavedAtUtc == default)
            return new(YoutubeQueueRestoreStatus.Failed, "save timestamp is missing", null);
        if (state.SavedAtUtc > now.AddMinutes(5))
            return new(YoutubeQueueRestoreStatus.Failed, "save timestamp is in the future", null);
        if (now - state.SavedAtUtc > MaximumAge)
            return new(YoutubeQueueRestoreStatus.Skipped,
                $"snapshot is stale ({Math.Floor((now - state.SavedAtUtc).TotalDays)} days old; maximum is {MaximumAge.TotalDays:0})", null, true);
        if (!CompatibleChannel(state.ChannelUrl, channelUrl))
            return new(YoutubeQueueRestoreStatus.Skipped,
                $"saved channel URL '{state.ChannelUrl}' is incompatible with current channel URL '{channelUrl}'", null, true);
        if (state.MinimumDurationSeconds != minimumDurationSeconds)
            return new(YoutubeQueueRestoreStatus.Skipped,
                $"saved minimum duration {state.MinimumDurationSeconds} seconds is incompatible with current value {minimumDurationSeconds} seconds", null, true);
        if (state.QueuedVideos is null || state.CompletedOrSkippedVideoIds is null)
            return new(YoutubeQueueRestoreStatus.Failed, "queued videos or completed/skipped IDs are missing", null);
        if (state.QueuedVideos.Length > 20)
            return new(YoutubeQueueRestoreStatus.Failed, "saved queue exceeds the 20-video limit", null);
        if (state.QueuedVideos.Any(video => video is null || !ValidId(video.Id) || string.IsNullOrWhiteSpace(video.Title) ||
            !double.IsFinite(video.DurationSeconds) || video.DurationSeconds < minimumDurationSeconds))
            return new(YoutubeQueueRestoreStatus.Failed,
                "saved queue contains an invalid video or one below the configured minimum duration", null);
        if (state.QueuedVideos.Select(video => video.Id).Distinct(StringComparer.Ordinal).Count() != state.QueuedVideos.Length)
            return new(YoutubeQueueRestoreStatus.Failed, "saved queue contains duplicate video IDs", null);
        if (state.CompletedOrSkippedVideoIds.Any(id => !ValidId(id)) ||
            state.CompletedOrSkippedVideoIds.Distinct(StringComparer.Ordinal).Count() != state.CompletedOrSkippedVideoIds.Length)
            return new(YoutubeQueueRestoreStatus.Failed, "completed/skipped IDs are invalid or duplicated", null);
        if (state.QueuedVideos.Any(video => state.CompletedOrSkippedVideoIds.Contains(video.Id, StringComparer.Ordinal)))
            return new(YoutubeQueueRestoreStatus.Failed, "a queued video is also marked completed/skipped", null);

        if (state.CurrentVideo is null && state.QueuedVideos.Length > 0)
            return new(YoutubeQueueRestoreStatus.Failed, "current video is missing from a non-empty queue", null);
        if (state.CurrentVideo is not null)
        {
            var current = state.CurrentVideo;
            if (!ValidId(current.Id) || string.IsNullOrWhiteSpace(current.Title) ||
                current.PlaybackPositionSeconds is < 0 ||
                current.PlaybackPositionSeconds is double position && !double.IsFinite(position))
                return new(YoutubeQueueRestoreStatus.Failed, "current video or playback position is invalid", null);
            if (state.QueuedVideos.Length == 0 || state.QueuedVideos[0].Id != current.Id)
                return new(YoutubeQueueRestoreStatus.Failed, "current video is not first in the saved queue", null);
            if (state.CompletedOrSkippedVideoIds.Contains(current.Id, StringComparer.Ordinal))
                return new(YoutubeQueueRestoreStatus.Failed, "current video is also marked completed/skipped", null);
        }

        var browserState = new YoutubeQueueBrowserState(
            state.CurrentVideo,
            state.QueuedVideos,
            state.CompletedOrSkippedVideoIds);
        return new(YoutubeQueueRestoreStatus.Succeeded,
            $"restored {state.QueuedVideos.Length} queued videos from snapshot saved {state.SavedAtUtc:O}", browserState);
    }

    private static bool CompatibleChannel(string saved, string current)
    {
        if (!Uri.TryCreate(saved, UriKind.Absolute, out var savedUri) ||
            !Uri.TryCreate(current, UriKind.Absolute, out var currentUri))
            return false;
        return CanonicalChannel(savedUri) == CanonicalChannel(currentUri);
    }

    private static string CanonicalChannel(Uri uri)
    {
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.StartsWith("/@", StringComparison.OrdinalIgnoreCase))
            path = path.ToLowerInvariant();
        return $"{uri.Scheme.ToLowerInvariant()}://{uri.IdnHost.ToLowerInvariant()}{path}";
    }

    private static bool ValidId(string? id) => id is { Length: 11 } &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
