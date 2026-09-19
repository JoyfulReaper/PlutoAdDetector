using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

internal sealed class LocalYoutubePlayerHost : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly byte[] _response;
    private readonly Task _serverTask;

    private LocalYoutubePlayerHost(TcpListener listener, string html)
    {
        _listener = listener;
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Url = new Uri($"http://127.0.0.1:{port}/");

        var body = Encoding.UTF8.GetBytes(html);
        var headers = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n");
        _response = [.. headers, .. body];
        _serverTask = ServeAsync();
    }

    internal Uri Url { get; }

    internal static LocalYoutubePlayerHost Start(string videoUrl)
    {
        var videoId = GetVideoId(videoUrl);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LocalYoutubePlayerHost(listener, CreateHtml(videoId));
    }

    internal static string GetVideoId(string videoUrl)
    {
        var uri = new Uri(videoUrl, UriKind.Absolute);
        var host = uri.Host.ToLowerInvariant();

        if (host is "youtu.be" or "www.youtu.be")
        {
            return ValidateVideoId(uri.AbsolutePath.Trim('/'));
        }

        if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com")
        {
            if (uri.AbsolutePath.Equals("/watch", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = pair.Split('=', 2);
                    if (parts.Length == 2 && parts[0].Equals("v", StringComparison.OrdinalIgnoreCase))
                    {
                        return ValidateVideoId(Uri.UnescapeDataString(parts[1]));
                    }
                }
            }

            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 &&
                (segments[0].Equals("embed", StringComparison.OrdinalIgnoreCase) ||
                 segments[0].Equals("shorts", StringComparison.OrdinalIgnoreCase)))
            {
                return ValidateVideoId(segments[1]);
            }
        }

        throw new ArgumentException("--youtube-url must identify a YouTube watch, short, embed, or youtu.be video.");
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        _listener.Stop();

        try
        {
            await _serverTask;
        }
        catch (OperationCanceledException)
        {
        }

        _shutdown.Dispose();
    }

    private async Task ServeAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
            }
            catch (SocketException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }

            _ = RespondAsync(client);
        }
    }

    private async Task RespondAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                string? line;
                do
                {
                    line = await reader.ReadLineAsync(_shutdown.Token);
                }
                while (!string.IsNullOrEmpty(line));

                await stream.WriteAsync(_response, _shutdown.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
        }
    }

    private static string ValidateVideoId(string value)
    {
        if (value.Length is < 6 or > 20 || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("--youtube-url contains an invalid YouTube video ID.");
        }

        return value;
    }

    private static string CreateHtml(string videoId)
    {
        var videoIdJson = JsonSerializer.Serialize(videoId);
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>PlutoAdDetector YouTube Player</title>
              <style>
                html, body { width: 100%; height: 100%; margin: 0; background: #000; overflow: hidden; }
                #player { width: 100%; height: 100%; }
                #status { position: fixed; left: 12px; bottom: 12px; z-index: 10; padding: 6px 9px;
                          border-radius: 4px; color: #fff; background: rgba(0, 0, 0, .72);
                          font: 13px system-ui, sans-serif; pointer-events: none; }
              </style>
            </head>
            <body>
              <div id="player"></div>
              <div id="status">Loading YouTube player…</div>
              <script>
                const videoId = {{videoIdJson}};
                const status = document.getElementById('status');
                let player;
                let ready = false;
                let lastError = null;

                const errorNames = {
                  2: 'Invalid video ID or parameter',
                  5: 'HTML5 player error',
                  100: 'Video not found or private',
                  101: 'Embedding is not allowed by the video owner',
                  150: 'Embedding is not allowed by the video owner',
                  153: 'Missing or invalid playback client identity'
                };

                window.youtubePlayerControls = {
                  isReady: () => ready,
                  play: () => {
                    if (!ready) return { success: false, error: 'YouTube IFrame player is not ready.' };
                    try {
                      lastError = null;
                      player.playVideo();
                      return { success: true, error: null };
                    } catch (error) {
                      return { success: false, error: `${error?.name || 'Error'}: ${error?.message || String(error)}` };
                    }
                  },
                  pause: () => {
                    if (!ready) return { success: false, error: 'YouTube IFrame player is not ready.' };
                    try {
                      player.pauseVideo();
                      return { success: true, error: null };
                    } catch (error) {
                      return { success: false, error: `${error?.name || 'Error'}: ${error?.message || String(error)}` };
                    }
                  },
                  getError: () => lastError
                };

                window.onYouTubeIframeAPIReady = () => {
                  player = new YT.Player('player', {
                    width: '100%',
                    height: '100%',
                    videoId,
                    playerVars: {
                      autoplay: 0,
                      controls: 1,
                      playsinline: 1,
                      origin: window.location.origin
                    },
                    events: {
                      onReady: () => {
                        ready = true;
                        player.pauseVideo();
                        status.textContent = 'YouTube ready';
                      },
                      onStateChange: event => {
                        if (event.data === YT.PlayerState.PLAYING) status.textContent = 'YouTube playing';
                        if (event.data === YT.PlayerState.PAUSED) status.textContent = 'YouTube paused';
                        if (event.data === YT.PlayerState.ENDED) status.textContent = 'YouTube ended';
                      },
                      onError: event => {
                        const detail = errorNames[event.data] || 'Unknown YouTube player error';
                        lastError = `YouTube IFrame API error ${event.data}: ${detail}`;
                        status.textContent = lastError;
                      }
                    }
                  });
                };
              </script>
              <script src="https://www.youtube.com/iframe_api"></script>
            </body>
            </html>
            """;
    }
}
