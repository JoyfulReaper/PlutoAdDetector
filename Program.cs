using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;

DetectorOptions options;
try
{
    options = DetectorOptions.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine(DetectorOptions.Usage);
    return 2;
}

if (options.ShowHelp)
{
    Console.Error.WriteLine(DetectorOptions.Usage);
    return 0;
}

using var shutdown = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};
Console.CancelKeyPress += cancelHandler;

try
{
    await RunAsync(options, shutdown.Token);
    return 0;
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    return 0;
}
catch (PlaywrightException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"fatal error: {exception.Message}");
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

static async Task RunAsync(DetectorOptions options, CancellationToken cancellationToken)
{
    Directory.CreateDirectory(options.CaptureDirectory);
    var visualSignaturePath = Path.GetFullPath(VisualSignatureStore.DefaultFileName);
    var visualSignatureStore = new VisualSignatureStore(visualSignaturePath);
    var visualSignatureLoad = visualSignatureStore.Load();
    var learnedVisualSignatures = visualSignatureLoad.Signatures.ToList();
    var visualDebugEnabled = string.Equals(
        Environment.GetEnvironmentVariable("PLUTO_VISUAL_DEBUG"),
        "1",
        StringComparison.Ordinal);
    var visualMatcher = new VisualSequenceMatcher(
        learnedVisualSignatures,
        visualDebugEnabled,
        message => Console.Error.WriteLine(message));
    foreach (var warning in visualSignatureLoad.Warnings)
        Console.Error.WriteLine($"visual signatures: {warning}");
    if (visualSignatureLoad.Signatures.Count > 0)
        Console.Error.WriteLine($"visual signatures loaded: {visualSignatureLoad.Signatures.Count} from {visualSignaturePath}");
    if (visualDebugEnabled)
        Console.Error.WriteLine("visual debug diagnostics enabled by PLUTO_VISUAL_DEBUG=1");

    var browserProfileDirectory = Path.GetFullPath("browser-profile");
    Directory.CreateDirectory(browserProfileDirectory);
    var sourceUri = new Uri(options.SourceUrl);
    if (!IsPlutoSource(sourceUri))
    {
        Console.Error.WriteLine(
            $"warning: the current automatic ad detector profile is Pluto-specific and may not work for source URL {options.SourceUrl}");
    }
    Console.Error.WriteLine($"pluto scan mode: {options.ScanMode.ToString().ToLowerInvariant()}");
    var automaticQueueMode = YoutubeQueueRefreshPolicy.IsAutomaticMode(options.YoutubeVideoId);
    var queueStatePath = Path.GetFullPath("youtube-queue.json");
    YoutubeQueueBrowserState? startupQueueState = null;
    if (options.Resume)
    {
        var restoreResult = automaticQueueMode
            ? YoutubeQueueStateLoader.Load(
                queueStatePath,
                options.ChannelUrl,
                options.MinimumDurationSeconds,
                DateTimeOffset.UtcNow)
            : new YoutubeQueueRestoreResult(
                YoutubeQueueRestoreStatus.Skipped,
                "--resume is unavailable in --youtube-url single-video mode",
                null);
        LogYoutubeQueueRestoreResult(restoreResult);
        startupQueueState = restoreResult.BrowserState;
    }
    YoutubeUpload[] candidates;
    try
    {
        candidates = automaticQueueMode && startupQueueState is null
            ? (await YoutubeFeed.FetchAsync(options.ChannelUrl, cancellationToken)).Uploads
            : [];
    }
    catch (Exception exception) when (exception is HttpRequestException or System.Xml.XmlException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
    {
        Console.Error.WriteLine($"youtube discovery failed: {exception.Message}");
        candidates = [];
    }
    var initialDiscoveryCompletedAt = DateTimeOffset.UtcNow;
    await using var youtubePlayerHost = LocalYoutubePlayerHost.Start(
        candidates,
        options.MinimumDurationSeconds,
        options.YoutubeVideoId,
        startupQueueState);

    using var playwright = await Playwright.CreateAsync();
    var chromeExecutable = FindInstalledGoogleChrome();
    await using var context = await playwright.Chromium.LaunchPersistentContextAsync(
        browserProfileDirectory,
        new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = options.Headless,
            ExecutablePath = chromeExecutable,
            ChromiumSandbox = true,
            Args = ["--autoplay-policy=no-user-gesture-required"],
            ViewportSize = options.Headless
            ? new ViewportSize { Width = 1440, Height = 900 }
            : ViewportSize.NoViewport,
            Locale = "en-US"
        });
    Console.Error.WriteLine(chromeExecutable is null
        ? "browser: Playwright Chromium"
        : $"browser: Google Chrome ({chromeExecutable})");
    Console.Error.WriteLine($"browser profile: {browserProfileDirectory}");

    var trackingToggleRequests = 0;
    var detectorResetRequests = 0;
    var visualMatchingToggleRequests = 0;
    var queueRestoreRequests = 0;
    var visualTrainingRequests = 0;
    await context.ExposeFunctionAsync("requestAdTrackingToggle", () =>
    {
        Interlocked.Increment(ref trackingToggleRequests);
    });
    await context.ExposeFunctionAsync("requestDetectorReset", () =>
    {
        Interlocked.Increment(ref detectorResetRequests);
    });
    await context.ExposeFunctionAsync("requestVisualMatchingToggle", () =>
    {
        Interlocked.Increment(ref visualMatchingToggleRequests);
    });
    await context.AddInitScriptAsync(script: AdTrackingShortcut.Script);

    var sourcePage = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
    await sourcePage.ExposeFunctionAsync("requestVisualSignatureTraining", () =>
    {
        Interlocked.Increment(ref visualTrainingRequests);
    });
    await sourcePage.GotoAsync(options.SourceUrl, new PageGotoOptions
    {
        WaitUntil = WaitUntilState.DOMContentLoaded,
        Timeout = 90_000
    });

    var youtubePage = await context.NewPageAsync();
    await youtubePage.ExposeFunctionAsync("requestYoutubeQueueRestore", () =>
    {
        Interlocked.Increment(ref queueRestoreRequests);
    });
    youtubePage.Console += (_, message) =>
    {
        if (message.Text.StartsWith("youtube queue:", StringComparison.Ordinal))
            Console.Error.WriteLine(message.Text);
    };
    await youtubePage.GotoAsync(youtubePlayerHost.Url.AbsoluteUri, new PageGotoOptions
    {
        WaitUntil = WaitUntilState.DOMContentLoaded,
        Timeout = 90_000
    });
    await youtubePage.WaitForFunctionAsync(
        "() => window.youtubePlayerControls?.isReady() === true",
        null,
        new PageWaitForFunctionOptions { Timeout = 60_000 });
    await PauseYoutubeAsync(youtubePage);
    await sourcePage.BringToFrontAsync();
    await using var youtubeQueueStateSaver = new YoutubeQueueStateSaver(
        youtubePage,
        queueStatePath,
        options.ChannelUrl,
        options.MinimumDurationSeconds,
        options.YoutubeVideoId);
    if (automaticQueueMode)
        await youtubeQueueStateSaver.RefreshSnapshotAsync();

    bool? publishedState = null;
    bool? pendingState = null;
    var pendingCount = 0;
    var everFoundSemanticIndicator = false;
    var nextCaptureAt = DateTimeOffset.UtcNow;
    var youtubePlaybackExpected = false;
    string? loggedYoutubeError = null;
    var nextYoutubeHealthCheckAt = DateTimeOffset.MinValue;
    var queueRefreshPolicy = new YoutubeQueueRefreshPolicy(initialDiscoveryCompletedAt);
    var nextQueueDepthCheckAt = DateTimeOffset.UtcNow;
    var nextQueueSnapshotAt = DateTimeOffset.UtcNow.Add(YoutubeQueueStateSaver.CacheRefreshInterval);
    Task<YoutubeDiscovery>? feedRefresh = null;
    using var feedRefreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var trackingPaused = false;
    Task<VisualSignatureTrainingResult>? visualTraining = null;
    CancellationTokenSource? visualTrainingCancellation = null;
    long visualTrainingGeneration = 0;
    Task<VisualFrameSample?>? visualRuntimeCapture = null;
    long visualRuntimeCaptureGeneration = 0;
    long visualRuntimeGeneration = 0;
    long visualGeneration = 0;
    var nextVisualRuntimeSampleAt = DateTimeOffset.UtcNow;
    DetectionBounds? lastVisualRuntimeBounds = null;
    string? loggedVisualRuntimeError = null;
    var visualSamplingDisabled = false;
    LearnedVisualMatch? pendingVisualMatch = null;
    VisualBlockedState? visualBlockedState = null;
    var recoveryBaseline = new DetectorRecoveryBaseline();
    var visualMatchingEnabled = !options.NoVisual;
    if (!visualMatchingEnabled)
        Console.WriteLine("visual matching disabled");

    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (Interlocked.Exchange(ref detectorResetRequests, 0) > 0)
            {
                visualGeneration++;
                visualTrainingCancellation?.Cancel();
                Interlocked.Exchange(ref visualTrainingRequests, 0);
                visualBlockedState = null;
                publishedState = null;
                pendingState = null;
                pendingCount = 0;
                VisualRuntimeState.ResetBoundary(
                    visualMatcher,
                    ref visualRuntimeGeneration,
                    ref pendingVisualMatch);
                youtubePlaybackExpected = false;
                await PauseYoutubeAsync(youtubePage);
                loggedYoutubeError = null;
                await sourcePage.BringToFrontAsync();
                await SetSourceMutedAsync(sourcePage, muted: false);
                recoveryBaseline.Begin();
                Console.WriteLine("manual reset: source restored");
                Console.WriteLine("detector waiting for clean baseline");
            }

            var visualToggleCount = Interlocked.Exchange(ref visualMatchingToggleRequests, 0);
            while (visualToggleCount-- > 0)
            {
                visualMatchingEnabled = !visualMatchingEnabled;
                VisualRuntimeState.ResetBoundary(
                    visualMatcher,
                    ref visualRuntimeGeneration,
                    ref pendingVisualMatch);
                Console.WriteLine(visualMatchingEnabled
                    ? "visual matching enabled"
                    : "visual matching disabled");
            }

            if (visualRuntimeCapture?.IsCompleted is true)
            {
                var completedCaptureGeneration = visualRuntimeCaptureGeneration;
                try
                {
                    var runtimeSample = await visualRuntimeCapture;
                    if (!VisualRuntimeState.IsCaptureCurrent(
                            completedCaptureGeneration,
                            visualRuntimeGeneration) ||
                        Volatile.Read(ref detectorResetRequests) > 0 ||
                        Volatile.Read(ref visualMatchingToggleRequests) > 0)
                    {
                        // X or V invalidated this automatic sample while it was in flight.
                    }
                    else if (runtimeSample is null)
                    {
                        visualMatcher.ResetProgressions();
                    }
                    else
                    {
                        if (lastVisualRuntimeBounds is null)
                        {
                            Console.Error.WriteLine(
                                $"visual runtime matching started: signatures={learnedVisualSignatures.Count} " +
                                $"player={FormatBounds(runtimeSample.PlayerBounds)} " +
                                $"normalized={VisualFrameSampler.NormalizedWidth}x{VisualFrameSampler.NormalizedHeight} " +
                                $"interval={VisualSequenceMatcher.RuntimeSampleIntervalMilliseconds}ms " +
                                $"sampleLatency={runtimeSample.SamplingMilliseconds:0.#}ms");
                        }
                        else if (visualDebugEnabled && lastVisualRuntimeBounds != runtimeSample.PlayerBounds)
                        {
                            Console.Error.WriteLine(
                                $"visual debug: player bounds changed from {FormatBounds(lastVisualRuntimeBounds)} " +
                                $"to {FormatBounds(runtimeSample.PlayerBounds)}");
                        }

                        lastVisualRuntimeBounds = runtimeSample.PlayerBounds;
                        loggedVisualRuntimeError = null;
                        foreach (var match in visualMatcher.AddSample(runtimeSample.Fingerprint, DateTimeOffset.UtcNow))
                        {
                            Console.WriteLine($"learned visual match: {match.SignatureName}");
                            pendingVisualMatch ??= match;
                        }
                    }
                }
                catch (Exception) when (
                    !VisualRuntimeState.IsCaptureCurrent(
                        completedCaptureGeneration,
                        visualRuntimeGeneration) ||
                    Volatile.Read(ref detectorResetRequests) > 0 ||
                    Volatile.Read(ref visualMatchingToggleRequests) > 0)
                {
                    // Failures from invalidated work are intentionally ignored.
                }
                catch (VisualSamplingUnsupportedException exception)
                {
                    visualMatcher.ResetProgressions();
                    if (!visualSamplingDisabled)
                        Console.Error.WriteLine($"learned visual sampling disabled for this run: {exception.Message}");
                    visualSamplingDisabled = true;
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    visualMatcher.ResetProgressions();
                    if (loggedVisualRuntimeError != exception.Message)
                        Console.Error.WriteLine($"visual runtime sample failed: {exception.Message}");
                    loggedVisualRuntimeError = exception.Message;
                }
                finally
                {
                    visualRuntimeCapture = null;
                    nextVisualRuntimeSampleAt = DateTimeOffset.UtcNow.AddMilliseconds(
                        VisualSequenceMatcher.RuntimeSampleIntervalMilliseconds);
                }
            }

            if (visualTraining?.IsCompleted is true)
            {
                var completedTrainingGeneration = visualTrainingGeneration;
                try
                {
                    var result = await visualTraining;
                    if (completedTrainingGeneration != visualGeneration ||
                        Volatile.Read(ref detectorResetRequests) > 0)
                    {
                        // X invalidated this training result before it could be saved.
                    }
                    else if (result.Signature is null)
                    {
                        Console.Error.WriteLine(
                            $"visual training failed: {result.Error ?? "unknown error"} ({result.UsableSamples} usable samples)");
                    }
                    else
                    {
                        learnedVisualSignatures.Add(result.Signature);
                        var saveResult = visualSignatureStore.Save(learnedVisualSignatures);
                        if (saveResult.Success)
                        {
                            visualMatcher.ReplaceSignatures(learnedVisualSignatures);
                            lastVisualRuntimeBounds = null;
                            Console.WriteLine($"visual training completed: {result.UsableSamples} usable samples");
                            Console.WriteLine($"visual signature generated: {result.Signature.Name} [{result.Signature.Id}]");
                        }
                        else
                        {
                            learnedVisualSignatures.Remove(result.Signature);
                            Console.Error.WriteLine($"visual training failed: could not save signature: {saveResult.Error}");
                        }
                    }
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested ||
                    visualTrainingCancellation?.IsCancellationRequested is true)
                {
                }
                catch (Exception) when (
                    completedTrainingGeneration != visualGeneration ||
                    Volatile.Read(ref detectorResetRequests) > 0)
                {
                    // Failures from invalidated work are intentionally ignored.
                }
                catch (VisualSamplingUnsupportedException exception)
                {
                    if (!visualSamplingDisabled)
                        Console.Error.WriteLine($"learned visual sampling disabled for this run: {exception.Message}");
                    visualSamplingDisabled = true;
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"visual training failed: {exception.Message}");
                }
                finally
                {
                    visualTraining = null;
                    visualTrainingCancellation?.Dispose();
                    visualTrainingCancellation = null;
                }
            }

            if (Interlocked.Exchange(ref visualTrainingRequests, 0) > 0)
            {
                if (visualSamplingDisabled)
                {
                    Console.Error.WriteLine("visual training unavailable: direct video sampling is disabled for this run");
                }
                else if (visualTraining is not null)
                {
                    Console.Error.WriteLine("visual training already active; request ignored");
                }
                else
                {
                    Console.WriteLine(
                        $"visual training started: {VisualSignatureTrainer.TrainingDurationMilliseconds / 1000.0:0.#} seconds at {VisualSignatureTrainer.SampleIntervalMilliseconds} ms intervals");
                    visualTrainingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    visualTrainingGeneration = visualGeneration;
                    visualTraining = VisualSignatureTrainer.TrainAsync(
                        sourcePage,
                        visualTrainingCancellation.Token);
                }
            }

            if (learnedVisualSignatures.Count > 0 &&
                !visualSamplingDisabled &&
                visualMatchingEnabled &&
                !trackingPaused &&
                !recoveryBaseline.IsAwaiting &&
                publishedState is not true &&
                visualBlockedState is null &&
                pendingVisualMatch is null &&
                visualTraining is null &&
                visualRuntimeCapture is null &&
                DateTimeOffset.UtcNow >= nextVisualRuntimeSampleAt)
            {
                visualRuntimeCaptureGeneration = visualRuntimeGeneration;
                visualRuntimeCapture = VisualFrameSampler.CaptureAsync(sourcePage);
            }

            if (feedRefresh?.IsCompleted is true)
            {
                try
                {
                    var discovery = await feedRefresh;
                    await youtubePage.EvaluateAsync("items => { window.youtubePlayerControls.refresh(items); }",
                        discovery.Uploads.Select(item => new
                        {
                            id = item.Id,
                            title = item.Title,
                            automaticSkipReason = item.AutomaticSkipReason
                        }).ToArray());
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    Console.Error.WriteLine($"youtube discovery refresh failed (queue retained): {exception.Message}");
                }
                finally
                {
                    feedRefresh = null;
                    queueRefreshPolicy.RecordAttemptCompleted(DateTimeOffset.UtcNow);
                }
            }

            if (automaticQueueMode && DateTimeOffset.UtcNow >= nextQueueSnapshotAt)
            {
                await youtubeQueueStateSaver.RefreshSnapshotAsync();
                nextQueueSnapshotAt = DateTimeOffset.UtcNow.Add(YoutubeQueueStateSaver.CacheRefreshInterval);
            }

            var refreshCheckTime = DateTimeOffset.UtcNow;
            if (automaticQueueMode &&
                feedRefresh is null &&
                refreshCheckTime >= nextQueueDepthCheckAt &&
                queueRefreshPolicy.CooldownElapsed(refreshCheckTime))
            {
                nextQueueDepthCheckAt = refreshCheckTime.AddSeconds(5);
                try
                {
                    var queueStatus = await GetYoutubeQueueRefreshStatusAsync(youtubePage);
                    if (!queueStatus.RefreshActive &&
                        queueRefreshPolicy.ShouldRefresh(queueStatus.RemainingVideos, refreshCheckTime))
                    {
                        Console.Error.WriteLine(
                            $"youtube discovery refresh triggered: automatic queue has {queueStatus.RemainingVideos} remaining videos (threshold: {YoutubeQueueRefreshPolicy.RemainingVideoThreshold})");
                        // Fetch asynchronously so slow RSS requests never block ad detection.
                        feedRefresh = YoutubeFeed.FetchAsync(options.ChannelUrl, feedRefreshCancellation.Token);
                    }
                }
                catch (PlaywrightException exception)
                {
                    Console.Error.WriteLine($"youtube queue depth check failed: {exception.Message}");
                }
            }

            var toggleCount = Interlocked.Exchange(ref trackingToggleRequests, 0);
            while (toggleCount-- > 0)
            {
                trackingPaused = !trackingPaused;
                publishedState = null;
                pendingState = null;
                pendingCount = 0;
                VisualRuntimeState.ResetBoundary(
                    visualMatcher,
                    ref visualRuntimeGeneration,
                    ref pendingVisualMatch);

                if (trackingPaused)
                {
                    visualBlockedState = null;
                    youtubePlaybackExpected = false;
                    await PauseYoutubeAsync(youtubePage);
                    loggedYoutubeError = null;
                    await sourcePage.BringToFrontAsync();
                    await SetSourceMutedAsync(sourcePage, muted: false);
                    Console.WriteLine("ad tracking paused");
                }
                else
                {
                    Console.WriteLine("ad tracking resumed");
                }
            }

            if (pendingVisualMatch is not null &&
                visualMatchingEnabled &&
                !recoveryBaseline.IsAwaiting &&
                Volatile.Read(ref trackingToggleRequests) == 0 &&
                Volatile.Read(ref detectorResetRequests) == 0 &&
                Volatile.Read(ref visualMatchingToggleRequests) == 0)
            {
                var match = pendingVisualMatch;
                pendingVisualMatch = null;
                var start = VisualBlockPolicy.TryStart(
                    visualBlockedState,
                    match,
                    DateTimeOffset.UtcNow,
                    trackingPaused,
                    publishedState is true);
                if (start.Started)
                {
                    visualBlockedState = start.State;
                    await SetSourceMutedAsync(sourcePage, muted: true);
                    await youtubePage.BringToFrontAsync();
                    youtubePlaybackExpected = true;
                    await ResumeYoutubeAsync(youtubePage);
                    loggedYoutubeError = null;
                    nextYoutubeHealthCheckAt = DateTimeOffset.UtcNow;
                    Console.WriteLine($"blocked segment started [VISUAL:{match.SignatureName}]");
                }
            }

            if (Interlocked.Exchange(ref queueRestoreRequests, 0) > 0)
            {
                if (!automaticQueueMode)
                {
                    Console.Error.WriteLine("youtube queue restore skipped: R is unavailable in --youtube-url single-video mode");
                }
                else
                {
                    try
                    {
                        var youtubeForegrounded = await youtubePage.EvaluateAsync<bool>(
                            "() => document.visibilityState === 'visible'");
                        if (!YoutubeQueueRestoreAccess.CanReload(trackingPaused, youtubeForegrounded))
                        {
                            Console.Error.WriteLine(
                                "youtube queue restore skipped: R is allowed only while ad tracking is paused or YouTube is foregrounded");
                        }
                        else
                        {
                            var restoreResult = YoutubeQueueStateLoader.Load(
                                queueStatePath,
                                options.ChannelUrl,
                                options.MinimumDurationSeconds,
                                DateTimeOffset.UtcNow);
                            if (restoreResult.Status == YoutubeQueueRestoreStatus.Succeeded &&
                                restoreResult.BrowserState is not null)
                            {
                                var applyResult = await ApplyYoutubeQueueRestoreAsync(youtubePage, restoreResult.BrowserState);
                                if (applyResult.Success)
                                {
                                    youtubeQueueStateSaver.CacheSnapshot(restoreResult.BrowserState);
                                    LogYoutubeQueueRestoreResult(restoreResult);
                                }
                                else
                                    Console.Error.WriteLine($"youtube queue restore failed: {applyResult.Error ?? "Unknown player error."}");
                            }
                            else
                            {
                                LogYoutubeQueueRestoreResult(restoreResult);
                            }
                        }
                    }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        Console.Error.WriteLine($"youtube queue restore failed: {exception.Message}");
                    }
                }
            }

            var sample = await DetectAsync(sourcePage, options.ScanMode);
            // If P was pressed while detection was running, normalize tracking before
            // this sample can trigger a switch. A resumed loop will take a new sample.
            if (Volatile.Read(ref trackingToggleRequests) > 0 ||
                Volatile.Read(ref detectorResetRequests) > 0)
                continue;

            if (trackingPaused)
            {
                await Task.Delay(options.PollInterval, cancellationToken);
                continue;
            }

            if (recoveryBaseline.IsAwaiting)
            {
                visualMatcher.ResetProgressions();
                if (recoveryBaseline.Observe(sample.IsAd, options.ConfirmationSamples))
                {
                    publishedState = null;
                    pendingState = null;
                    pendingCount = 0;
                    Console.WriteLine("detector re-armed");
                }

                await Task.Delay(options.PollInterval, cancellationToken);
                continue;
            }

            everFoundSemanticIndicator |= sample.IsAd;

            if (pendingState == sample.IsAd)
            {
                pendingCount++;
            }
            else
            {
                pendingState = sample.IsAd;
                pendingCount = 1;
            }

            if (pendingCount >= options.ConfirmationSamples && publishedState != sample.IsAd)
            {
                // A non-ad page load is baseline state, not an "ad ended" transition.
                if (sample.IsAd)
                {
                    VisualRuntimeState.ResetBoundary(
                        visualMatcher,
                        ref visualRuntimeGeneration,
                        ref pendingVisualMatch);
                    var requiresDomStartSwitch = VisualBlockPolicy.RequiresSwitchForDomStart(visualBlockedState);
                    visualBlockedState = null;
                    if (requiresDomStartSwitch)
                    {
                        await SetSourceMutedAsync(sourcePage, muted: true);
                        await youtubePage.BringToFrontAsync();
                        youtubePlaybackExpected = true;
                        await ResumeYoutubeAsync(youtubePage);
                        loggedYoutubeError = null;
                        nextYoutubeHealthCheckAt = DateTimeOffset.UtcNow;
                    }
                    Console.WriteLine($"ad started [{sample.Method}]");
                    publishedState = true;
                }
                else if (publishedState is true)
                {
                    VisualRuntimeState.ResetBoundary(
                        visualMatcher,
                        ref visualRuntimeGeneration,
                        ref pendingVisualMatch);
                    youtubePlaybackExpected = false;
                    await PauseYoutubeAsync(youtubePage);
                    loggedYoutubeError = null;
                    await sourcePage.BringToFrontAsync();
                    await SetSourceMutedAsync(sourcePage, muted: false);
                    Console.WriteLine($"ad ended [{sample.Method}]");
                    publishedState = false;
                }
            }

            if (VisualBlockPolicy.ShouldTimeout(
                visualBlockedState,
                publishedState is true,
                DateTimeOffset.UtcNow))
            {
                visualBlockedState = null;
                youtubePlaybackExpected = false;
                await PauseYoutubeAsync(youtubePage);
                loggedYoutubeError = null;
                await sourcePage.BringToFrontAsync();
                await SetSourceMutedAsync(sourcePage, muted: false);
                pendingState = null;
                pendingCount = 0;
                Console.WriteLine("blocked segment ended [VISUAL:timeout]");
            }

            if (!everFoundSemanticIndicator &&
                !sample.IsAd &&
                sample.HasPlayer &&
                DateTimeOffset.UtcNow >= nextCaptureAt)
            {
                await SaveDiagnosticCropAsync(sourcePage, sample, options.CaptureDirectory);
                nextCaptureAt = DateTimeOffset.UtcNow.Add(options.CaptureInterval);
            }

            if (youtubePlaybackExpected && DateTimeOffset.UtcNow >= nextYoutubeHealthCheckAt)
            {
                var youtubeError = await DetectYoutubeErrorAsync(youtubePage);
                if (youtubeError is not null && youtubeError != loggedYoutubeError)
                {
                    Console.Error.WriteLine($"youtube player error: {youtubeError}");
                }

                loggedYoutubeError = youtubeError;
                nextYoutubeHealthCheckAt = DateTimeOffset.UtcNow.AddSeconds(2);
            }

            await Task.Delay(options.PollInterval, cancellationToken);
        }
    }
    finally
    {
        await feedRefreshCancellation.CancelAsync();
        if (visualTraining is not null)
        {
            try
            {
                await visualTraining;
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested ||
                visualTrainingCancellation?.IsCancellationRequested is true)
            {
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"visual training cleanup failed: {exception.Message}");
            }
            finally
            {
                visualTrainingCancellation?.Dispose();
                visualTrainingCancellation = null;
            }
        }
        if (visualRuntimeCapture is not null)
        {
            try
            {
                await visualRuntimeCapture;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine($"visual runtime cleanup failed: {exception.Message}");
            }
        }
        if (feedRefresh is not null)
        {
            try
            {
                await feedRefresh;
            }
            catch (OperationCanceledException) when (feedRefreshCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"youtube discovery refresh cleanup failed: {exception.Message}");
            }
        }
    }
}

static async Task<YoutubeQueueRefreshStatus> GetYoutubeQueueRefreshStatusAsync(IPage page)
{
    var json = await page.EvaluateAsync<string>(
        """
        () => {
          const controls = window.youtubePlayerControls;
          const queuedVideos = controls?.getQueueState?.().queuedVideos;
          if (!Array.isArray(queuedVideos)) throw new Error('Local YouTube queue state is unavailable.');
          return JSON.stringify({
            remainingVideos: queuedVideos.length,
            refreshActive: controls.isRefreshActive?.() === true
          });
        }
        """);
    return JsonSerializer.Deserialize<YoutubeQueueRefreshStatus>(json, JsonOptions.Instance)
        ?? throw new InvalidOperationException("Local YouTube queue returned no refresh status.");
}

static string FormatBounds(DetectionBounds bounds) =>
    $"({bounds.X:0.#},{bounds.Y:0.#}) {bounds.Width:0.#}x{bounds.Height:0.#}";

static async Task<YoutubeControlResult> ApplyYoutubeQueueRestoreAsync(
    IPage page,
    YoutubeQueueBrowserState browserState)
{
    var stateJson = YoutubeQueueStateSerializer.SerializeBrowserState(browserState);
    var resultJson = await page.EvaluateAsync<string>(
        """
        stateJson => {
          const controls = window.youtubePlayerControls;
          return JSON.stringify(controls
            ? controls.restoreQueue(JSON.parse(stateJson))
            : { success: false, error: 'Local YouTube player controls are unavailable.' });
        }
        """,
        stateJson);
    return JsonSerializer.Deserialize<YoutubeControlResult>(resultJson, JsonOptions.Instance)
        ?? new YoutubeControlResult(false, "Local YouTube player returned no restore result.");
}

static void LogYoutubeQueueRestoreResult(YoutubeQueueRestoreResult result)
{
    var status = result.Status.ToString().ToLowerInvariant();
    var prefix = result.IsWarning ? "warning: " : "";
    Console.Error.WriteLine($"{prefix}youtube queue restore {status}: {result.Reason}");
}

static string? FindInstalledGoogleChrome()
{
    string[] candidates =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Google", "Chrome", "Application", "chrome.exe")
    ];

    return candidates.FirstOrDefault(File.Exists);
}

static bool IsPlutoSource(Uri sourceUri) =>
    sourceUri.Host.Equals("pluto.tv", StringComparison.OrdinalIgnoreCase) ||
    sourceUri.Host.EndsWith(".pluto.tv", StringComparison.OrdinalIgnoreCase);

static async Task SetSourceMutedAsync(IPage page, bool muted)
{
    await page.EvaluateAsync(SourceMuteScript.Script, muted);
}

static async Task PauseYoutubeAsync(IPage page)
{
    var json = await page.EvaluateAsync<string>(
        """
        () => {
          const controls = window.youtubePlayerControls;
          return JSON.stringify(controls
            ? controls.pause()
            : { success: false, error: 'Local YouTube player controls are unavailable.' });
        }
        """);
    var result = JsonSerializer.Deserialize<YoutubeControlResult>(json, JsonOptions.Instance);
    Console.Error.WriteLine(result?.Success is true
        ? "youtube paused"
        : $"youtube pause failed: {result?.Error ?? "Unknown error."}");
}

static async Task ResumeYoutubeAsync(IPage page)
{
    var json = await page.EvaluateAsync<string>(
        """
        () => {
          const controls = window.youtubePlayerControls;
          return JSON.stringify(controls
            ? controls.play()
            : { success: false, error: 'Local YouTube player controls are unavailable.' });
        }
        """);
    var result = JsonSerializer.Deserialize<YoutubeControlResult>(json, JsonOptions.Instance);
    Console.Error.WriteLine(result?.Success is true
        ? "youtube resumed"
        : $"youtube resume failed: {result?.Error ?? "Unknown error."}");
}

static async Task<string?> DetectYoutubeErrorAsync(IPage page)
{
    return await page.EvaluateAsync<string?>(
        """
        () => {
          const controls = window.youtubePlayerControls;
          return controls
            ? controls.getError()
            : 'Local YouTube player controls are unavailable.';
        }
        """);
}

static async Task<DetectionSample> DetectAsync(IPage page, PlutoScanMode scanMode)
{
    var mode = scanMode == PlutoScanMode.Full ? "full" : "focused";
    var json = await page.EvaluateAsync<string>(PlutoDetectionScript.Script, mode);
    return JsonSerializer.Deserialize<DetectionSample>(json, JsonOptions.Instance)
        ?? new DetectionSample(false, "DOM", false, DetectionBounds.Empty, DetectionBounds.Empty);
}

static async Task SaveDiagnosticCropAsync(IPage page, DetectionSample sample, string captureDirectory)
{
    try
    {
        var region = sample.DomDetectionRegion;
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var path = Path.Combine(captureDirectory, $"player-upper-left-{timestamp}.png");
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = path,
            Clip = new Clip
            {
                X = region.X,
                Y = region.Y,
                Width = region.Width,
                Height = region.Height
            }
        });
    }
    catch (PlaywrightException exception)
    {
        Console.Error.WriteLine($"diagnostic capture failed (continuing): {exception.Message}");
    }
}

internal sealed record DetectionSample(
    bool IsAd,
    string Method,
    bool HasPlayer,
    DetectionBounds DomDetectionRegion,
    DetectionBounds PlayerBounds);

internal sealed record DetectionBounds(float X, float Y, float Width, float Height)
{
    internal static readonly DetectionBounds Empty = new(0, 0, 0, 0);
}

internal sealed record YoutubeControlResult(bool Success, string? Error);

internal sealed record YoutubeQueueRefreshStatus(int RemainingVideos, bool RefreshActive);

internal static class JsonOptions
{
    internal static readonly JsonSerializerOptions Instance = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
