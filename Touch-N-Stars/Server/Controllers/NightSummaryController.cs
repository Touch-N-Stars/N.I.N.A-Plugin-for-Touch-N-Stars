using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TouchNStars.Server.Models;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// API Controller for Night Summary plugin integration.
/// Binds to the stable in-process facade <c>NINA.Plugin.NightSummary.Integration.NightSummaryApi</c>
/// via reflection (avoids a compile-time dependency on the Night Summary plugin assembly, whose
/// internal types are not part of any stability contract — the facade is).
/// </summary>
public class NightSummaryController : WebApiController
{
    private static Assembly _nsAssembly;
    private static Type _apiType;
    private static readonly object _initLock = new object();

    private static Assembly GetNightSummaryAssembly()
    {
        if (_nsAssembly != null) return _nsAssembly;
        lock (_initLock)
        {
            if (_nsAssembly != null) return _nsAssembly;
            _nsAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "NINA.Plugin.NightSummary");
        }
        return _nsAssembly;
    }

    private static Type GetNightSummaryApiType()
    {
        if (_apiType != null) return _apiType;
        lock (_initLock)
        {
            if (_apiType != null) return _apiType;
            var asm = GetNightSummaryAssembly();
            _apiType = asm?.GetType("NINA.Plugin.NightSummary.Integration.NightSummaryApi");
        }
        return _apiType;
    }

    /// <summary>
    /// Invokes a public static method on the NightSummaryApi facade and returns its JSON string
    /// result, or null if the facade type/method isn't present (plugin not loaded / too old).
    /// </summary>
    private static string InvokeApi(string methodName, params object[] args)
    {
        var apiType = GetNightSummaryApiType();
        var method = apiType?.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        return (string)method?.Invoke(null, args);
    }

    private async Task SendJsonAsync(string json)
    {
        await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8);
    }

    private async Task SendErrorAsync(string message)
    {
        await SendJsonAsync(JsonSerializer.Serialize(new ApiResponse { Success = false, Error = message }));
    }

    /// <summary>
    /// Converts an object with public properties to a Dictionary for JSON serialization.
    /// Only used by the Test* endpoints below, which still need the raw (unmasked) live
    /// settings object to actually send a test notification.
    /// </summary>
    private static Dictionary<string, object> MapToDict(object obj)
    {
        if (obj == null) return null;
        var dict = new Dictionary<string, object>();
        foreach (var prop in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            try { dict[prop.Name] = prop.GetValue(obj); }
            catch { dict[prop.Name] = null; }
        }
        return dict;
    }

    private static T GetVal<T>(Dictionary<string, object> dict, string key, T fallback = default)
    {
        if (dict == null || !dict.TryGetValue(key, out var raw) || raw == null)
            return fallback;
        try { return (T)Convert.ChangeType(raw, typeof(T)); }
        catch { return fallback; }
    }

    /// <summary>Reads a JsonElement-backed object's properties into a lookup for LINQ computation.</summary>
    private static Dictionary<string, JsonElement> ToElementDict(JsonElement obj)
    {
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (obj.ValueKind == JsonValueKind.Object)
            foreach (var prop in obj.EnumerateObject())
                dict[prop.Name] = prop.Value;
        return dict;
    }

    private static double GetD(Dictionary<string, JsonElement> d, string key) =>
        d.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static bool GetBool(Dictionary<string, JsonElement> d, string key) =>
        d.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.True;

    private static string GetStr(Dictionary<string, JsonElement> d, string key) =>
        d.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>GET /api/nightsummary/status — returns whether the Night Summary plugin is loaded.</summary>
    [Route(HttpVerbs.Get, "/nightsummary/status")]
    public async Task GetNightSummaryStatus()
    {
        try
        {
            var json = await Task.Run(() => InvokeApi("Status"));
            if (json == null) { await SendErrorAsync("Night Summary plugin not loaded"); return; }
            await SendJsonAsync(json);
        }
        catch (Exception ex)
        {
            Logger.Error($"NightSummaryController: GetNightSummaryStatus failed: {ex.InnerException?.Message ?? ex.Message}");
            await SendErrorAsync(ex.InnerException?.Message ?? ex.Message);
        }
    }

    /// <summary>GET /api/nightsummary/sessions?limit=50 — list recent sessions.</summary>
    [Route(HttpVerbs.Get, "/nightsummary/sessions")]
    public async Task GetSessions([QueryField] int limit = 50)
    {
        try
        {
            var json = await Task.Run(() => InvokeApi("Sessions", limit));
            if (json == null) { await SendErrorAsync("Night Summary plugin not loaded"); return; }
            await SendJsonAsync(json);
        }
        catch (Exception ex)
        {
            Logger.Error($"NightSummaryController: GetSessions failed: {ex.InnerException?.Message ?? ex.Message}");
            await SendErrorAsync(ex.InnerException?.Message ?? ex.Message);
        }
    }

    /// <summary>
    /// GET /api/nightsummary/sessions/{sessionId} — full session detail: session record, summary
    /// stats, per-target/filter breakdown, images, events, timing events, session history.
    /// The facade's Session() call returns the raw building blocks (Session/Images/Events/
    /// TimingEvents/SessionHistory); Stats/ByTarget/ReportAvailable are computed here from the
    /// Images array, same as before the facade migration.
    /// </summary>
    [Route(HttpVerbs.Get, "/nightsummary/sessions/{sessionId}")]
    public async Task GetSession(string sessionId)
    {
        try
        {
            var json = await Task.Run(() => InvokeApi("Session", sessionId));
            if (json == null) { await SendErrorAsync("Night Summary plugin not loaded"); return; }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("Success", out var successEl) || successEl.ValueKind != JsonValueKind.True)
            {
                await SendJsonAsync(json); // pass the facade's own error envelope through unchanged
                return;
            }

            var response = root.GetProperty("Response");
            var sessionEl = response.GetProperty("Session");
            var imagesEl = response.TryGetProperty("Images", out var im) ? im : default;
            var eventsEl = response.TryGetProperty("Events", out var ev) ? ev : default;
            var timingEl = response.TryGetProperty("TimingEvents", out var te) ? te : default;
            var historyEl = response.TryGetProperty("SessionHistory", out var sh) ? sh : default;

            var images = imagesEl.ValueKind == JsonValueKind.Array
                ? imagesEl.EnumerateArray().Select(ToElementDict).ToList()
                : new List<Dictionary<string, JsonElement>>();

            var lightImages = images.Where(i => { var t = GetStr(i, "ImageType"); return string.IsNullOrEmpty(t) || t == "LIGHT"; }).ToList();
            var acceptedImages = lightImages.Where(i => GetBool(i, "Accepted")).ToList();

            double totalExpSec = lightImages.Sum(i => GetD(i, "ExposureDuration"));
            var targets = lightImages
                .Select(i => GetStr(i, "TargetName"))
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t)
                .ToList();

            double avgHfr = acceptedImages.Any()
                ? acceptedImages.Select(i => GetD(i, "HFR")).Where(h => h > 0).DefaultIfEmpty(0).Average()
                : 0;
            double avgGuiding = acceptedImages.Any()
                ? acceptedImages.Select(i => GetD(i, "GuidingRMSTotal")).Where(g => g > 0).DefaultIfEmpty(0).Average()
                : 0;
            double avgFwhm = acceptedImages.Any()
                ? acceptedImages.Select(i => GetD(i, "FWHM")).Where(f => f > 0).DefaultIfEmpty(0).Average()
                : 0;

            var byTarget = lightImages
                .GroupBy(i => GetStr(i, "TargetName") ?? "")
                .Select(g => new
                {
                    Target = g.Key,
                    ImageCount = g.Count(),
                    AcceptedCount = g.Count(i => GetBool(i, "Accepted")),
                    TotalExposureSeconds = g.Sum(i => GetD(i, "ExposureDuration")),
                    AvgHfr = Math.Round(g.Select(i => GetD(i, "HFR")).Where(h => h > 0).DefaultIfEmpty(0).Average(), 2),
                    Filters = g
                        .GroupBy(i => GetStr(i, "Filter") ?? "")
                        .Select(fg => new
                        {
                            Filter = fg.Key,
                            Count = fg.Count(),
                            AcceptedCount = fg.Count(i => GetBool(i, "Accepted")),
                            TotalExposureSeconds = fg.Sum(i => GetD(i, "ExposureDuration"))
                        })
                        .OrderBy(f => f.Filter)
                        .ToList()
                })
                .OrderBy(t => t.Target)
                .ToList();

            var sessionDict = ToElementDict(sessionEl);
            int skippedExposures = sessionDict.TryGetValue("SkippedExposures", out var se) && se.ValueKind == JsonValueKind.Number
                ? se.GetInt32()
                : 0;

            var result = new
            {
                Success = true,
                Response = new
                {
                    Session = sessionEl,
                    ReportAvailable = GetReportPath(sessionId) is string rp && File.Exists(rp),
                    Stats = new
                    {
                        TotalImages = lightImages.Count,
                        AcceptedImages = acceptedImages.Count,
                        TotalExposureSeconds = Math.Round(totalExpSec, 1),
                        Targets = targets,
                        AvgHfr = Math.Round(avgHfr, 2),
                        AvgGuidingRms = Math.Round(avgGuiding, 2),
                        AvgFwhm = Math.Round(avgFwhm, 2),
                        SkippedExposures = skippedExposures
                    },
                    ByTarget = byTarget,
                    Images = imagesEl,
                    Events = eventsEl,
                    TimingEvents = timingEl,
                    SessionHistory = historyEl
                }
            };

            await SendJsonAsync(JsonSerializer.Serialize(result));
        }
        catch (Exception ex)
        {
            Logger.Error($"NightSummaryController: GetSession failed: {ex.InnerException?.Message ?? ex.Message}");
            await SendErrorAsync(ex.InnerException?.Message ?? ex.Message);
        }
    }

    /// <summary>
    /// GET /api/nightsummary/sessions/{sessionId}/report — serve the session's full HTML report.
    /// The report is written automatically for every session to
    /// %LOCALAPPDATA%\NINA\NightSummary\reports\{sessionId}.html (keyed by SessionId).
    /// Returns raw text/html (not a JSON ApiResponse) so it can be embedded in an iframe.
    /// Reads the report directly off disk — not part of the NightSummaryApi facade surface.
    /// </summary>
    [Route(HttpVerbs.Get, "/nightsummary/sessions/{sessionId}/report")]
    public async Task GetSessionReport(string sessionId)
    {
        var reportPath = GetReportPath(sessionId);
        if (reportPath == null || !File.Exists(reportPath))
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.SendStringAsync("Report not found", "text/plain", Encoding.UTF8);
            return;
        }

        var html = await File.ReadAllTextAsync(reportPath);
        await HttpContext.SendStringAsync(html, "text/html", Encoding.UTF8);
    }

    /// <summary>
    /// Resolves the on-disk path of a session's HTML report
    /// (%LOCALAPPDATA%\NINA\NightSummary\reports\{sessionId}.html).
    /// Returns null if the sessionId would escape the reports directory (path-traversal guard).
    /// Does not check for existence — callers use File.Exists on the result.
    /// </summary>
    private static string GetReportPath(string sessionId)
    {
        var reportsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NINA", "NightSummary", "reports");
        var reportPath = Path.GetFullPath(Path.Combine(reportsDir, Path.GetFileName(sessionId) + ".html"));
        return reportPath.StartsWith(Path.GetFullPath(reportsDir), StringComparison.OrdinalIgnoreCase)
            ? reportPath
            : null;
    }

    /// <summary>
    /// DELETE /api/nightsummary/sessions/{sessionId} — delete a session and all its records.
    /// Cleanup-aware via the facade: also removes the report HTML, settings sidecar, livestack
    /// masters and thumbnails (the raw SessionDatabase.DeleteSession this used to call orphaned them).
    /// </summary>
    [Route(HttpVerbs.Delete, "/nightsummary/sessions/{sessionId}")]
    public async Task DeleteSession(string sessionId)
    {
        try
        {
            var json = await Task.Run(() => InvokeApi("DeleteSession", sessionId));
            if (json == null) { await SendErrorAsync("Night Summary plugin not loaded"); return; }
            await SendJsonAsync(json);
        }
        catch (Exception ex)
        {
            Logger.Error($"NightSummaryController: DeleteSession failed: {ex.InnerException?.Message ?? ex.Message}");
            await SendErrorAsync(ex.InnerException?.Message ?? ex.Message);
        }
    }

    // ─── Helpers (still needed by the Test* endpoints, which have no facade equivalent) ───────

    private static object GetSettingsManager()
    {
        var asm = GetNightSummaryAssembly();
        if (asm == null) return null;
        var managerType = asm.GetType("NINA.Plugin.NightSummary.Data.SettingsManager");
        if (managerType == null) return null;
        var instanceProp = managerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
        return instanceProp?.GetValue(null);
    }

    private static object GetCurrentSettings()
    {
        var manager = GetSettingsManager();
        if (manager == null) return null;
        var currentProp = manager.GetType().GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
        return currentProp?.GetValue(manager);
    }

    // ─── Settings ─────────────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/nightsummary/settings — read plugin settings. The facade already masks the 5
    /// secret fields (SmtpPassword, DiscordWebhookUrl, PushoverAppToken, PushoverUserKey,
    /// DashboardApiKey) into "&lt;field&gt;Set" booleans; we only add the NINA-profile filter
    /// name list, which the facade has no reason to know about.
    /// </summary>
    [Route(HttpVerbs.Get, "/nightsummary/settings")]
    public async Task GetSettings()
    {
        try
        {
            var json = await Task.Run(() => InvokeApi("GetSettings"));
            if (json == null) { await SendErrorAsync("Night Summary plugin not loaded"); return; }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("Success", out var successEl) || successEl.ValueKind != JsonValueKind.True)
            {
                await SendJsonAsync(json);
                return;
            }

            var dict = new Dictionary<string, object>();
            foreach (var prop in root.GetProperty("Response").EnumerateObject())
                dict[prop.Name] = prop.Value;
            dict["_filterNames"] = GetProfileFilterNames();

            await SendJsonAsync(JsonSerializer.Serialize(new { Success = true, Response = dict }));
        }
        catch (Exception ex)
        {
            Logger.Error($"NightSummaryController: GetSettings failed: {ex.InnerException?.Message ?? ex.Message}");
            await SendErrorAsync(ex.InnerException?.Message ?? ex.Message);
        }
    }

    /// <summary>
    /// PUT /api/nightsummary/settings — update settings. The request body is forwarded verbatim
    /// as the facade's patchJson; write-only secret semantics (blank keeps current value) and
    /// persisting through SettingsManager are the facade's responsibility now.
    /// </summary>
    [Route(HttpVerbs.Put, "/nightsummary/settings")]
    public async Task UpdateSettings()
    {
        try
        {
            var bodyStr = await ReadBodyStringAsync();
            if (string.IsNullOrWhiteSpace(bodyStr)) { await SendErrorAsync("Invalid request body"); return; }

            var json = await Task.Run(() => InvokeApi("UpdateSettings", bodyStr));
            if (json == null) { await SendErrorAsync("Night Summary plugin not loaded"); return; }
            await SendJsonAsync(json);
        }
        catch (Exception ex)
        {
            Logger.Error($"NightSummaryController: UpdateSettings failed: {ex.InnerException?.Message ?? ex.Message}");
            await SendErrorAsync(ex.InnerException?.Message ?? ex.Message);
        }
    }

    private async Task<string> ReadBodyStringAsync()
    {
        using var reader = new StreamReader(HttpContext.Request.InputStream);
        return await reader.ReadToEndAsync();
    }

    private static List<string> GetProfileFilterNames()
    {
        try
        {
            var profile = TouchNStars.Mediators?.Profile?.ActiveProfile;
            if (profile == null) return new List<string>();
            var filters = profile.FilterWheelSettings?.FilterWheelFilters;
            if (filters == null) return new List<string>();
            return ((IEnumerable)filters).Cast<object>()
                .Select(f => f.GetType().GetProperty("Name")?.GetValue(f)?.ToString() ?? "")
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    // ─── Test notifications (no facade equivalent — still read the raw live settings) ─────────

    /// <summary>POST /api/nightsummary/test-email — send a test email.</summary>
    [Route(HttpVerbs.Post, "/nightsummary/test-email")]
    public async Task<object> TestEmail()
    {
        return await Task.Run(async () =>
        {
            var settings = GetCurrentSettings();
            if (settings == null)
                return (object)new ApiResponse { Success = false, Error = "Night Summary plugin not loaded" };

            try
            {
                var s = MapToDict(settings);
                var useGmail = GetVal<bool>(s, "UseGmailSmtp", true);
                var sender = (string)(s.GetValueOrDefault("SenderAddress") ?? "");
                var password = (string)(s.GetValueOrDefault("SmtpPassword") ?? "");
                var recipient = (string)(s.GetValueOrDefault("RecipientAddress") ?? "");
                var smtpHost = useGmail ? "smtp.gmail.com" : (string)(s.GetValueOrDefault("SmtpHost") ?? "smtp.gmail.com");
                var smtpPort = useGmail ? 587 : GetVal<int>(s, "SmtpPort", 587);
                var smtpSsl = useGmail || GetVal<bool>(s, "SmtpSsl", true);

                if (string.IsNullOrWhiteSpace(sender) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(recipient))
                    return (object)new { Success = true, Response = new { Ok = false, Message = "Fill in all email fields first" } };

                var asm = GetNightSummaryAssembly();
                var emailSenderType = asm?.GetType("NINA.Plugin.NightSummary.Reporting.EmailSender");
                if (emailSenderType == null)
                    return (object)new ApiResponse { Success = false, Error = "EmailSender type not found" };

                var emailSender = Activator.CreateInstance(emailSenderType, smtpHost, smtpPort, smtpSsl, sender, password, recipient);
                var sendMethod = emailSenderType.GetMethod("SendTestAsync");
                var task = (Task<bool>)sendMethod.Invoke(emailSender, null);
                bool ok = await task;
                return (object)new { Success = true, Response = new { Ok = ok, Message = ok ? "Test email sent" : "Failed — check NINA log" } };
            }
            catch (Exception ex)
            {
                Logger.Error($"NightSummaryController: TestEmail failed: {ex.InnerException?.Message ?? ex.Message}");
                return (object)new { Success = true, Response = new { Ok = false, Message = ex.InnerException?.Message ?? ex.Message } };
            }
        });
    }

    /// <summary>POST /api/nightsummary/test-discord — send a test Discord message.</summary>
    [Route(HttpVerbs.Post, "/nightsummary/test-discord")]
    public async Task<object> TestDiscord()
    {
        return await Task.Run(async () =>
        {
            var settings = GetCurrentSettings();
            if (settings == null)
                return (object)new ApiResponse { Success = false, Error = "Night Summary plugin not loaded" };

            try
            {
                var s = MapToDict(settings);
                var url = (string)(s.GetValueOrDefault("DiscordWebhookUrl") ?? "");
                if (string.IsNullOrWhiteSpace(url))
                    return (object)new { Success = true, Response = new { Ok = false, Message = "Webhook URL is empty" } };

                var asm = GetNightSummaryAssembly();
                var senderType = asm?.GetType("NINA.Plugin.NightSummary.Reporting.DiscordSender");
                if (senderType == null)
                    return (object)new ApiResponse { Success = false, Error = "DiscordSender type not found" };

                var discordSender = Activator.CreateInstance(senderType, url);
                var sendMethod = senderType.GetMethod("SendTestAsync");
                var task = (Task<bool>)sendMethod.Invoke(discordSender, null);
                bool ok = await task;
                return (object)new { Success = true, Response = new { Ok = ok, Message = ok ? "Test message sent" : "Failed — check NINA log" } };
            }
            catch (Exception ex)
            {
                Logger.Error($"NightSummaryController: TestDiscord failed: {ex.InnerException?.Message ?? ex.Message}");
                return (object)new { Success = true, Response = new { Ok = false, Message = ex.InnerException?.Message ?? ex.Message } };
            }
        });
    }

    /// <summary>POST /api/nightsummary/test-pushover — send a test Pushover notification.</summary>
    [Route(HttpVerbs.Post, "/nightsummary/test-pushover")]
    public async Task<object> TestPushover()
    {
        return await Task.Run(async () =>
        {
            var settings = GetCurrentSettings();
            if (settings == null)
                return (object)new ApiResponse { Success = false, Error = "Night Summary plugin not loaded" };

            try
            {
                var s = MapToDict(settings);
                var appToken = (string)(s.GetValueOrDefault("PushoverAppToken") ?? "");
                var userKey = (string)(s.GetValueOrDefault("PushoverUserKey") ?? "");

                if (string.IsNullOrWhiteSpace(appToken) || string.IsNullOrWhiteSpace(userKey))
                    return (object)new { Success = true, Response = new { Ok = false, Message = "App token or user key is empty" } };

                var asm = GetNightSummaryAssembly();
                var senderType = asm?.GetType("NINA.Plugin.NightSummary.Reporting.PushoverSender");
                if (senderType == null)
                    return (object)new ApiResponse { Success = false, Error = "PushoverSender type not found" };

                var pushoverSender = Activator.CreateInstance(senderType, appToken, userKey);
                var sendMethod = senderType.GetMethod("SendAsync");
                var task = (Task<bool>)sendMethod.Invoke(pushoverSender, new object[] { "Night Summary", "Pushover is configured correctly!" });
                bool ok = await task;
                return (object)new { Success = true, Response = new { Ok = ok, Message = ok ? "Test notification sent" : "Failed — check NINA log" } };
            }
            catch (Exception ex)
            {
                Logger.Error($"NightSummaryController: TestPushover failed: {ex.InnerException?.Message ?? ex.Message}");
                return (object)new { Success = true, Response = new { Ok = false, Message = ex.InnerException?.Message ?? ex.Message } };
            }
        });
    }

    /// <summary>
    /// POST /api/nightsummary/sessions/{sessionId}/resend — re-fire configured delivery channels
    /// for a historical session. Replaces the previous ~60-line reflection-based reconstruction
    /// of SessionService (fragile constructor-parameter guessing) with a single facade call.
    /// </summary>
    [Route(HttpVerbs.Post, "/nightsummary/sessions/{sessionId}/resend")]
    public async Task ResendSession(string sessionId)
    {
        try
        {
            var json = await Task.Run(() => InvokeApi("Resend", sessionId));
            if (json == null) { await SendErrorAsync("Night Summary plugin not loaded"); return; }
            await SendJsonAsync(json);
        }
        catch (Exception ex)
        {
            Logger.Error($"NightSummaryController: ResendSession failed: {ex.InnerException?.Message ?? ex.Message}");
            await SendErrorAsync(ex.InnerException?.Message ?? ex.Message);
        }
    }
}
