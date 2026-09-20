internal static class VisualRuntimeState
{
    internal static void ResetBoundary(
        VisualSequenceMatcher matcher,
        ref long generation,
        ref LearnedVisualMatch? pendingMatch)
    {
        generation++;
        pendingMatch = null;
        matcher.ResetProgressions();
    }

    internal static bool IsCaptureCurrent(long captureGeneration, long currentGeneration) =>
        captureGeneration == currentGeneration;
}
