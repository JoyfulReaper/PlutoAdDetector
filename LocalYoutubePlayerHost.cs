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
    private readonly object _clientTasksLock = new();
    private readonly HashSet<Task> _clientTasks = [];

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

    internal static LocalYoutubePlayerHost Start(
        YoutubeUpload[] candidates,
        int minimumDurationSeconds,
        string? singleVideoId = null,
        YoutubeQueueBrowserState? restoredQueueState = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LocalYoutubePlayerHost(listener, CreateHtml(candidates, minimumDurationSeconds, singleVideoId, restoredQueueState));
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
        catch (Exception exception)
        {
            Console.Error.WriteLine($"local YouTube player host shutdown failed: {exception.Message}");
        }

        Task[] clientTasks;
        lock (_clientTasksLock)
            clientTasks = [.. _clientTasks];
        await Task.WhenAll(clientTasks);

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
            catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }

            TrackClient(RespondAsync(client));
        }
    }

    private void TrackClient(Task task)
    {
        lock (_clientTasksLock)
            _clientTasks.Add(task);

        _ = task.ContinueWith(
            completedTask =>
            {
                _ = completedTask.Exception;
                lock (_clientTasksLock)
                    _clientTasks.Remove(completedTask);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
            catch (Exception exception)
            {
                Console.Error.WriteLine($"local YouTube player response failed: {exception.Message}");
            }
        }
    }


    private static string CreateHtml(
        YoutubeUpload[] candidates,
        int minimumDurationSeconds,
        string? singleVideoId,
        YoutubeQueueBrowserState? restoredQueueState)
    {
        var candidatesJson = JsonSerializer.Serialize(candidates, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var restoredQueueStateJson = restoredQueueState is null
            ? "null"
            : YoutubeQueueStateSerializer.SerializeBrowserState(restoredQueueState);
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
                #keyboard-help {
                  position: fixed;
                  top: 12px;
                  right: 12px;
                  z-index: 10;
                  padding: 8px 10px;
                  border-radius: 6px;
                  background: rgba(0, 0, 0, 0.78);
                  color: #fff;
                  font: 13px/1.5 system-ui, sans-serif;
                  opacity: 0;
                  pointer-events: none;
                  transition: opacity 120ms ease;
                }
                #keyboard-help.visible { opacity: 1; }
              </style>
            </head>
            <body>
              <div id="player"></div>
              <div id="probe"></div>
              <div id="keyboard-help" role="status" aria-live="polite" aria-hidden="true">
                <div>N = next video</div>
                <div>P = pause/resume ad tracking</div>
                <div>R = reload saved queue</div>
                <div>T = teach source visual (source tab)</div>
              </div>
              <script>
                const candidates = {{candidatesJson}};
                const minimumDuration = {{minimumDurationSeconds}};
                const singleVideoId = {{JsonSerializer.Serialize(singleVideoId)}};
                const restoredQueueState = {{restoredQueueStateJson}};
                let probe;
                let probeReady = false;
                let player;
                let ready = false;
                let lastError = null;
                let queue = [];
                let current = null;
                const completed = new Set();
                const durations = new Map();
                let queueRevision = 0;
                let refreshing = false;
                let pendingRefresh = null;
                let wantsPlayback = false;
                let probeError = null;
                let keyboardHelpTimer = null;
                const log = message => console.log(`youtube queue: ${message}`);
                const delay = ms => new Promise(resolve => setTimeout(resolve, ms));

                function showKeyboardHelp() {
                  const overlay = document.getElementById('keyboard-help');
                  if (!overlay) return;
                  overlay.classList.add('visible');
                  overlay.setAttribute('aria-hidden', 'false');
                  if (keyboardHelpTimer !== null) clearTimeout(keyboardHelpTimer);
                  keyboardHelpTimer = setTimeout(() => {
                    overlay.classList.remove('visible');
                    overlay.setAttribute('aria-hidden', 'true');
                    keyboardHelpTimer = null;
                  }, 2500);
                }

                function selectCurrent() {
                  current = queue[0] || null;
                  if (!current) {
                    lastError = 'Queue empty; waiting for the next feed refresh.';
                    player.pauseVideo();
                    log(lastError);
                    return;
                  }
                  lastError = null;
                  log(`selected ${current.title} [${current.id}] (${Math.round(current.duration)} seconds)`);
                  player.unMute();
                  if (wantsPlayback) player.loadVideoById(current.id);
                  else player.cueVideoById(current.id);
                }

                async function refreshQueue(items, requestedRevision = queueRevision) {
                  if (singleVideoId) return;
                  if (!ready || !probeReady || refreshing) {
                    pendingRefresh = { items, revision: requestedRevision };
                    return;
                  }
                  if (requestedRevision !== queueRevision) {
                    log(`discarded stale refresh (revision ${requestedRevision}; current ${queueRevision})`);
                    return;
                  }
                  const refreshRevision = requestedRevision;
                  refreshing = true;
                  try {
                  const valid = [];
                  const probedDurations = new Map();
                  const seen = new Set();
                  for (const item of items) {
                    const { id, title } = item;
                    if (seen.has(id) || completed.has(id) || id === current?.id) continue;
                    seen.add(id);
                    if (item.automaticSkipReason) {
                      log(`skipped ${title} [${id}] (duration not checked): ${item.automaticSkipReason}`);
                      continue;
                    }
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
                      const videoData = probe.getVideoData?.();
                      if (videoData?.video_id === id) {
                        duration = probe.getDuration();
                        if (Number.isFinite(duration) && duration > 0) break;
                      }
                    }
                    probe.pauseVideo();
                    if (!probeError && Number.isFinite(duration) && duration > 0) probedDurations.set(id, duration);
                    }
                    if (!probeError && Number.isFinite(duration) && duration >= minimumDuration) {
                      valid.push({ ...item, duration });
                    } else {
                      log(`skipped ${title} [${id}] (${duration > 0 ? Math.round(duration) + ' seconds' : 'duration unknown'}): ${probeError || (duration > 0 ? 'below minimum duration' : 'duration unavailable')}`);
                    }
                  }
                  if (refreshRevision !== queueRevision) {
                    log(`discarded stale refresh (revision ${refreshRevision}; current ${queueRevision})`);
                    return;
                  }
                  for (const [id, duration] of probedDurations) durations.set(id, duration);
                  // Re-read current/completed after async probing: playback may have advanced.
                  const retained = queue.filter(item => !valid.some(fresh => fresh.id === item.id));
                  const waiting = [...valid, ...retained]
                    .filter(item => !completed.has(item.id) && item.id !== current?.id)
                    .filter((item, position, all) => all.findIndex(candidate => candidate.id === item.id) === position);
                  queue = [...(current ? [current] : []), ...waiting].slice(0, 20);
                  if (!current) selectCurrent();
                  log(`refreshed (${queue.length}/20): ` + queue.map(item =>
                    `${item.id === current?.id ? '[current] ' : ''}${item.title} [${item.id}] (${Math.round(item.duration)} seconds)`).join(' | '));
                  } catch (error) { log(`refresh failed (queue retained): ${error.message || error}`); }
                  finally {
                    refreshing = false;
                    if (pendingRefresh) {
                      const next = pendingRefresh;
                      pendingRefresh = null;
                      void refreshQueue(next.items, next.revision);
                    }
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

                function skipCurrent() {
                  if (singleVideoId) {
                    const message = 'manual skipping is unavailable in single-video mode';
                    log(message);
                    return { success: false, error: message };
                  }
                  if (!ready) return { success: false, error: 'YouTube IFrame player is not ready.' };
                  if (!current) {
                    const message = 'manual skip requested with no current queued video';
                    log(message);
                    return { success: false, error: message };
                  }
                  const skipped = current;
                  completed.add(skipped.id);
                  queue = queue.filter(item => item.id !== skipped.id);
                  log(`manually skipped ${skipped.title} [${skipped.id}]`);
                  selectCurrent();
                  return { success: true, error: null };
                }

                function restoreQueue(state) {
                  if (singleVideoId) return { success: false, error: 'Queue restore is unavailable in single-video mode.' };
                  if (!ready) return { success: false, error: 'YouTube IFrame player is not ready.' };
                  if (!state || !Array.isArray(state.queuedVideos) || !Array.isArray(state.completedOrSkippedVideoIds))
                    return { success: false, error: 'Saved queue state is invalid.' };
                  try {
                    queueRevision += 1;
                    const playerState = player.getPlayerState?.();
                    const resumeAfterRestore = playerState === YT.PlayerState.PLAYING ||
                      playerState === YT.PlayerState.BUFFERING ||
                      (playerState === undefined && wantsPlayback);
                    wantsPlayback = resumeAfterRestore;
                    completed.clear();
                    for (const id of state.completedOrSkippedVideoIds) completed.add(id);
                    durations.clear();
                    const seen = new Set();
                    queue = state.queuedVideos
                      .filter(item => item && !seen.has(item.id) && !completed.has(item.id) && seen.add(item.id))
                      .map(item => {
                        durations.set(item.id, item.durationSeconds);
                        return { id: item.id, title: item.title, duration: item.durationSeconds };
                      });
                    const savedCurrent = state.currentVideo;
                    current = savedCurrent ? queue.find(item => item.id === savedCurrent.id) || null : null;
                    if (current) {
                      current = { ...current, title: savedCurrent.title };
                      queue = [current, ...queue.filter(item => item.id !== current.id)];
                    }
                    else if (queue.length) current = queue[0];

                    lastError = current ? null : 'Queue empty; waiting for the next feed refresh.';
                    if (current) {
                      const startSeconds = Number.isFinite(savedCurrent?.playbackPositionSeconds)
                        ? Math.max(0, savedCurrent.playbackPositionSeconds)
                        : 0;
                      player.unMute();
                      const request = { videoId: current.id, startSeconds };
                      if (resumeAfterRestore) player.loadVideoById(request);
                      else player.cueVideoById(request);
                    }
                    log(`restored (${queue.length}/20): ` + queue.map(item =>
                      `${item.id === current?.id ? '[current] ' : ''}${item.title} [${item.id}] (${Math.round(item.duration)} seconds)`).join(' | '));
                    return { success: true, error: null };
                  } catch (error) {
                    return { success: false, error: `${error?.name || 'Error'}: ${error?.message || String(error)}` };
                  }
                }

                window.youtubePlayerControls = {
                  isReady: () => ready,
                  play: () => {
                    if (!ready) return { success: false, error: 'YouTube IFrame player is not ready.' };
                    if (!current) {
                      wantsPlayback = true;
                      return { success: true, error: null };
                    }
                    try {
                      wantsPlayback = true;
                      lastError = null;
                      player.unMute();
                      player.playVideo();
                      return { success: true, error: null };
                    } catch (error) {
                      wantsPlayback = false;
                      lastError = `YouTube play command failed: ${error?.name || 'Error'}: ${error?.message || String(error)}`;
                      return { success: false, error: lastError };
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
                  skip: skipCurrent,
                  restoreQueue,
                  refresh: items => { void refreshQueue(items); },
                  isRefreshActive: () => refreshing || pendingRefresh !== null,
                  getQueue: () => ({ currentId: current?.id || null, videos: queue }),
                  getQueueState: () => {
                    let playbackPositionSeconds = null;
                    try {
                      const value = player?.getCurrentTime?.();
                      if (Number.isFinite(value) && value >= 0) playbackPositionSeconds = value;
                    } catch { /* Position is optional during best-effort shutdown. */ }
                    const savedVideo = item => ({
                      id: item.id,
                      title: item.title,
                      durationSeconds: Number.isFinite(item.duration) ? item.duration : 0
                    });
                    return {
                      currentVideo: current ? {
                        id: current.id,
                        title: current.title,
                        playbackPositionSeconds
                      } : null,
                      queuedVideos: queue.map(savedVideo),
                      completedOrSkippedVideoIds: [...completed]
                    };
                  }
                };

                window.addEventListener('keydown', event => {
                  if (event.repeat || event.ctrlKey || event.altKey || event.metaKey) return;
                  if (event.code === 'KeyN') {
                    event.preventDefault();
                    skipCurrent();
                  } else if (event.code === 'KeyH' || event.key === '?') {
                    event.preventDefault();
                    showKeyboardHelp();
                  } else if (event.code === 'KeyR') {
                    event.preventDefault();
                    void window.requestYoutubeQueueRestore?.();
                  }
                });
                window.addEventListener('message', event => {
                  if (event.data === 'pluto-ad-detector:skip-youtube-video') skipCurrent();
                  else if (event.data === 'pluto-ad-detector:show-youtube-help') showKeyboardHelp();
                  else if (event.data === 'pluto-ad-detector:reload-youtube-queue')
                    void window.requestYoutubeQueueRestore?.();
                });

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
                        if (singleVideoId) {
                          current = { id: singleVideoId, title: singleVideoId };
                          queue = [current];
                          player.cueVideoById(singleVideoId);
                          log(`single-video override ${singleVideoId}; RSS and duration filtering disabled`);
                          return;
                        }
                        if (restoredQueueState) restoreQueue(restoredQueueState);
                        else void refreshQueue(candidates);
                      },
                      onStateChange: event => {
                        if (event.data === YT.PlayerState.ENDED && current && !singleVideoId) {
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
                  if (singleVideoId) return;
                  probe = new YT.Player('probe', {
                    width: '200', height: '200',
                    playerVars: { autoplay: 0, controls: 0, origin: window.location.origin },
                    events: {
                      onReady: () => {
                        probeReady = true;
                        probe.mute();
                        const pending = pendingRefresh;
                        pendingRefresh = null;
                        if (pending) void refreshQueue(pending.items, pending.revision);
                        else if (!restoredQueueState && candidates.length) void refreshQueue(candidates);
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
