using System.Globalization;

namespace Meteo.Validation;

public readonly record struct Fix(DateTime Utc, double Lat, double Lon, double Alt, int Enl);

/// <summary>A circling climb extracted from a flight log.</summary>
public sealed record Thermal(string FlightId, DateTime StartUtc, DateTime EndUtc, double Lat, double Lon,
    double BottomAlt, double TopAlt, double ClimbRate)
{
    public DateTime MidUtc => StartUtc + (EndUtc - StartUtc) / 2;
    public double Gain => TopAlt - BottomAlt;
}

/// <summary>Minimal IGC reader (B records, I-record extensions for ENL/MOP, HFDTE date, HFALG altitude datum).</summary>
public static class IgcReader
{
    /// <summary>Geoid undulation over the Czech Republic (EGM96 ≈ 44–48 m): ellipsoidal → AMSL.</summary>
    private const double GeoidCz = 46;

    public static List<Fix> Read(string text)
    {
        var fixes = new List<(DateTime T, double Lat, double Lon, int P, int G, int Enl)>();
        DateTime? date = null;
        int enlStart = -1, enlEnd = -1;
        bool ellipsoid = false;
        DateTime last = DateTime.MinValue;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("HFDTE", StringComparison.OrdinalIgnoreCase))
            {
                var digits = new string(line.SkipWhile(c => !char.IsDigit(c)).Take(6).ToArray());
                if (digits.Length == 6 && DateTime.TryParseExact(digits, "ddMMyy", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d))
                    date = DateTime.SpecifyKind(d, DateTimeKind.Utc);
            }
            else if (line.StartsWith("HFALG", StringComparison.OrdinalIgnoreCase))
                ellipsoid = line.Contains("ELL", StringComparison.OrdinalIgnoreCase);
            else if (line.StartsWith('I') && line.Length >= 3 && int.TryParse(line.AsSpan(1, 2), out int n))
            {
                for (int k = 0; k < n && 3 + 7 * k + 7 <= line.Length; k++)
                {
                    var code = line.Substring(3 + 7 * k + 4, 3);
                    if (code is "ENL" or "MOP" && enlStart < 0)
                    {
                        enlStart = int.Parse(line.AsSpan(3 + 7 * k, 2)) - 1;
                        enlEnd = int.Parse(line.AsSpan(3 + 7 * k + 2, 2));
                    }
                }
            }
            else if (line.StartsWith('B') && line.Length >= 35 && date is { } day)
            {
                if (line[24] != 'A') continue; // 2D / invalid fix
                int hh = int.Parse(line.AsSpan(1, 2)), mm = int.Parse(line.AsSpan(3, 2)), ss = int.Parse(line.AsSpan(5, 2));
                double lat = int.Parse(line.AsSpan(7, 2)) + int.Parse(line.AsSpan(9, 5)) / 60000.0;
                if (line[14] == 'S') lat = -lat;
                double lon = int.Parse(line.AsSpan(15, 3)) + int.Parse(line.AsSpan(18, 5)) / 60000.0;
                if (line[23] == 'W') lon = -lon;
                int pAlt = int.Parse(line.AsSpan(25, 5));
                int gAlt = int.Parse(line.AsSpan(30, 5));
                var t = day.AddHours(hh).AddMinutes(mm).AddSeconds(ss);
                while (fixes.Count > 0 && t < last.AddHours(-12)) t = t.AddDays(1); // midnight UTC rollover
                last = t;
                int enl = enlStart >= 0 && line.Length >= enlEnd && int.TryParse(line.AsSpan(enlStart, enlEnd - enlStart), out var e) ? e : 0;
                fixes.Add((t, lat, lon, pAlt, gAlt, enl));
            }
        }
        // GNSS altitude (true altitude) unless the logger does not record it for most fixes; never mix the two.
        bool gnss = fixes.Count(x => x.G != 0) > 0.95 * fixes.Count;
        return fixes.Where(x => !gnss || x.G != 0)
            .Select(x => new Fix(x.T, x.Lat, x.Lon, gnss ? x.G - (ellipsoid ? GeoidCz : 0) : x.P, x.Enl)).ToList();
    }
}

/// <summary>
/// Circling detection: a fix is "circling" when the heading changes by ≥ 300° within the surrounding 40 s in one direction.
/// Circling segments ≥ 60 s with ≥ 100 m gain become thermals; the climb rate is the least-squares slope of altitude.
/// Segments with engine noise (ENL/MOP) are dropped.
/// </summary>
public static class ThermalDetector
{
    public static List<Thermal> Detect(string flightId, IReadOnlyList<Fix> f)
    {
        var res = new List<Thermal>();
        if (f.Count < 50) return res;
        int n = f.Count;
        var turn = new double[n]; // signed heading change from fix i-1 to i (deg)
        double? prevHdg = null;
        for (int i = 1; i < n; i++)
        {
            double dy = (f[i].Lat - f[i - 1].Lat) * 111_320;
            double dx = (f[i].Lon - f[i - 1].Lon) * 111_320 * Math.Cos(f[i].Lat * Math.PI / 180);
            if (dx * dx + dy * dy < 4) continue; // < 2 m: no reliable heading
            double hdg = Math.Atan2(dx, dy) * 180 / Math.PI;
            if (prevHdg is { } p) turn[i] = ((hdg - p + 540) % 360) - 180;
            prevHdg = hdg;
        }

        var circling = new bool[n];
        int lo = 0; double sum = 0;
        for (int hi = 0; hi < n; hi++)
        {
            sum += turn[hi];
            while ((f[hi].Utc - f[lo].Utc).TotalSeconds > 40) sum -= turn[lo++];
            if (Math.Abs(sum) >= 300) for (int k = lo; k <= hi; k++) circling[k] = true;
        }

        int s = -1;
        for (int i = 0; i <= n; i++)
        {
            bool c = i < n && circling[i];
            if (c && s < 0) s = i;
            // allow short interruptions (re-centring) of up to 20 s
            if (!c && s >= 0 && (i == n || !NextCirclingWithin(circling, f, i, 20)))
            {
                Add(s, i - 1);
                s = -1;
            }
        }
        return res;

        void Add(int a, int b)
        {
            double dur = (f[b].Utc - f[a].Utc).TotalSeconds;
            if (dur < 60) return;
            for (int k = a; k <= b; k++) if (f[k].Enl > 500) return; // engine running
            double st = 0, sa = 0, stt = 0, sta = 0; int m = 0;
            double top = double.MinValue, bottom = double.MaxValue, lat = 0, lon = 0;
            for (int k = a; k <= b; k++)
            {
                double t = (f[k].Utc - f[a].Utc).TotalSeconds;
                st += t; sa += f[k].Alt; stt += t * t; sta += t * f[k].Alt; m++;
                top = Math.Max(top, f[k].Alt); bottom = Math.Min(bottom, f[k].Alt);
                lat += f[k].Lat; lon += f[k].Lon;
            }
            double slope = (m * sta - st * sa) / (m * stt - st * st);
            if (top - bottom < 100 || slope <= 0.2) return;
            res.Add(new Thermal(flightId, f[a].Utc, f[b].Utc, lat / m, lon / m, bottom, top, slope));
        }
    }

    private static bool NextCirclingWithin(bool[] c, IReadOnlyList<Fix> f, int i, double seconds)
    {
        for (int k = i; k < c.Length && (f[k].Utc - f[i].Utc).TotalSeconds <= seconds; k++)
            if (c[k]) return true;
        return false;
    }
}
