var startupCompleted = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
var policy = new YoutubeQueueRefreshPolicy(startupCompleted);

Equal(false, policy.CooldownElapsed(startupCompleted.AddMinutes(4).AddSeconds(59)));
Equal(false, policy.ShouldRefresh(0, startupCompleted.AddMinutes(4).AddSeconds(59)));
Equal(false, policy.ShouldRefresh(6, startupCompleted.AddMinutes(5)));
Equal(true, policy.ShouldRefresh(5, startupCompleted.AddMinutes(5)));
Equal(true, policy.ShouldRefresh(0, startupCompleted.AddMinutes(5)));

var failedAttemptCompleted = startupCompleted.AddMinutes(6);
policy.RecordAttemptCompleted(failedAttemptCompleted);
Equal(false, policy.ShouldRefresh(2, failedAttemptCompleted.AddMinutes(4).AddSeconds(59)));
Equal(true, policy.ShouldRefresh(2, failedAttemptCompleted.AddMinutes(5)));
Equal(true, YoutubeQueueRefreshPolicy.IsAutomaticMode(null));
Equal(false, YoutubeQueueRefreshPolicy.IsAutomaticMode("AAAAAAAAAAA"));
Console.WriteLine("PASS: low-queue threshold, completion-based cooldown, and single-video discovery exclusion");

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}, got {actual}");
}
