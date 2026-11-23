using System.Collections.Concurrent;
using Meteo.Core.Dwd;
using Meteo.Core.Export;
using Meteo.Core.Geo;
using Meteo.Core.Model;
using Meteo.Core.Processing;
using Meteo.Core.Storage;
using Meteo.Core.Vector;

namespace Meteo.Core;

/// <summary>
/// Public entry point: downloads/processes forecasts and serves vector layers per day and local hour.
/// Thread-safe for reading; <see cref="UpdateAsync"/> should not run concurrently with itself.
/// </summary>
public sealed class ForecastService : IDisposable
{
    private readonly ForecastStore _store;
    private readonly DwdOpenDataClient _client;
    private readonly LayerBuilder _layers;
    private readonly ConcurrentDictionary<(LayerKind, DateTime), VectorLayer> _cache = new();
    private readonly Lazy<VectorLayer> _relief;
    private readonly Lazy<VectorLayer> _borders = new(GeoResources.Borders);
    private readonly Lazy<Physics.ThermalModel> _thermal;
    private readonly ConcurrentDictionary<DateTime, Physics.ThermalFields> _thermalCache = new();

    public ForecastService(ForecastOptions? options = null)
    {
        Options = options ?? new ForecastOptions();
        _store = new ForecastStore(Options.CacheDirectory);
        _client = new DwdOpenDataClient(Options.MaxParallelDownloads);
        _layers = new LayerBuilder(Options.Grid);
        _relief = new Lazy<VectorLayer>(() => GeoResources.Relief(Options.Grid));
        _thermal = new Lazy<Physics.ThermalModel>(() => new Physics.ThermalModel(Options.Grid, SurfaceData.Default));
    }

    public ForecastOptions Options { get; }
    public ForecastRun? Current { get; private set; }

    /// <summary>Loads the last processed forecast from the disk cache. Returns false if there is none.</summary>
    public bool LoadCached()
    {
        var run = _store.Load();
        if (run is null || run.Grid != Options.Grid) return false;
        SetCurrent(run);
        return true;
    }

    /// <summary>Downloads the latest ICON-D2/ICON-EU runs and processes them.</summary>
    public async Task<ForecastRun> UpdateAsync(IProgress<ForecastProgress>? progress = null, CancellationToken ct = default)
    {
        var builder = new ForecastBuilder(Options, _client, _store);
        var run = await builder.BuildAsync(progress, ct);
        SetCurrent(run);
        return run;
    }

    private void SetCurrent(ForecastRun run)
    {
        Current = run;
        _cache.Clear();
        _thermalCache.Clear();
    }

    /// <summary>Local dates with at least one forecast hour.</summary>
    public IReadOnlyList<DateOnly> AvailableDays =>
        Current?.Hours.Select(h => DateOnly.FromDateTime(ToLocal(h.ValidUtc))).Distinct().OrderBy(d => d).ToList()
        ?? (IReadOnlyList<DateOnly>)Array.Empty<DateOnly>();

    /// <summary>Local hours available for a date.</summary>
    public IReadOnlyList<int> GetHours(DateOnly day) =>
        Current?.Hours.Select(h => ToLocal(h.ValidUtc)).Where(t => DateOnly.FromDateTime(t) == day).Select(t => t.Hour).OrderBy(h => h).ToList()
        ?? (IReadOnlyList<int>)Array.Empty<int>();

    public ForecastHour? GetHour(DateOnly day, int localHour) =>
        Current?.Hours.FirstOrDefault(h => { var t = ToLocal(h.ValidUtc); return DateOnly.FromDateTime(t) == day && t.Hour == localHour; });

    /// <summary>Wind layers available: 10 m above ground (height 0) and the configured heights AMSL.</summary>
    public IReadOnlyList<LayerKind> AvailableLayers =>
        new[]
        {
            new LayerKind(LayerType.ThermalStrength), new LayerKind(LayerType.ThermalCeiling), new LayerKind(LayerType.ThermalEnd),
            new LayerKind(LayerType.Overdevelopment), new LayerKind(LayerType.Spreading),
            new LayerKind(LayerType.Lee), new LayerKind(LayerType.Convergence),
            new LayerKind(LayerType.SolarRadiation), new LayerKind(LayerType.SolarDaily),
            LayerKind.CloudLow, LayerKind.CloudMid, LayerKind.CloudHigh, LayerKind.Showers, LayerKind.Temperature2m, LayerKind.Wind(0),
        }
        .Concat((Current?.WindHeights ?? Options.WindHeights).Select(LayerKind.Wind)).ToList();

    public VectorLayer? GetLayer(LayerKind kind, DateOnly day, int localHour)
    {
        if (kind.IsDaily)
        {
            var hours = GetHours(day);
            if (hours.Count == 0) return null;
            var key = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            return _cache.GetOrAdd((kind, key), _ =>
            {
                if (kind.Type == LayerType.SolarDaily)
                {
                    var kwh = Physics.ThermalModel.DailyEnergy(hours.Select(h => GetThermal(GetHour(day, h)!)));
                    return _layers.BuildSolarDaily(kwh, _thermal.Value.Grid, $"{day:dddd d. M. yyyy} · se započtením oblačnosti a sklonu svahů");
                }
                var end = Physics.ThermalModel.ThermalEnd(hours.Select(h => (h, GetThermal(GetHour(day, h)!))));
                return _layers.BuildThermalEnd(end, _thermal.Value.Grid, $"{day:dddd d. M. yyyy} · W* ≥ 1 m/s, nízké riziko přerůstání");
            });
        }

        var hour = GetHour(day, localHour);
        if (hour is null) return null;
        return _cache.GetOrAdd((kind, hour.ValidUtc), _ =>
        {
            var local = ToLocal(hour.ValidUtc);
            var subtitle = $"{local:dddd d. M. yyyy HH:mm} · {hour.Model} běh {hour.RunUtc:dd.MM. HH} UTC";
            if ((hour.ValidUtc - hour.RunUtc).TotalHours > IconModel.IconEu.MaxHourlyLead) subtitle += " · krok 3 h (srážky a záření = průměr 3 h)";
            if (kind.IsThermal) return _layers.BuildThermal(kind, GetThermal(hour), subtitle + " · 1 km");
            return _layers.Build(kind, hour, subtitle);
        });
    }

    /// <summary>Thermal diagnostics of an hour on the 1 km grid (cached for recent hours).</summary>
    public Physics.ThermalFields GetThermal(ForecastHour hour)
    {
        if (_thermalCache.TryGetValue(hour.ValidUtc, out var f)) return f;
        f = _thermal.Value.Compute(hour, Current?.WindHeights ?? Options.WindHeights);
        if (_thermalCache.Count > 24)
            foreach (var old in _thermalCache.Keys.OrderBy(k => Math.Abs((k - hour.ValidUtc).TotalHours)).Skip(12).ToList())
                _thermalCache.TryRemove(old, out _);
        _thermalCache[hour.ValidUtc] = f;
        return f;
    }

    /// <summary>Human readable value of a layer at a map point (UTM metres), e.g. "BKN 70 %" or "25 km/h z 270°".</summary>
    public string? DescribePoint(LayerKind kind, DateOnly day, int localHour, double x, double y)
    {
        if (kind.IsThermal) return DescribeThermal(kind, day, localHour, x, y);
        var hour = GetHour(day, localHour);
        var g = Options.Grid;
        int i = (int)Math.Floor((x - g.X0) / g.CellSize), j = (int)Math.Floor((y - g.Y0) / g.CellSize);
        if (hour is null || i < 0 || j < 0 || i >= g.Nx || j >= g.Ny) return null;
        int k = j * g.Nx + i;

        string? Field(string name, Func<float, string> fmt)
        {
            var f = hour.Get(name);
            return f is null || !float.IsFinite(f[k]) ? null : fmt(f[k]);
        }

        switch (kind.Type)
        {
            case LayerType.CloudLow: return Field(FieldNames.CloudLow, v => $"{v:0} %");
            case LayerType.CloudMid: return Field(FieldNames.CloudMid, v => $"{v:0} %");
            case LayerType.CloudHigh: return Field(FieldNames.CloudHigh, v => $"{v:0} %");
            case LayerType.Temperature2m: return Field(FieldNames.Temperature2m, v => $"{v:0.0} °C");
            case LayerType.Showers:
            {
                var rate = Field(FieldNames.ShowerPrecip, v => $"přeháňky {v:0.0} mm/h");
                var all = Field(FieldNames.Precip, v => $"srážky celkem {v:0.0} mm/h");
                var th = hour.Get(FieldNames.Thunder) is { } t && t[k] > 0 ? ", bouřka" : "";
                return $"{rate}, {all}{th}";
            }
            case LayerType.Wind:
            {
                var u = hour.Get(kind.Height == 0 ? FieldNames.WindU10 : FieldNames.WindU(kind.Height));
                var v = hour.Get(kind.Height == 0 ? FieldNames.WindV10 : FieldNames.WindV(kind.Height));
                if (u is null || v is null) return null;
                if (!float.IsFinite(u[k])) return "pod terénem";
                var a = new WindArrow(x, y, u[k], v[k]);
                // Direction is relative to map (UTM grid) north; differs from true north by < 3° over CZ.
                return $"{a.SpeedKmh:0} km/h z {Math.Round(a.FromDirection / 10) * 10 % 360:000}°";
            }
            default: return null;
        }
    }

    private string? DescribeThermal(LayerKind kind, DateOnly day, int localHour, double x, double y)
    {
        var tg = _thermal.Value.Grid;
        int i = (int)Math.Floor((x - tg.X0) / tg.CellSize), j = (int)Math.Floor((y - tg.Y0) / tg.CellSize);
        if (i < 0 || j < 0 || i >= tg.Nx || j >= tg.Ny) return null;
        int k = j * tg.Nx + i;
        var surf = _thermal.Value.Surface;
        string terrain = $"terén {surf.Elevation[k]:0} m";

        if (kind.IsDaily)
        {
            var hours = GetHours(day);
            if (kind.Type == LayerType.SolarDaily)
            {
                var kwh = Physics.ThermalModel.DailyEnergy(hours.Select(h => GetThermal(GetHour(day, h)!)));
                return kwh.Length > k && float.IsFinite(kwh[k]) ? $"{kwh[k]:0.00} kWh/m² za 07–19 h · {terrain}" : null;
            }
            var end = Physics.ThermalModel.ThermalEnd(hours.Select(h => (h, GetThermal(GetHour(day, h)!))));
            return end.Length > k && float.IsFinite(end[k]) ? $"termika do {end[k]:0}:00 · {terrain}" : $"bez použitelné termiky · {terrain}";
        }

        var hour = GetHour(day, localHour);
        if (hour is null) return null;
        var f = GetThermal(hour);
        string ceil = float.IsFinite(f.Ceiling[k])
            ? (float.IsFinite(f.CloudBase[k]) ? $"Cu základna {f.CloudBase[k]:0} m" : $"bezoblačná do {f.Ceiling[k]:0} m")
            : "bez termiky";
        string common = $"W* {f.WStar[k]:0.0} m/s · {ceil} · {terrain}";
        return kind.Type switch
        {
            LayerType.Overdevelopment => $"přerůstání {f.Overdevelopment[k] * 100:0} % · {common}",
            LayerType.Spreading => $"rozlití {f.Spreading[k] * 100:0} % · {common}",
            LayerType.Lee => $"závětří {f.Lee[k] * 100:0} % · {common}",
            LayerType.Convergence => ConvergenceAt(f, x, y) + " · " + common,
            LayerType.SolarRadiation => $"{f.Radiation[k]:0} W/m² na terén" + (f.StepHours > 1 ? $" (průměr {f.StepHours} h)" : "") +
                                        $" · sklon {Math.Acos(Math.Clamp(surf.NormalU[k], -1, 1)) * 180 / Math.PI:0}° · {terrain}",
            _ => $"{common} · tok tepla {f.HeatFlux[k]:0} W/m²",
        };
    }

    private static string ConvergenceAt(Physics.ThermalFields f, double x, double y)
    {
        var g = f.CoarseGrid;
        int i = (int)Math.Floor((x - g.X0) / g.CellSize), j = (int)Math.Floor((y - g.Y0) / g.CellSize);
        if (i < 0 || j < 0 || i >= g.Nx || j >= g.Ny) return "";
        float c = f.Convergence[j * g.Nx + i];
        return float.IsFinite(c) ? $"konvergence {c * 1e4:0.0}·10⁻⁴/s" : "";
    }

    public VectorLayer GetRelief() => _relief.Value;
    public VectorLayer GetBorders() => _borders.Value;

    /// <summary>GeoJSON (WGS84) of a layer for web clients.</summary>
    public static string ToGeoJson(VectorLayer layer, bool indented = false) => GeoJsonExporter.ToGeoJson(layer, true, indented);

    private DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Options.TimeZone);

    public void Dispose() => _client.Dispose();
}
