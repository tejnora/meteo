namespace Meteo.Core.Styling;

/// <summary>A stepped colour scale: colour i applies to values &gt;= Levels[i].</summary>
public sealed record ColorScale(string Title, string Unit, double[] Levels, string[] Colors, string[] Labels)
{
    public static ColorScale Clouds { get; } = new("Oblačnost", "%",
        new[] { 12.5, 37.5, 62.5, 87.5 },
        new[] { "#F4F6F8", "#D3D8DE", "#A9B1BA", "#7D8793" },
        new[] { "FEW ≥ 1/8", "SCT ≥ 3/8", "BKN ≥ 5/8", "OVC ≥ 7/8" });

    public static ColorScale ShowerRate { get; } = new("Přeháňky", "mm/h",
        new[] { 0.1, 0.5, 1, 2, 5, 10 },
        new[] { "#A6D8F7", "#4FA9E8", "#1F6FD1", "#2DB34A", "#F2C318", "#E0412D" },
        new[] { "0,1", "0,5", "1", "2", "5", "≥ 10 mm/h" });

    public static ColorScale WindSpeed { get; } = new("Rychlost větru", "km/h",
        new double[] { 10, 20, 30, 40, 50, 60, 75 },
        new[] { "#E3F2E1", "#B9E2B0", "#8CCF84", "#F5E27A", "#F3B04F", "#E8743B", "#C9393A" },
        new[] { "10", "20", "30", "40", "50", "60", "≥ 75 km/h" });

    /// <summary>Temperature; the first level (-100) covers the whole area so the layer has no holes.</summary>
    public static ColorScale Temperature { get; } = new("Teplota 2 m", "°C",
        new double[] { -100, -10, -5, 0, 5, 10, 15, 20, 25, 30, 35 },
        new[] { "#5E3C99", "#3F5FBF", "#4A90D9", "#7FC3E8", "#B8E3D0", "#DDEFA8", "#F5E07A", "#F6B24F", "#EE7A3A", "#D8432C", "#A8132A" },
        new[] { "< -10 °C", "-10", "-5", "0", "5", "10", "15", "20", "25", "30", "≥ 35 °C" });

    public static ColorScale ThermalStrength { get; } = new("Síla termiky W*", "m/s",
        new[] { 0.5, 1.0, 1.5, 2.0, 2.5, 3.0, 4.0 },
        new[] { "#FFF7BC", "#FEE391", "#FEC44F", "#FE9929", "#EC7014", "#CC4C02", "#8C2D04" },
        new[] { "0,5", "1,0", "1,5", "2,0", "2,5", "3,0", "≥ 4 m/s" });

    public static ColorScale ThermalCeiling { get; } = new("Výška termiky", "m n. m.",
        new double[] { 600, 800, 1000, 1200, 1400, 1600, 1800, 2000, 2400, 2800 },
        new[] { "#E8F1FA", "#CFE3F3", "#B0D2EC", "#8FBFE3", "#6DAAD8", "#4E94CB", "#3A7DBA", "#2D67A4", "#24528C", "#1C3F72" },
        new[] { "600", "800", "1000", "1200", "1400", "1600", "1800", "2000", "2400", "≥ 2800 m" });

    public static ColorScale Overdevelopment { get; } = new("Riziko přerůstání", "%",
        new[] { 0.2, 0.4, 0.6, 0.8 },
        new[] { "#FFE9A8", "#FDB863", "#E66101", "#B2182B" },
        new[] { "20 %", "40 %", "60 %", "≥ 80 %" });

    public static ColorScale Spreading { get; } = new("Riziko rozlití", "%",
        new[] { 0.2, 0.4, 0.6, 0.8 },
        new[] { "#E0E0E0", "#BDBDBD", "#8C8C8C", "#5A5A5A" },
        new[] { "20 %", "40 %", "60 %", "≥ 80 %" });

    public static ColorScale ThermalEnd { get; } = new("Konec termiky", "h",
        new double[] { 10, 12, 13, 14, 15, 16, 17, 18 },
        new[] { "#D73027", "#F46D43", "#FDAE61", "#FEE08B", "#D9EF8B", "#A6D96A", "#66BD63", "#1A9850" },
        new[] { "do 11 h", "12 h", "13 h", "14 h", "15 h", "16 h", "17 h", "≥ 18 h" });

    public static ColorScale Lee { get; } = new("Závětří", "",
        new[] { 0.25, 0.5, 0.75 },
        new[] { "#D9C4E8", "#B48ED1", "#7B4AAE" },
        new[] { "slabé", "střední", "silné" });

    /// <summary>Convergence in 10⁻⁴ s⁻¹.</summary>
    public static ColorScale Convergence { get; } = new("Konvergence", "10⁻⁴/s",
        new[] { 0.5, 1.0, 2.0 },
        new[] { "#FDD49E", "#FC8D59", "#D7301F" },
        new[] { "slabá", "střední", "silná" });

    public static ColorScale SolarRadiation { get; } = new("Sluneční energie", "W/m²",
        new double[] { 25, 50, 100, 200, 300, 400, 500, 600, 700, 850 },
        new[] { "#2C2D54", "#47407A", "#6A4C93", "#9A5A8E", "#C66B7C", "#E5855F", "#F4A548", "#F9C74F", "#FBE38E", "#FFF7D1" },
        new[] { "25", "50", "100", "200", "300", "400", "500", "600", "700", "≥ 850 W/m²" });

    public static ColorScale SolarDaily { get; } = new("Denní součet", "kWh/m²",
        new[] { 0.25, 0.5, 1, 1.5, 2, 3, 4, 5, 6, 7 },
        new[] { "#2C2D54", "#47407A", "#6A4C93", "#9A5A8E", "#C66B7C", "#E5855F", "#F4A548", "#F9C74F", "#FBE38E", "#FFF7D1" },
        new[] { "0,25", "0,5", "1", "1,5", "2", "3", "4", "5", "6", "≥ 7 kWh/m²" });

    /// <summary>Thermal quality score of the planning view (≈ W* reduced by low ceiling, lee and spreading), m/s.</summary>
    public static ColorScale FlightQuality { get; } = new("Kvalita termiky", "m/s",
        new[] { 1.0, 1.6, 2.2 },
        new[] { "#FFF1A8", "#FDB54E", "#E4570B" },
        new[] { "slabá termika", "dobrá termika", "výborná termika" });

    public const string BlueThermalSymbol = "#1E6FD9";
    public const string CumulusSymbolFill = "#FFFFFF";
    public const string CumulusSymbolStroke = "#5B6B7C";
    public const string StratocumulusSymbolFill = "#9AA3AD";
    public const string StratocumulusSymbolStroke = "#4A525B";
    public const string CumulonimbusSymbolFill = "#3E4652";
    public const string CumulonimbusSymbolStroke = "#1E232A";
    public const string LightningFill = "#FFD400";
    public const string OverdevelopmentStroke = "#C2185B";
    public const string ConvergenceStroke = "#D35400";
    public const string LeeHatch = "#6A3D9A";

    public const string CumulusFill = "#59FFFFFF";
    public const string CumulusStroke = "#FFFFFF";

    /// <summary>Hypsometric tints; <see cref="ReliefBase"/> is used below the first level.</summary>
    public static ColorScale Relief { get; } = new("Nadmořská výška", "m",
        new double[] { 200, 300, 400, 500, 600, 700, 800, 900, 1000, 1200, 1400 },
        new[] { "#C4DDB0", "#D3E4B4", "#E1E8B8", "#ECE6B5", "#EADBA6", "#E3CC96", "#DABA86", "#CFA878", "#C3976D", "#B58866", "#A67B62" },
        new[] { "200", "300", "400", "500", "600", "700", "800", "900", "1000", "1200", "≥ 1400 m" });

    public const string ReliefBase = "#B5D3A2";
    public const string ThunderStroke = "#C2185B";
    public const string BorderMain = "#202020";
    public const string BorderOther = "#6E6E6E";
}
