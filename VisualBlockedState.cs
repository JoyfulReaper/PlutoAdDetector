internal sealed record VisualBlockedState(
    string SignatureId,
    string SignatureName,
    DateTimeOffset StartedAt,
    DateTimeOffset Deadline);

internal sealed record VisualBlockStartDecision(
    VisualBlockedState? State,
    bool Started);

internal static class VisualBlockPolicy
{
    internal static readonly TimeSpan DomHandoffGracePeriod = TimeSpan.FromSeconds(30);

    internal static VisualBlockStartDecision TryStart(
        VisualBlockedState? current,
        LearnedVisualMatch match,
        DateTimeOffset now,
        bool trackingPaused,
        bool domAdConfirmed)
    {
        if (trackingPaused || domAdConfirmed || current is not null)
            return new(current, false);

        return new(
            new VisualBlockedState(
                match.SignatureId,
                match.SignatureName,
                now,
                now.Add(DomHandoffGracePeriod)),
            true);
    }

    internal static bool ShouldTimeout(
        VisualBlockedState? current,
        bool domAdConfirmed,
        DateTimeOffset now) =>
        current is not null && !domAdConfirmed && now >= current.Deadline;

    internal static bool RequiresSwitchForDomStart(VisualBlockedState? current) => current is null;
}
