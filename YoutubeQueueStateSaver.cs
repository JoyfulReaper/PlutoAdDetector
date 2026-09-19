using System.Text.Json;
using Microsoft.Playwright;

internal sealed class YoutubeQueueStateSaver(
    IPage youtubePage,
    string path,
    string channelUrl,
    int minimumDurationSeconds,
    string? youtubeVideoId) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        if (!YoutubeQueueStateSerializer.ShouldSave(youtubeVideoId))
            return;

        try
        {
            var json = await youtubePage.EvaluateAsync<string>(
                """
                () => JSON.stringify(window.youtubePlayerControls?.getQueueState?.() ?? null)
                """).WaitAsync(TimeSpan.FromSeconds(2));
            var browserState = JsonSerializer.Deserialize<YoutubeQueueBrowserState>(json, JsonOptions.Instance)
                ?? throw new InvalidOperationException("Local YouTube queue state is unavailable.");
            var contents = YoutubeQueueStateSerializer.Serialize(
                browserState,
                channelUrl,
                minimumDurationSeconds,
                DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(path, contents).WaitAsync(TimeSpan.FromSeconds(2));
            Console.Error.WriteLine($"youtube queue state saved: {path}");
        }
        catch (Exception exception)
        {
            // Persistence is diagnostic/future restore state and must never block shutdown.
            Console.Error.WriteLine($"youtube queue state save failed (shutdown continuing): {exception.Message}");
        }
    }
}
