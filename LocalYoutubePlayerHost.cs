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

    internal static LocalYoutubePlayerHost Start(string[] candidates)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LocalYoutubePlayerHost(listener, CreateHtml(candidates));
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


    private static string CreateHtml(string[] candidates)
    {
        var candidatesJson = JsonSerializer.Serialize(candidates);
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
              </style>
            </head>
            <body>
              <div id="player"></div>
              <script>
                const candidates = {{candidatesJson}};
                let player;
                let ready = false;
                let lastError = null;
                const queue = [];
                let index = -1;
                let preparing = true;
                let wantsPlayback = false;
                let probeError = null;
                const log = message => console.log(`youtube queue: ${message}`);
                const delay = ms => new Promise(resolve => setTimeout(resolve, ms));

                function selectCurrent() {
                  if (index >= queue.length) {
                    lastError = 'Queue exhausted; restart to fetch recent uploads.';
                    log(lastError);
                    return;
                  }
                  lastError = null;
                  log(`selected ${queue[index].id} (${Math.round(queue[index].duration)} seconds)`);
                  player.unMute();
                  if (wantsPlayback) player.loadVideoById(queue[index].id);
                  else player.cueVideoById(queue[index].id);
                }

                async function prepareQueue() {
                  for (const id of candidates) {
                    probeError = null;
                    player.mute();
                    player.loadVideoById(id);
                    let duration = 0;
                    // Metadata is not available immediately. Verify the loaded ID so a
                    // previous candidate's duration cannot admit a short video.
                    for (let attempt = 0; attempt < 60; attempt++) {
                      await delay(250);
                      if (probeError) break;
                      if (player.getVideoData().video_id === id) {
                        duration = player.getDuration();
                        if (Number.isFinite(duration) && duration > 0) break;
                      }
                    }
                    player.pauseVideo();
                    if (!probeError && Number.isFinite(duration) && duration >= 300) {
                      queue.push({ id, duration });
                      log(`added ${id} (${Math.round(duration)} seconds)`);
                    } else {
                      log(`skipped ${id}: ${probeError || (duration > 0 ? 'under 5 minutes' : 'duration unavailable')}`);
                    }
                  }
                  preparing = false;
                  index = 0;
                  log(`${queue.length} eligible videos, newest first`);
                  selectCurrent();
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
                      if (preparing) return { success: true, error: null };
                      if (index >= queue.length) return { success: false, error: lastError || 'Queue exhausted.' };
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
                      if (!preparing) player.pauseVideo();
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
                    playerVars: {
                      autoplay: 0,
                      controls: 1,
                      playsinline: 1,
                      origin: window.location.origin
                    },
                    events: {
                      onReady: () => {
                        ready = true;
                        prepareQueue().catch(error => {
                          preparing = false;
                          index = queue.length;
                          player.pauseVideo();
                          lastError = `Queue preparation failed: ${error.message || error}`;
                          log(lastError);
                        });
                      },
                      onStateChange: event => {
                        if (preparing) return;
                        if (event.data === YT.PlayerState.ENDED && index < queue.length) {
                          index++;
                          selectCurrent();
                        } else if (event.data === YT.PlayerState.PLAYING && !wantsPlayback) {
                          player.pauseVideo();
                        }
                      },
                      onError: event => {
                        const detail = errorNames[event.data] || 'Unknown YouTube player error';
                        const message = `YouTube IFrame API error ${event.data}: ${detail}`;
                        if (preparing) probeError = message;
                        else lastError = message;
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
