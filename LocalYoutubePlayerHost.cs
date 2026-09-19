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

    internal static LocalYoutubePlayerHost Start(YoutubeUpload[] candidates, int minimumDurationSeconds)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LocalYoutubePlayerHost(listener, CreateHtml(candidates, minimumDurationSeconds));
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


    private static string CreateHtml(YoutubeUpload[] candidates, int minimumDurationSeconds)
    {
        var candidatesJson = JsonSerializer.Serialize(candidates, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>PlutoAdDetector YouTube Player</title>
              <style>
                html, body, #player {
                  width: 100%;
                  height: 100%;
                  margin: 0;
                  padding: 0;
                  overflow: hidden;
                  background: #000;
                }
                #player { display: block; border: 0; }
                #probe { position: absolute; left: -1000px; top: 0; width: 200px; height: 200px; border: 0; }
              </style>
            </head>
            <body>
              <div id="player"></div>
              <div id="probe"></div>
              <script>
                const candidates = {{candidatesJson}};
                const minimumDuration = {{minimumDurationSeconds}};
                let probe;
                let probeReady = false;
                let player;
                let ready = false;
                let lastError = null;
                let queue = [];
                let current = null;
                const completed = new Set();
                const durations = new Map();
                let refreshing = false;
                let pendingRefresh = null;
                let wantsPlayback = false;
                let probeError = null;
                const log = message => console.log(`youtube queue: ${message}`);
                const delay = ms => new Promise(resolve => setTimeout(resolve, ms));

                function selectCurrent() {
                  current = queue[0] || null;
                  if (!current) {
                    lastError = 'Queue empty; waiting for the next feed refresh.';
                    log(lastError);
                    return;
                  }
                  lastError = null;
                  log(`selected ${current.title} [${current.id}] (${Math.round(current.duration)} seconds)`);
                  player.unMute();
                  if (wantsPlayback) player.loadVideoById(current.id);
                  else player.cueVideoById(current.id);
                }

                async function refreshQueue(items) {
                  if (!ready || !probeReady || refreshing) { pendingRefresh = items; return; }
                  refreshing = true;
                  try {
                  const valid = [];
                  const seen = new Set();
                  for (const item of [...items].sort((a, b) => Date.parse(b.published) - Date.parse(a.published))) {
                    const { id, title } = item;
                    if (seen.has(id) || completed.has(id) || id === current?.id) continue;
                    seen.add(id);
                    probeError = null;
                    let duration = durations.get(id) || 0;
                    if (!duration) {
                    probe.mute();
                    probe.loadVideoById(id);
                    // Metadata is not available immediately. Verify the loaded ID so a
                    // previous candidate's duration cannot admit a short video.
                    for (let attempt = 0; attempt < 60; attempt++) {
                      await delay(250);
                      if (probeError) break;
                      if (probe.getVideoData().video_id === id) {
                        duration = probe.getDuration();
                        if (Number.isFinite(duration) && duration > 0) break;
                      }
                    }
                    probe.pauseVideo();
                    if (!probeError && Number.isFinite(duration) && duration > 0) durations.set(id, duration);
                    }
                    if (!probeError && Number.isFinite(duration) && duration >= minimumDuration) {
                      valid.push({ ...item, duration });
                    } else {
                      log(`skipped ${title} [${id}] (${duration > 0 ? Math.round(duration) + ' seconds' : 'duration unknown'}): ${probeError || (duration > 0 ? 'below minimum duration' : 'duration unavailable')}`);
                    }
                  }
                  // Re-read current/completed after async probing: playback may have advanced.
                  const merged = new Map([...queue, ...valid].map(item => [item.id, item]));
                  const waiting = [...merged.values()].filter(item => !completed.has(item.id) && item.id !== current?.id)
                    .sort((a, b) => Date.parse(b.published) - Date.parse(a.published));
                  queue = [...(current ? [current] : []), ...waiting].slice(0, 20);
                  if (!current) selectCurrent();
                  log(`refreshed (${queue.length}/20): ` + queue.map(item =>
                    `${item.id === current?.id ? '[current] ' : ''}${item.title} [${item.id}] (${Math.round(item.duration)} seconds)`).join(' | '));
                  } catch (error) { log(`refresh failed (queue retained): ${error.message || error}`); }
                  finally {
                    refreshing = false;
                    if (pendingRefresh) { const next = pendingRefresh; pendingRefresh = null; void refreshQueue(next); }
                  }
                }

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
                      wantsPlayback = true;
                      if (!current) return { success: true, error: null };
                      lastError = null;
                      player.unMute();
                      player.playVideo();
                      return { success: true, error: null };
                    } catch (error) {
                      return { success: false, error: `${error?.name || 'Error'}: ${error?.message || String(error)}` };
                    }
                  },
                  pause: () => {
                    wantsPlayback = false;
                    if (!ready) return { success: false, error: 'YouTube IFrame player is not ready.' };
                    try {
                      player.pauseVideo();
                      return { success: true, error: null };
                    } catch (error) {
                      return { success: false, error: `${error?.name || 'Error'}: ${error?.message || String(error)}` };
                    }
                  },
                  getError: () => lastError,
                  refresh: items => { void refreshQueue(items); },
                  getQueue: () => ({ currentId: current?.id || null, videos: queue })
                };

                window.onYouTubeIframeAPIReady = () => {
                  player = new YT.Player('player', {
                    width: '100%',
                    height: '100%',
                    playerVars: {
                      autoplay: 0,
                      controls: 1,
                      playsinline: 1,
                      origin: window.location.origin
                    },
                    events: {
                      onReady: () => {
                        ready = true;
                        void refreshQueue(candidates);
                      },
                      onStateChange: event => {
                        if (event.data === YT.PlayerState.ENDED && current) {
                          completed.add(current.id);
                          queue = queue.filter(item => item.id !== current.id);
                          selectCurrent();
                        } else if (event.data === YT.PlayerState.PLAYING && !wantsPlayback) {
                          player.pauseVideo();
                        }
                      },
                      onError: event => {
                        const detail = errorNames[event.data] || 'Unknown YouTube player error';
                        const message = `YouTube IFrame API error ${event.data}: ${detail}`;
                        lastError = message;
                      }
                    }
                  });
                  probe = new YT.Player('probe', {
                    width: '200', height: '200',
                    playerVars: { autoplay: 0, controls: 0, origin: window.location.origin },
                    events: {
                      onReady: () => {
                        probeReady = true;
                        probe.mute();
                        const items = pendingRefresh || candidates;
                        pendingRefresh = null;
                        void refreshQueue(items);
                      },
                      onError: event => { probeError = `YouTube IFrame API error ${event.data}: ${errorNames[event.data] || 'Unknown error'}`; }
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
