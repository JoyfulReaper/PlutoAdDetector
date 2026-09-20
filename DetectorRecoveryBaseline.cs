internal sealed class DetectorRecoveryBaseline
{
    internal bool IsAwaiting { get; private set; }
    internal int CleanSampleCount { get; private set; }

    internal void Begin()
    {
        IsAwaiting = true;
        CleanSampleCount = 0;
    }

    internal bool Observe(bool isAd, int requiredCleanSamples)
    {
        if (!IsAwaiting)
            return false;

        if (isAd)
        {
            CleanSampleCount = 0;
            return false;
        }

        CleanSampleCount++;
        if (CleanSampleCount < requiredCleanSamples)
            return false;

        IsAwaiting = false;
        CleanSampleCount = 0;
        return true;
    }
}
