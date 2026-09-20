using System.Text.Json;
using Microsoft.Playwright;

internal sealed class YoutubeQueueStateSaver : IAsyncDisposable
{
    internal static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan CacheRefreshInterval = TimeSpan.FromSeconds(15);

    private readonly Func<Task<YoutubeQueueBrowserState?>> _captureSnapshot;
    private readonly string _path;
    private readonly string _channelUrl;
    private readonly int _minimumDurationSeconds;
    private readonly string? _youtubeVideoId;
    private YoutubeQueueBrowserState? _cachedSnapshot;

    internal YoutubeQueueStateSaver(
        IPage youtubePage,
        string path,
        string channelUrl,
        int minimumDurationSeconds,
        string? youtubeVideoId)
        : this(
            () => CaptureBrowserStateAsync(youtubePage),
            path,
            channelUrl,
            minimumDurationSeconds,
            youtubeVideoId)
    {
    }

    // The delegate overload keeps cached-fallback behavior testable without a
    // live browser. Production always uses the IPage constructor above.
    internal YoutubeQueueStateSaver(
        Func<Task<YoutubeQueueBrowserState?>> captureSnapshot,
        string path,
        string channelUrl,
        int minimumDurationSeconds,
        string? youtubeVideoId)
    {
        _captureSnapshot = captureSnapshot;
        _path = path;
        _channelUrl = channelUrl;
        _minimumDurationSeconds = minimumDurationSeconds;
        _youtubeVideoId = youtubeVideoId;
    }

    internal async Task<bool> RefreshSnapshotAsync()
    {
        if (!YoutubeQueueStateSerializer.ShouldSave(_youtubeVideoId))
            return false;

        var result = await TryCaptureSnapshotAsync();
        if (result.Snapshot is null)
            return false;

        _cachedSnapshot = result.Snapshot;
        return true;
    }

    internal void CacheSnapshot(YoutubeQueueBrowserState snapshot)
    {
        if (YoutubeQueueStateSerializer.ShouldSave(_youtubeVideoId))
            _cachedSnapshot = snapshot;
    }

    public async ValueTask DisposeAsync()
    {
        if (!YoutubeQueueStateSerializer.ShouldSave(_youtubeVideoId))
            return;

        try
        {
            var fresh = await TryCaptureSnapshotAsync();
            var snapshot = fresh.Snapshot;
            var snapshotSource = "fresh shutdown snapshot";
            if (snapshot is not null)
            {
                _cachedSnapshot = snapshot;
            }
            else
            {
                snapshot = _cachedSnapshot;
                snapshotSource = "cached fallback snapshot";
            }

            if (snapshot is null)
            {
                var detail = fresh.Error is null ? string.Empty : $": {fresh.Error.Message}";
                Console.Error.WriteLine(
                    $"youtube queue state save failed (shutdown continuing): no usable queue snapshot{detail}");
                return;
            }

            var contents = YoutubeQueueStateSerializer.Serialize(
                snapshot,
                _channelUrl,
                _minimumDurationSeconds,
                DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(_path, contents).WaitAsync(SnapshotTimeout);
            Console.Error.WriteLine($"youtube queue state saved using {snapshotSource}: {_path}");
            if (fresh.Error is not null)
                Console.Error.WriteLine($"youtube queue shutdown snapshot unavailable; cached state preserved: {fresh.Error.Message}");
        }
        catch (Exception exception)
        {
            // Persistence is diagnostic/future restore state and must never block shutdown.
            Console.Error.WriteLine($"youtube queue state save failed (shutdown continuing): {exception.Message}");
        }
    }

    private async Task<SnapshotCaptureResult> TryCaptureSnapshotAsync()
    {
        try
        {
            var snapshot = await _captureSnapshot().WaitAsync(SnapshotTimeout);
            return snapshot is null
                ? new(null, new InvalidOperationException("Local YouTube queue state is unavailable."))
                : new(snapshot, null);
        }
        catch (Exception exception)
        {
            return new(null, exception);
        }
    }

    private static async Task<YoutubeQueueBrowserState?> CaptureBrowserStateAsync(IPage youtubePage)
    {
        var json = await youtubePage.EvaluateAsync<string>(
            """
            () => JSON.stringify(window.youtubePlayerControls?.getQueueState?.() ?? null)
            """);
        return JsonSerializer.Deserialize<YoutubeQueueBrowserState>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    private sealed record SnapshotCaptureResult(
        YoutubeQueueBrowserState? Snapshot,
        Exception? Error);
}
