internal sealed record LearnedVisualMatch(string SignatureId, string SignatureName);

internal sealed class VisualSequenceMatcher
{
    // Initial tuning values for 64-bit dHash. PLUTO_VISUAL_DEBUG=1 logs real
    // distances so these can be adjusted from evidence instead of guesswork.
    internal const int PerFrameHammingThreshold = 10;
    internal const int RuntimeSampleIntervalMilliseconds = 500;
    internal const int DiagnosticNearDistance = 16;
    internal const int RequiredOrderedMatches = 4;
    internal const int MaximumReferenceAdvance = 4;
    internal const int AllowedRuntimeMisses = 2;
    internal const int MinimumDistinctEvidence = 3;
    internal const int MinimumIndependentEvidenceHammingDistance = 3;
    internal const int MinimumReferenceSpan = 3;
    internal const int RollingHistoryLength = 16;
    internal const int RearmHammingDistance = 14;
    internal const int RearmDissimilarSamples = 6;
    internal static readonly TimeSpan MatchCooldown = TimeSpan.FromSeconds(15);

    private const int MaximumActiveAlignments = 96;

    private readonly bool _debugEnabled;
    private readonly Action<string>? _debugLog;
    private MatcherState[] _states = [];
    private long _runtimeSampleNumber;
    private DateTimeOffset? _firstRuntimeSampleAt;

    internal VisualSequenceMatcher(
        IEnumerable<LearnedVisualSignature> signatures,
        bool debugEnabled = false,
        Action<string>? debugLog = null)
    {
        _debugEnabled = debugEnabled;
        _debugLog = debugLog;
        ReplaceSignatures(signatures);
    }

    internal void ReplaceSignatures(IEnumerable<LearnedVisualSignature> signatures)
    {
        _states = signatures.Select(signature => new MatcherState(
            signature,
            signature.FrameFingerprints.Select(VisualFingerprint.ParseDHash64).ToArray())).ToArray();
        _runtimeSampleNumber = 0;
        _firstRuntimeSampleAt = null;
    }

    internal void ResetProgressions()
    {
        foreach (var state in _states.Where(state => state.Armed))
            state.ResetProgression();
    }

    internal IReadOnlyList<LearnedVisualMatch> AddSample(string fingerprint, DateTimeOffset capturedAt)
    {
        var runtimeFingerprint = VisualFingerprint.ParseDHash64(fingerprint);
        _runtimeSampleNumber++;
        _firstRuntimeSampleAt ??= capturedAt;
        var elapsed = capturedAt - _firstRuntimeSampleAt.Value;
        var matches = new List<LearnedVisualMatch>();

        foreach (var state in _states)
        {
            var closest = FindClosest(runtimeFingerprint, state.ReferenceFrames, 0, state.ReferenceFrames.Length - 1);

            if (!state.Armed)
            {
                var cooldownElapsed = capturedAt - state.LastMatchAt >= MatchCooldown;
                if (cooldownElapsed && closest.Distance > RearmHammingDistance)
                    state.DissimilarSamples++;
                else
                    state.DissimilarSamples = 0;

                var cooldownReason = cooldownElapsed
                    ? $"stalled: waiting for {RearmDissimilarSamples - state.DissimilarSamples} dissimilar samples"
                    : "stalled: cooldown";
                if (state.DissimilarSamples >= RearmDissimilarSamples)
                {
                    state.Armed = true;
                    state.ResetProgression();
                    cooldownReason = "reset: re-armed after cooldown and dissimilar content";
                }

                LogDiagnostic(state, closest, default, default, null, elapsed, cooldownReason);
                continue;
            }

            var previousBest = BestAlignment(state.ActiveAlignments);
            var expectedRange = ForwardRange(previousBest, state.ReferenceFrames.Length);
            var bestForward = FindClosest(
                runtimeFingerprint,
                state.ReferenceFrames,
                expectedRange.Start,
                expectedRange.End);

            var next = AdvanceAlignments(state, runtimeFingerprint);
            state.ActiveAlignments = next;
            var currentBest = BestAlignment(next);
            var matching = next
                .Where(IsConfidentMatch)
                .OrderByDescending(candidate => candidate.OrderedMatches)
                .ThenByDescending(candidate => candidate.DistinctEvidenceCount)
                .ThenBy(candidate => candidate.TotalDistance)
                .FirstOrDefault();

            string reason;
            if (matching is not null)
            {
                matches.Add(new(state.Signature.Id, state.Signature.Name));
                state.Armed = false;
                state.LastMatchAt = capturedAt;
                state.DissimilarSamples = 0;
                reason = "advanced: matched; entering cooldown";
                currentBest = matching;
                state.ResetProgression();
            }
            else
            {
                reason = DescribeTransition(previousBest, currentBest);
            }

            LogDiagnostic(state, closest, expectedRange, bestForward, currentBest, elapsed, reason);
        }

        return matches;
    }

    private List<Alignment> AdvanceAlignments(MatcherState state, ulong runtimeFingerprint)
    {
        var candidates = new List<Alignment>();

        // Every sufficiently close reference may begin a new hypothesis. Keeping
        // alternatives is what prevents one ambiguous global nearest frame from
        // committing the matcher to the wrong temporal path.
        for (var referenceIndex = 0; referenceIndex < state.ReferenceFrames.Length; referenceIndex++)
        {
            var distance = VisualFingerprint.HammingDistance(runtimeFingerprint, state.ReferenceFrames[referenceIndex]);
            if (distance <= PerFrameHammingThreshold)
                candidates.Add(Alignment.Start(referenceIndex, distance, _runtimeSampleNumber, runtimeFingerprint));
        }

        foreach (var alignment in state.ActiveAlignments)
        {
            if (_runtimeSampleNumber - alignment.StartRuntimeSampleNumber >= RollingHistoryLength)
                continue;

            if (alignment.RuntimeMisses < AllowedRuntimeMisses)
                candidates.Add(alignment with { RuntimeMisses = alignment.RuntimeMisses + 1 });

            var range = ForwardRange(alignment, state.ReferenceFrames.Length);
            for (var referenceIndex = range.Start; referenceIndex <= range.End; referenceIndex++)
            {
                var distance = VisualFingerprint.HammingDistance(runtimeFingerprint, state.ReferenceFrames[referenceIndex]);
                if (distance <= PerFrameHammingThreshold)
                    candidates.Add(alignment.Advance(referenceIndex, distance, runtimeFingerprint));
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.OrderedMatches)
            .ThenByDescending(candidate => candidate.DistinctEvidenceCount)
            .ThenBy(candidate => candidate.RuntimeMisses)
            .ThenByDescending(candidate => candidate.ReferenceSpan)
            .ThenBy(candidate => candidate.TotalDistance)
            .Take(MaximumActiveAlignments)
            .ToList();
    }

    private static string DescribeTransition(Alignment? previous, Alignment? current)
    {
        if (current is null)
            return previous is null
                ? "stalled: no anchor within threshold"
                : "reset: no viable ordered alignment";
        if (previous is null)
            return $"restarted: began at reference {current.LastReferenceIndex}";
        if (current.StartRuntimeSampleNumber > previous.StartRuntimeSampleNumber)
            return $"restarted: selected reference {current.LastReferenceIndex}";
        if (current.OrderedMatches > previous.OrderedMatches)
            return $"advanced: reference {current.LastReferenceIndex}";
        if (current.RuntimeMisses > previous.RuntimeMisses)
            return $"stalled: miss {current.RuntimeMisses}/{AllowedRuntimeMisses}";
        return "stalled: retained alternate alignment";
    }

    private void LogDiagnostic(
        MatcherState state,
        ClosestFrame closest,
        ReferenceRange forwardRange,
        ClosestFrame bestForward,
        Alignment? progression,
        TimeSpan elapsed,
        string reason)
    {
        if (!_debugEnabled || _debugLog is null ||
            (closest.Distance > DiagnosticNearDistance && progression is null &&
             !reason.StartsWith("reset", StringComparison.Ordinal)))
        {
            return;
        }

        var rangeText = forwardRange.IsValid ? $"{forwardRange.Start}-{forwardRange.End}" : "none";
        var forwardText = bestForward.Index >= 0 ? $"{bestForward.Index}/{bestForward.Distance}" : "none";
        _debugLog(
            $"visual debug: {state.Signature.Name} runtime={_runtimeSampleNumber} time={elapsed.TotalSeconds:0.0}s " +
            $"best={closest.Index}/{closest.Distance} threshold={PerFrameHammingThreshold} " +
            $"forward={rangeText} bestForward={forwardText} " +
            $"progression={progression?.OrderedMatches ?? 0} misses={progression?.RuntimeMisses ?? 0} {reason}");
    }

    private static bool IsConfidentMatch(Alignment alignment) =>
        alignment.OrderedMatches >= RequiredOrderedMatches &&
        alignment.DistinctEvidenceCount >= MinimumDistinctEvidence &&
        alignment.ReferenceSpan >= MinimumReferenceSpan;

    private static Alignment? BestAlignment(IEnumerable<Alignment> alignments) => alignments
        .OrderByDescending(candidate => candidate.OrderedMatches)
        .ThenByDescending(candidate => candidate.DistinctEvidenceCount)
        .ThenBy(candidate => candidate.RuntimeMisses)
        .ThenByDescending(candidate => candidate.ReferenceSpan)
        .ThenBy(candidate => candidate.TotalDistance)
        .FirstOrDefault();

    private static ReferenceRange ForwardRange(Alignment? alignment, int referenceCount)
    {
        if (referenceCount == 0)
            return default;
        if (alignment is null)
            return new(0, referenceCount - 1);

        var start = alignment.LastReferenceIndex + 1;
        var intervals = alignment.RuntimeMisses + 1;
        var end = Math.Min(referenceCount - 1, alignment.LastReferenceIndex + (MaximumReferenceAdvance * intervals));
        return start <= end ? new(start, end) : default;
    }

    private static ClosestFrame FindClosest(ulong runtime, ulong[] references, int start, int end)
    {
        if (start < 0 || end < start || start >= references.Length)
            return new(-1, int.MaxValue);

        var best = new ClosestFrame(-1, int.MaxValue);
        for (var index = start; index <= end; index++)
        {
            var distance = VisualFingerprint.HammingDistance(runtime, references[index]);
            if (distance < best.Distance)
                best = new(index, distance);
        }

        return best;
    }

    private readonly record struct ClosestFrame(int Index, int Distance);

    private readonly record struct ReferenceRange(int Start, int End)
    {
        internal bool IsValid => End >= Start;
    }

    private sealed record Alignment(
        int FirstReferenceIndex,
        int LastReferenceIndex,
        int OrderedMatches,
        int RuntimeMisses,
        int TotalDistance,
        long StartRuntimeSampleNumber,
        ulong[] DistinctEvidence)
    {
        internal int DistinctEvidenceCount => DistinctEvidence.Length;
        internal int ReferenceSpan => LastReferenceIndex - FirstReferenceIndex;

        internal static Alignment Start(
            int referenceIndex,
            int distance,
            long runtimeSampleNumber,
            ulong fingerprint) =>
            new(referenceIndex, referenceIndex, 1, 0, distance, runtimeSampleNumber, [fingerprint]);

        internal Alignment Advance(int referenceIndex, int distance, ulong fingerprint)
        {
            var independent = DistinctEvidence.All(existing =>
                VisualFingerprint.HammingDistance(existing, fingerprint) >= MinimumIndependentEvidenceHammingDistance);
            var evidence = independent
                ? [.. DistinctEvidence, fingerprint]
                : DistinctEvidence;
            return this with
            {
                LastReferenceIndex = referenceIndex,
                OrderedMatches = OrderedMatches + 1,
                RuntimeMisses = 0,
                TotalDistance = TotalDistance + distance,
                DistinctEvidence = evidence
            };
        }
    }

    private sealed class MatcherState(LearnedVisualSignature signature, ulong[] referenceFrames)
    {
        internal LearnedVisualSignature Signature { get; } = signature;
        internal ulong[] ReferenceFrames { get; } = referenceFrames;
        internal bool Armed { get; set; } = true;
        internal List<Alignment> ActiveAlignments { get; set; } = [];
        internal int DissimilarSamples { get; set; }
        internal DateTimeOffset LastMatchAt { get; set; }

        internal void ResetProgression() => ActiveAlignments.Clear();
    }
}
