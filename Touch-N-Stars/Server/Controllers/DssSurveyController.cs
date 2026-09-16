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
/// Atlas DSS survey download: status, start, cancel, delete. The tiles themselves are served
/// by the static route registered for <see cref="DssSurveyService.SurveyRoute"/>.
/// </summary>
public class DssSurveyController : WebApiController
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
            DssSurveyService.SurveyStatus status = DssSurveyService.Instance.GetStatus();
            return SendJson(new
            {
                success = true,
                status.Path,
                status.SourceUrls,
                status.InstalledOrder,
                status.HasAllsky,
                status.LegacyFormat,
                status.TotalBytes,
                status.FreeBytes,
                status.Orders,
                status.Job,
                minOrder = DssSurveyService.MinOrder,
                baseOrder = DssSurveyService.BaseOrder,
                maxOrder = DssSurveyService.MaxOrder
            }, 200);
        }
        catch (Exception ex)
        {
            Logger.Error($"[DssSurveyController.Status] {ex.Message}", ex);
            return SendJson(new { success = false, error = "Failed to read the survey status." }, 500);
        }
    }

    [Route(HttpVerbs.Post, "/atlas/survey/download")]
    public async Task Start()
    {
        try
        {
            int? targetOrder = await ReadIntFieldAsync("targetOrder").ConfigureAwait(false);
            if (targetOrder == null)
            {
                await SendJson(new { success = false, error = "Field 'targetOrder' must be an integer." }, 400).ConfigureAwait(false);
                return;
            }

            await SendResult(DssSurveyService.Instance.StartDownload(targetOrder.Value)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error($"[DssSurveyController.Start] {ex.Message}", ex);
            await SendJson(new { success = false, error = "Failed to start the survey download." }, 500).ConfigureAwait(false);
        }
    }

    [Route(HttpVerbs.Post, "/atlas/survey/cancel")]
    public Task Cancel()
    {
        return SendResult(DssSurveyService.Instance.CancelDownload());
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
            int? keepOrder = await ReadIntFieldAsync("keepOrder").ConfigureAwait(false);
            await SendResult(DssSurveyService.Instance.DeleteSurvey(keepOrder)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error($"[DssSurveyController.Delete] {ex.Message}", ex);
            await SendJson(new { success = false, error = "Failed to delete the survey." }, 500).ConfigureAwait(false);
        }
    }

    private async Task<int?> ReadIntFieldAsync(string fieldName)
    {
        string query = HttpContext.Request.QueryString[fieldName];
        if (int.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fromQuery))
        {
            return fromQuery;
        }

        try
        {
            Dictionary<string, object> body = await HttpContext
                .GetRequestDataAsync<Dictionary<string, object>>()
                .ConfigureAwait(false);
            if (body != null
                && body.TryGetValue(fieldName, out object raw)
                && int.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out int fromBody))
            {
                return fromBody;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"[DssSurveyController] Request body unreadable: {ex.Message}");
        }

        return null;
    }

    // Business-rule refusals (already running, not enough space) travel as HTTP 200 with
    // success=false: the app's axios interceptor swallows the body of non-2xx responses and
    // the reason would never reach the user.
    private Task SendResult(DssSurveyService.OperationResult result)
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
