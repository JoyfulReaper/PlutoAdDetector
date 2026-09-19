using System.Diagnostics;
using System.Text.Json;
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

    private const int TargetSampleCount = TrainingDurationMilliseconds / SampleIntervalMilliseconds;

    private const string NormalizeScreenshotScript = """
        async base64 => {
          const binary = atob(base64);
          const bytes = new Uint8Array(binary.length);
          for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
          const bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
          try {
            const canvas = typeof OffscreenCanvas === 'function'
              ? new OffscreenCanvas(9, 8)
              : Object.assign(document.createElement('canvas'), { width: 9, height: 8 });
            const context = canvas.getContext('2d', { willReadFrequently: true });
            if (!context) throw new Error('Could not create a 2D canvas context.');
            context.drawImage(bitmap, 0, 0, 9, 8);
            const pixels = context.getImageData(0, 0, 9, 8).data;
            const luminance = [];
            for (let index = 0; index < pixels.length; index += 4) {
              luminance.push(Math.round(
                (pixels[index] * 0.299) +
                (pixels[index + 1] * 0.587) +
                (pixels[index + 2] * 0.114)));
            }
            return luminance;
          } finally {
            bitmap.close?.();
          }
        }
        """;

    internal static async Task<VisualSignatureTrainingResult> TrainAsync(
        IPage sourcePage,
        CancellationToken cancellationToken)
    {
        var fingerprints = new List<string>(TargetSampleCount);
        string? lastCaptureError = null;
        var timer = Stopwatch.StartNew();

        for (var sampleIndex = 0; sampleIndex < TargetSampleCount; sampleIndex++)
        {
            var targetTime = TimeSpan.FromMilliseconds(sampleIndex * SampleIntervalMilliseconds);
            var delay = targetTime - timer.Elapsed;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);
            else if (sampleIndex > 0 && timer.Elapsed >= TimeSpan.FromMilliseconds(TrainingDurationMilliseconds))
                break;

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fingerprint = await CaptureFingerprintAsync(sourcePage);
                if (fingerprint is not null)
                    fingerprints.Add(fingerprint);
                else
                    lastCaptureError = "source player was not visible";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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

    private static async Task<string?> CaptureFingerprintAsync(IPage sourcePage)
    {
        var detectionJson = await sourcePage.EvaluateAsync<string>(PlutoDetectionScript.Script, "focused");
        var sample = JsonSerializer.Deserialize<TrainingDetectionSample>(detectionJson, JsonOptions.Instance);
        var bounds = sample?.PlayerBounds;
        if (sample?.HasPlayer is not true || bounds is null || bounds.Width < 2 || bounds.Height < 2)
            return null;

        var screenshot = await sourcePage.ScreenshotAsync(new PageScreenshotOptions
        {
            Clip = new Clip
            {
                X = bounds.X,
                Y = bounds.Y,
                Width = bounds.Width,
                Height = bounds.Height
            }
        });
        var luminance = await sourcePage.EvaluateAsync<int[]>(
            NormalizeScreenshotScript,
            Convert.ToBase64String(screenshot));
        return VisualFingerprint.CreateDHash64(luminance);
    }

    private sealed record TrainingDetectionSample(bool HasPlayer, DetectionBounds PlayerBounds);
}
