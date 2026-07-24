using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TouchNStars.Server.Infrastructure;
using TouchNStars.Server.Models;
using TouchNStars.Utility;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// Controller for the Filter Offset Calculator (DarksCustoms plugin) web interface.
/// Replicates the logic of FilterOffsetCalculator.Execute() without requiring WPF dialogs.
/// 
/// Workflow:
///   POST /api/filter-offset/start   → starts background calculation
///   GET  /api/filter-offset/status  → poll for progress
///   GET  /api/filter-offset/stop    → cancel
///   GET  /api/filter-offset/filters → list profile filters
///   GET  /api/filter-offset/result  → fetch pending old/new offsets (state == PendingResult)
///   POST /api/filter-offset/apply   → write the chosen offsets to the profile
///   GET  /api/filter-offset/discard → restore old values and go back to Idle
/// </summary>
public class FilterOffsetController : WebApiController
{
    // ── Shared state ─────────────────────────────────────────────────────────
    private static Task _offsetTask;
    private static CancellationTokenSource _cts;

    // "Idle" | "Running" | "PendingResult" | "Error"
    private static string _state = "Idle";
    private static int _currentLoop;
    private static int _totalLoops;
    private static int _currentFilterIndex;
    private static int _totalFilters;
    private static string _currentFilterName = "";
    private static string _errorMessage = "";

    // Saved values for discard
    private static bool _oldUseOffsets;
    private static int? _oldDefaultFilterPosition;
    private static List<(int Position, string Name, int FocusOffset)> _oldOffsets = new();

    // Computed result waiting for user accept/discard
    private static FilterOffsetResult _result;

    // ── GET /api/filter-offset/filters ───────────────────────────────────────
    [Route(HttpVerbs.Get, "/filter-offset/filters")]
    public ApiResponse GetFilters()
    {
        try
        {
            var profile = TouchNStars.Mediators?.Profile?.ActiveProfile;
            if (profile == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new ApiResponse { Success = false, Error = "Profile not available", StatusCode = 503, Type = "Error" };
            }

            var filters = profile.FilterWheelSettings.FilterWheelFilters
                .Select((f, idx) => new
                {
                    index = idx,
                    position = (int)f.Position,
                    name = f.Name,
                    focusOffset = f.FocusOffset,
                    autoFocusFilter = f.AutoFocusFilter,
                    autoFocusExposureTime = f.AutoFocusExposureTime,
                })
                .ToList();

            return new ApiResponse
            {
                Success = true,
                StatusCode = 200,
                Type = "FilterList",
                Response = new
                {
                    Filters = filters,
                    UseFilterWheelOffsets = profile.FocuserSettings.UseFilterWheelOffsets,
                    DefaultAutofocusExposureTime = profile.FocuserSettings.AutoFocusExposureTime,
                }
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    // ── POST /api/filter-offset/start ────────────────────────────────────────
    [Route(HttpVerbs.Post, "/filter-offset/start")]
    public async Task<ApiResponse> StartCalculation()
    {
        if (_offsetTask != null && !_offsetTask.IsCompleted)
        {
            HttpContext.Response.StatusCode = 409;
            return new ApiResponse { Success = false, Error = "Calculation already running", StatusCode = 409, Type = "Error" };
        }

        FilterOffsetStartRequest payload;
        try
        {
            using var reader = new StreamReader(HttpContext.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            payload = JsonSerializer.Deserialize<FilterOffsetStartRequest>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse { Success = false, Error = $"Invalid request body: {ex.Message}", StatusCode = 400, Type = "Error" };
        }

        if (payload?.FilterPositions == null || payload.FilterPositions.Count == 0)
        {
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse { Success = false, Error = "No filters specified", StatusCode = 400, Type = "Error" };
        }

        if (payload.Loops < 1)
        {
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse { Success = false, Error = "Loops must be >= 1", StatusCode = 400, Type = "Error" };
        }

        var profile = TouchNStars.Mediators?.Profile?.ActiveProfile;
        if (profile == null)
        {
            HttpContext.Response.StatusCode = 503;
            return new ApiResponse { Success = false, Error = "Profile not available", StatusCode = 503, Type = "Error" };
        }

        var selectedFilters = profile.FilterWheelSettings.FilterWheelFilters
            .Where(f => payload.FilterPositions.Contains((int)f.Position))
            .OrderBy(f => f.Position)
            .ToList();

        if (selectedFilters.Count == 0)
        {
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse { Success = false, Error = "No matching filters found in profile", StatusCode = 400, Type = "Error" };
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _state = "Running";
        _currentLoop = 0;
        _totalLoops = payload.Loops;
        _currentFilterIndex = 0;
        _totalFilters = selectedFilters.Count;
        _currentFilterName = "";
        _errorMessage = "";
        _result = null;

        _offsetTask = Task.Run(async () =>
        {
            try
            {
                await RunCalculation(selectedFilters, payload.Loops, token);
            }
            catch (OperationCanceledException)
            {
                Logger.Info("FilterOffset: calculation cancelled");
                RestoreOldValues();
                _state = "Idle";
            }
            catch (Exception ex)
            {
                Logger.Error($"FilterOffset: calculation failed: {ex}");
                RestoreOldValues();
                _state = "Error";
                _errorMessage = ex.Message;
            }
        }, token);

        return new ApiResponse { Success = true, Response = "Filter offset calculation started", StatusCode = 200, Type = "Success" };
    }

    // ── GET /api/filter-offset/status ────────────────────────────────────────
    [Route(HttpVerbs.Get, "/filter-offset/status")]
    public ApiResponse GetStatus()
    {
        return new ApiResponse
        {
            Success = true,
            StatusCode = 200,
            Type = "Success",
            Response = new
            {
                State = _state,
                CurrentLoop = _currentLoop,
                TotalLoops = _totalLoops,
                CurrentFilterIndex = _currentFilterIndex,
                TotalFilters = _totalFilters,
                CurrentFilterName = _currentFilterName,
                Error = _errorMessage,
            }
        };
    }

    // ── GET /api/filter-offset/stop ──────────────────────────────────────────
    [Route(HttpVerbs.Get, "/filter-offset/stop")]
    public ApiResponse StopCalculation()
    {
        try
        {
            _cts?.Cancel();
            return new ApiResponse { Success = true, Response = "Stop requested", StatusCode = 200, Type = "Success" };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    // ── GET /api/filter-offset/result ────────────────────────────────────────
    [Route(HttpVerbs.Get, "/filter-offset/result")]
    public ApiResponse GetResult()
    {
        if (_state != "PendingResult" || _result == null)
        {
            return new ApiResponse { Success = false, Error = "No result pending", StatusCode = 404, Type = "Error" };
        }

        return new ApiResponse
        {
            Success = true,
            StatusCode = 200,
            Type = "Success",
            Response = _result
        };
    }

    // ── POST /api/filter-offset/apply ────────────────────────────────────────
    [Route(HttpVerbs.Post, "/filter-offset/apply")]
    public async Task<ApiResponse> ApplyResult()
    {
        if (_state != "PendingResult" || _result == null)
        {
            HttpContext.Response.StatusCode = 409;
            return new ApiResponse { Success = false, Error = "No result pending", StatusCode = 409, Type = "Error" };
        }

        FilterOffsetApplyRequest payload;
        try
        {
            using var reader = new StreamReader(HttpContext.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            payload = JsonSerializer.Deserialize<FilterOffsetApplyRequest>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse { Success = false, Error = $"Invalid request body: {ex.Message}", StatusCode = 400, Type = "Error" };
        }

        try
        {
            var profile = TouchNStars.Mediators?.Profile?.ActiveProfile;
            if (profile == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new ApiResponse { Success = false, Error = "Profile not available", StatusCode = 503, Type = "Error" };
            }

            // Build the list of offsets to write; apply relative shift if requested
            var newOffsets = _result.NewOffsets
                .Select(o => (o.Position, o.Name, o.FocusOffset))
                .ToList();

            if (payload?.UseRelativeOffsets == true && payload.NewDefaultFilterPosition.HasValue)
            {
                var base_ = newOffsets.FirstOrDefault(o => o.Position == payload.NewDefaultFilterPosition.Value);
                int baseVal = base_.FocusOffset;
                newOffsets = newOffsets
                    .Select(o => (o.Position, o.Name, o.FocusOffset - baseVal))
                    .ToList();
            }

            // Write offsets to profile
            profile.FocuserSettings.UseFilterWheelOffsets = true;
            foreach (var (Position, Name, FocusOffset) in newOffsets)
            {
                var f = profile.FilterWheelSettings.FilterWheelFilters
                    .FirstOrDefault(x => x.Position == Position);
                if (f != null)
                    f.FocusOffset = FocusOffset;
            }

            // Set new AutoFocus filter
            foreach (var f in profile.FilterWheelSettings.FilterWheelFilters)
                f.AutoFocusFilter = f.Position == (payload?.NewDefaultFilterPosition ?? -1);

            profile.Save();

            _result = null;
            _state = "Idle";

            return new ApiResponse { Success = true, Response = "Offsets applied and profile saved", StatusCode = 200, Type = "Success" };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    // ── GET /api/filter-offset/discard ───────────────────────────────────────
    [Route(HttpVerbs.Get, "/filter-offset/discard")]
    public ApiResponse DiscardResult()
    {
        if (_state != "PendingResult")
        {
            HttpContext.Response.StatusCode = 409;
            return new ApiResponse { Success = false, Error = "No result pending", StatusCode = 409, Type = "Error" };
        }

        try
        {
            RestoreOldValues();
            _result = null;
            _state = "Idle";
            return new ApiResponse { Success = true, Response = "Discarded; old values restored", StatusCode = 200, Type = "Success" };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    // ── Calculation ───────────────────────────────────────────────────────────

    private async Task RunCalculation(List<FilterInfo> selectedFilters, int loops, CancellationToken token)
    {
        var profile = TouchNStars.Mediators.Profile.ActiveProfile;

        // 1. Save old state
        _oldUseOffsets = profile.FocuserSettings.UseFilterWheelOffsets;
        _oldDefaultFilterPosition = profile.FilterWheelSettings.FilterWheelFilters.FirstOrDefault(f => f.AutoFocusFilter)?.Position;
        _oldOffsets = selectedFilters
            .Select(f => ((int)f.Position, f.Name, f.FocusOffset))
            .ToList();

        // 2. Setup: disable offsets & autofocus-filter flag, reset offsets to 0
        profile.FocuserSettings.UseFilterWheelOffsets = false;
        foreach (var f in selectedFilters)
        {
            f.AutoFocusFilter = false;
            f.FocusOffset = 0;
        }

        // position → list of AF settled positions, one per loop
        var calculatedPositions = new Dictionary<int, List<int>>();
        foreach (var f in selectedFilters)
            calculatedPositions[(int)f.Position] = new List<int>();

        var apiUrl = await CoreUtility.GetApiUrl();
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        // 3. Run loops × filters
        for (_currentLoop = 1; _currentLoop <= loops; _currentLoop++)
        {
            _currentFilterIndex = 0;

            foreach (var filter in selectedFilters)
            {
                token.ThrowIfCancellationRequested();

                _currentFilterIndex++;
                _currentFilterName = filter.Name;

                Logger.Info($"FilterOffset: loop {_currentLoop}/{loops} — switching to filter '{filter.Name}' (position {filter.Position})");

                // Re-apply suppression immediately before each filter's AF attempts.
                // Something resets UseFilterWheelOffsets or AutoFocusFilter between iterations;
                // keeping both false prevents HocusFocus's SetAutofocusFilter from switching
                // the filter wheel away from the intended target during the AF run.
                profile.FocuserSettings.UseFilterWheelOffsets = false;
                foreach (var f in profile.FilterWheelSettings.FilterWheelFilters)
                    f.AutoFocusFilter = false;

                int position = await MeasureFilterFocusPositionAsync(filter, apiUrl, client, token);
                Logger.Info($"FilterOffset: filter '{filter.Name}' settled at position {position}");

                calculatedPositions[(int)filter.Position].Add(position);
            }
        }

        // 4. Compute new offsets using the same algorithm as FilterOffsetCalculator.Execute()
        var newOffsets = ComputeOffsets(selectedFilters, calculatedPositions, profile);

        // 5. Store result and move to PendingResult state
        _result = new FilterOffsetResult
        {
            OldOffsets = _oldOffsets
                .Select(o => new FilterOffsetEntry { Position = o.Position, Name = o.Name, FocusOffset = o.FocusOffset })
                .ToList(),
            NewOffsets = newOffsets
                .Select(o => new FilterOffsetEntry { Position = o.Position, Name = o.Name, FocusOffset = o.FocusOffset })
                .ToList(),
            OldDefaultFilterPosition = _oldDefaultFilterPosition.HasValue ? (int?)_oldDefaultFilterPosition.Value : null,
            SuggestedDefaultFilterPosition = _oldDefaultFilterPosition.HasValue ? (int?)_oldDefaultFilterPosition.Value : null,
        };

        _state = "PendingResult";
    }

    // Runs AF for one filter and returns its settled focuser position, guaranteeing the wheel was
    // actually on the requested filter for the whole run — not just at the moment ChangeFilter was
    // first called.
    //
    // The race this guards against: DataContainer.afRun (mirroring HocusFocus's "AF completed" broadcast)
    // clears BEFORE HocusFocus's own post-run cleanup restores the filter that was active when THAT run
    // started. Reacting to that early signal and switching to the next filter lets the still-in-flight
    // cleanup silently move the wheel back afterward — right as the next AF trigger fires. That gets
    // rejected ("Another AutoFocus is already in progress"), and once the retry succeeds, the wheel is
    // sitting on the reverted (wrong) filter.
    //
    // The primary fix is WaitForHocusFocusCleanupAsync below: it doesn't let this method return — and so
    // doesn't let the caller switch to the next filter — until HocusFocus's OWN in-progress guard
    // (HocusFocusVM.AutoFocusInProgress) has cleared, which only happens after that filter-restore cleanup
    // has actually finished. Re-asserting ChangeFilter before every trigger attempt (including retries),
    // and verifying the actually-mounted filter right after completion, are kept as defense in depth for
    // anything else (a manual AF from another client, etc.) that might still move the wheel mid-run.
    private static async Task<int> MeasureFilterFocusPositionAsync(FilterInfo filter, string apiUrl, HttpClient client, CancellationToken token)
    {
        const int maxVerificationAttempts = 3;

        for (var verificationAttempt = 1; ; verificationAttempt++)
        {
            token.ThrowIfCancellationRequested();

            await StartAutofocusWithRetryAsync(apiUrl, client, filter, token);

            // Wait until the AF file watcher (BackgroundWorker) signals completion
            await WaitForAutofocusAsync(token);

            if (DataContainer.afError)
                throw new Exception($"AutoFocus failed for filter '{filter.Name}'");

            // The just-completed run's own filter-restore cleanup hasn't fired yet at this point (it only
            // runs after the completion signal we just waited on), so the wheel still reflects whatever
            // filter the run actually measured through.
            var actualFilter = TouchNStars.Mediators.FilterWheel.GetInfo()?.SelectedFilter;
            if (actualFilter != null && actualFilter.Position != filter.Position)
            {
                if (verificationAttempt >= maxVerificationAttempts)
                    throw new Exception($"FilterOffset: AutoFocus for filter '{filter.Name}' kept measuring through filter '{actualFilter.Name}' instead after {verificationAttempt} attempts");

                Logger.Warning($"FilterOffset: AutoFocus for filter '{filter.Name}' actually ran through filter '{actualFilter.Name}' (race with previous run's cleanup) — discarding result and re-measuring (attempt {verificationAttempt})");
                await TouchNStars.Mediators.FilterWheel.ChangeFilter(filter, token);
                continue;
            }

            int position = await GetFocuserPositionAsync(apiUrl, client, token);

            // Don't return — and so don't let the caller switch to the next filter — until HocusFocus has
            // actually finished tearing this run down (filter restored, guard released). This is what
            // prevents the race from happening in the first place, rather than just detecting it above.
            await WaitForHocusFocusCleanupAsync(token);

            return position;
        }
    }

    // Reflects into HocusFocus's own in-progress guard (HocusFocusVM.Current.AutoFocusInProgress), which —
    // unlike DataContainer.afRun — only clears in the finally block of HocusFocusVM.StartAutoFocus, i.e.
    // after "await autoFocusEngine.Run(...)" has fully returned, including AutoFocusEngine.RunImpl's own
    // outer finally (PerformPostAutoFocusActions' filter restore, then ReleaseAutoFocusInProgress). That
    // makes it the one externally-observable signal that means "safe to switch to the next filter now".
    // Returns null (and doesn't block) if HocusFocus isn't loaded or the property can't be read, so this
    // degrades gracefully rather than hanging the calibration on an unrelated AF backend.
    private static bool? IsHocusFocusAutoFocusInProgress()
    {
        try
        {
            var hocusFocusVMType = Type.GetType("NINA.Joko.Plugins.HocusFocus.AutoFocus.HocusFocusVM, NINA.Joko.Plugins.HocusFocus");
            var currentVM = hocusFocusVMType?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            return currentVM?.GetType().GetProperty("AutoFocusInProgress")?.GetValue(currentVM) as bool?;
        }
        catch (Exception ex)
        {
            Logger.Warning($"FilterOffset: could not read HocusFocus AutoFocusInProgress via reflection: {ex.Message}");
            return null;
        }
    }

    private static async Task WaitForHocusFocusCleanupAsync(CancellationToken token)
    {
        // PerformPostAutoFocusActions' individual steps (filter restore, temp-comp restore, guiding
        // restart) each allow up to 1 minute, so give this generous headroom before giving up.
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();

            if (IsHocusFocusAutoFocusInProgress() != true) return; // false (torn down) or null (unavailable) — either way, don't block

            await Task.Delay(250, token);
        }

        Logger.Warning("FilterOffset: HocusFocus AutoFocus cleanup did not clear within 90s — proceeding anyway");
    }

    private static async Task StartAutofocusWithRetryAsync(string apiUrl, HttpClient client, FilterInfo filter, CancellationToken token)
    {
        // The previous run's post-AF cleanup steps each time out after 1 minute (filter restore,
        // temp-comp restore, guiding restart), so allow up to 3 minutes of re-triggering before
        // giving up with a clear error instead of hanging.
        var deadline = DateTime.UtcNow.AddMinutes(3);

        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();

            // Re-assert the target filter right before every trigger attempt, including retries — a
            // previous run's delayed cleanup can revert the wheel between attempts (see
            // MeasureFilterFocusPositionAsync), and this is the last write before AF captures "current
            // filter" as the run's imaging filter.
            await TouchNStars.Mediators.FilterWheel.ChangeFilter(filter, token);

            // Reset AF tracking state and start AF (ninaAPI call is async: returns "started" immediately)
            lock (DataContainer.lockObj)
            {
                DataContainer.afRun = true;
                DataContainer.afError = false;
                DataContainer.afErrorText = string.Empty;
                DataContainer.newAfGraph = false;
                DataContainer.afStartConfirmed = false;
            }

            await client.GetAsync($"{apiUrl}/equipment/focuser/auto-focus", token);

            if (await WaitForAutofocusStartAsync(token)) return;

            if (DateTime.UtcNow >= deadline)
                throw new Exception($"AutoFocus did not start for filter '{filter.Name}' after {attempt} attempts — a previous AutoFocus run may be stuck (see NINA log)");

            Logger.Warning($"FilterOffset: AutoFocus for filter '{filter.Name}' did not start (attempt {attempt}, previous run likely still finishing) — retrying");
            await Task.Delay(5000, token);
        }
    }

    private static async Task<bool> WaitForAutofocusStartAsync(CancellationToken token)
    {
        // AutoFocusRunStarting is broadcast right after the AF run claims its in-progress guard,
        // well before any exposures, so a healthy start confirms within a few seconds.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();

            lock (DataContainer.lockObj)
            {
                if (DataContainer.afStartConfirmed) return true;
            }

            await Task.Delay(500, token);
        }
        return false;
    }

    private static async Task WaitForAutofocusAsync(CancellationToken token)
    {
        // Give NINA a moment to write its AF file / log before we start polling
        await Task.Delay(2000, token);

        var deadline = DateTime.UtcNow.AddMinutes(20);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();

            bool stillRunning;
            lock (DataContainer.lockObj)
                stillRunning = DataContainer.afRun;

            if (!stillRunning) return;

            await Task.Delay(1000, token);
        }

        throw new TimeoutException("AutoFocus did not complete within 20 minutes");
    }

    private static async Task<int> GetFocuserPositionAsync(string apiUrl, HttpClient client, CancellationToken token)
    {
        try
        {
            var resp = await client.GetAsync($"{apiUrl}/equipment/focuser/info", token);
            if (!resp.IsSuccessStatusCode) return 0;

            var json = await resp.Content.ReadAsStringAsync(token);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("Response", out var response) &&
                response.TryGetProperty("Position", out var pos))
                return pos.GetInt32();
        }
        catch (Exception ex)
        {
            Logger.Warning($"FilterOffset: could not read focuser position: {ex.Message}");
        }
        return 0;
    }

    /// <summary>
    /// Replicates the offset-calculation math from FilterOffsetCalculator.Execute().
    /// </summary>
    private static List<(int Position, string Name, int FocusOffset)> ComputeOffsets(
        List<FilterInfo> selectedFilters,
        Dictionary<int, List<int>> calculatedPositions,
        NINA.Profile.Interfaces.IProfile profile)
    {
        // Temperature drift: average drift per loop of the first (base) filter
        int temperatureDrift = 0;
        if (selectedFilters.Count > 0)
        {
            var basePositions = calculatedPositions[(int)selectedFilters[0].Position];
            if (basePositions.Count > 1)
            {
                var drifts = new List<int>();
                for (int i = 0; i < basePositions.Count - 1; i++)
                    drifts.Add(basePositions[i] - basePositions[i + 1]);
                temperatureDrift = (int)Math.Ceiling(drifts.Average());
            }
        }

        double defaultAfTime = profile.FocuserSettings.AutoFocusExposureTime;
        double defaultFilterAfTime = new FilterInfo().AutoFocusExposureTime;

        // Total exposure time across selected filters (used for ratio weighting)
        int totalTime = (int)selectedFilters.Sum(f =>
        {
            var pf = profile.FilterWheelSettings.FilterWheelFilters.FirstOrDefault(x => x.Position == f.Position);
            return pf?.AutoFocusExposureTime == defaultFilterAfTime ? defaultAfTime : (pf?.AutoFocusExposureTime ?? defaultAfTime);
        });
        if (totalTime <= 0) totalTime = 1;

        var result = new List<(int Position, string Name, int FocusOffset)>();
        double totalRatio = 0.0;

        foreach (var filter in selectedFilters)
        {
            var pf = profile.FilterWheelSettings.FilterWheelFilters.FirstOrDefault(x => x.Position == filter.Position);
            double filterTime = pf?.AutoFocusExposureTime == defaultFilterAfTime
                ? defaultAfTime
                : (pf?.AutoFocusExposureTime ?? defaultAfTime);

            double filterRatio = filterTime / totalTime;
            var positions = calculatedPositions[(int)filter.Position];
            int focusOffset = positions.Count > 0
                ? (int)Math.Ceiling(positions.Average() + (totalRatio * temperatureDrift))
                : 0;

            result.Add(((int)filter.Position, filter.Name, focusOffset));
            totalRatio += filterRatio;
        }

        return result;
    }

    private static void RestoreOldValues()
    {
        try
        {
            var profile = TouchNStars.Mediators?.Profile?.ActiveProfile;
            if (profile == null) return;

            profile.FocuserSettings.UseFilterWheelOffsets = _oldUseOffsets;

            foreach (var (Position, Name, FocusOffset) in _oldOffsets)
            {
                var f = profile.FilterWheelSettings.FilterWheelFilters.FirstOrDefault(x => x.Position == Position);
                if (f != null) f.FocusOffset = FocusOffset;
            }

            foreach (var f in profile.FilterWheelSettings.FilterWheelFilters)
                f.AutoFocusFilter = f.Position == (_oldDefaultFilterPosition ?? -1);
        }
        catch (Exception ex)
        {
            Logger.Error($"FilterOffset: failed to restore old values: {ex.Message}");
        }
    }
}

// ── Request / response models ─────────────────────────────────────────────────

public class FilterOffsetStartRequest
{
    public int Loops { get; set; } = 3;
    public List<int> FilterPositions { get; set; }
}

public class FilterOffsetApplyRequest
{
    public bool UseRelativeOffsets { get; set; }
    public int? NewDefaultFilterPosition { get; set; }
}

public class FilterOffsetEntry
{
    public int Position { get; set; }
    public string Name { get; set; }
    public int FocusOffset { get; set; }
}

public class FilterOffsetResult
{
    public List<FilterOffsetEntry> OldOffsets { get; set; }
    public List<FilterOffsetEntry> NewOffsets { get; set; }
    public int? OldDefaultFilterPosition { get; set; }
    public int? SuggestedDefaultFilterPosition { get; set; }
}
