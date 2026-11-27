using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Meteo.Core.Geo;
using Meteo.Core.Vector;
using NetTopologySuite.Geometries;

namespace Meteo.Viewer;

/// <summary>One GPS fix of a flight log, position in UTM 33N metres.</summary>
public sealed record TrackFix(DateTime Utc, double X, double Y, double Alt, double Vario);

/// <summary>A glider flight loaded from an IGC file, drawn over the forecast map.</summary>
public sealed partial class FlightTrack
{
    private static readonly GeometryFactory Factory = new();

    public required string Name { get; init; }
    public required DateOnly DateUtc { get; init; }
    public required IReadOnlyList<TrackFix> Fixes { get; init; }

    public static FlightTrack Load(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.Latin1);
        var date = DateOnly.FromDateTime(File.GetLastWriteTimeUtc(path));
        foreach (var l in lines)
            if (l.StartsWith("HFDTE", StringComparison.Ordinal) && DateRegex().Match(l) is { Success: true } m)
                date = new DateOnly(2000 + int.Parse(m.Groups[3].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[1].Value));

        var raw = new List<(DateTime T, double X, double Y, double Alt)>();
        DateTime? prev = null;
        var day = date;
        foreach (var l in lines)
        {
            if (l.Length < 35 || l[0] != 'B') continue;
            var tod = new TimeSpan(int.Parse(l.AsSpan(1, 2)), int.Parse(l.AsSpan(3, 2)), int.Parse(l.AsSpan(5, 2)));
            var t = day.ToDateTime(TimeOnly.FromTimeSpan(tod), DateTimeKind.Utc);
            if (prev is { } p && t < p.AddHours(-12)) { day = day.AddDays(1); t = t.AddDays(1); } // past midnight UTC
            prev = t;
            double lat = int.Parse(l.AsSpan(7, 2)) + int.Parse(l.AsSpan(9, 5)) / 60000.0;
            if (l[14] == 'S') lat = -lat;
            double lon = int.Parse(l.AsSpan(15, 3)) + int.Parse(l.AsSpan(18, 5)) / 60000.0;
            if (l[23] == 'W') lon = -lon;
            int pAlt = int.Parse(l.AsSpan(25, 5), CultureInfo.InvariantCulture), gAlt = int.Parse(l.AsSpan(30, 5), CultureInfo.InvariantCulture);
            var (x, y) = UtmProjection.Zone33.Forward(lon, lat);
            raw.Add((t, x, y, gAlt != 0 ? gAlt : pAlt));
        }

        // vertical speed averaged over ~20 s (removes GPS altitude noise)
        var fixes = new List<TrackFix>(raw.Count);
        int a = 0, b = 0;
        for (int i = 0; i < raw.Count; i++)
        {
            while (raw[i].T - raw[a].T > TimeSpan.FromSeconds(10)) a++;
            while (b < raw.Count - 1 && raw[b + 1].T - raw[i].T <= TimeSpan.FromSeconds(10)) b++;
            double dt = (raw[b].T - raw[a].T).TotalSeconds;
            double vario = dt > 0 ? (raw[b].Alt - raw[a].Alt) / dt : 0;
            fixes.Add(new TrackFix(raw[i].T, raw[i].X, raw[i].Y, raw[i].Alt, vario));
        }
        return new FlightTrack { Name = Path.GetFileNameWithoutExtension(path), DateUtc = date, Fixes = fixes };
    }

    /// <summary>
    /// The track as a layer: thin blue line while gliding, warm colours while climbing (darker = stronger), and a wide
    /// yellow highlight of the part flown within ±30 min of the forecast hour shown.
    /// </summary>
    public VectorLayer ToLayer(DateTime highlightFromUtc, DateTime highlightToUtc)
    {
        var layer = new VectorLayer { Title = Name };
        if (Fixes.Count < 2) return layer;

        var now = Fixes.Where(f => f.Utc >= highlightFromUtc && f.Utc <= highlightToUtc).Select(f => new Coordinate(f.X, f.Y)).ToArray();
        if (now.Length >= 2) layer.Lines.Add(new StyledLine(Factory.CreateLineString(now), "#B3FFE14D", 11));

        // runs of fixes with the same colour class → one line each
        int Class(double v) => v >= 2.5 ? 3 : v >= 1.5 ? 2 : v >= 0.5 ? 1 : 0;
        string[] colors = ["#1F4E99", "#F5B041", "#E67E22", "#C0392B"];
        double[] widths = [1.6, 3.2, 3.6, 4.0];
        int start = 0;
        for (int i = 1; i <= Fixes.Count; i++)
        {
            if (i < Fixes.Count && Class(Fixes[i].Vario) == Class(Fixes[start].Vario)) continue;
            int c = Class(Fixes[start].Vario);
            // include the next fix so the runs join without gaps
            var coords = Fixes.Skip(start).Take(Math.Min(i + 1, Fixes.Count) - start).Select(f => new Coordinate(f.X, f.Y)).ToArray();
            if (coords.Length >= 2) layer.Lines.Add(new StyledLine(Factory.CreateLineString(coords), colors[c], widths[c]));
            start = i;
        }
        // climbs on top of the glide line
        var ordered = layer.Lines.Take(now.Length >= 2 ? 1 : 0)
            .Concat(layer.Lines.Skip(now.Length >= 2 ? 1 : 0).OrderBy(l => Array.IndexOf(colors, l.Stroke))).ToList();
        layer.Lines.Clear();
        layer.Lines.AddRange(ordered);
        return layer;
    }

    /// <summary>The fix closest to a map point within <paramref name="maxDistance"/> metres, if any.</summary>
    public TrackFix? Nearest(double x, double y, double maxDistance)
    {
        TrackFix? best = null;
        double bd = maxDistance * maxDistance;
        foreach (var f in Fixes)
        {
            double d = (f.X - x) * (f.X - x) + (f.Y - y) * (f.Y - y);
            if (d < bd) { bd = d; best = f; }
        }
        return best;
    }

    [GeneratedRegex(@"(\d\d)(\d\d)(\d\d)")]
    private static partial Regex DateRegex();
}
