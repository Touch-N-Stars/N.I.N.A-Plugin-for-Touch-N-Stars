using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem.FlatDevice;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.WPF.Base.Interfaces.Mediator;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TouchNStars.Server.Services;

/// <summary>
/// Runs the Touch-N-Stars flat assistant on the NINA side, so a run keeps going while the app
/// is closed or the phone is locked. One run covers one or more filters: flats for every filter
/// first, then (optionally) dark flats for every filter whose flats succeeded, the way NINA's own
/// flat wizard orders them. The app only starts, observes and stops a run.
///
/// Dark flats are captured directly through the imaging mediator rather than with NINA's
/// TrainedDarkFlatExposure: that item needs a connected flat device for its brightness step and
/// deliberately skips its filter change, which labels every dark with the wrong filter.
/// </summary>
public static class FlatWizardService
{
    public const int WorkflowVersion = 2;

    private const int MaxTargetNameLength = 64;

    private static readonly object gate = new();

    private static Task runTask;
    private static CancellationTokenSource runCts;
    private static TaskCompletionSource<bool> coverConfirmation;

    // Run state, guarded by `gate`. Survives until the next run starts so an app that reconnects
    // after the run ended can still show its result.
    private static string runId;
    private static string mode;
    private static string phase = "idle";
    private static bool running;
    private static bool stopRequested;
    private static string runError;
    private static int darkCount;
    private static DateTime? startedAt;
    private static DateTime? finishedAt;
    private static int currentIndex = -1;
    private static List<FilterRunState> filterStates = new();
    private static Func<double?> liveADU;
    private static double? lastADU;
    private static int lastBitDepth;
    private static string targetName;
    // Target name the run's last flat was saved with; its dark flats are saved under the same name.
    private static string flatTargetName;

    public static bool IsRunning
    {
        get { lock (gate) return running; }
    }

    /// <summary>
    /// Validates the request and starts the run in the background. Returns status 200 on success,
    /// otherwise the HTTP status and the reason it was refused.
    /// </summary>
    public static (int StatusCode, string Error) Start(FlatRunRequest request)
    {
        var mediators = TouchNStars.Mediators;
        var profile = mediators?.Profile?.ActiveProfile;
        if (profile == null)
        {
            return (503, "Profile not available");
        }

        if (request?.Filters == null || request.Filters.Count == 0)
        {
            return (400, "No filters specified");
        }

        var filterList = profile.FilterWheelSettings.FilterWheelFilters;
        foreach (var fc in request.Filters)
        {
            if (fc.FilterId.HasValue && (fc.FilterId < 0 || fc.FilterId >= filterList.Count))
            {
                return (400, $"Filter id {fc.FilterId} is not available");
            }
            if (fc.Count <= 0)
            {
                return (400, "Count must be greater than zero");
            }
        }

        if (mediators.Camera?.GetInfo()?.Connected != true)
        {
            return (409, "Camera not connected");
        }

        var requestedMode = request.Mode switch
        {
            "AutoBrightness" => "AutoBrightness",
            "SkyFlat" => "SkyFlat",
            _ => "AutoExposure",
        };

        lock (gate)
        {
            if (running)
            {
                return (409, "Flat capture already running");
            }

            runCts?.Dispose();
            runCts = new CancellationTokenSource();
            coverConfirmation = null;

            runId = Guid.NewGuid().ToString("N");
            mode = requestedMode;
            phase = "flats";
            running = true;
            stopRequested = false;
            runError = null;
            darkCount = Math.Max(0, request.DarkCount);
            startedAt = DateTime.UtcNow;
            finishedAt = null;
            currentIndex = -1;
            liveADU = null;
            lastADU = null;
            lastBitDepth = 0;
            targetName = SanitizeTargetName(request.TargetName);
            flatTargetName = null;
            filterStates = request.Filters.Select(fc => new FilterRunState
            {
                Config = fc,
                FilterId = fc.FilterId,
                FilterName = fc.FilterId.HasValue ? filterList[fc.FilterId.Value]?.Name : null,
                State = "pending",
                Count = fc.Count,
                DarkCount = darkCount,
                DarkState = darkCount > 0 ? "pending" : "none",
            }).ToList();

            var token = runCts.Token;
            var keepClosed = request.KeepClosed;
            runTask = Task.Run(() => RunAsync(keepClosed, token));
        }

        return (200, null);
    }

    public static void Stop()
    {
        lock (gate)
        {
            if (!running) return;
            stopRequested = true;
            runCts?.Cancel();
            coverConfirmation?.TrySetResult(false);
        }
    }

    /// <summary>
    /// Answers the "cover the telescope" question the run waits on before its dark flats.
    /// </summary>
    public static bool ConfirmCover(bool confirmed)
    {
        lock (gate)
        {
            if (coverConfirmation == null) return false;
            return coverConfirmation.TrySetResult(confirmed);
        }
    }

    public static object GetStatus()
    {
        lock (gate)
        {
            var current = currentIndex >= 0 && currentIndex < filterStates.Count ? filterStates[currentIndex] : null;
            var inDarks = phase == "darks" || phase == "waiting-for-cover";
            var phaseFilters = inDarks ? filterStates.Where(f => f.DarkState != "none" && f.DarkState != "skipped").ToList() : filterStates;
            var adu = liveADU?.Invoke() ?? lastADU;

            int total, completed;
            if (current == null)
            {
                total = filterStates.Sum(f => f.Count);
                completed = filterStates.Sum(f => f.Completed);
            }
            else if (inDarks)
            {
                total = current.DarkCount;
                completed = current.DarkCompleted;
            }
            else
            {
                total = current.Count;
                completed = current.LiveCompleted?.Invoke() ?? current.Completed;
            }

            return new
            {
                Workflow = WorkflowVersion,
                RunId = runId,
                State = running ? "Running" : (runId == null ? "Idle" : "Finished"),
                Phase = phase,
                Type = inDarks ? "darks" : "flats",
                Mode = mode,
                StopRequested = stopRequested,
                WaitingForCover = phase == "waiting-for-cover",
                Error = runError,
                TotalIterations = total,
                CompletedIterations = completed,
                TotalFilters = phaseFilters.Count,
                CompletedFilters = current == null ? -1 : Math.Max(0, phaseFilters.IndexOf(current)),
                CurrentFilterId = current?.FilterId,
                CurrentFilterName = current?.FilterName,
                CurrentADU = adu,
                // Bit depth of the image CurrentADU was measured on (full scale 2^BitDepth - 1). For RAW
                // frames it can be below the camera's profile bit depth, e.g. 14 for a NEF.
                BitDepth = adu.HasValue && lastBitDepth > 0 ? lastBitDepth : (int?)null,
                DarkCount = darkCount,
                StartedAt = startedAt,
                FinishedAt = finishedAt,
                Filters = filterStates.Select(f => new
                {
                    f.FilterId,
                    f.FilterName,
                    f.State,
                    Completed = f.LiveCompleted?.Invoke() ?? f.Completed,
                    f.Count,
                    f.ExposureTime,
                    f.Brightness,
                    f.LastADU,
                    f.Error,
                    f.DarkState,
                    f.DarkCompleted,
                    f.DarkCount,
                }).ToList(),
            };
        }
    }

    // ── Run ─────────────────────────────────────────────────────────────────────

    private static async Task RunAsync(bool keepClosed, CancellationToken token)
    {
        var mediators = TouchNStars.Mediators;
        var statusMediator = mediators.ApplicationStatus;
        IProgress<ApplicationStatus> progress = new Progress<ApplicationStatus>(s =>
        {
            if (s == null) return;
            s.Source = "Touch 'N' Stars Flats";
            statusMediator?.StatusUpdate(s);
        });

        AttachImageHooks();

        List<FilterRunState> states;
        string runMode;
        int darks;
        lock (gate)
        {
            states = filterStates;
            runMode = mode;
            darks = darkCount;
        }

        var usesPanel = runMode != "SkyFlat";

        try
        {
            for (int i = 0; i < states.Count; i++)
            {
                if (token.IsCancellationRequested) break;

                var state = states[i];
                // Between filters the panel stays closed; after the last one it only opens when
                // neither the user nor the upcoming dark flats need it closed.
                var isLast = i == states.Count - 1;
                var keepPanelClosed = !isLast || keepClosed || darks > 0;

                lock (gate)
                {
                    currentIndex = i;
                    state.State = "running";
                }

                await RunFlatsForFilter(state, runMode, keepPanelClosed, progress, token);
            }

            var darkTargets = states.Where(s => s.State == "success" && s.DarkCount > 0).ToList();
            lock (gate)
            {
                foreach (var s in states.Where(s => s.DarkState == "pending" && !darkTargets.Contains(s)))
                {
                    s.DarkState = "skipped";
                }
            }

            if (darkTargets.Count > 0 && !token.IsCancellationRequested)
            {
                await RunDarkFlats(darkTargets, usesPanel, progress, token);
            }

            if (usesPanel && !keepClosed && !token.IsCancellationRequested)
            {
                await OpenCoverIfSupported(progress, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Stop requested - the per-filter states already say where it stopped. NINA's flat
            // items only switch the panel light off when they fail, not when they are cancelled.
            if (usesPanel) await SwitchLightOffAfterStop();
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            lock (gate) runError = ex.Message;
        }
        finally
        {
            // NINA saves through a background queue, so the run's last frames can still be waiting
            // to be written when the run ends. Keep the hooks a little longer for them.
            _ = DetachImageHooksLater();

            lock (gate)
            {
                foreach (var s in filterStates)
                {
                    if (s.State == "pending" || s.State == "running") s.State = "stopped";
                    // Dark flats only ever run for filters whose flats succeeded.
                    if (s.DarkState == "pending" || s.DarkState == "running")
                        s.DarkState = stopRequested && s.State == "success" ? "stopped" : "skipped";
                }
                currentIndex = -1;
                liveADU = null;
                coverConfirmation = null;
                phase = "done";
                running = false;
                finishedAt = DateTime.UtcNow;
            }

            progress.Report(new ApplicationStatus());
        }
    }

    private static async Task RunFlatsForFilter(FilterRunState state, string runMode, bool keepPanelClosed,
        IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        var fc = state.Config;
        SequentialContainer flat;
        TakeExposure exposureItem;
        Func<double> determinedADU;
        Func<double?> brightness;

        try
        {
            var filter = fc.FilterId.HasValue
                ? TouchNStars.Mediators.Profile.ActiveProfile.FilterWheelSettings.FilterWheelFilters[fc.FilterId.Value]
                : null;
            (flat, exposureItem, determinedADU, brightness) = BuildFlat(runMode, fc, filter, keepPanelClosed);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            lock (gate)
            {
                state.State = "failed";
                state.Error = ex.Message;
            }
            return;
        }

        if (!flat.Validate())
        {
            var issues = string.Join(", ", flat.Issues ?? Enumerable.Empty<string>());
            Logger.Warning($"Flat validation failed for filter {state.FilterName}: {issues}");
            lock (gate)
            {
                state.State = "failed";
                state.Error = issues;
            }
            return;
        }

        var loop = GetIterations(flat);
        lock (gate)
        {
            state.LiveCompleted = () => loop.CompletedIterations;
            liveADU = () => determinedADU() > 0 ? determinedADU() : (double?)null;
        }

        string outcome;
        string error = null;
        try
        {
            await flat.Execute(progress, token);
            outcome = loop.CompletedIterations >= state.Count ? "success" : "failed";
        }
        catch (OperationCanceledException)
        {
            outcome = "stopped";
        }
        catch (Exception ex)
        {
            Logger.Warning($"Flats failed for filter {state.FilterName}: {ex.Message}");
            error = ex.Message;
            outcome = token.IsCancellationRequested ? "stopped" : ClassifyFailure(determinedADU(), fc.HistogramMean / 100.0);
        }

        lock (gate)
        {
            var adu = determinedADU();
            state.Completed = loop.CompletedIterations;
            state.LiveCompleted = null;
            state.LastADU = adu > 0 ? adu : null;
            state.ExposureTime = exposureItem.ExposureTime;
            state.Binning = exposureItem.Binning;
            state.Gain = exposureItem.Gain;
            state.Offset = exposureItem.Offset;
            state.Brightness = brightness();
            state.State = outcome;
            state.Error = error;
            lastADU = state.LastADU ?? lastADU;
            liveADU = null;
        }

        if (outcome == "stopped") token.ThrowIfCancellationRequested();
    }

    private static (SequentialContainer, TakeExposure, Func<double>, Func<double?>) BuildFlat(
        string runMode, FlatRunFilterConfig fc, FilterInfo filter, bool keepPanelClosed)
    {
        var m = TouchNStars.Mediators;

        switch (runMode)
        {
            case "AutoBrightness":
                {
                    var flat = new AutoBrightnessFlat(m.Profile, m.Camera, m.Imaging, m.ImageSaveMediator,
                        m.ImageHistory, m.FilterWheel, m.FlatDevice);
                    flat.GetIterations().Iterations = fc.Count;
                    flat.GetExposureItem().ExposureTime = fc.ExposureTime;

#if PINS
                    // Widen the expression range so Validate() accepts panels whose maximum is above 100.
                    var deviceInfo = m.FlatDevice?.GetInfo();
                    double deviceMax = deviceInfo?.Connected == true ? deviceInfo.MaxBrightness : 0;
                    flat.MaxBrightnessExpression.Range = [0, deviceMax, 0];
                    flat.MinBrightnessExpression.Range = [0, deviceMax, 0];
#endif
                    flat.MaxBrightness = fc.MaxBrightness;
                    flat.MinBrightness = fc.MinBrightness;

                    flat.HistogramTargetPercentage = fc.HistogramMean / 100.0;
                    flat.HistogramTolerancePercentage = fc.MeanTolerance / 100.0;
                    flat.KeepPanelClosed = keepPanelClosed;
                    ApplyExposureSettings(flat.GetExposureItem(), fc);
                    if (filter != null) flat.GetSwitchFilterItem().Filter = filter;
                    return (flat, flat.GetExposureItem(), () => flat.DeterminedHistogramADU,
                        () => flat.GetSetBrightnessItem().Brightness);
                }
            case "SkyFlat":
                {
#if PINS
                    var flat = new SkyFlat(m.Profile, m.Camera, m.Telescope, m.Imaging, m.ImageSaveMediator,
                        m.ImageHistory, m.FilterWheel, m.TwilightCalculator, m.SymbolBroker);
#else
                    var flat = new SkyFlat(m.Profile, m.Camera, m.Telescope, m.Imaging, m.ImageSaveMediator,
                        m.ImageHistory, m.FilterWheel, m.TwilightCalculator);
#endif
                    flat.GetIterations().Iterations = fc.Count;
                    flat.MinExposure = fc.MinExposure;
                    flat.MaxExposure = fc.MaxExposure;
                    flat.HistogramTargetPercentage = fc.HistogramMean / 100.0;
                    flat.HistogramTolerancePercentage = fc.MeanTolerance / 100.0;
                    ApplyExposureSettings(flat.GetExposureItem(), fc);
                    if (filter != null) flat.GetSwitchFilterItem().Filter = filter;
                    return (flat, flat.GetExposureItem(), () => flat.DeterminedHistogramADU, () => null);
                }
            default:
                {
                    var flat = new AutoExposureFlat(m.Profile, m.Camera, m.Imaging, m.ImageSaveMediator,
                        m.ImageHistory, m.FilterWheel, m.FlatDevice);
                    flat.GetIterations().Iterations = fc.Count;
                    flat.MinExposure = fc.MinExposure;
                    flat.MaxExposure = fc.MaxExposure;
                    flat.GetSetBrightnessItem().Brightness = fc.Brightness;
                    flat.HistogramTargetPercentage = fc.HistogramMean / 100.0;
                    flat.HistogramTolerancePercentage = fc.MeanTolerance / 100.0;
                    flat.KeepPanelClosed = keepPanelClosed;
                    ApplyExposureSettings(flat.GetExposureItem(), fc);
                    if (filter != null) flat.GetSwitchFilterItem().Filter = filter;
                    return (flat, flat.GetExposureItem(), () => flat.DeterminedHistogramADU,
                        () => flat.GetSetBrightnessItem().Brightness);
                }
        }
    }

    private static NINA.Sequencer.Conditions.LoopCondition GetIterations(SequentialContainer flat) => flat switch
    {
        AutoExposureFlat aef => aef.GetIterations(),
        AutoBrightnessFlat abf => abf.GetIterations(),
        SkyFlat sf => sf.GetIterations(),
        _ => throw new InvalidOperationException("Unknown flat item"),
    };

    private static void ApplyExposureSettings(TakeExposure exposure, FlatRunFilterConfig fc)
    {
        exposure.Gain = fc.Gain;
        exposure.Offset = fc.Offset;
        var binning = ParseBinning(fc.Binning);
        if (binning != null) exposure.Binning = binning;
    }

    private static BinningMode ParseBinning(string binningName)
    {
        if (string.IsNullOrEmpty(binningName)) return null;
        var parts = binningName.Split('x');
        if (parts.Length == 2 && short.TryParse(parts[0], out short x) && short.TryParse(parts[1], out short y))
            return new BinningMode(x, y);
        return null;
    }

    /// <summary>
    /// NINA only says "too dim" / "too bright" in a localized exception message, so the
    /// direction is recovered from the last measured mean instead.
    /// </summary>
    private static string ClassifyFailure(double adu, double targetFraction)
    {
        if (adu <= 0) return "failed";
        int bitDepth;
        lock (gate) bitDepth = lastBitDepth;
        if (bitDepth <= 0) bitDepth = TouchNStars.Mediators.Camera?.GetInfo()?.BitDepth ?? 16;
        var maxADU = Math.Pow(2, bitDepth) - 1;
        var fraction = adu / maxADU;
        if (fraction < targetFraction) return "dim";
        if (fraction > targetFraction) return "bright";
        return "failed";
    }

    // ── Dark flats ─────────────────────────────────────────────────────────────

    private static async Task RunDarkFlats(List<FilterRunState> targets, bool usesPanel,
        IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        var m = TouchNStars.Mediators;
        var flatInfo = m.FlatDevice?.GetInfo();
        var panelConnected = usesPanel && flatInfo?.Connected == true;

        if (panelConnected)
        {
            await m.FlatDevice.ToggleLight(false, progress, token);
            if (flatInfo.SupportsOpenClose)
            {
                await m.FlatDevice.CloseCover(progress, token);
            }
        }

        // A flip-flat panel shuts out the light by itself; everything else needs the user to cover
        // the telescope. The run waits for that answer, however long the app stays closed.
        if (!(panelConnected && flatInfo.SupportsOpenClose))
        {
            TaskCompletionSource<bool> confirmation;
            lock (gate)
            {
                confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                coverConfirmation = confirmation;
                phase = "waiting-for-cover";
                currentIndex = filterStates.IndexOf(targets[0]);
            }

            bool confirmed;
            using (token.Register(() => confirmation.TrySetResult(false)))
            {
                confirmed = await confirmation.Task;
            }

            lock (gate) coverConfirmation = null;
            token.ThrowIfCancellationRequested();

            if (!confirmed)
            {
                lock (gate)
                {
                    foreach (var t in targets) t.DarkState = "skipped";
                }
                return;
            }
        }

        lock (gate) phase = "darks";

        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();

            lock (gate)
            {
                currentIndex = filterStates.IndexOf(target);
                target.DarkState = "running";
            }

            var filter = target.FilterId.HasValue
                ? m.Profile.ActiveProfile.FilterWheelSettings.FilterWheelFilters[target.FilterId.Value]
                : null;
            var sequence = new CaptureSequence(target.ExposureTime ?? 0, CaptureSequence.ImageTypes.DARK, filter,
                target.Binning ?? new BinningMode(1, 1), target.DarkCount)
            {
                Gain = target.Gain ?? -1,
                Offset = target.Offset ?? -1,
            };

            try
            {
                while (sequence.ProgressExposureCount < sequence.TotalExposureCount)
                {
                    token.ThrowIfCancellationRequested();

                    // CaptureImage switches the filter wheel to sequence.FilterType before exposing.
                    var exposure = await m.Imaging.CaptureImage(sequence, token, progress);
                    var imageData = await exposure.ToImageData(progress, token);
                    StampDarkTargetName(imageData.MetaData);
                    var prepareTask = m.Imaging.PrepareImage(imageData, new PrepareImageParameters(true, false), token);
                    await m.ImageSaveMediator.Enqueue(imageData, prepareTask, progress, token);

                    sequence.ProgressExposureCount++;
                    lock (gate) target.DarkCompleted = sequence.ProgressExposureCount;
                }

                lock (gate) target.DarkState = "success";
            }
            catch (OperationCanceledException)
            {
                lock (gate) target.DarkState = "stopped";
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                lock (gate)
                {
                    target.DarkState = "failed";
                    target.Error = ex.Message;
                }
            }
        }
    }

    // Set before the frame enters NINA's save queue, so it does not depend on the save hook's timing.
    private static void StampDarkTargetName(NINA.Image.ImageData.ImageMetaData metaData)
    {
        if (metaData?.Target == null || !string.IsNullOrWhiteSpace(metaData.Target.Name)) return;
        lock (gate)
        {
            var name = flatTargetName ?? targetName;
            if (!string.IsNullOrEmpty(name)) metaData.Target.Name = name;
        }
    }

    private static async Task OpenCoverIfSupported(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        var m = TouchNStars.Mediators;
        var info = m.FlatDevice?.GetInfo();
        if (info?.Connected != true || !info.SupportsOpenClose) return;
        try
        {
            await m.FlatDevice.OpenCover(progress, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning($"Could not open the flat panel cover after the flat run: {ex.Message}");
        }
    }

    private static async Task SwitchLightOffAfterStop()
    {
        var m = TouchNStars.Mediators;
        var info = m.FlatDevice?.GetInfo();
        if (info?.Connected != true || !info.SupportsOnOff) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await m.FlatDevice.ToggleLight(false, null, timeout.Token);
        }
        catch (Exception ex)
        {
            Logger.Warning($"Could not switch the flat panel light off after stopping the flat run: {ex.Message}");
        }
    }

    // ── Image hooks (attached while a run is active and shortly after) ─────────

    private static readonly TimeSpan HookGracePeriod = TimeSpan.FromSeconds(60);
    private static bool hooksAttached;

    private static void AttachImageHooks()
    {
        var m = TouchNStars.Mediators;
        lock (gate)
        {
            if (hooksAttached) return;
            if (m.Imaging != null) m.Imaging.ImagePrepared += OnImagePrepared;
            if (m.ImageSaveMediator != null) m.ImageSaveMediator.BeforeImageSaved += OnBeforeImageSaved;
            hooksAttached = true;
        }
    }

    private static async Task DetachImageHooksLater()
    {
        await Task.Delay(HookGracePeriod);
        var m = TouchNStars.Mediators;
        lock (gate)
        {
            // A new run started meanwhile and needs the hooks itself.
            if (!hooksAttached || running) return;
            if (m.Imaging != null) m.Imaging.ImagePrepared -= OnImagePrepared;
            if (m.ImageSaveMediator != null) m.ImageSaveMediator.BeforeImageSaved -= OnBeforeImageSaved;
            hooksAttached = false;
        }
    }

    // The flat items prepare every measured frame before reading its statistics, so the image
    // prepared last during a run is the one DeterminedHistogramADU came from.
    private static void OnImagePrepared(object sender, ImagePreparedEventArgs e)
    {
        var bitDepth = e?.RenderedImage?.RawImageData?.Properties?.BitDepth;
        if (bitDepth > 0)
        {
            lock (gate) lastBitDepth = bitDepth.Value;
        }
    }

    // Flats from standalone sequence items carry no target name, so $$TARGETNAME$$ would resolve to
    // nothing. Frames that already have a name are never touched. The run's dark flats get the name
    // its flats were saved with (PINS fills in the active sequence target), so both land together.
    private static Task OnBeforeImageSaved(object sender, BeforeImageSavedEventArgs e)
    {
        try
        {
            var metaData = e?.Image?.MetaData;
            if (metaData?.Target == null) return Task.CompletedTask;

            var imageType = metaData.Image?.ImageType;
            var isFlat = string.Equals(imageType, CaptureSequence.ImageTypes.FLAT, StringComparison.OrdinalIgnoreCase);
            var isDark = string.Equals(imageType, CaptureSequence.ImageTypes.DARK, StringComparison.OrdinalIgnoreCase);
            if (!isFlat && !isDark) return Task.CompletedTask;

            lock (gate)
            {
                if (isFlat)
                {
                    if (string.IsNullOrWhiteSpace(metaData.Target.Name) && !string.IsNullOrEmpty(targetName))
                        metaData.Target.Name = targetName;
                    if (!string.IsNullOrWhiteSpace(metaData.Target.Name))
                        flatTargetName = metaData.Target.Name;
                }
                else if (string.IsNullOrWhiteSpace(metaData.Target.Name))
                {
                    var name = flatTargetName ?? targetName;
                    if (!string.IsNullOrEmpty(name)) metaData.Target.Name = name;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"FlatWizardService: failed to stamp target name: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// The name arrives from a network client and ends up in a file path, so strip anything that
    /// cannot appear in a file name and cap the length.
    /// </summary>
    private static string SanitizeTargetName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var sanitized = CoreUtil.ReplaceAllInvalidFilenameChars(value.Trim()).Trim();
        if (sanitized.Length > MaxTargetNameLength)
        {
            var cut = MaxTargetNameLength;
            // Do not cut a surrogate pair in half and leave a lone surrogate in a path.
            if (char.IsHighSurrogate(sanitized[cut - 1])) cut--;
            sanitized = sanitized.Substring(0, cut).Trim();
        }
        return string.IsNullOrEmpty(sanitized) ? null : sanitized;
    }

    private class FilterRunState
    {
        public FlatRunFilterConfig Config;
        public int? FilterId;
        public string FilterName;
        // pending | running | success | failed | dim | bright | stopped
        public string State;
        public int Completed;
        public int Count;
        public Func<int> LiveCompleted;
        public double? ExposureTime;
        public double? Brightness;
        public double? LastADU;
        public BinningMode Binning;
        public int? Gain;
        public int? Offset;
        public string Error;
        // none | pending | running | success | failed | skipped | stopped
        public string DarkState;
        public int DarkCompleted;
        public int DarkCount;
    }
}

// ── Request models ────────────────────────────────────────────────────────────

public class FlatRunRequest
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; }

    [JsonPropertyName("keepClosed")]
    public bool KeepClosed { get; set; }

    /// <summary>Dark flats per filter, taken after all flats. 0 = none.</summary>
    [JsonPropertyName("darkCount")]
    public int DarkCount { get; set; }

    /// <summary>Stamped on flat and dark flat frames that carry no target name. Empty = off.</summary>
    [JsonPropertyName("targetName")]
    public string TargetName { get; set; }

    [JsonPropertyName("filters")]
    public List<FlatRunFilterConfig> Filters { get; set; }
}

public class FlatRunFilterConfig
{
    /// <summary>Profile filter index; null keeps the current filter (no filter wheel).</summary>
    [JsonPropertyName("filterId")]
    public int? FilterId { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; } = 20;

    [JsonPropertyName("gain")]
    public int Gain { get; set; } = -1;

    [JsonPropertyName("offset")]
    public int Offset { get; set; } = -1;

    [JsonPropertyName("binning")]
    public string Binning { get; set; } = "1x1";

    /// <summary>0–100 % — converted to a 0–1 fraction before passing to NINA.</summary>
    [JsonPropertyName("histogramMean")]
    public double HistogramMean { get; set; } = 50;

    /// <summary>0–100 % — converted to a 0–1 fraction before passing to NINA.</summary>
    [JsonPropertyName("meanTolerance")]
    public double MeanTolerance { get; set; } = 10;

    // AutoExposure & SkyFlat
    [JsonPropertyName("minExposure")]
    public double MinExposure { get; set; } = 0.01;

    [JsonPropertyName("maxExposure")]
    public double MaxExposure { get; set; } = 20;

    // AutoExposure only
    [JsonPropertyName("brightness")]
    public int Brightness { get; set; } = 50;

    // AutoBrightness only
    [JsonPropertyName("exposureTime")]
    public double ExposureTime { get; set; } = 2;

    [JsonPropertyName("minBrightness")]
    public int MinBrightness { get; set; } = 0;

    [JsonPropertyName("maxBrightness")]
    public int MaxBrightness { get; set; } = 32000;
}
