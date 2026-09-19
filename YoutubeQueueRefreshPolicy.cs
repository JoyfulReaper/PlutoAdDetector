internal sealed class YoutubeQueueRefreshPolicy(DateTimeOffset initialAttemptCompletedAt)
{
    internal const int RemainingVideoThreshold = 5;
    internal static readonly TimeSpan AttemptCooldown = TimeSpan.FromMinutes(5);

    private DateTimeOffset _nextAttemptAt = initialAttemptCompletedAt.Add(AttemptCooldown);

    internal static bool IsAutomaticMode(string? youtubeVideoId) => youtubeVideoId is null;

    internal bool CooldownElapsed(DateTimeOffset now) => now >= _nextAttemptAt;

    internal bool ShouldRefresh(int remainingVideos, DateTimeOffset now) =>
        remainingVideos <= RemainingVideoThreshold && CooldownElapsed(now);

    internal void RecordAttemptCompleted(DateTimeOffset completedAt)
    {
        _nextAttemptAt = completedAt.Add(AttemptCooldown);
    }
}
