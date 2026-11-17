using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using TouchNStars.Server.Services;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// Atlas survey download: status, start, cancel, delete. The optional 'survey' field
/// ("dss" or "nsns", query or body) selects the survey; without it DSS is meant, which is
/// what apps from before NSNS send. The tiles themselves are served by the static route
/// registered for each <see cref="SurveyDefinition.Route"/>.
/// </summary>
public class HipsSurveyController : WebApiController
{
    // Encoding.UTF8 would prefix the body with a BOM, which non-browser JSON parsers reject.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Include
    };

    [Route(HttpVerbs.Get, "/atlas/survey/status")]
    public Task Status()
    {
        try
        {
            HipsSurveyService service = HipsSurveyService.ForId(HttpContext.Request.QueryString["survey"]);
            if (service == null)
            {
                return SendUnknownSurvey();
            }

            HipsSurveyService.SurveyStatus status = service.GetStatus();
            return SendJson(new
            {
                success = true,
                status.Survey,
                status.Path,
                status.SourceUrls,
                status.InstalledOrder,
                status.HasAllsky,
                status.LegacyFormat,
                status.TotalBytes,
                status.FreeBytes,
                status.Orders,
                status.Job,
                minOrder = service.Definition.MinOrder,
                baseOrder = service.Definition.BaseOrder,
                maxOrder = service.Definition.MaxOrder
            }, 200);
        }
        catch (Exception ex)
        {
            Logger.Error($"[HipsSurveyController.Status] {ex.Message}", ex);
            return SendJson(new { success = false, error = "Failed to read the survey status." }, 500);
        }
    }

    [Route(HttpVerbs.Post, "/atlas/survey/download")]
    public async Task Start()
    {
        try
        {
            Dictionary<string, object> body = await ReadBodyAsync().ConfigureAwait(false);
            HipsSurveyService service = HipsSurveyService.ForId(ReadField(body, "survey"));
            if (service == null)
            {
                await SendUnknownSurvey().ConfigureAwait(false);
                return;
            }

            int? targetOrder = ReadIntField(body, "targetOrder");
            if (targetOrder == null)
            {
                await SendJson(new { success = false, error = "Field 'targetOrder' must be an integer." }, 400).ConfigureAwait(false);
                return;
            }

            await SendResult(service.StartDownload(targetOrder.Value)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error($"[HipsSurveyController.Start] {ex.Message}", ex);
            await SendJson(new { success = false, error = "Failed to start the survey download." }, 500).ConfigureAwait(false);
        }
    }

    [Route(HttpVerbs.Post, "/atlas/survey/cancel")]
    public async Task Cancel()
    {
        Dictionary<string, object> body = await ReadBodyAsync().ConfigureAwait(false);
        HipsSurveyService service = HipsSurveyService.ForId(ReadField(body, "survey"));
        if (service == null)
        {
            await SendUnknownSurvey().ConfigureAwait(false);
            return;
        }

        await SendResult(service.CancelDownload()).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the survey. With no 'keepOrder' field the whole survey is removed; with it,
    /// only the orders above that order are removed (downgrade instead of a full wipe).
    /// </summary>
    [Route(HttpVerbs.Post, "/atlas/survey/delete")]
    public async Task Delete()
    {
        try
        {
            Dictionary<string, object> body = await ReadBodyAsync().ConfigureAwait(false);
            HipsSurveyService service = HipsSurveyService.ForId(ReadField(body, "survey"));
            if (service == null)
            {
                await SendUnknownSurvey().ConfigureAwait(false);
                return;
            }

            int? keepOrder = ReadIntField(body, "keepOrder");
            await SendResult(service.DeleteSurvey(keepOrder)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error($"[HipsSurveyController.Delete] {ex.Message}", ex);
            await SendJson(new { success = false, error = "Failed to delete the survey." }, 500).ConfigureAwait(false);
        }
    }

    /// <summary>The request body as a dictionary (empty when absent or unreadable); read once per request.</summary>
    private async Task<Dictionary<string, object>> ReadBodyAsync()
    {
        try
        {
            return await HttpContext
                .GetRequestDataAsync<Dictionary<string, object>>()
                .ConfigureAwait(false) ?? new Dictionary<string, object>();
        }
        catch (Exception ex)
        {
            Logger.Debug($"[HipsSurveyController] Request body unreadable: {ex.Message}");
            return new Dictionary<string, object>();
        }
    }

    /// <summary>A field from the query string, falling back to the request body.</summary>
    private string ReadField(Dictionary<string, object> body, string fieldName)
    {
        string query = HttpContext.Request.QueryString[fieldName];
        if (!string.IsNullOrWhiteSpace(query))
        {
            return query;
        }

        return body.TryGetValue(fieldName, out object raw) ? Convert.ToString(raw, CultureInfo.InvariantCulture) : null;
    }

    private int? ReadIntField(Dictionary<string, object> body, string fieldName)
    {
        return int.TryParse(ReadField(body, fieldName), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;
    }

    private Task SendUnknownSurvey()
    {
        return SendJson(new { success = false, error = "Unknown survey.", code = 400 }, 200);
    }

    // Business-rule refusals (already running, not enough space) travel as HTTP 200 with
    // success=false: the app's axios interceptor swallows the body of non-2xx responses and
    // the reason would never reach the user.
    private Task SendResult(HipsSurveyService.OperationResult result)
    {
        return SendJson(new { result.Success, result.Error, code = result.StatusCode }, 200);
    }

    private Task SendJson(object data, int statusCode)
    {
        HttpContext.Response.StatusCode = statusCode;
        string json = JsonConvert.SerializeObject(data, JsonSettings);
        return HttpContext.SendStringAsync(json, "application/json", Utf8NoBom);
    }
}
