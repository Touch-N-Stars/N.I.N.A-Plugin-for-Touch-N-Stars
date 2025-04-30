using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Equipment.Equipment.MyGuider.PHD2;
using System;
using System.Threading.Tasks;
using NINA.Core.Utility;
using System.IO;
using System.Windows.Media.Imaging;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;

public class PhdImageResultResponse : PhdMethodResponse {
    public PhdImageResult result { get; set; }
}

public class PhdImageResult {
    public int frame { get; set; }
    public int width { get; set; }
    public int height { get; set; }
    public double[] star_pos { get; set; }
    public string pixels { get; set; }
}

public class SaveImageResult {
    public string filename { get; set; }
}
public class SaveImageResponse : PhdMethodResponse {
    public SaveImageResult result { get; set; }
}
public static class LastPhdImage {
    public static string LatestFilePath { get; set; }
}


namespace TouchNStars.Server {
    public class Phd2Controller : WebApiController {
        [Route(HttpVerbs.Get, "/phd2/state")]
        public async Task<object> GetPhd2State() {
            try {
                if (TouchNStars.Mediators.Guider.GetDevice() is PHD2Guider phd2Guider) {
                    var response = await phd2Guider.SendMessage(new Phd2GetAppState());

                    if (response?.result != null) {
                        return new {
                            success = true,
                            state = response.result.ToString()
                        };
                    }

                    return new {
                        success = false,
                        error = "Keine Antwort von PHD2"
                    };
                } else {
                    return new {
                        success = false,
                        error = "PHD2 nicht verbunden"
                    };
                }
            } catch (Exception ex) {
                Logger.Error(ex);
                HttpContext.Response.StatusCode = 500;
                return new {
                    success = false,
                    error = "Fehler beim Abruf des PHD2-Status"
                };
            }
        }


        [Route(HttpVerbs.Get, "/phd2/stop")]
        public async Task<object> StopGuiding() {
            try {
                // Prüfen, ob der aktuell gewählte Guider auch wirklich PHD2 ist
                if (TouchNStars.Mediators.Guider.GetDevice() is PHD2Guider phd2Guider) {
                    // Dies sendet den JSON-RPC-Befehl "stop_guiding"
                    var response = await phd2Guider.SendMessage(new Phd2StopCapture());

                    // Optional: auswerten, ob eine Fehlermeldung zurückkommt
                    if (response?.error != null) {
                        return new { success = false, error = response.error };
                    }

                    return new { success = true, message = "Guiding wurde gestoppt." };
                } else {
                    return new { success = false, error = "PHD2 nicht verbunden." };
                }
            } catch (Exception ex) {
                Logger.Error(ex);
                HttpContext.Response.StatusCode = 500;
                return new { success = false, error = "Interner Fehler beim Stopp des Guidings." };
            }
        }


        [Route(HttpVerbs.Get, "/phd2/set_exposure")]
        public async Task<object> SetPhdExposure() {
            try {
                if (TouchNStars.Mediators.Guider.GetDevice() is PHD2Guider phd2Guider) {
                    int exposureMillis = (int)(2 * 1000);
                    var response = await phd2Guider.SendMessage<GenericPhdMethodResponse>(new Phd2SetExposure() {  Parameters = new int[] { exposureMillis } }

                    );

                    if (response?.error != null) {
                        return new { success = false, error = response.error.message };
                    }

                    return new { success = true, message = "Belichtungszeit gesetzt auf 2 Sekunden." };
                } else {
                    return new { success = false, error = "PHD2 nicht verbunden" };
                }
            } catch (Exception ex) {
                Logger.Error(ex);
                HttpContext.Response.StatusCode = 500;
                return new { success = false, error = "Fehler beim Setzen der Belichtungszeit" };
            }
        }


        [Route(HttpVerbs.Get, "/phd2/starimage")]
        public async Task<object> GetStarImageAsBase64() {
            try {
                // Prüfen, ob PHD2 aktiv ist
                if (TouchNStars.Mediators.Guider.GetDevice() is PHD2Guider phd2Guider) {
                    // JSON-RPC-Abfrage "get_star_image"
                    var response = await phd2Guider.SendMessage<PhdImageResultResponse>(new Phd2GetStarImage());

                    // Prüfen, ob ein Bild ankam
                    if (response?.error == null && response?.result != null && !string.IsNullOrEmpty(response.result.pixels)) {
                        // 1) Base64 -> byte[]
                        byte[] raw = Convert.FromBase64String(response.result.pixels.Trim('\0'));

                        // 2) byte[] -> ushort[] (16-Bit pro Pixel)
                        ushort[] pixels = new ushort[raw.Length / 2];
                        Buffer.BlockCopy(raw, 0, pixels, 0, raw.Length);

                        // 3) Mit NINA-ImageData in ein BitmapSource rendern
                        var imageDataFactory = TouchNStars.Mediators.ImageDataFactory;
                        var arr = new ImageArray(pixels);
                        var imageData = imageDataFactory.CreateBaseImageData(
                            arr,
                            response.result.width,
                            response.result.height,
                            16,
                            false,
                            new ImageMetaData()
                        );

                        var rendered = imageData.RenderImage();
                        // Evtl. minimal stretchen
                        var stretched = await ImageUtility.Stretch(rendered, 0.25, -2.8);

                        // 4) In PNG oder JPEG konvertieren
                        using var memStream = new MemoryStream();
                        var encoder = new PngBitmapEncoder();  // oder JpegBitmapEncoder()
                        encoder.Frames.Add(BitmapFrame.Create(stretched));
                        encoder.Save(memStream);

                        // 5) Die Bilddaten wieder Base64-kodieren
                        string base64Img = Convert.ToBase64String(memStream.ToArray());

                        // 6) Als JSON-Objekt mit "image" zurückgeben
                        return new {
                            success = true,
                            width = response.result.width,
                            height = response.result.height,
                            image = base64Img // => "data:image/png;base64,{base64Img}" im Frontend
                        };
                    } else {
                        // Keine gültigen Daten
                        return new { success = false, error = "Keine Bilddaten von PHD2" };
                    }
                } else {
                    return new { success = false, error = "PHD2 nicht verbunden" };
                }
            } catch (Exception ex) {
                NINA.Core.Utility.Logger.Error(ex);
                HttpContext.Response.StatusCode = 500;
                return new { success = false, error = "Interner Fehler beim Abruf des Starimage." };
            }
        }


        [Route(HttpVerbs.Get, "/phd2/save-image")]
        public async Task<object> SaveImageAndReturnAsPngBase64() {
            if (TouchNStars.Mediators.Guider.GetDevice() is PHD2Guider phd2Guider) {
                var response = await phd2Guider.SendMessage<SaveImageResponse>(
                    new Phd2SaveImage()
                );

                var query = HttpContext.Request.QueryString;
                double black = double.TryParse(query.Get("black"), out var bVal) ? bVal : 0.25;
                double midtone = double.TryParse(query.Get("midtone"), out var mVal) ? mVal : 2.0;


                var filePath = response?.result?.filename;

                if (response?.error == null && !string.IsNullOrEmpty(filePath) && File.Exists(filePath)) {
                    LastPhdImage.LatestFilePath = filePath;

                    // FITS-Datei einlesen (16 Bit pro Pixel)
                    byte[] raw = File.ReadAllBytes(filePath);
                    ushort[] pixels = new ushort[raw.Length / 2];
                    Buffer.BlockCopy(raw, 0, pixels, 0, raw.Length);

                    // Mit ImageDataFactory verarbeiten
                    var imageDataFactory = TouchNStars.Mediators.ImageDataFactory;
                    var arr = new ImageArray(pixels);

                    // HACK: Bildgröße aus vorherigem StarImage verwenden oder schätzen
                    // Alternativ: Dateigröße durch 2 = Pixelanzahl, dann √N = Breite/Höhe
                    int pixelCount = pixels.Length;
                    int width = (int)Math.Sqrt(pixelCount);
                    int height = width;

                    var imageData = imageDataFactory.CreateBaseImageData(
                        arr,
                        width,
                        height,
                        16,
                        false,
                        new ImageMetaData()
                    );

                    var rendered = imageData.RenderImage();
                    var stretched = await ImageUtility.Stretch(rendered, black, midtone);
                    
                    using var memStream = new MemoryStream();
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(stretched));
                    encoder.Save(memStream);

                    string base64Img = Convert.ToBase64String(memStream.ToArray());

                    try {
                        File.Delete(filePath);
                    } catch (Exception ex) {
                        Console.WriteLine($"[WARN] Bild konnte nicht gelöscht werden: {ex.Message}");
                    }

                    return new {
                        success = true,
                        width = width,
                        height = height,
                        image = base64Img
                    };
                } else {
                    return new { success = false, error = "Bild konnte nicht gespeichert oder geladen werden." };
                }
            } else {
                return new { success = false, error = "PHD2 nicht verbunden." };
            }
        }




        //_________________________________________

    }
}


