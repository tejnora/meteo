using NetTopologySuite.Geometries;

namespace Meteo.Core.Vector;

/// <summary>What can be displayed. <see cref="Height"/> is used by <see cref="LayerType.Wind"/>: 0 = 10 m above ground, otherwise m AMSL.</summary>
public readonly record struct LayerKind(LayerType Type, int Height = 0)
{
    public static LayerKind CloudLow => new(LayerType.CloudLow);
    public static LayerKind CloudMid => new(LayerType.CloudMid);
    public static LayerKind CloudHigh => new(LayerType.CloudHigh);
    public static LayerKind Showers => new(LayerType.Showers);
    public static LayerKind Wind(int heightAmsl = 0) => new(LayerType.Wind, heightAmsl);
    public static LayerKind Temperature2m => new(LayerType.Temperature2m);

    /// <summary>Thermal layers computed on the 1 km grid by <see cref="Physics.ThermalModel"/>.</summary>
    public bool IsThermal => Type >= LayerType.ThermalStrength;
    /// <summary>Layer describes a whole day (hour is ignored).</summary>
    public bool IsDaily => Type is LayerType.ThermalEnd or LayerType.SolarDaily;

    public string DisplayName => Type switch
    {
        LayerType.CloudLow => "Nízká oblačnost",
        LayerType.CloudMid => "Střední oblačnost",
        LayerType.CloudHigh => "Vysoká oblačnost",
        LayerType.Showers => "Přeháňky",
        LayerType.Wind => Height == 0 ? "Vítr 10 m nad zemí" : $"Vítr {Height} m n. m.",
        LayerType.Temperature2m => "Teplota 2 m nad zemí",
        LayerType.ThermalStrength => "Síla termiky (W*)",
        LayerType.ThermalCeiling => "Výška termiky / základny Cu",
        LayerType.Overdevelopment => "Riziko přerůstání do přeháněk",
        LayerType.Spreading => "Riziko rozlití kumulů (Sc)",
        LayerType.ThermalEnd => "Konec použitelné termiky",
        LayerType.Lee => "Závětří",
        LayerType.Convergence => "Konvergence v mezní vrstvě",
        LayerType.SolarRadiation => "Sluneční energie dopadající na terén",
        LayerType.SolarDaily => "Denní součet sluneční energie (07–19 h)",
        _ => Type.ToString(),
    };
}

public enum LayerType
{
    CloudLow, CloudMid, CloudHigh, Showers, Wind, Temperature2m,
    ThermalStrength, ThermalCeiling, Overdevelopment, Spreading, ThermalEnd, Lee, Convergence,
    SolarRadiation, SolarDaily,
}

/// <summary>Colours are "#RRGGBB" strings so the library stays UI-framework neutral.</summary>
public sealed record StyledPolygon(Geometry Geometry, string? Fill, string? Stroke = null, double StrokeWidth = 1, bool Dashed = false, double Value = double.NaN);

public sealed record StyledLine(Geometry Geometry, string Stroke, double Width, bool Dashed = false);

/// <summary>Wind at a point. U/V are map (grid) oriented, m/s.</summary>
public sealed record WindArrow(double X, double Y, double U, double V)
{
    public double SpeedMs => Math.Sqrt(U * U + V * V);
    public double SpeedKmh => SpeedMs * 3.6;
    /// <summary>Meteorological direction the wind blows FROM, degrees clockwise from map north.</summary>
    public double FromDirection => (Math.Atan2(-U, -V) * 180 / Math.PI + 360) % 360;
}

public sealed record LegendItem(string Color, string Label);

/// <summary>
/// A renderable vector layer. Geometries are in UTM 33N metres. Polygons are listed in drawing order;
/// filled contour polygons are cumulative (value &gt;= level), so later polygons are drawn on top of earlier ones.
/// </summary>
public sealed class VectorLayer
{
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    /// <summary>Suggested opacity of the whole polygon group.</summary>
    public double Opacity { get; init; } = 1;
    public List<StyledPolygon> Polygons { get; } = new();
    public List<StyledLine> Lines { get; } = new();
    public List<WindArrow> Arrows { get; } = new();
    public List<LegendItem> Legend { get; } = new();
}
