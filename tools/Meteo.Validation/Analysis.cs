using System.Globalization;
using System.Text;
using System.Text.Json;
using Meteo.Core;
using Meteo.Core.Geo;
using Meteo.Core.Model;
using Meteo.Core.Physics;

namespace Meteo.Validation;

public static class Analysis
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    public sealed record Matched(DateOnly Day, string Flight, DateTime MidUtc, double Lat, double Lon, double Climb, double Top, double Bottom,
        double WStar, double WStarMax2Km, double FcTop, double FcBase, double FcCeiling, double Cumulus, double Overdev, double Elev, double Lead);

    public static void Run(ValidationContext ctx, IReadOnlyList<DateOnly> days)
    {
        var matched = new List<Matched>();
        var daySummaries = new List<Dictionary<string, object?>>();
        var satAll = new List<(double Fc, double Obs)>();
        var satHourly = new List<Dictionary<string, object?>>();

        foreach (var day in days)
        {
            var svc = ctx.Service(day);
            var d = new Dictionary<string, object?> { ["day"] = day.ToString("yyyy-MM-dd") };
            daySummaries.Add(d);
            if (svc is null) { d["error"] = "chybí předpověď"; continue; }
            var hours = svc.Current!.Hours.OrderBy(h => h.ValidUtc).ToList();
            var fields = hours.ToDictionary(h => h.ValidUtc, svc.GetThermal);
            var tg = fields.Values.First().Grid;
            var surf = SurfaceData.Default;

            // ---- forecast overview of the day (inside CZ) ----
            var czMask = CzMask(tg);
            d["run"] = $"{hours[0].Model} {hours[0].RunUtc:yyyy-MM-dd HH}Z";
            d["fc_wstar_max_hour_mean"] = Math.Round(hours.Max(h => MeanMasked(fields[h.ValidUtc].WStar, czMask)), 2);
            d["fc_ceiling_13h_mean"] = Math.Round(MeanMasked(CeilingOrTop(fields[HourAt(hours, day, 13).ValidUtc]), czMask), 0);
            var end = ThermalModel.ThermalEnd(hours.Select(h => (ToLocal(h.ValidUtc).Hour, fields[h.ValidUtc])));

            // ---- satellite ----
            var satFile = ctx.PathFor("satellite", $"{day:yyyy-MM-dd}.csv");
            if (File.Exists(satFile))
            {
                var rows = ReadCsv(satFile).Where(r => double.IsFinite(r.D("fc_ghi")) && double.IsFinite(r.D("sat_ghi"))).ToList();
                var pairs = rows.Select(r => (Fc: r.D("fc_ghi"), Obs: r.D("sat_ghi"))).ToList();
                satAll.AddRange(pairs);
                d["sat_n"] = pairs.Count;
                d["sat_fc_mean"] = Math.Round(pairs.Average(p => p.Fc), 0);
                d["sat_obs_mean"] = Math.Round(pairs.Average(p => p.Obs), 0);
                d["sat_bias"] = Math.Round(pairs.Average(p => p.Fc - p.Obs), 0);
                d["sat_rmse"] = Math.Round(Math.Sqrt(pairs.Average(p => (p.Fc - p.Obs) * (p.Fc - p.Obs))), 0);
                d["sat_r"] = Math.Round(Pearson(pairs.Select(p => p.Fc), pairs.Select(p => p.Obs)), 2);
                // spatial skill: per hour, correlation over the points (does the model put the clouds in the right place?)
                var spatial = rows.GroupBy(r => r.S("valid_utc")).Select(g => Pearson(g.Select(r => r.D("fc_ghi")), g.Select(r => r.D("sat_ghi"))))
                    .Where(double.IsFinite).ToList();
                d["sat_spatial_r"] = spatial.Count > 0 ? Math.Round(spatial.Average(), 2) : null;
                // daily energy per point (kWh/m²) – hours 07–19 local
                var energy = rows.GroupBy(r => r.S("cell")).Select(g => (Fc: g.Sum(r => r.D("fc_ghi")) / 1000, Obs: g.Sum(r => r.D("sat_ghi")) / 1000)).ToList();
                d["energy_fc"] = Math.Round(energy.Average(e => e.Fc), 2);
                d["energy_obs"] = Math.Round(energy.Average(e => e.Obs), 2);
                d["energy_r"] = Math.Round(Pearson(energy.Select(e => e.Fc), energy.Select(e => e.Obs)), 2);
                foreach (var g in rows.GroupBy(r => r.S("valid_utc")).OrderBy(g => g.Key))
                    satHourly.Add(new() { ["day"] = day.ToString("yyyy-MM-dd"), ["local"] = ToLocal(DateTime.Parse(g.Key, CultureInfo.InvariantCulture)).ToString("HH:mm"),
                        ["fc"] = Math.Round(g.Average(r => r.D("fc_ghi")), 0), ["obs"] = Math.Round(g.Average(r => r.D("sat_ghi")), 0) });
            }

            // ---- thermals from IGC ----
            var thFile = ctx.PathFor("thermals", $"{day:yyyy-MM-dd}.csv");
            if (!File.Exists(thFile)) continue;
            var th = ReadCsv(thFile).ToList();
            var dayMatched = new List<Matched>();
            foreach (var r in th)
            {
                var start = DateTime.SpecifyKind(DateTime.Parse(r.S("start_utc"), CultureInfo.InvariantCulture), DateTimeKind.Utc);
                var stop = DateTime.SpecifyKind(DateTime.Parse(r.S("end_utc"), CultureInfo.InvariantCulture), DateTimeKind.Utc);
                var mid = start + (stop - start) / 2;
                double lat = r.D("lat"), lon = r.D("lon");
                var (x, y) = UtmProjection.Zone33.Forward(lon, lat);
                int i = (int)Math.Floor((x - tg.X0) / tg.CellSize), j = (int)Math.Floor((y - tg.Y0) / tg.CellSize);
                if (i < 2 || j < 2 || i >= tg.Nx - 2 || j >= tg.Ny - 2) continue; // outside the forecast grid
                int k = j * tg.Nx + i;
                var (a, b, w) = Bracket(hours, mid);
                var fa = fields[a.ValidUtc]; var fb = fields[b.ValidUtc];
                double L(float[] fa_, float[] fb_) => Lerp(fa_[k], fb_[k], w);
                double wmax = 0;
                for (int dj = -2; dj <= 2; dj++)
                    for (int di = -2; di <= 2; di++)
                    {
                        int kk = (j + dj) * tg.Nx + i + di;
                        wmax = Math.Max(wmax, Lerp(fa.WStar[kk], fb.WStar[kk], w) is var v && double.IsFinite(v) ? v : 0);
                    }
                var ceilA = CeilingOrTop(fa); var ceilB = CeilingOrTop(fb);
                var m = new Matched(day, r.S("flight"), mid, lat, lon, r.D("climb"), r.D("top"), r.D("bottom"),
                    L(fa.WStar, fb.WStar), wmax, L(fa.Top, fb.Top), L(fa.CloudBase, fb.CloudBase), Lerp(ceilA[k], ceilB[k], w),
                    L(fa.Cumulus, fb.Cumulus), L(fa.Overdevelopment, fb.Overdevelopment), surf.Elevation[k],
                    (mid - a.RunUtc).TotalHours);
                if (double.IsFinite(m.WStar)) dayMatched.Add(m);
            }
            matched.AddRange(dayMatched);
            if (dayMatched.Count == 0) continue;

            d["flights"] = dayMatched.Select(m => m.Flight).Distinct().Count();
            d["thermals"] = dayMatched.Count;
            d["climb_median"] = Math.Round(Median(dayMatched.Select(m => m.Climb)), 2);
            d["climb_best_half"] = Math.Round(Median(dayMatched.Select(m => m.Climb).OrderDescending().Take(Math.Max(1, dayMatched.Count / 2))), 2);
            d["wstar_median"] = Math.Round(Median(dayMatched.Select(m => m.WStar)), 2);
            d["wstar_max2km_median"] = Math.Round(Median(dayMatched.Select(m => m.WStarMax2Km)), 2);
            d["top_p90"] = Math.Round(Percentile(dayMatched.Select(m => m.Top), 0.9), 0);
            d["fc_ceiling_median"] = Math.Round(Median(dayMatched.Select(m => m.FcCeiling).Where(double.IsFinite)), 0);
            // end of thermals: per flight the last thermal with ≥ 1 m/s (pilots may land earlier → lower bound)
            var lastStrong = dayMatched.Where(m => m.Climb >= 1).GroupBy(m => m.Flight).Select(g => g.MaxBy(m => m.MidUtc)!).ToList();
            if (lastStrong.Count > 0)
            {
                var latest = lastStrong.OrderByDescending(m => m.MidUtc).Take(Math.Max(1, lastStrong.Count / 4)).ToList(); // latest quarter of flights
                d["obs_end_local"] = ToLocal(latest.Min(m => m.MidUtc)).ToString("HH:mm");
                d["obs_end_latest_local"] = ToLocal(latest.Max(m => m.MidUtc)).ToString("HH:mm");
                string FcEnd(float[] e)
                {
                    var v = latest.Select(m =>
                    {
                        var (x, y) = UtmProjection.Zone33.Forward(m.Lon, m.Lat);
                        int i = (int)Math.Floor((x - tg.X0) / tg.CellSize), j = (int)Math.Floor((y - tg.Y0) / tg.CellSize);
                        return (double)e[j * tg.Nx + i];
                    }).Where(double.IsFinite).ToList();
                    return v.Count > 0 ? $"{Median(v):00}:00" : "–";
                }
                d["fc_end_local"] = FcEnd(end);   // application threshold W* ≥ 1 m/s
                d["fc_end15_local"] = FcEnd(ThermalModel.ThermalEnd(hours.Select(h => (ToLocal(h.ValidUtc).Hour, fields[h.ValidUtc])), 1.5f));
            }
        }

        // ---- matched thermals → CSV ----
        var csv = new StringBuilder("day,flight,mid_local,lat,lon,climb,bottom,top,wstar,wstar_max2km,fc_top,fc_cloudbase,fc_ceiling,cumulus,overdev,elev,lead_h\n");
        foreach (var m in matched)
            csv.Append(CultureInfo.InvariantCulture, $"{m.Day:yyyy-MM-dd},{m.Flight},{ToLocal(m.MidUtc):HH:mm:ss},{m.Lat:0.0000},{m.Lon:0.0000},{m.Climb:0.00},{m.Bottom:0},{m.Top:0},{m.WStar:0.00},{m.WStarMax2Km:0.00},{m.FcTop:0},{m.FcBase:0},{m.FcCeiling:0},{m.Cumulus:0.00},{m.Overdev:0.00},{m.Elev:0},{m.Lead:0}\n");
        File.WriteAllText(ctx.PathFor("thermals_matched.csv"), csv.ToString());

        // ---- overall statistics ----
        var overall = new Dictionary<string, object?>();
        if (satAll.Count > 0)
        {
            overall["sat_n"] = satAll.Count;
            overall["sat_bias"] = Math.Round(satAll.Average(p => p.Fc - p.Obs), 1);
            overall["sat_mae"] = Math.Round(satAll.Average(p => Math.Abs(p.Fc - p.Obs)), 1);
            overall["sat_rmse"] = Math.Round(Math.Sqrt(satAll.Average(p => (p.Fc - p.Obs) * (p.Fc - p.Obs))), 1);
            overall["sat_r"] = Math.Round(Pearson(satAll.Select(p => p.Fc), satAll.Select(p => p.Obs)), 3);
            overall["sat_obs_mean"] = Math.Round(satAll.Average(p => p.Obs), 1);
        }
        if (matched.Count > 0)
        {
            overall["thermals"] = matched.Count;
            overall["flights"] = matched.Select(m => (m.Day, m.Flight)).Distinct().Count();
            overall["r_thermal_climb_wstar"] = Math.Round(Pearson(matched.Select(m => m.WStar), matched.Select(m => m.Climb)), 2);
            // bins: day × local hour × 50 km box – removes the noise of single thermals
            var bins = matched.GroupBy(m => (m.Day, ToLocal(m.MidUtc).Hour, Bx: (int)Math.Floor(m.Lon / 0.7), By: (int)Math.Floor(m.Lat / 0.45)))
                .Where(g => g.Count() >= 4)
                .Select(g => new
                {
                    g.Key.Day, g.Key.Hour, N = g.Count(),
                    Climb = Median(g.Select(m => m.Climb)),
                    WStar = Median(g.Select(m => m.WStar)),
                    Top90 = Percentile(g.Select(m => m.Top), 0.9),
                    Ceil = Median(g.Select(m => m.FcCeiling).Where(double.IsFinite)),
                }).ToList();
            overall["bins"] = bins.Count;
            if (bins.Count >= 3)
            {
                overall["r_bin_climb_wstar"] = Math.Round(Pearson(bins.Select(b => b.WStar), bins.Select(b => b.Climb)), 2);
                var (sl, ic) = Fit(bins.Select(b => b.WStar), bins.Select(b => b.Climb));
                overall["fit_climb_eq"] = $"stoupání ≈ {sl:0.00} · W* {(ic >= 0 ? "+" : "−")} {Math.Abs(ic):0.00} m/s";
                overall["bin_wstar_minus_climb"] = Math.Round(bins.Average(b => b.WStar - b.Climb), 2);
                var tb = bins.Where(b => double.IsFinite(b.Ceil)).ToList();
                overall["r_bin_top_ceiling"] = Math.Round(Pearson(tb.Select(b => b.Ceil), tb.Select(b => b.Top90)), 2);
                overall["bin_ceiling_bias"] = Math.Round(tb.Average(b => b.Ceil - b.Top90), 0);
                overall["bin_ceiling_mae"] = Math.Round(tb.Average(b => Math.Abs(b.Ceil - b.Top90)), 0);
            }
            File.WriteAllText(ctx.PathFor("bins.csv"), "day,hour,n,climb_median,wstar_median,top_p90,fc_ceiling\n" +
                string.Concat(bins.Select(b => FormattableString.Invariant($"{b.Day:yyyy-MM-dd},{b.Hour},{b.N},{b.Climb:0.00},{b.WStar:0.00},{b.Top90:0},{b.Ceil:0}\n"))));
            var dl = daySummaries.Where(d => d.ContainsKey("climb_median")).ToList();
            if (dl.Count >= 3)
            {
                overall["r_day_climb_wstar"] = Math.Round(Pearson(dl.Select(d => (double)d["wstar_median"]!), dl.Select(d => (double)d["climb_median"]!)), 2);
                overall["r_day_top_ceiling"] = Math.Round(Pearson(dl.Select(d => (double)d["fc_ceiling_median"]!), dl.Select(d => (double)d["top_p90"]!)), 2);
            }
        }

        // ---- flat CSV exports for the report page ----
        string[] dayCols = ["day", "sat_bias", "sat_r", "sat_spatial_r", "sat_fc_mean", "sat_obs_mean", "energy_fc", "energy_obs", "energy_r",
            "fc_wstar_max_hour_mean", "fc_ceiling_13h_mean", "flights", "thermals", "climb_median", "climb_best_half", "wstar_median",
            "top_p90", "fc_ceiling_median", "obs_end_local", "obs_end_latest_local", "fc_end_local", "fc_end15_local"];
        static string Cell(object? v) => v switch { null => "", double d => d.ToString(CultureInfo.InvariantCulture), _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" };
        File.WriteAllText(ctx.PathFor("report", "days.csv"), string.Join(",", dayCols) + "\n" +
            string.Concat(daySummaries.Select(d => string.Join(",", dayCols.Select(c => Cell(d.GetValueOrDefault(c)))) + "\n")));
        File.WriteAllText(ctx.PathFor("report", "satellite_hourly.csv"), "day,local,fc,obs\n" +
            string.Concat(satHourly.Select(h => $"{h["day"]},{h["local"]},{Cell(h["fc"])},{Cell(h["obs"])}\n")));
        File.WriteAllText(ctx.PathFor("report", "overall.csv"), string.Join(",", overall.Keys) + "\n" + string.Join(",", overall.Values.Select(v => Cell(v)?.Replace(",", ";"))) + "\n");
        File.WriteAllText(ctx.PathFor("report", "thermals.csv"), "day,mid_local,climb,top,wstar,fc_ceiling,cumulus\n" +
            string.Concat(matched.Select(m => FormattableString.Invariant($"{m.Day:yyyy-MM-dd},{ToLocal(m.MidUtc):HH:mm:ss},{m.Climb:0.00},{m.Top:0},{m.WStar:0.00},{m.FcCeiling:0},{m.Cumulus:0.00}\n"))));
        if (File.Exists(ctx.PathFor("bins.csv"))) File.Copy(ctx.PathFor("bins.csv"), ctx.PathFor("report", "bins.csv"), true);
        Report.Write(ctx);

        var summary = new { overall, days = daySummaries, satellite_hourly = satHourly };
        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(ctx.PathFor("summary.json"), json);
        Console.WriteLine(JsonSerializer.Serialize(new { overall, days = daySummaries }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    // ---- helpers ----

    /// <summary>W* etc. of an hour represent the middle of the preceding hour (fluxes are means of that hour).</summary>
    private static (ForecastHour A, ForecastHour B, double W) Bracket(List<ForecastHour> hours, DateTime t)
    {
        DateTime Eff(ForecastHour h) => h.ValidUtc.AddMinutes(-30);
        if (t <= Eff(hours[0])) return (hours[0], hours[0], 0);
        for (int k = 1; k < hours.Count; k++)
            if (t <= Eff(hours[k]))
                return (hours[k - 1], hours[k], (t - Eff(hours[k - 1])).TotalSeconds / (Eff(hours[k]) - Eff(hours[k - 1])).TotalSeconds);
        return (hours[^1], hours[^1], 0);
    }

    private static double Lerp(float a, float b, double w)
    {
        if (!float.IsFinite(a)) return w >= 0.5 || !float.IsFinite(b) ? b : double.NaN;
        if (!float.IsFinite(b)) return w < 0.5 ? a : double.NaN;
        return a + (b - a) * w;
    }

    private static float[] CeilingOrTop(ThermalFields f) =>
        f.CloudBase.Select((cb, k) => float.IsFinite(cb) ? cb : f.Top[k]).ToArray();

    private static ForecastHour HourAt(List<ForecastHour> hours, DateOnly day, int localHour) =>
        hours.MinBy(h => Math.Abs((ToLocal(h.ValidUtc) - day.ToDateTime(new TimeOnly(localHour, 0))).TotalMinutes))!;

    private static DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Tz);

    private static bool[] CzMask(GridDefinition g)
    {
        var cz = GeoResources.CzechRepublic;
        var prepared = NetTopologySuite.Geometries.Prepared.PreparedGeometryFactory.Prepare(cz);
        var gf = new NetTopologySuite.Geometries.GeometryFactory();
        var m = new bool[g.Nx * g.Ny];
        for (int j = 0; j < g.Ny; j++)
            for (int i = 0; i < g.Nx; i++)
                m[j * g.Nx + i] = prepared.Contains(gf.CreatePoint(new NetTopologySuite.Geometries.Coordinate(g.CenterX(i), g.CenterY(j))));
        return m;
    }

    private static double MeanMasked(float[] a, bool[] mask)
    {
        double s = 0; int n = 0;
        for (int k = 0; k < a.Length; k++) if (mask[k] && float.IsFinite(a[k])) { s += a[k]; n++; }
        return n > 0 ? s / n : double.NaN;
    }

    public static double Median(IEnumerable<double> v) => Percentile(v, 0.5);
    public static double Percentile(IEnumerable<double> v, double p)
    {
        var s = v.Where(double.IsFinite).Order().ToArray();
        if (s.Length == 0) return double.NaN;
        double idx = p * (s.Length - 1);
        int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
        return s[lo] + (s[hi] - s[lo]) * (idx - lo);
    }

    public static double Pearson(IEnumerable<double> xs, IEnumerable<double> ys)
    {
        var p = xs.Zip(ys).Where(t => double.IsFinite(t.First) && double.IsFinite(t.Second)).ToList();
        if (p.Count < 3) return double.NaN;
        double mx = p.Average(t => t.First), my = p.Average(t => t.Second);
        double sxy = p.Sum(t => (t.First - mx) * (t.Second - my)), sxx = p.Sum(t => (t.First - mx) * (t.First - mx)), syy = p.Sum(t => (t.Second - my) * (t.Second - my));
        return sxx > 0 && syy > 0 ? sxy / Math.Sqrt(sxx * syy) : double.NaN;
    }

    private static (double Slope, double Intercept) Fit(IEnumerable<double> xs, IEnumerable<double> ys)
    {
        var p = xs.Zip(ys).Where(t => double.IsFinite(t.First) && double.IsFinite(t.Second)).ToList();
        double mx = p.Average(t => t.First), my = p.Average(t => t.Second);
        double sxy = p.Sum(t => (t.First - mx) * (t.Second - my)), sxx = p.Sum(t => (t.First - mx) * (t.First - mx));
        double sl = sxx > 0 ? sxy / sxx : double.NaN;
        return (sl, my - sl * mx);
    }

    private sealed class Row(Dictionary<string, string> v)
    {
        public string S(string k) => v[k];
        public double D(string k) => double.TryParse(v[k], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
    }

    private static IEnumerable<Row> ReadCsv(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0) yield break;
        var head = lines[0].Split(',');
        foreach (var l in lines.Skip(1).Where(l => l.Length > 0))
        {
            var c = l.Split(',');
            yield return new Row(head.Select((h, i) => (h, i)).ToDictionary(t => t.h, t => i_or(c, t.i)));
        }
        static string i_or(string[] c, int i) => i < c.Length ? c[i] : "";
    }
}
