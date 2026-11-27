using NetTopologySuite.Geometries;
using Meteo.Core.Geo;
using Meteo.Core.Model;
using Meteo.Core.Styling;

namespace Meteo.Core.Vector;

/// <summary>Turns gridded forecast fields into styled vector layers.</summary>
public sealed class LayerBuilder
{
    private readonly GridDefinition _grid;

    public LayerBuilder(GridDefinition grid) => _grid = grid;

    /// <summary>Chaikin iterations applied to contour polygons (0 = raw triangulated contours).</summary>
    public int SmoothingIterations { get; init; } = 2;
    /// <summary>Simplification tolerance in metres.</summary>
    public double SimplifyTolerance { get; init; } = 250;
    /// <summary>Wind arrow spacing in grid cells.</summary>
    public int ArrowStep { get; init; } = 3;

    public VectorLayer Build(LayerKind kind, ForecastHour hour, string subtitle) => kind.Type switch
    {
        LayerType.CloudLow => Clouds(kind, hour.Get(FieldNames.CloudLow), subtitle),
        LayerType.CloudMid => Clouds(kind, hour.Get(FieldNames.CloudMid), subtitle),
        LayerType.CloudHigh => Clouds(kind, hour.Get(FieldNames.CloudHigh), subtitle),
        LayerType.Showers => Showers(kind, hour, subtitle),
        LayerType.Wind => Wind(kind, hour, subtitle),
        LayerType.Temperature2m => Temperature(kind, hour.Get(FieldNames.Temperature2m), subtitle),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private VectorLayer Clouds(LayerKind kind, float[]? values, string subtitle)
    {
        var layer = new VectorLayer { Title = kind.DisplayName, Subtitle = subtitle, Opacity = 0.8 };
        if (values != null) AddFilledContours(layer, values, ColorScale.Clouds);
        AddLegend(layer, ColorScale.Clouds);
        return layer;
    }

    private VectorLayer Temperature(LayerKind kind, float[]? values, string subtitle)
    {
        var layer = new VectorLayer { Title = kind.DisplayName, Subtitle = subtitle, Opacity = 0.7 };
        if (values != null) AddFilledContours(layer, values, ColorScale.Temperature);
        else layer.Legend.Add(new LegendItem("#FFFFFF", "chybí data – aktualizujte předpověď"));
        AddLegend(layer, ColorScale.Temperature);
        return layer;
    }

    private VectorLayer Showers(LayerKind kind, ForecastHour hour, string subtitle)
    {
        var layer = new VectorLayer { Title = kind.DisplayName, Subtitle = subtitle, Opacity = 0.85 };
        var rate = hour.Get(FieldNames.ShowerPrecip);
        if (rate != null) AddFilledContours(layer, rate, ColorScale.ShowerRate);
        var thunder = hour.Get(FieldNames.Thunder);
        if (thunder != null)
        {
            var g = Contour(thunder, 0.1);
            if (!g.IsEmpty) layer.Polygons.Add(new StyledPolygon(g, null, ColorScale.ThunderStroke, 2.5, Dashed: true, Value: 1));
        }
        AddLegend(layer, ColorScale.ShowerRate);
        layer.Legend.Add(new LegendItem(ColorScale.ThunderStroke, "bouřky (obrys)"));
        return layer;
    }

    private VectorLayer Wind(LayerKind kind, ForecastHour hour, string subtitle)
    {
        var layer = new VectorLayer { Title = kind.DisplayName, Subtitle = subtitle, Opacity = 0.75 };
        var u = hour.Get(kind.Height == 0 ? FieldNames.WindU10 : FieldNames.WindU(kind.Height));
        var v = hour.Get(kind.Height == 0 ? FieldNames.WindV10 : FieldNames.WindV(kind.Height));
        if (u == null || v == null) return layer;

        var speed = new float[u.Length];
        for (int k = 0; k < speed.Length; k++) speed[k] = (float)(Math.Sqrt(u[k] * u[k] + v[k] * v[k]) * 3.6);
        AddFilledContours(layer, speed, ColorScale.WindSpeed);

        int off = ArrowStep / 2;
        for (int j = off; j < _grid.Ny; j += ArrowStep)
            for (int i = off; i < _grid.Nx; i += ArrowStep)
            {
                int k = j * _grid.Nx + i;
                if (float.IsFinite(u[k]) && float.IsFinite(v[k]))
                    layer.Arrows.Add(new WindArrow(_grid.CenterX(i), _grid.CenterY(j), u[k], v[k]));
            }
        AddLegend(layer, ColorScale.WindSpeed);
        return layer;
    }

    // ---------------------------------------------------------------------------------------------

    private void AddFilledContours(VectorLayer layer, float[] values, ColorScale scale, GridDefinition? grid = null)
    {
        for (int l = 0; l < scale.Levels.Length; l++)
        {
            var g = Contour(values, scale.Levels[l], grid);
            if (!g.IsEmpty) layer.Polygons.Add(new StyledPolygon(g, scale.Colors[l], Value: scale.Levels[l]));
        }
    }

    private Geometry Contour(float[] values, double level, GridDefinition? grid = null)
    {
        var gr = grid ?? _grid;
        var g = Isobands.Extract(values, gr, level, double.PositiveInfinity);
        // finer grids need less smoothing and a smaller simplification tolerance
        bool fine = gr.CellSize < _grid.CellSize;
        return GeometrySmoothing.SmoothAndSimplify(g, fine ? 1 : SmoothingIterations, fine ? gr.CellSize * 0.1 : SimplifyTolerance);
    }

    // ---- thermal layers (1 km grid) -------------------------------------------------------------------

    public VectorLayer BuildThermal(LayerKind kind, Physics.ThermalFields f, string subtitle)
    {
        var layer = new VectorLayer { Title = kind.DisplayName, Subtitle = subtitle, Opacity = 0.75 };
        switch (kind.Type)
        {
            case LayerType.ThermalStrength:
                AddFilledContours(layer, f.WStar, ColorScale.ThermalStrength, f.Grid);
                AddLegend(layer, ColorScale.ThermalStrength);
                break;

            case LayerType.ThermalCeiling:
                AddFilledContours(layer, f.Ceiling, ColorScale.ThermalCeiling, f.Grid);
                // cumulus area as a translucent white overlay with outline; elsewhere thermals are blue (cloudless)
                var cu = new float[f.Cumulus.Length];
                for (int k = 0; k < cu.Length; k++) cu[k] = float.IsFinite(f.Ceiling[k]) ? f.Cumulus[k] : 0f;
                var cuGeom = Contour(cu, 0.5, f.Grid);
                if (!cuGeom.IsEmpty) layer.Polygons.Add(new StyledPolygon(cuGeom, ColorScale.CumulusFill, ColorScale.CumulusStroke, 1.5, Value: 1));
                AddLegend(layer, ColorScale.ThermalCeiling);
                layer.Legend.Add(new LegendItem(ColorScale.CumulusStroke, "bílý obrys = s kumuly (výška = základna)"));
                layer.Legend.Add(new LegendItem("#00FFFFFF", "bez obrysu = bezoblačná termika"));
                break;

            case LayerType.Overdevelopment:
                AddFilledContours(layer, f.Overdevelopment, ColorScale.Overdevelopment, f.Grid);
                AddLegend(layer, ColorScale.Overdevelopment);
                break;

            case LayerType.Spreading:
                AddFilledContours(layer, f.Spreading, ColorScale.Spreading, f.Grid);
                AddLegend(layer, ColorScale.Spreading);
                break;

            case LayerType.Lee:
                AddFilledContours(layer, f.Lee, ColorScale.Lee, f.Grid);
                AddLegend(layer, ColorScale.Lee);
                break;

            case LayerType.SolarRadiation:
                AddFilledContours(layer, f.Radiation, ColorScale.SolarRadiation, f.Grid);
                AddLegend(layer, ColorScale.SolarRadiation);
                break;

            case LayerType.Convergence:
                var conv = f.Convergence.Select(v => v * 1e4f).ToArray();
                AddFilledContours(layer, conv, ColorScale.Convergence, f.CoarseGrid);
                AddLegend(layer, ColorScale.Convergence);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
        return layer;
    }

    /// <summary>Symbol lattice spacing of the planning view (m).</summary>
    public double SymbolSpacing { get; init; } = 6000;

    /// <summary>
    /// Thermal quality score (m/s) of the planning view: W* reduced where the usable height above the terrain is low
    /// (&lt; 800 m), in the lee and where cumulus spread out and shade the ground. 0 where there are no usable thermals.
    /// Overdevelopment is not included – it is shown by symbols and an outline (validation: frequent false alarms).
    /// </summary>
    public static float[] FlightQuality(Physics.ThermalFields f, float[] elevation)
    {
        var q = new float[f.WStar.Length];
        for (int k = 0; k < q.Length; k++) q[k] = FlightQualityAt(f, k, elevation[k]);
        return q;
    }

    /// <inheritdoc cref="FlightQuality"/>
    public static float FlightQualityAt(Physics.ThermalFields f, int k, float elevation)
    {
        float w = f.WStar[k], ceil = f.Ceiling[k];
        if (!float.IsFinite(w) || !float.IsFinite(ceil) || !float.IsFinite(elevation)) return 0;
        float pen = 0.6f * Math.Clamp((800f - (ceil - elevation)) / 500f, 0f, 1f)
                  + 1.0f * Math.Clamp((f.Lee[k] - 0.25f) / 0.5f, 0f, 1f)
                  + 0.7f * Math.Clamp((f.Spreading[k] - 0.4f) / 0.4f, 0f, 1f);
        return Math.Max(0, w - pen);
    }

    /// <summary>Combined planning view: thermal quality, cloud symbols, overdevelopment and convergence outlines.</summary>
    public VectorLayer BuildFlightPlan(Physics.ThermalFields f, float[] elevation, string subtitle)
    {
        var g = f.Grid;
        int step = Math.Max(1, (int)Math.Round(SymbolSpacing / g.CellSize));
        var layer = new VectorLayer
        {
            Title = new LayerKind(LayerType.FlightPlan).DisplayName, Subtitle = subtitle, Opacity = 0.7,
            SymbolSpacing = step * g.CellSize,
        };

        AddFilledContours(layer, FlightQuality(f, elevation), ColorScale.FlightQuality, g);

        // lee: diagonal hatching (sink, broken thermals) – denser for strong lee
        foreach (var (level, spacing) in new[] { (0.35, 10.0), (0.65, 5.0) })
        {
            var leeGeom = Contour(f.Lee, level, g);
            if (!leeGeom.IsEmpty) layer.Polygons.Add(new StyledPolygon(leeGeom, null, Value: level, Hatch: ColorScale.LeeHatch, HatchSpacing: spacing, HatchBackslash: true));
        }

        // convergence: the opposite of lee (rising air) – hatching in the other direction, denser for strong convergence
        var conv = f.Convergence.Select(v => v * 1e4f).ToArray();
        foreach (var (level, spacing) in new[] { (1.0, 10.0), (2.0, 5.0) })
        {
            var convGeom = Contour(conv, level, f.CoarseGrid);
            if (!convGeom.IsEmpty)
                layer.Polygons.Add(new StyledPolygon(convGeom, null, Value: level, Hatch: ColorScale.ConvergenceStroke, HatchSpacing: spacing));
        }
        var odGeom = Contour(f.Overdevelopment, 0.6, g);
        if (!odGeom.IsEmpty) layer.Polygons.Add(new StyledPolygon(odGeom, null, ColorScale.OverdevelopmentStroke, 2.2, Dashed: true, Value: 0.6));

        // cloud symbols: one per step × step window (window means), the most significant phenomenon wins
        var cells = new Dictionary<(int I, int J), (SymbolKind? Kind, int Amount)>();
        for (int J = 0; J * step < g.Ny; J++)
            for (int I = 0; I * step < g.Nx; I++)
            {
                double sumOd = 0, sumSp = 0, sumCu = 0, sumCc = 0, sumSt = 0; int n = 0, usable = 0, flyable = 0;
                for (int j = J * step; j < Math.Min(g.Ny, (J + 1) * step); j++)
                    for (int i = I * step; i < Math.Min(g.Nx, (I + 1) * step); i++)
                    {
                        int k = j * g.Nx + i;
                        if (!float.IsFinite(elevation[k]) || !float.IsFinite(f.WStar[k])) continue;
                        n++;
                        sumOd += f.Overdevelopment[k];
                        sumSp += f.Spreading[k];
                        sumCu += f.Cumulus[k];
                        if (f.CumulusCover.Length > k) sumCc += f.CumulusCover[k];
                        if (f.Storm.Length > k) sumSt += f.Storm[k];
                        if (float.IsFinite(f.Ceiling[k])) usable++;
                        if (FlightQualityAt(f, k, elevation[k]) >= 1.0f) flyable++;  // same threshold as "weak thermals" in the fill
                    }
                if (n < step * step / 2) continue; // mostly outside the data area
                double storm = sumSt / n;
                SymbolKind? kind = storm >= 0.35 ? SymbolKind.Cumulonimbus
                    : sumOd / n >= 0.6 ? SymbolKind.Congestus
                    : sumSp / n >= 0.5 ? SymbolKind.Stratocumulus
                    : sumCu / n >= 0.5 && usable >= n * 0.3 ? SymbolKind.Cumulus
                    : flyable >= n * 0.3 ? SymbolKind.Blue
                    : null;
                // cumulus amount from the covered sky share: < 20 % few, < 40 % scattered, more = many (beyond ~55 % → Sc)
                double cover = sumCc / n;
                // amount is meaningful only for Cu (cover) and Cb (showers / thunder); 1 otherwise so it does not count as a change
                int amount = kind switch
                {
                    SymbolKind.Cumulonimbus => storm >= 0.85 ? 2 : 1,
                    SymbolKind.Cumulus => f.CumulusCover.Length == 0 ? 2 : cover < 0.2 ? 1 : cover < 0.4 ? 2 : 3,
                    _ => 1,
                };
                cells[(I, J)] = (kind, amount);
            }

        // majority filter over 3 × 3 windows: removes the salt-and-pepper changes of the cloud type between neighbouring
        // windows (noise of the forecast, not a real boundary); storms are always kept – they matter most
        foreach (var ((I, J), (kind, amount)) in cells)
        {
            SymbolKind? k2 = kind;
            int a2 = amount;
            if (kind != SymbolKind.Cumulonimbus)
            {
                var around = new List<(SymbolKind? Kind, int Amount)>(9);
                for (int dj = -1; dj <= 1; dj++)
                    for (int di = -1; di <= 1; di++)
                        if (cells.TryGetValue((I + di, J + dj), out var c)) around.Add(c);
                var major = around.GroupBy(c => c.Kind).OrderByDescending(gr => gr.Count()).First();
                if (major.Key != kind && major.Count() * 2 > around.Count && major.Key != SymbolKind.Cumulonimbus) k2 = major.Key;
                if (k2 == SymbolKind.Cumulus)
                {
                    var amounts = around.Where(c => c.Kind == SymbolKind.Cumulus).Select(c => c.Amount).OrderBy(x => x).ToList();
                    a2 = amounts.Count > 0 ? amounts[amounts.Count / 2] : 2;
                }
                else if (k2 != kind) a2 = 1;
            }
            if (k2 is { } sk)
                layer.Symbols.Add(new MapSymbol(g.X0 + (I + 0.5) * step * g.CellSize, g.Y0 + (J + 0.5) * step * g.CellSize, sk, I, J, a2));
        }

        AddLegend(layer, ColorScale.FlightQuality);
        layer.Legend.Add(new LegendItem(ColorScale.BlueThermalSymbol, "modrý kroužek – bezoblačná termika (bez kumulů)"));
        layer.Legend.Add(new LegendItem(ColorScale.CumulusSymbolFill, "Cu – kumuly: 1 obláček málo, 2 rozptýleně, 3 hodně"));
        layer.Legend.Add(new LegendItem(ColorScale.StratocumulusSymbolFill, "Sc – rozlévání, stínění"));
        layer.Legend.Add(new LegendItem(ColorScale.CumulusSymbolFill, "věžovitý Cu – přerůstá (riziko přeháněk)"));
        layer.Legend.Add(new LegendItem(ColorScale.CumulonimbusSymbolFill, "Cb – přeháňky (déšť), s bleskem bouřky"));
        layer.Legend.Add(new LegendItem(ColorScale.LeeHatch, "šrafování /// : závětří – klesání (hustší = silné)"));
        layer.Legend.Add(new LegendItem(ColorScale.OverdevelopmentStroke, "čárkovaně: riziko přerůstání ≥ 60 %"));
        layer.Legend.Add(new LegendItem(ColorScale.ConvergenceStroke, "šrafování \\\\\\ : konvergence – stoupavý pás (hustší = silná)"));
        return layer;
    }

    public VectorLayer BuildSolarDaily(float[] kwh, GridDefinition fineGrid, string subtitle)
    {
        var layer = new VectorLayer { Title = new LayerKind(LayerType.SolarDaily).DisplayName, Subtitle = subtitle, Opacity = 0.75 };
        if (kwh.Length == fineGrid.Nx * fineGrid.Ny) AddFilledContours(layer, kwh, ColorScale.SolarDaily, fineGrid);
        AddLegend(layer, ColorScale.SolarDaily);
        return layer;
    }

    public VectorLayer BuildThermalEnd(float[] endHour, GridDefinition fineGrid, string subtitle)
    {
        var layer = new VectorLayer { Title = new LayerKind(LayerType.ThermalEnd).DisplayName, Subtitle = subtitle, Opacity = 0.75 };
        if (endHour.Length == fineGrid.Nx * fineGrid.Ny) AddFilledContours(layer, endHour, ColorScale.ThermalEnd, fineGrid);
        AddLegend(layer, ColorScale.ThermalEnd);
        return layer;
    }

    private static void AddLegend(VectorLayer layer, ColorScale scale)
    {
        for (int l = 0; l < scale.Levels.Length; l++) layer.Legend.Add(new LegendItem(scale.Colors[l], scale.Labels[l]));
    }
}
