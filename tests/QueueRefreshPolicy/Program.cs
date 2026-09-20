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

var discoveryGeneration = new YoutubeFeedDiscoveryGeneration();

// A discovery captured before a successful manual restore is stale.
var discoveryBeforeRestore = discoveryGeneration.Capture();
discoveryGeneration.RecordSuccessfulQueueRestore();
Equal(false, discoveryGeneration.IsCurrent(discoveryBeforeRestore));

// A failed restore does not advance the generation, so the discovery remains usable.
var discoveryBeforeFailedRestore = discoveryGeneration.Capture();
Equal(true, discoveryGeneration.IsCurrent(discoveryBeforeFailedRestore));

// Normal discovery remains current while no successful restore occurs.
var normalDiscovery = discoveryGeneration.Capture();
Equal(true, discoveryGeneration.IsCurrent(normalDiscovery));

// Discarding stale results still records completion and enforces the cooldown.
var staleAttemptCompleted = startupCompleted.AddMinutes(12);
policy.RecordAttemptCompleted(staleAttemptCompleted);
Equal(false, policy.CooldownElapsed(staleAttemptCompleted.AddMinutes(4).AddSeconds(59)));
Equal(true, policy.CooldownElapsed(staleAttemptCompleted.AddMinutes(5)));

Console.WriteLine("PASS: low-queue threshold, completion cooldown, discovery restore generation, and single-video exclusion");

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}, got {actual}");
}
