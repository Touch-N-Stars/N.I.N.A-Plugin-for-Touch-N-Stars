using System;

namespace TouchNStars.Server.Services;

internal static class HealpixNestedProjection
{
    private const double QuarterPi = Math.PI / 4.0;
    private const double TwoPi = Math.PI * 2.0;

    private static readonly int[,] Faces =
    {
        { 1, 0 },
        { 3, 0 },
        { 5, 0 },
        { 7, 0 },
        { 0, -1 },
        { 2, -1 },
        { 4, -1 },
        { 6, -1 },
        { 1, -2 },
        { 3, -2 },
        { 5, -2 },
        { 7, -2 }
    };

    internal readonly record struct SphericalCoordinates(double LongitudeRadians, double LatitudeRadians);

    internal static SphericalCoordinates BasePixelUvToLonLat(int basePixel, double u, double v)
    {
        if (basePixel < 0 || basePixel > 11)
        {
            throw new ArgumentOutOfRangeException(nameof(basePixel), "Base pixel must be in range 0..11.");
        }

        double uu = Math.Clamp(u, 0.0, 1.0);
        double vv = Math.Clamp(v, 0.0, 1.0);

        // Equivalent to Stellarium's healpix_get_mat3 + healpix_map at nside=1.
        double x = (QuarterPi * uu) + (-QuarterPi * vv) + (Faces[basePixel, 0] * QuarterPi);
        double y = (QuarterPi * uu) + (QuarterPi * vv) + (Faces[basePixel, 1] * QuarterPi);

        (double z, double phi) = HealpixXYToZPhi(x, y);

        double longitude = NormalizeLongitudeRadians(phi);
        double latitude = Math.Asin(Math.Clamp(z, -1.0, 1.0));

        return new SphericalCoordinates(longitude, latitude);
    }

    // Port of Stellarium's healpix_xy2_z_phi from src/algos/healpix.c.
    private static (double Z, double Phi) HealpixXYToZPhi(double x, double y)
    {
        if (Math.Abs(y) > QuarterPi)
        {
            // Polar branch.
            double sigma = 2.0 - Math.Abs((y * 4.0) / Math.PI);
            double z = (y > 0.0 ? 1.0 : -1.0) * (1.0 - ((sigma * sigma) / 3.0));
            double xc = -Math.PI + ((2.0 * Math.Floor(((x + Math.PI) * 4.0) / (2.0 * Math.PI)) + 1.0) * QuarterPi);
            double phi = Math.Abs(sigma) > 1e-12 ? (xc + ((x - xc) / sigma)) : x;
            return (z, phi);
        }

        // Equatorial branch.
        return ((y * 8.0) / (Math.PI * 3.0), x);
    }

    internal static double NormalizeLongitudeRadians(double longitude)
    {
        double normalized = longitude % TwoPi;
        if (normalized < 0.0)
        {
            normalized += TwoPi;
        }

        return normalized;
    }
}
