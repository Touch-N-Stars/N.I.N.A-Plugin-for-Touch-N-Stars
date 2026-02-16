using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using TouchNStars.Server.Models;
using TouchNStars.Server.Services;

namespace TouchNStars.Server.Controllers {
    public class BahtinovController : WebApiController {
        private readonly BahtinovAnalysisService analysisService = new BahtinovAnalysisService();

        [Route(HttpVerbs.Post, "/bahtinov/analyze")]
        public async Task<ApiResponse> Analyze() {
            string inboundContentType = HttpContext.Request.ContentType ?? "<none>";
            Logger.Info($"Received Bahtinov analyze request. ContentType={inboundContentType}");

            try {
                BahtinovAnalysisRequest request = await ReadRequestAsync();
                Logger.Info($"Bahtinov request ready. Mode={(request.ImageBytes != null ? "binary" : "base64")}; Crop={DescribeCrop(request.Crop)}; Resize={(request.ResizeFactor?.ToString() ?? "none")}");

                BahtinovAnalysisResult result = analysisService.Analyze(request);
                Logger.Info($"Bahtinov analysis succeeded. FocusError={result.FocusErrorPixels:F3}; WithinCriticalFocus={result.IsWithinCriticalFocus}");

                return new ApiResponse {
                    Success = true,
                    Response = result,
                    StatusCode = 200,
                    Type = "BahtinovAnalysis"
                };
            } catch (ArgumentException ex) {
                Logger.Warning($"Bahtinov analysis request invalid: {ex.Message}");
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse {
                    Success = false,
                    Error = ex.Message,
                    StatusCode = 400,
                    Type = "BadRequest"
                };
            } catch (Exception ex) {
                Logger.Error(ex);
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse {
                    Success = false,
                    Error = "Bahtinov analysis failed.",
                    StatusCode = 500,
                    Type = "Error"
                };
            }
        }

        private async Task<BahtinovAnalysisRequest> ReadRequestAsync() {
            string contentType = HttpContext.Request.ContentType ?? string.Empty;

            if (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(contentType)) {
                BahtinovAnalysisRequest jsonRequest = await HttpContext.GetRequestDataAsync<BahtinovAnalysisRequest>();
                if (!string.IsNullOrEmpty(jsonRequest?.ImageBase64)) {
                    Logger.Info($"Bahtinov JSON payload received. Base64 length={jsonRequest.ImageBase64.Length} characters");
                }
                return jsonRequest;
            }

            if (IsBinaryContentType(contentType)) {
                string metadataJson = HttpContext.Request.Headers["X-Bahtinov-Metadata"];
                if (string.IsNullOrWhiteSpace(metadataJson)) {
                    throw new ArgumentException("Missing X-Bahtinov-Metadata header containing request parameters.");
                }

                BahtinovAnalysisRequest request;
                try {
                    request = JsonSerializer.Deserialize<BahtinovAnalysisRequest>(metadataJson, new JsonSerializerOptions {
                        PropertyNameCaseInsensitive = true
                    });
                } catch (JsonException ex) {
                    Logger.Warning($"Bahtinov metadata parse failed: {ex.Message}");
                    throw new ArgumentException("Invalid metadata JSON supplied in X-Bahtinov-Metadata header.", ex);
                }

                if (request == null) {
                    throw new ArgumentException("Metadata JSON could not be parsed.");
                }

                Logger.Info("Bahtinov metadata parsed successfully.");

                using Stream input = HttpContext.Request.InputStream;
                using MemoryStream buffer = new MemoryStream();
                await input.CopyToAsync(buffer);
                request.ImageBytes = buffer.ToArray();
                Logger.Info($"Bahtinov binary payload received. Length={request.ImageBytes.Length} bytes");

                if (request.ImageBytes == null || request.ImageBytes.Length == 0) {
                    throw new ArgumentException("Request body did not contain any image data.");
                }

                return request;
            }

            throw new ArgumentException($"Unsupported content type '{contentType}'. Use application/json or a supported binary image type.");
        }

        private static bool IsBinaryContentType(string contentType) {
            if (string.IsNullOrWhiteSpace(contentType)) {
                return false;
            }

            static bool Matches(string candidate, string expected) => candidate.StartsWith(expected, StringComparison.OrdinalIgnoreCase);

            return Matches(contentType, "application/octet-stream")
                || Matches(contentType, "application/x-fits")
                || Matches(contentType, "application/fit")
                || Matches(contentType, "application/fits")
                || Matches(contentType, "image/jpeg")
                || Matches(contentType, "image/png")
                || Matches(contentType, "image/tiff")
                || Matches(contentType, "image/fits")
                || Matches(contentType, "image/fit")
                || Matches(contentType, "image/x-fits");
        }

        private static string DescribeCrop(CropRegion crop) {
            if (crop == null) {
                return "none";
            }

            return $"{crop.Width}x{crop.Height}@{crop.X},{crop.Y}";
        }
    }
}
