using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.WPF.Base.SkySurvey;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using TouchNStars.Utility;

namespace TouchNStars.Server.Controllers;

public class TargetSearchController : WebApiController
{
    [Route(HttpVerbs.Get, "/ngc/search")]
    public async Task<object> SearchNGC([QueryField(true)] string query, [QueryField] int limit)
    {
        TouchNStars.Mediators.DeepSkyObjectSearchVM.Limit = limit;
        TouchNStars.Mediators.DeepSkyObjectSearchVM.TargetName = query; // Setting the target name automatically starts the search

        await TouchNStars.Mediators.DeepSkyObjectSearchVM.TargetSearchResult.Task; // Wait for the search to finish
        List<NGCSearchResult> results = new List<NGCSearchResult>();

        foreach (var result in TouchNStars.Mediators.DeepSkyObjectSearchVM.TargetSearchResult.Result)
        { // bring the results in a better format
            results.Add(new NGCSearchResult()
            {
                Name = result.Column1,
                RA = CoreUtility.HmsToDegrees(result.Column2),
                Dec = CoreUtility.DmsToDegrees(result.Column3.Replace(" ", "").Replace('°', ':').Replace('\'', ':').Replace("\"", "")) // maybe use reflection to directly get the coordinates
            });
        }

        return results;
    }

    [Route(HttpVerbs.Get, "/targetpic")]
    public async Task FetchTargetPicture([QueryField(true)] int width, [QueryField(true)] int height, [QueryField(true)] double fov, [QueryField(true)] double ra, [QueryField(true)] double dec, [QueryField] bool useCache)
    {
        try
        {
            HttpContext.Response.ContentType = "image/jpeg";
            if (useCache)
            {
                string framingCache = TouchNStars.Mediators.Profile.ActiveProfile.ApplicationSettings.SkySurveyCacheDirectory;
                CacheSkySurveyImageFactory factory = new CacheSkySurveyImageFactory(width, height, new CacheSkySurvey(framingCache));
                BitmapSource source = factory.Render(new Coordinates(Angle.ByDegree(ra), Angle.ByDegree(dec), Epoch.J2000), fov, 0);

                JpegBitmapEncoder encoder = new JpegBitmapEncoder();
                encoder.QualityLevel = 100;
                using (MemoryStream stream = new MemoryStream())
                {
                    encoder.Frames.Add(BitmapFrame.Create(source));
                    encoder.Save(stream);
                    stream.Position = 0;
                    Response.OutputStream.Write(stream.ToArray(), 0, (int)stream.Length);
                }
            }
            else
            {
                HttpClient client = new HttpClient();
                string raInvariant = ra.ToString(CultureInfo.InvariantCulture);
                string decInvariant = dec.ToString(CultureInfo.InvariantCulture);
                string fovInvariant = fov.ToString(CultureInfo.InvariantCulture);
                byte[] image = await client.GetByteArrayAsync($"{CoreUtility.Hips2FitsUrl}?hips=CDS%2FP%2FDSS2%2Fcolor&ra={raInvariant}&dec={decInvariant}&width={width}&height={height}&fov={fovInvariant}&projection=TAN&coordsys=icrs&rotation_angle=0.0&format=jpg");
                Response.OutputStream.Write(image, 0, image.Length);

                client.Dispose();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            throw;
        }
    }
}

public class NGCSearchResult
{
    public string Name { get; set; }
    public double RA { get; set; }
    public double Dec { get; set; }
}
