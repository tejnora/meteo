namespace Meteo.Core.Physics;

/// <summary>Sun position (NOAA solar calculator algorithm, accuracy ~0.01°, ignoring refraction).</summary>
public static class SolarPosition
{
    /// <returns>Zenith angle and azimuth (clockwise from north), degrees.</returns>
    public static (double Zenith, double Azimuth) Compute(DateTime utc, double latDeg, double lonDeg)
    {
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        double jd = utc.ToOADate() + 2415018.5;
        double t = (jd - 2451545.0) / 36525.0;

        double l0 = Mod360(280.46646 + t * (36000.76983 + t * 0.0003032));
        double m = 357.52911 + t * (35999.05029 - 0.0001537 * t);
        double e = 0.016708634 - t * (0.000042037 + 0.0000001267 * t);
        double mr = Rad(m);
        double c = Math.Sin(mr) * (1.914602 - t * (0.004817 + 0.000014 * t))
                 + Math.Sin(2 * mr) * (0.019993 - 0.000101 * t)
                 + Math.Sin(3 * mr) * 0.000289;
        double trueLong = l0 + c;
        double omega = 125.04 - 1934.136 * t;
        double lambda = trueLong - 0.00569 - 0.00478 * Math.Sin(Rad(omega));
        double eps0 = 23 + (26 + (21.448 - t * (46.815 + t * (0.00059 - t * 0.001813))) / 60) / 60;
        double eps = eps0 + 0.00256 * Math.Cos(Rad(omega));
        double decl = Math.Asin(Math.Sin(Rad(eps)) * Math.Sin(Rad(lambda)));

        double y = Math.Tan(Rad(eps) / 2); y *= y;
        double l0r = Rad(l0);
        double eqTime = 4 * Deg(y * Math.Sin(2 * l0r) - 2 * e * Math.Sin(mr) + 4 * e * y * Math.Sin(mr) * Math.Cos(2 * l0r)
                                - 0.5 * y * y * Math.Sin(4 * l0r) - 1.25 * e * e * Math.Sin(2 * mr));

        double minutes = utc.TimeOfDay.TotalMinutes;
        double trueSolarTime = minutes + eqTime + 4 * lonDeg;
        double ha = Rad(trueSolarTime / 4 - 180);
        double lat = Rad(latDeg);

        double cosZ = Math.Sin(lat) * Math.Sin(decl) + Math.Cos(lat) * Math.Cos(decl) * Math.Cos(ha);
        double zenith = Deg(Math.Acos(Math.Clamp(cosZ, -1, 1)));
        double az = Deg(Math.Atan2(Math.Sin(ha), Math.Cos(ha) * Math.Sin(lat) - Math.Tan(decl) * Math.Cos(lat))) + 180;
        return (zenith, Mod360(az));
    }

    /// <summary>Unit vector towards the sun in (east, north, up).</summary>
    public static (double E, double N, double U) Vector(double zenithDeg, double azimuthDeg)
    {
        double z = Rad(zenithDeg), a = Rad(azimuthDeg);
        return (Math.Sin(z) * Math.Sin(a), Math.Sin(z) * Math.Cos(a), Math.Cos(z));
    }

    private static double Rad(double d) => d * Math.PI / 180;
    private static double Deg(double r) => r * 180 / Math.PI;
    private static double Mod360(double d) => ((d % 360) + 360) % 360;
}
