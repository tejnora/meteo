using Meteo.Core.Geo;

namespace Meteo.Core.Model;

/// <summary>Names of the gridded fields stored for each forecast hour.</summary>
public static class FieldNames
{
    public const string CloudLow = "clcl";     // %
    public const string CloudMid = "clcm";     // %
    public const string CloudHigh = "clch";    // %
    public const string Precip = "prec";       // mm/h (previous hour)
    public const string ShowerPrecip = "shower"; // mm/h where weather is convective (showers / thunder)
    public const string Thunder = "thunder";   // 0..1 fraction of sub-samples with thunderstorm
    public const string WindU10 = "u10";       // m/s, map (UTM grid) oriented
    public const string WindV10 = "v10";
    public const string Temperature2m = "t2m"; // °C
    public const string Dewpoint2m = "td2";    // °C
    public const string RadDirect = "swdir";   // W/m², direct shortwave on horizontal surface, mean of previous hour
    public const string RadDiffuse = "swdif";  // W/m², diffuse shortwave, mean of previous hour
    public const string SensibleHeat = "shf";  // W/m², upward positive, mean of previous hour
    public const string MixingHeight = "mh";   // m above ground
    public const string Cape = "cape";         // J/kg (mixed layer)
    public const string DryConvTop = "htopdc"; // m AMSL, top of dry convection (NaN = none)
    public const string CuBase = "cubase";     // m AMSL, base of shallow convective cloud (NaN = none)
    public const string CuFraction = "cufrac"; // 0..1 share of the cell with convective cloud base
    public const string ModelTerrain = "hsurf"; // m AMSL, model orography

    /// <summary>Wind component at a height above mean sea level, map oriented (m/s). NaN below terrain.</summary>
    public static string WindU(int heightAmsl) => $"u{heightAmsl}m";
    public static string WindV(int heightAmsl) => $"v{heightAmsl}m";
}

public sealed class ForecastOptions
{
    public GridDefinition Grid { get; init; } = GridDefinition.CzechRepublic5Km;
    /// <summary>Heights (m AMSL) at which wind is interpolated.</summary>
    public IReadOnlyList<int> WindHeights { get; init; } = new[] { 500, 1000, 1500, 2000, 2500, 3000 };
    /// <summary>First and last local hour of the soaring day (inclusive).</summary>
    public int DayStartHour { get; init; } = 8;
    public int DayEndHour { get; init; } = 19;
    /// <summary>Number of days (days 4–5 come from ICON-EU in 3-hour steps).</summary>
    public int Days { get; init; } = 5;
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
    public int MaxParallelDownloads { get; init; } = 8;
    /// <summary>
    /// Minimum mean vertical spacing (m) between model levels used for wind interpolation.
    /// 0 = use all adjacent levels (most accurate, roughly twice the download volume).
    /// </summary>
    public double MinLevelSpacing { get; init; } = 250;
    /// <summary>Folder for processed 5 km fields (not raw GRIB).</summary>
    public string CacheDirectory { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meteo", "cache");
}

/// <summary>All fields of one valid hour on the forecast grid.</summary>
public sealed class ForecastHour
{
    public required DateTime ValidUtc { get; init; }
    public required string Model { get; init; }
    public required DateTime RunUtc { get; init; }
    public Dictionary<string, float[]> Fields { get; init; } = new();

    public float[]? Get(string name) => Fields.TryGetValue(name, out var f) ? f : null;

    /// <summary>Averaging step of radiation / fluxes / precipitation: 3 h for ICON-EU beyond +78 h, otherwise 1 h.</summary>
    public int StepHours => Model == "ICON-EU" && (ValidUtc - RunUtc).TotalHours > 78 ? 3 : 1;
}

public sealed class ForecastRun
{
    public required GridDefinition Grid { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public required IReadOnlyList<int> WindHeights { get; init; }
    public List<ForecastHour> Hours { get; init; } = new();
}

public sealed record ForecastProgress(string Message, int Done, int Total, long BytesDownloaded);
