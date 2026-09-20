using System.Text.Json;
using Microsoft.Playwright;

internal sealed record VisualFrameSample(string Fingerprint, DetectionBounds PlayerBounds);

internal static class VisualFrameSampler
{
    internal const int NormalizedWidth = VisualFingerprint.NormalizedWidth;
    internal const int NormalizedHeight = VisualFingerprint.NormalizedHeight;

    // Both training and runtime matching call this exact script. It deliberately
    // stretches the full player crop to 9x8: dHash compares relative neighboring
    // luminance, and using one shared normalization path makes window size irrelevant.
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

    internal static async Task<VisualFrameSample?> CaptureAsync(IPage sourcePage)
    {
        var detectionJson = await sourcePage.EvaluateAsync<string>(PlutoDetectionScript.Script, "focused");
        var sample = JsonSerializer.Deserialize<FrameDetectionSample>(detectionJson, JsonOptions.Instance);
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
        return new(VisualFingerprint.CreateDHash64(luminance), bounds);
    }

    private sealed record FrameDetectionSample(bool HasPlayer, DetectionBounds PlayerBounds);
}
