using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Meteo.Core.Dwd;
using Meteo.Core.Geo;
using Meteo.Core.Model;
using Meteo.Core.Storage;

namespace Meteo.Core.Processing;

/// <summary>
/// Builds a <see cref="ForecastRun"/> from DWD ICON open data:
/// days 1–2 from ICON-D2 (2.2 km, up to +48 h), later hours from ICON-EU (6.5 km, hourly up to +78 h).
/// Only daytime hours are processed. All fields are resampled onto the 5 km UTM grid.
/// </summary>
public sealed class ForecastBuilder
{
    private readonly ForecastOptions _opt;
    private readonly DwdOpenDataClient _client;
    private readonly ForecastStore _store;
    private readonly Resampler _resampler;
    private readonly Func<DateTime> _utcNow;
    private readonly ConcurrentDictionary<(string Model, DateTime Run, string Var, int Lead), float[]> _accumulated = new();

    public ForecastBuilder(ForecastOptions options, DwdOpenDataClient client, ForecastStore store, Func<DateTime>? utcNow = null)
    {
        _opt = options;
        _client = client;
        _store = store;
        _resampler = new Resampler(options.Grid);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    // ---------------------------------------------------------------------------------------------

    public async Task<ForecastRun> BuildAsync(IProgress<ForecastProgress>? progress, CancellationToken ct)
    {
        var now = _utcNow();
        var wanted = WantedValidTimes(now);
        Report(progress, "Hledám poslední běhy modelů…", 0, 1);

        var runD2 = await FindLatestRunAsync(IconModel.IconD2, now, ct);
        var runEu = await FindLatestRunAsync(IconModel.IconEu, now, ct);
        int LeadOf(DateTime valid, DateTime? r) => r is { } x ? (int)Math.Round((valid - x).TotalHours) : -1;

        var plan = new List<(DateTime Valid, IconModel Model, DateTime Run, int Lead)>();
        foreach (var valid in wanted)
        {
            int l2 = LeadOf(valid, runD2), le = LeadOf(valid, runEu);
            if (IconModel.IconD2.HasLead(l2)) plan.Add((valid, IconModel.IconD2, runD2!.Value, l2));
            else if (IconModel.IconEu.HasLead(le)) plan.Add((valid, IconModel.IconEu, runEu!.Value, le));
        }
        if (plan.Count == 0) throw new InvalidOperationException("Na DWD nejsou k dispozici žádná data pro požadované hodiny.");

        // Vertical structure per model (HHL) and the model levels needed for the wind heights.
        // ICON-D2 does not cover the eastern edge of the grid (incl. part of Moravia, ~18.4° E+):
        // those cells are filled from ICON-EU for the same valid time.
        VerticalInfo? vD2 = null, vEu = null;
        bool[]? gap = null;
        if (plan.Any(p => p.Model == IconModel.IconD2))
        {
            Report(progress, "ICON-D2: výšky modelových hladin (HHL)…", 0, 1);
            vD2 = await GetVerticalAsync(IconModel.IconD2, runD2!.Value, null, ct);
            var ground = vD2.Hhl[vD2.SurfaceHalfLevel];
            gap = ground.Select(float.IsNaN).ToArray();
            if (!gap.Any(g => g) || runEu is null) gap = null;
        }
        if (plan.Any(p => p.Model == IconModel.IconEu) || gap != null)
        {
            Report(progress, "ICON-EU: výšky modelových hladin (HHL)…", 0, 1);
            var mask = plan.Any(p => p.Model == IconModel.IconEu) ? null : gap;
            vEu = await GetVerticalAsync(IconModel.IconEu, runEu!.Value, mask, ct);
        }

        Report(progress, $"Hladiny pro vítr: ICON-D2 {vD2?.NeededLevels.Count ?? 0}, ICON-EU {vEu?.NeededLevels.Count ?? 0}", 0, 1);
        // 12 instantaneous single-level fields + 4 time-averaged/accumulated ones + u,v per level
        int FilesPerHour(VerticalInfo v) => 16 + 2 * v.NeededLevels.Count;
        int total = plan.Sum(p => p.Model == IconModel.IconD2 ? FilesPerHour(vD2!) + (gap != null ? FilesPerHour(vEu!) : 0) : FilesPerHour(vEu!));
        // the first hour of each continuous block also needs the previous step of the 4 averaged fields
        int blocks = plan.Where((p, k) => k == 0 || plan[k - 1].Model != p.Model || plan[k - 1].Lead != p.Model.PreviousLead(p.Lead)).Count();
        total += 4 * blocks * (gap != null ? 2 : 1);
        int done = 0;
        _accumulated.Clear();
        var run = new ForecastRun { Grid = _opt.Grid, CreatedUtc = now, WindHeights = _opt.WindHeights.ToList() };

        foreach (var p in plan)
        {
            ct.ThrowIfCancellationRequested();
            var local = TimeZoneInfo.ConvertTimeFromUtc(p.Valid, _opt.TimeZone);
            void Tick() => Report(progress, $"{p.Model.Name} {p.Run:dd.MM. HH}Z +{p.Lead} h → {local:ddd dd.MM. HH:mm}", Interlocked.Increment(ref done), total);
            var hour = await BuildHourAsync(p.Model, p.Run, p.Lead, p.Valid, p.Model == IconModel.IconD2 ? vD2! : vEu!, Tick, ct);

            if (p.Model == IconModel.IconD2 && gap != null)
            {
                int le = LeadOf(p.Valid, runEu);
                if (IconModel.IconEu.HasLead(le))
                {
                    void TickEu() => Report(progress, $"ICON-EU doplnění okraje → {local:ddd dd.MM. HH:mm}", Interlocked.Increment(ref done), total);
                    var eu = await BuildHourAsync(IconModel.IconEu, runEu!.Value, le, p.Valid, vEu!, TickEu, ct);
                    FillGaps(hour, eu, gap);
                }
            }
            run.Hours.Add(hour);
        }

        _store.Save(run);
        Report(progress, $"Hotovo: {run.Hours.Count} hodin, staženo {_client.BytesDownloaded / 1_048_576} MB", total, total);
        return run;

        void Report(IProgress<ForecastProgress>? pr, string msg, int d, int t) =>
            pr?.Report(new ForecastProgress(msg, d, t, _client.BytesDownloaded));
    }

    private static float[] KelvinToCelsius(float[] v)
    {
        for (int i = 0; i < v.Length; i++) v[i] -= 273.15f;
        return v;
    }

    /// <summary>Copies values from <paramref name="source"/> into cells of <paramref name="target"/> that lie outside its model domain.</summary>
    private static void FillGaps(ForecastHour target, ForecastHour source, bool[] gap)
    {
        foreach (var (name, dst) in target.Fields)
        {
            if (!source.Fields.TryGetValue(name, out var src)) continue;
            for (int k = 0; k < dst.Length; k++)
                if (gap[k]) dst[k] = src[k];
        }
    }

    /// <summary>
    /// Valid times (UTC) of the daytime hours of the next <see cref="ForecastOptions.Days"/> local days, not in the past.
    /// A day whose daytime hours are already over does not count (in the evening the days start tomorrow).
    /// </summary>
    public IReadOnlyList<DateTime> WantedValidTimes(DateTime nowUtc)
    {
        var res = new List<DateTime>();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, _opt.TimeZone));
        int days = 0;
        for (int d = 0; days < _opt.Days && d <= _opt.Days; d++)
        {
            var date = today.AddDays(d);
            int before = res.Count;
            for (int h = _opt.DayStartHour; h <= _opt.DayEndHour; h++)
            {
                var local = date.ToDateTime(new TimeOnly(h, 0), DateTimeKind.Unspecified);
                if (_opt.TimeZone.IsInvalidTime(local)) continue;
                var utc = TimeZoneInfo.ConvertTimeToUtc(local, _opt.TimeZone);
                if (utc >= nowUtc.AddHours(-1)) res.Add(utc);
            }
            if (res.Count > before) days++;
        }
        return res;
    }

    /// <summary>Most recent run whose last needed file is already published.</summary>
    private async Task<DateTime?> FindLatestRunAsync(IconModel model, DateTime nowUtc, CancellationToken ct)
    {
        var t = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour / model.RunInterval * model.RunInterval, 0, 0, DateTimeKind.Utc);
        for (int k = 0; k < 8; k++, t = t.AddHours(-model.RunInterval))
        {
            // The open-data server keeps roughly the last 24 h of runs.
            if (nowUtc - t > TimeSpan.FromHours(23)) break;
            var probe = model.SingleLevelUrl(t, model.MaxLead, "ww");
            if (await _client.ExistsAsync(probe, ct)) return t;
        }
        return null;
    }

    // ---- vertical structure ------------------------------------------------------------------------

    private sealed class VerticalInfo
    {
        public required int SurfaceHalfLevel { get; init; }      // index of the lowest half level (= ground)
        public required Dictionary<int, float[]> Hhl { get; init; } // half level -> height AMSL on our grid
        public required List<int> NeededLevels { get; init; }    // full model levels to download
        public float[] FullHeight(int level) // full level k lies between half levels k and k+1
        {
            var a = Hhl[level]; var b = Hhl[level + 1];
            var r = new float[a.Length];
            for (int i = 0; i < r.Length; i++) r[i] = 0.5f * (a[i] + b[i]);
            return r;
        }
    }

    /// <param name="mask">Cells for which wind levels are needed (null = all cells).</param>
    private async Task<VerticalInfo> GetVerticalAsync(IconModel model, DateTime runUtc, bool[]? mask, CancellationToken ct)
    {
        int maxH = _opt.WindHeights.Count > 0 ? _opt.WindHeights.Max() : 0;
        string key = $"{model.Directory}_hhl_{_opt.Grid.CellSize:0}m_{maxH}";
        var cached = _store.LoadStatic(key);
        var cacheFile = Path.Combine(_opt.CacheDirectory, "static", key + ".bin");
        bool fresh = cached != null && File.GetLastWriteTimeUtc(cacheFile) > DateTime.UtcNow.AddDays(-30);

        Dictionary<int, float[]> hhl;
        int surface;
        if (fresh)
        {
            surface = (int)cached!["meta"][0];
            hhl = cached.Where(kv => kv.Key.StartsWith("hhl")).ToDictionary(kv => int.Parse(kv.Key[3..]), kv => kv.Value);
        }
        else
        {
            var names = await _client.ListAsync(model.VariableDirectoryUrl(runUtc, "hhl"), ct);
            var rx = new Regex(@"regular-lat-lon_time-invariant_\d{10}_(?:000_)?(\d+)_hhl", RegexOptions.IgnoreCase);
            surface = names.Select(n => rx.Match(n)).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(0).Max();
            if (surface == 0) throw new InvalidOperationException($"{model.Name}: HHL files not found.");

            hhl = new Dictionary<int, float[]>();
            // Download from the ground upwards until the full level is above maxH everywhere.
            for (int k = surface; k >= 1; k--)
            {
                var f = await _client.GetFieldAsync(model.TimeInvariantUrl(runUtc, k, "hhl"), ct);
                hhl[k] = _resampler.Resample(LatLonGrid.FromGrib(f));
                if (k < surface)
                {
                    var full = new VerticalInfo { SurfaceHalfLevel = surface, Hhl = hhl, NeededLevels = new() }.FullHeight(k);
                    if (full.Where(float.IsFinite).DefaultIfEmpty(float.MaxValue).Min() > maxH + 200) break;
                }
            }
            var save = hhl.ToDictionary(kv => "hhl" + kv.Key, kv => kv.Value);
            save["meta"] = new float[] { surface };
            _store.SaveStatic(key, save);
        }

        int topHalf = hhl.Keys.Min();
        var fullHeights = new Dictionary<int, float[]>();
        var vi = new VerticalInfo { SurfaceHalfLevel = surface, Hhl = hhl, NeededLevels = new() };
        for (int k = topHalf; k < surface; k++) fullHeights[k] = vi.FullHeight(k);
        int n = _opt.Grid.Nx * _opt.Grid.Ny;
        var ground = hhl[surface];
        bool Use(int c) => (mask == null || mask[c]) && float.IsFinite(ground[c]);

        // Candidate levels: thin the (dense near-ground) model levels to a minimum mean spacing above ground.
        // Downloads are the bottleneck and wind varies smoothly enough for ~250 m steps.
        var candidates = new List<int>(); // ordered from the ground upwards
        double lastAgl = double.NegativeInfinity;
        for (int k = surface - 1; k >= topHalf; k--)
        {
            double sum = 0; int cnt = 0;
            for (int c = 0; c < n; c++) if (Use(c)) { sum += fullHeights[k][c] - ground[c]; cnt++; }
            if (cnt == 0) break;
            double agl = sum / cnt;
            // Denser near the ground where the wind profile is strongly non-linear (boundary layer):
            // 40 % of the spacing at the surface, full spacing from 1000 m above ground.
            double spacing = _opt.MinLevelSpacing * Math.Clamp(lastAgl / 1000.0, 0.4, 1.0);
            if (candidates.Count == 0 || agl - lastAgl >= spacing || k == topHalf)
            {
                candidates.Add(k);
                lastAgl = agl;
            }
        }

        // For each cell and wind height take the candidate levels just below and just above.
        var needed = new SortedSet<int>();
        for (int c = 0; c < n; c++)
        {
            if (!Use(c)) continue;
            needed.Add(candidates[0]); // lowest level (fallback near the ground)
            foreach (var h in _opt.WindHeights)
            {
                if (h < ground[c]) continue;
                for (int q = 0; q < candidates.Count; q++)
                {
                    if (fullHeights[candidates[q]][c] >= h)
                    {
                        needed.Add(candidates[q]);
                        if (q > 0) needed.Add(candidates[q - 1]);
                        break;
                    }
                }
            }
        }
        return new VerticalInfo { SurfaceHalfLevel = surface, Hhl = hhl, NeededLevels = needed.ToList() };
    }

    // ---- one forecast hour ---------------------------------------------------------------------------

    private async Task<ForecastHour> BuildHourAsync(IconModel m, DateTime run, int lead, DateTime valid, VerticalInfo vi,
        Action tick, CancellationToken ct)
    {
        var fields = new ConcurrentDictionary<string, float[]>();
        var raw = new ConcurrentDictionary<string, LatLonGrid>();

        async Task Single(string variable, int ld, Action<LatLonGrid> use)
        {
            var f = await _client.GetFieldAsync(m.SingleLevelUrl(run, ld, variable), ct);
            use(LatLonGrid.FromGrib(f));
            tick();
        }

        var tasks = new List<Task>
        {
            Single("clcl", lead, g => fields[FieldNames.CloudLow] = _resampler.ResampleMean(g)),
            Single("clcm", lead, g => fields[FieldNames.CloudMid] = _resampler.ResampleMean(g)),
            Single("clch", lead, g => fields[FieldNames.CloudHigh] = _resampler.ResampleMean(g)),
            Single("u_10m", lead, g => fields[FieldNames.WindU10] = _resampler.Resample(g)),
            Single("v_10m", lead, g => fields[FieldNames.WindV10] = _resampler.Resample(g)),
            Single("t_2m", lead, g => fields[FieldNames.Temperature2m] = KelvinToCelsius(_resampler.ResampleMean(g))),
            Single("ww", lead, g => raw["ww"] = g),
            Single("td_2m", lead, g => fields[FieldNames.Dewpoint2m] = KelvinToCelsius(_resampler.ResampleMean(g))),
            Single("mh", lead, g => fields[FieldNames.MixingHeight] = _resampler.ResampleMean(g)),
            Single("cape_ml", lead, g => fields[FieldNames.Cape] = _resampler.ResampleMean(g)),
            Single("htop_dc", lead, g => fields[FieldNames.DryConvTop] = _resampler.ResampleMeanWhere(g, v => v > 0)),
            Single(m.CloudBaseVariable, lead, g =>
            {
                fields[FieldNames.CuFraction] = _resampler.ResampleFraction(g, v => v > 0);
                fields[FieldNames.CuBase] = _resampler.ResampleMeanWhere(g, v => v > 0);
            }),
        };

        // Accumulated (tot_prec) and since-start averaged (radiation, heat flux) fields: the value of the previous hour is
        // derived from two consecutive steps. Resampled steps are cached so consecutive hours download each step once.
        async Task<float[]> Step(string variable, int ld)
        {
            if (ld <= 0) return new float[_opt.Grid.Nx * _opt.Grid.Ny];
            var key = (m.Directory, run, variable, ld);
            if (_accumulated.TryGetValue(key, out var cached)) return cached;
            var f = await _client.GetFieldAsync(m.SingleLevelUrl(run, ld, variable), ct);
            var a = _resampler.ResampleMean(LatLonGrid.FromGrib(f));
            _accumulated[key] = a;
            tick();
            return a;
        }
        // Previous published step: 1 h back, beyond the hourly range of ICON-EU (+78 h) 3 h back.
        int prev = m.PreviousLead(lead);
        float span = lead - prev;
        async Task<float[]> HourlyMean(string variable)  // average since start → mean over the previous step
        {
            var a1 = await Step(variable, lead);
            var a0 = await Step(variable, prev);
            var r = new float[a1.Length];
            for (int i = 0; i < r.Length; i++) r[i] = (a1[i] * lead - a0[i] * prev) / span;
            return r;
        }
        async Task<float[]> HourlySum(string variable)   // accumulated since start → mean hourly rate over the previous step
        {
            var a1 = await Step(variable, lead);
            var a0 = await Step(variable, prev);
            var r = new float[a1.Length];
            for (int i = 0; i < r.Length; i++) r[i] = Math.Max(0, a1[i] - a0[i]) / span;
            return r;
        }
        var precTask = HourlySum("tot_prec");
        var dirTask = HourlyMean("aswdir_s");
        var difTask = HourlyMean("aswdifd_s");
        var shfTask = HourlyMean("ashfl_s");
        tasks.AddRange(new Task[] { precTask, dirTask, difTask, shfTask });

        var ml = new ConcurrentDictionary<(string, int), float[]>();
        foreach (var level in vi.NeededLevels)
            foreach (var comp in new[] { "u", "v" })
            {
                int lv = level; string cp = comp;
                tasks.Add(Task.Run(async () =>
                {
                    var f = await _client.GetFieldAsync(m.ModelLevelUrl(run, lead, lv, cp), ct);
                    ml[(cp, lv)] = _resampler.Resample(LatLonGrid.FromGrib(f));
                    tick();
                }, ct));
            }

        await Task.WhenAll(tasks);

        // Precipitation of the previous hour, area-averaged.
        var prec = precTask.Result;
        fields[FieldNames.Precip] = prec;

        // Surface energy: radiation is positive downwards; DWD heat flux is positive downwards, so negate (upward = thermals).
        fields[FieldNames.RadDirect] = dirTask.Result.Select(v => Math.Max(0, v)).ToArray();
        fields[FieldNames.RadDiffuse] = difTask.Result.Select(v => Math.Max(0, v)).ToArray();
        fields[FieldNames.SensibleHeat] = shfTask.Result.Select(v => -v).ToArray();
        fields[FieldNames.ModelTerrain] = (float[])vi.Hhl[vi.SurfaceHalfLevel].Clone();

        // Convective weather from WW (significant weather, WMO 4677 subset used by ICON).
        var ww = raw["ww"];
        var showerFrac = _resampler.ResampleFraction(ww, v => (v >= 80 && v <= 90) || v >= 95);
        var thunder = _resampler.ResampleFraction(ww, v => v >= 95);
        var shower = new float[prec.Length];
        for (int i = 0; i < shower.Length; i++)
            shower[i] = float.IsNaN(prec[i]) ? float.NaN : (showerFrac[i] > 0 ? prec[i] : 0f);
        fields[FieldNames.ShowerPrecip] = shower;
        fields[FieldNames.Thunder] = thunder;

        // Winds: rotate to map orientation, interpolate model levels to heights AMSL.
        var u10 = fields[FieldNames.WindU10]; var v10 = fields[FieldNames.WindV10];
        _resampler.RotateToMap(u10, v10);
        foreach (var lv in vi.NeededLevels) _resampler.RotateToMap(ml[("u", lv)], ml[("v", lv)]);
        foreach (var h in _opt.WindHeights)
        {
            var (u, v) = WindInterpolation.ToHeight(h, vi.NeededLevels, lv => vi.FullHeight(lv), lv => ml[("u", lv)], lv => ml[("v", lv)],
                vi.Hhl[vi.SurfaceHalfLevel]);
            fields[FieldNames.WindU(h)] = u;
            fields[FieldNames.WindV(h)] = v;
        }

        return new ForecastHour { ValidUtc = valid, Model = m.Name, RunUtc = run, Fields = new Dictionary<string, float[]>(fields) };
    }
}
