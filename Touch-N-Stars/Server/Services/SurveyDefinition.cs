using System;
using System.Collections.Generic;
using System.Linq;

namespace TouchNStars.Server.Services;

/// <summary>
/// Everything that differs between the downloadable Atlas HiPS surveys: where the tiles come
/// from, which orders are offered, the source tile format and what the served
/// <c>properties</c> file says about provenance and licence.
/// </summary>
public sealed class SurveyDefinition
{
    public const string RouteBase = "/celestia-atlas-data/surveys/";

    /// <summary>
    /// DSS colour (CDS/P/DSS2/color). STScI publishes an official mirror of the identical CDS
    /// HiPS (same creator_did and release) on S3; it answers in well under a second where the
    /// CDS community server took 20-100 s per tile when measured, so the mirror goes first.
    /// Average source JPEG bytes per tile: means of 60 random tiles per order sampled from the
    /// STScI mirror on 2026-09-14 (the app carries the same table in offlineSkySurvey.js).
    /// </summary>
    public static readonly SurveyDefinition Dss = new()
    {
        Id = "dss",
        MinOrder = 3,
        BaseOrder = 4,
        MaxOrder = 7,
        SourceExtension = ".jpg",
        ConvertToJpeg = false,
        HasLegacyWebp = true,
        DefaultSourceUrls = new[]
        {
            "https://stpubdata.s3.us-east-1.amazonaws.com/mast/skybackgrounds/DSSColor",
            "https://alasky.cds.unistra.fr/DSS/DSSColor"
        },
        AverageTileBytes = new Dictionary<int, long>
        {
            [3] = 42_000,
            [4] = 55_000,
            [5] = 75_000,
            [6] = 93_000,
            [7] = 97_000
        },
        PropertiesHeader =
            "creator_did          = ivo://CDS/P/DSS2/color\n" +
            "obs_collection       = DSS colored\n" +
            "obs_title            = DSS colored\n" +
            "obs_copyright        = Digitized Sky Survey - STScI/NASA, Colored & Healpixed by CDS\n" +
            "obs_copyright_url    = http://archive.stsci.edu/dss/copyright.html\n" +
            "hips_copyright       = CNRS/Unistra\n" +
            "hips_creator         = CDS (A.Oberto, P.Fernique)\n",
        PropertiesFooter =
            "moc_sky_fraction     = 1\n" +
            "prov_progenitor      = STScI\n" +
            "obs_ack              = The Digitized Sky Surveys were produced at the Space Telescope Science Institute under U.S. Government grant NAG W-2166. The images of these surveys are based on photographic data obtained using the Oschin Schmidt Telescope on Palomar Mountain and the UK Schmidt Telescope. The plates were processed into the present compressed digital form with the permission of these institutions.\n"
    };

    /// <summary>
    /// Northern Sky Narrowband Survey DR0.2 by Stefan Ziegenbalg, CC BY-NC-SA 4.0. It covers
    /// the sky north of Dec -16 deg only, so the tile set comes from each product's Moc.fits.
    /// The master on simg.de answered in ~0.3 s per tile on 2026-10-03, the CDS mirror in
    /// 10-20 s. Source tiles are 8-bit PNGs (~120-610 kB) and are stored as JPEG q85; the
    /// sizes are means of 25-40 random converted tiles per order measured on 2026-10-03.
    /// </summary>
    public static readonly SurveyDefinition Nsns = CreateNsns(
        "nsns",
        "ohs8",
        "NSNS DR0.2: [OIII], H-alpha and [SII]",
        new Dictionary<int, long> { [3] = 68_000, [4] = 78_000, [5] = 92_000, [6] = 81_000 });

    public static readonly SurveyDefinition NsnsHalpha = CreateNsns(
        "nsns-ha",
        "halpha8",
        "NSNS DR0.2: H-alpha (8 bit)",
        new Dictionary<int, long> { [3] = 37_000, [4] = 53_000, [5] = 58_000, [6] = 50_000 });

    public static readonly SurveyDefinition NsnsOiii = CreateNsns(
        "nsns-oiii",
        "oiii8",
        "NSNS DR0.2: [OIII] (8 bit)",
        new Dictionary<int, long> { [3] = 61_000, [4] = 79_000, [5] = 78_000, [6] = 65_000 });

    public static readonly SurveyDefinition NsnsSii = CreateNsns(
        "nsns-sii",
        "sii8",
        "NSNS DR0.2: [SII] (8 bit)",
        new Dictionary<int, long> { [3] = 79_000, [4] = 98_000, [5] = 95_000, [6] = 74_000 });

    /// <summary>
    /// RGB continuum (blue/red continuum, stars partially subtracted). Its HiPS stops at
    /// order 5 and its Moc.fits differs slightly from the line products at order 6 only, so
    /// orders 3-5 share their tile counts. Sizes: means of 30 random tiles per order
    /// converted to JPEG q85, measured on 2026-11-08.
    /// </summary>
    public static readonly SurveyDefinition NsnsContinuum = CreateNsns(
        "nsns-cont",
        "rgb8",
        "NSNS DR0.2: RGB continuum",
        new Dictionary<int, long> { [3] = 69_000, [4] = 75_000, [5] = 64_000 },
        maxOrder: 5,
        mocSkyFraction: "0.651");

    private static SurveyDefinition CreateNsns(
        string id,
        string product,
        string title,
        Dictionary<int, long> averageTileBytes,
        int maxOrder = 6,
        string mocSkyFraction = "0.6464")
    {
        return new SurveyDefinition
        {
            Id = id,
            MinOrder = 3,
            BaseOrder = 4,
            MaxOrder = maxOrder,
            SourceExtension = ".png",
            ConvertToJpeg = true,
            UsesCoverageMoc = true,
            DefaultSourceUrls = new[]
            {
                $"https://www.simg.de/nebulae3/dr0_2/{product}",
                $"https://alasky.cds.unistra.fr/simg.de/simg.de_P_NSNS_DR0_2_{product}"
            },
            AverageTileBytes = averageTileBytes,
            // Tile counts of the DR0.2 coverage (identical for all products up to order 5),
            // used for estimates until Moc.fits is on disk.
            ExpectedTileCounts = new Dictionary<int, int>
            {
                [3] = 528,
                [4] = 2016,
                [5] = 8000,
                [6] = 31872
            },
            PropertiesHeader =
                $"creator_did          = ivo://simg.de/P/NSNS/DR0_2/{product}\n" +
                "obs_collection       = Northern Sky Narrowband Survey\n" +
                $"obs_title            = {title}\n" +
                "obs_copyright        = Northern Sky Narrowband Survey by Stefan Ziegenbalg. The material can be freely used and distributed under Creative Commons Attribution-Noncommercial-Share Alike 4.0 license (CC-BY-NC-SA). Converted to JPEG by Touch-N-Stars.\n" +
                "obs_copyright_url    = http://www.simg.de/nebulae3/dr0_2\n" +
                "hips_creator         = S. Ziegenbalg\n",
            PropertiesFooter =
                $"moc_sky_fraction     = {mocSkyFraction}\n" +
                "bib_reference_url    = https://doi.org/10.3847/2515-5172/adfec7\n"
        };
    }

    public static readonly IReadOnlyList<SurveyDefinition> All = new[] { Dss, Nsns, NsnsHalpha, NsnsOiii, NsnsSii, NsnsContinuum };

    public string Id { get; init; }
    public string FolderName => Id;
    public string Route => RouteBase + Id;
    public int MinOrder { get; init; }
    public int BaseOrder { get; init; }
    public int MaxOrder { get; init; }

    /// <summary>Extension of the tiles on the source server (".jpg" or ".png").</summary>
    public string SourceExtension { get; init; }

    /// <summary>Re-encode source tiles to JPEG instead of storing them unchanged.</summary>
    public bool ConvertToJpeg { get; init; }

    /// <summary>Only the tiles listed in the source's Moc.fits exist.</summary>
    public bool UsesCoverageMoc { get; init; }

    /// <summary>A plugin version once stored this survey as WebP; those files are cleaned up.</summary>
    public bool HasLegacyWebp { get; init; }

    public string[] DefaultSourceUrls { get; init; }
    public IReadOnlyDictionary<int, long> AverageTileBytes { get; init; }
    public IReadOnlyDictionary<int, int> ExpectedTileCounts { get; init; }
    public string PropertiesHeader { get; init; }
    public string PropertiesFooter { get; init; }

    public string PathEnvironmentVariable => $"TNS_{EnvironmentName}_SURVEY_PATH";
    public string SourceUrlEnvironmentVariable => $"TNS_{EnvironmentName}_SURVEY_SOURCE_URL";

    // Environment variable names cannot carry the hyphen of ids like "nsns-ha".
    private string EnvironmentName => Id.ToUpperInvariant().Replace('-', '_');

    public static SurveyDefinition Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Dss;
        }

        return All.FirstOrDefault(d => string.Equals(d.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
