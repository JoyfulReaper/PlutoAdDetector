using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;

internal sealed record VisualFrameSample(
    string Fingerprint,
    DetectionBounds PlayerBounds,
    string VideoElementIdentity,
    double SamplingMilliseconds);

internal sealed class VisualSamplingUnsupportedException(string message) : Exception(message);

internal static class VisualFrameSampler
{
    internal const int NormalizedWidth = VisualFingerprint.NormalizedWidth;
    internal const int NormalizedHeight = VisualFingerprint.NormalizedHeight;

    // Both training and runtime matching call this exact script. It samples decoded
    // video pixels rather than a composed page screenshot, so DOM overlays are excluded.
    // The full frame is deliberately stretched to 9x8 for the existing dHash format.
    internal const string DirectVideoSampleScript = """
        () => {
          const visible = (element, rect) => {
            if (!rect || rect.width < 1 || rect.height < 1) return false;
            if (typeof element.checkVisibility === 'function' &&
                !element.checkVisibility({
                  checkOpacity: true,
                  opacityProperty: true,
                  checkVisibilityCSS: true,
                  visibilityProperty: true,
                  contentVisibilityAuto: true
                })) return false;
            const style = getComputedStyle(element);
            return style.display !== 'none' && style.visibility !== 'hidden' &&
                   Number(style.opacity || 1) > 0;
          };

          const videos = [...document.querySelectorAll('video')]
            .map(element => ({ element, rect: element.getBoundingClientRect() }))
            .filter(item => visible(item.element, item.rect));
          const player = videos.sort((a, b) =>
            (b.rect.width * b.rect.height) - (a.rect.width * a.rect.height))[0];
          if (!player) return JSON.stringify({ status: 'unavailable' });

          const video = player.element;
          const rect = player.rect;
          const samplerStateKey = '__plutoAdDetectorVisualSamplerState';
          let samplerState = globalThis[samplerStateKey];
          if (!samplerState || !(samplerState.videoIds instanceof WeakMap)) {
            samplerState = {
              documentId: typeof globalThis.crypto?.randomUUID === 'function'
                ? globalThis.crypto.randomUUID()
                : `${Date.now()}-${Math.random()}`,
              videoIds: new WeakMap(),
              nextVideoId: 1
            };
            globalThis[samplerStateKey] = samplerState;
          }
          let videoId = samplerState.videoIds.get(video);
          if (videoId === undefined) {
            videoId = samplerState.nextVideoId++;
            samplerState.videoIds.set(video, videoId);
          }
          const videoElementIdentity = `${samplerState.documentId}:${videoId}`;
          const playerBounds = {
            x: Math.max(0, Math.min(innerWidth, rect.left)),
            y: Math.max(0, Math.min(innerHeight, rect.top)),
            width: Math.max(0, Math.min(innerWidth, rect.right) - Math.max(0, rect.left)),
            height: Math.max(0, Math.min(innerHeight, rect.bottom) - Math.max(0, rect.top))
          };
          if (playerBounds.width < 2 || playerBounds.height < 2 ||
              video.readyState < 2 || video.videoWidth < 1 || video.videoHeight < 1) {
            return JSON.stringify({ status: 'unavailable', playerBounds });
          }

          try {
            const canvas = typeof OffscreenCanvas === 'function'
              ? new OffscreenCanvas(9, 8)
              : Object.assign(document.createElement('canvas'), { width: 9, height: 8 });
            const context = canvas.getContext('2d', { willReadFrequently: true });
            if (!context) throw new Error('Could not create a 2D canvas context.');
            context.drawImage(video, 0, 0, 9, 8);
            const pixels = context.getImageData(0, 0, 9, 8).data;
            const luminance = [];
            for (let index = 0; index < pixels.length; index += 4) {
              luminance.push(Math.round(
                (pixels[index] * 0.299) +
                (pixels[index + 1] * 0.587) +
                (pixels[index + 2] * 0.114)));
            }
            return JSON.stringify({ status: 'ok', playerBounds, videoElementIdentity, luminance });
          } catch (error) {
            const errorName = error?.name || 'Error';
            const status = errorName === 'InvalidStateError' ? 'unavailable' : 'unsupported';
            return JSON.stringify({
              status,
              playerBounds,
              errorName,
              error: error?.message || String(error)
            });
          }
        }
        """;

    internal static async Task<VisualFrameSample?> CaptureAsync(IPage sourcePage)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var json = await sourcePage.EvaluateAsync<string>(DirectVideoSampleScript);
        var result = JsonSerializer.Deserialize<DirectVideoSampleResult>(json, JsonOptions.Instance)
            ?? throw new VisualSamplingUnsupportedException("Direct video sampler returned no result.");
        if (result.Status == "unavailable")
            return null;
        if (result.Status != "ok")
        {
            var detail = string.IsNullOrWhiteSpace(result.Error)
                ? result.ErrorName ?? "unknown canvas error"
                : $"{result.ErrorName ?? "Error"}: {result.Error}";
            throw new VisualSamplingUnsupportedException(
                $"direct video-frame pixel access is unsupported ({detail})");
        }
        if (result.PlayerBounds is null ||
            string.IsNullOrWhiteSpace(result.VideoElementIdentity) ||
            result.Luminance is null)
            throw new VisualSamplingUnsupportedException("Direct video sampler returned incomplete pixel data.");

        return new(
            VisualFingerprint.CreateDHash64(result.Luminance),
            result.PlayerBounds,
            result.VideoElementIdentity,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
    }

    private sealed record DirectVideoSampleResult(
        string Status,
        DetectionBounds? PlayerBounds,
        string? VideoElementIdentity,
        int[]? Luminance,
        string? ErrorName,
        string? Error);
}
