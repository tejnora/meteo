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
