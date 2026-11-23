namespace Meteo.Core.Geo;

/// <summary>WGS84 Transverse Mercator / UTM (Krüger series, sub-mm accuracy inside the zone).</summary>
public sealed class UtmProjection
{
    private const double A = 6378137.0;
    private const double F = 1 / 298.257223563;
    private const double K0 = 0.9996;
    private const double FalseEasting = 500000.0;

    private static readonly double N = F / (2 - F);
    private static readonly double ARect = A / (1 + N) * (1 + N * N / 4 + N * N * N * N / 64);
    private static readonly double[] Alpha =
    {
        N / 2 - 2 * N * N / 3 + 5 * N * N * N / 16,
        13 * N * N / 48 - 3 * N * N * N / 5,
        61 * N * N * N / 240,
    };
    private static readonly double[] Beta =
    {
        N / 2 - 2 * N * N / 3 + 37 * N * N * N / 96,
        N * N / 48 + N * N * N / 15,
        17 * N * N * N / 480,
    };
    private static readonly double[] Delta =
    {
        2 * N - 2 * N * N / 3 - 2 * N * N * N,
        7 * N * N / 3 - 8 * N * N * N / 5,
        56 * N * N * N / 15,
    };

    public int Zone { get; }
    public double CentralMeridian { get; }

    public UtmProjection(int zone = 33)
    {
        Zone = zone;
        CentralMeridian = zone * 6 - 183;
    }

    public static UtmProjection Zone33 { get; } = new(33);

    public (double X, double Y) Forward(double lon, double lat)
    {
        double phi = lat * Math.PI / 180;
        double lam = (lon - CentralMeridian) * Math.PI / 180;
        double e2n = 2 * Math.Sqrt(N) / (1 + N);
        double t = Math.Sinh(Math.Atanh(Math.Sin(phi)) - e2n * Math.Atanh(e2n * Math.Sin(phi)));
        double xi = Math.Atan2(t, Math.Cos(lam));
        double eta = Math.Atanh(Math.Sin(lam) / Math.Sqrt(1 + t * t));
        double e = eta, x = xi;
        for (int j = 1; j <= 3; j++)
        {
            x += Alpha[j - 1] * Math.Sin(2 * j * xi) * Math.Cosh(2 * j * eta);
            e += Alpha[j - 1] * Math.Cos(2 * j * xi) * Math.Sinh(2 * j * eta);
        }
        return (FalseEasting + K0 * ARect * e, K0 * ARect * x);
    }

    public (double Lon, double Lat) Inverse(double x, double y)
    {
        double xi = y / (K0 * ARect);
        double eta = (x - FalseEasting) / (K0 * ARect);
        double xi1 = xi, eta1 = eta;
        for (int j = 1; j <= 3; j++)
        {
            xi1 -= Beta[j - 1] * Math.Sin(2 * j * xi) * Math.Cosh(2 * j * eta);
            eta1 -= Beta[j - 1] * Math.Cos(2 * j * xi) * Math.Sinh(2 * j * eta);
        }
        double chi = Math.Asin(Math.Sin(xi1) / Math.Cosh(eta1));
        double phi = chi;
        for (int j = 1; j <= 3; j++) phi += Delta[j - 1] * Math.Sin(2 * j * chi);
        double lam = Math.Atan2(Math.Sinh(eta1), Math.Cos(xi1));
        return (CentralMeridian + lam * 180 / Math.PI, phi * 180 / Math.PI);
    }
}
