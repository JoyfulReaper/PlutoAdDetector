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
    internal const int AllowedRuntimeMisses = 1;
    internal const int RollingHistoryLength = 16;
    internal const int RearmHammingDistance = 14;
    internal const int RearmDissimilarSamples = 6;
    internal static readonly TimeSpan MatchCooldown = TimeSpan.FromSeconds(15);

    private readonly bool _debugEnabled;
    private readonly Action<string>? _debugLog;
    private readonly Queue<ulong> _runtimeHistory = new(RollingHistoryLength);
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
        _runtimeHistory.Clear();
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
        _runtimeHistory.Enqueue(runtimeFingerprint);
        if (_runtimeHistory.Count > RollingHistoryLength)
            _runtimeHistory.Dequeue();

        _runtimeSampleNumber++;
        _firstRuntimeSampleAt ??= capturedAt;
        var elapsed = capturedAt - _firstRuntimeSampleAt.Value;
        var matches = new List<LearnedVisualMatch>();

        foreach (var state in _states)
        {
            var closest = FindClosest(runtimeFingerprint, state.ReferenceFrames, 0, state.ReferenceFrames.Length - 1);
            var reason = "no anchor within threshold";
            var progressionForLog = state.OrderedMatches;

            if (!state.Armed)
            {
                var cooldownElapsed = capturedAt - state.LastMatchAt >= MatchCooldown;
                if (cooldownElapsed && closest.Distance > RearmHammingDistance)
                    state.DissimilarSamples++;
                else
                    state.DissimilarSamples = 0;

                if (state.DissimilarSamples >= RearmDissimilarSamples)
                {
                    state.Armed = true;
                    state.ResetProgression();
                    reason = "re-armed after cooldown and dissimilar content";
                }
                else
                {
                    reason = cooldownElapsed
                        ? $"waiting for {RearmDissimilarSamples - state.DissimilarSamples} dissimilar samples"
                        : "cooldown";
                }

                LogDiagnostic(state, closest, elapsed, progressionForLog, reason);
                continue;
            }

            if (state.OrderedMatches == 0)
            {
                if (closest.Distance <= PerFrameHammingThreshold)
                {
                    state.LastReferenceIndex = closest.Index;
                    state.OrderedMatches = 1;
                    state.RuntimeMisses = 0;
                    reason = $"started at reference {closest.Index}";
                }
            }
            else
            {
                var forwardStart = state.LastReferenceIndex + 1;
                var forwardEnd = Math.Min(
                    state.ReferenceFrames.Length - 1,
                    state.LastReferenceIndex + MaximumReferenceAdvance);
                var forward = FindClosest(runtimeFingerprint, state.ReferenceFrames, forwardStart, forwardEnd);
                if (forward.Index >= 0 && forward.Distance <= PerFrameHammingThreshold)
                {
                    state.LastReferenceIndex = forward.Index;
                    state.OrderedMatches++;
                    state.RuntimeMisses = 0;
                    reason = $"advanced to reference {forward.Index}";
                }
                else if (state.RuntimeMisses < AllowedRuntimeMisses)
                {
                    state.RuntimeMisses++;
                    reason = $"ordered anchor missed ({state.RuntimeMisses}/{AllowedRuntimeMisses})";
                }
                else
                {
                    state.ResetProgression();
                    if (closest.Distance <= PerFrameHammingThreshold)
                    {
                        state.LastReferenceIndex = closest.Index;
                        state.OrderedMatches = 1;
                        reason = $"reset and restarted at reference {closest.Index}";
                    }
                    else
                    {
                        reason = "reset after ordered anchors were lost";
                    }
                }
            }

            progressionForLog = state.OrderedMatches;
            if (state.OrderedMatches >= RequiredOrderedMatches)
            {
                matches.Add(new(state.Signature.Id, state.Signature.Name));
                state.Armed = false;
                state.LastMatchAt = capturedAt;
                state.DissimilarSamples = 0;
                reason = "matched; entering cooldown";
                state.ResetProgression();
            }

            LogDiagnostic(state, closest, elapsed, progressionForLog, reason);
        }

        return matches;
    }

    private void LogDiagnostic(
        MatcherState state,
        ClosestFrame closest,
        TimeSpan elapsed,
        int progression,
        string reason)
    {
        if (!_debugEnabled || _debugLog is null ||
            (closest.Distance > DiagnosticNearDistance && progression == 0 && !reason.StartsWith("reset", StringComparison.Ordinal)))
        {
            return;
        }

        _debugLog(
            $"visual debug: {state.Signature.Name} runtime={_runtimeSampleNumber} time={elapsed.TotalSeconds:0.0}s " +
            $"bestRef={closest.Index} distance={closest.Distance} threshold={PerFrameHammingThreshold} " +
            $"progression={progression} reason={reason}");
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

    private sealed class MatcherState(LearnedVisualSignature signature, ulong[] referenceFrames)
    {
        internal LearnedVisualSignature Signature { get; } = signature;
        internal ulong[] ReferenceFrames { get; } = referenceFrames;
        internal bool Armed { get; set; } = true;
        internal int LastReferenceIndex { get; set; } = -1;
        internal int OrderedMatches { get; set; }
        internal int RuntimeMisses { get; set; }
        internal int DissimilarSamples { get; set; }
        internal DateTimeOffset LastMatchAt { get; set; }

        internal void ResetProgression()
        {
            LastReferenceIndex = -1;
            OrderedMatches = 0;
            RuntimeMisses = 0;
        }
    }
}
