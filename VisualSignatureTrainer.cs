using System.Diagnostics;
using Microsoft.Playwright;

internal sealed record VisualSignatureTrainingResult(
    LearnedVisualSignature? Signature,
    int UsableSamples,
    string? Error);

internal static class VisualSignatureTrainer
{
    internal const int TrainingDurationMilliseconds = 6_000;
    internal const int SampleIntervalMilliseconds = 250;
    internal const int MinimumUsableSamples = 8;
    internal const string VideoElementChangedError = "source video element changed";

    private const int TargetSampleCount = TrainingDurationMilliseconds / SampleIntervalMilliseconds;

    internal static async Task<VisualSignatureTrainingResult> TrainAsync(
        IPage sourcePage,
        CancellationToken cancellationToken) =>
        await TrainAsync(
            _ => VisualFrameSampler.CaptureAsync(sourcePage),
            TargetSampleCount,
            SampleIntervalMilliseconds,
            cancellationToken);

    internal static async Task<VisualSignatureTrainingResult> TrainAsync(
        Func<CancellationToken, Task<VisualFrameSample?>> captureAsync,
        int targetSampleCount,
        int sampleIntervalMilliseconds,
        CancellationToken cancellationToken)
    {
        var fingerprints = new List<string>(targetSampleCount);
        string? lastCaptureError = null;
        DetectionBounds? initialBounds = null;
        string? videoElementIdentity = null;
        var timer = Stopwatch.StartNew();

        for (var sampleIndex = 0; sampleIndex < targetSampleCount; sampleIndex++)
        {
            var targetTime = TimeSpan.FromMilliseconds(sampleIndex * sampleIntervalMilliseconds);
            var delay = targetTime - timer.Elapsed;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);
            else if (sampleIndex > 0 && timer.Elapsed >= TimeSpan.FromMilliseconds(TrainingDurationMilliseconds))
                break;

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var sample = await captureAsync(cancellationToken);
                if (sample is not null)
                {
                    if (videoElementIdentity is null)
                    {
                        videoElementIdentity = sample.VideoElementIdentity;
                    }
                    else if (!string.Equals(
                        videoElementIdentity,
                        sample.VideoElementIdentity,
                        StringComparison.Ordinal))
                    {
                        return new(null, fingerprints.Count, VideoElementChangedError);
                    }

                    if (initialBounds is null)
                    {
                        initialBounds = sample.PlayerBounds;
                        Console.WriteLine(
                            $"visual training capture: player={FormatBounds(initialBounds)} " +
                            $"normalized={VisualFrameSampler.NormalizedWidth}x{VisualFrameSampler.NormalizedHeight} " +
                            $"sampleLatency={sample.SamplingMilliseconds:0.#}ms");
                    }
                    fingerprints.Add(sample.Fingerprint);
                }
                else
                    lastCaptureError = "source player was not visible";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (VisualSamplingUnsupportedException)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastCaptureError = exception.Message;
            }
        }

        if (fingerprints.Count < MinimumUsableSamples)
        {
            var detail = lastCaptureError is null ? string.Empty : $" Last capture error: {lastCaptureError}";
            return new(
                null,
                fingerprints.Count,
                $"only {fingerprints.Count} usable samples were captured; at least {MinimumUsableSamples} are required.{detail}");
        }

        var createdAt = DateTimeOffset.UtcNow;
        var signature = new LearnedVisualSignature(
            Guid.NewGuid().ToString("D"),
            $"Learned visual {createdAt:yyyy-MM-dd HH:mm:ss 'UTC'}",
            createdAt,
            SampleIntervalMilliseconds,
            VisualSignatureStore.CurrentFingerprintFormat,
            VisualSignatureStore.CurrentFingerprintVersion,
            [.. fingerprints]);
        return new(signature, fingerprints.Count, null);
    }

    private static string FormatBounds(DetectionBounds bounds) =>
        $"({bounds.X:0.#},{bounds.Y:0.#}) {bounds.Width:0.#}x{bounds.Height:0.#}";
}
