using System.Globalization;

namespace Meteo.Core.Dwd;

/// <summary>URL conventions of a DWD ICON open-data product (regular lat/lon files).</summary>
public sealed class IconModel
{
    public required string Name { get; init; }
    /// <summary>Directory under https://opendata.dwd.de/weather/nwp/, e.g. "icon-d2".</summary>
    public required string Directory { get; init; }
    /// <summary>File prefix, e.g. "icon-d2_germany".</summary>
    public required string Prefix { get; init; }
    public required bool UpperCaseVariables { get; init; }
    /// <summary>Hours between runs (D2: 3, EU: 6 for long runs).</summary>
    public required int RunInterval { get; init; }
    /// <summary>Maximum lead time with hourly output.</summary>
    public required int MaxHourlyLead { get; init; }
    /// <summary>Maximum lead time; between <see cref="MaxHourlyLead"/> and this the output is 3-hourly.</summary>
    public required int MaxLead { get; init; }

    /// <summary>True when the model publishes output for this lead time.</summary>
    public bool HasLead(int lead) => lead >= 1 && (lead <= MaxHourlyLead || (lead <= MaxLead && lead % 3 == 0));

    /// <summary>The previous published step (for de-accumulating time averages / sums).</summary>
    public int PreviousLead(int lead) => lead <= MaxHourlyLead ? lead - 1 : lead - 3;

    /// <summary>Single-level files of ICON-D2 contain a "2d" level token.</summary>
    public required bool SingleLevelToken { get; init; }
    /// <summary>ICON-D2 time-invariant files contain a "000" step token.</summary>
    public required bool TimeInvariantStepToken { get; init; }

    /// <summary>Index of the lowest HHL half level (= ground); used when the directory cannot be listed.</summary>
    public required int SurfaceHalfLevel { get; init; }

    /// <summary>
    /// Root of an archive with the dynamical.org layout (<c>{root}/{model}/regular-lat-lon/{yyyy-MM-ddTHH}/{variable}/</c>),
    /// e.g. https://data.source.coop/dynamical/dwd-icon-grib. Null = DWD open data (last ~24 h only).
    /// </summary>
    public string? ArchiveRoot { get; init; }

    /// <summary>The same model read from an archive of DWD files instead of the open-data server.</summary>
    public IconModel FromArchive(string root) => new()
    {
        Name = Name, Directory = Directory, Prefix = Prefix, UpperCaseVariables = UpperCaseVariables, RunInterval = RunInterval,
        MaxHourlyLead = MaxHourlyLead, MaxLead = MaxLead, SingleLevelToken = SingleLevelToken,
        TimeInvariantStepToken = TimeInvariantStepToken, SurfaceHalfLevel = SurfaceHalfLevel, ArchiveRoot = root.TrimEnd('/'),
    };

    /// <summary>Variable with the base of (shallow) convective clouds: D2 "hbas_sc", EU "hbas_con".</summary>
    public string CloudBaseVariable => Directory == "icon-d2" ? "hbas_sc" : "hbas_con";

    public const string BaseUrl = "https://opendata.dwd.de/weather/nwp";

    public static IconModel IconD2 { get; } = new()
    {
        Name = "ICON-D2", Directory = "icon-d2", Prefix = "icon-d2_germany", UpperCaseVariables = false,
        RunInterval = 3, MaxHourlyLead = 48, MaxLead = 48, SingleLevelToken = true, TimeInvariantStepToken = true, SurfaceHalfLevel = 66,
    };

    public static IconModel IconEu { get; } = new()
    {
        Name = "ICON-EU", Directory = "icon-eu", Prefix = "icon-eu_europe", UpperCaseVariables = true,
        RunInterval = 6, MaxHourlyLead = 78, MaxLead = 120, SingleLevelToken = false, TimeInvariantStepToken = false, SurfaceHalfLevel = 75,
    };

    private string V(string variable) => UpperCaseVariables ? variable.ToUpperInvariant() : variable.ToLowerInvariant();
    private static string Run(DateTime runUtc) => runUtc.ToString("yyyyMMddHH", CultureInfo.InvariantCulture);
    private string Dir(DateTime runUtc, string variable) => ArchiveRoot is null
        ? $"{BaseUrl}/{Directory}/grib/{runUtc:HH}/{variable.ToLowerInvariant()}"
        : $"{ArchiveRoot}/{Directory}/regular-lat-lon/{runUtc.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture)}/{variable.ToLowerInvariant()}";

    public string VariableDirectoryUrl(DateTime runUtc, string variable) => Dir(runUtc, variable) + "/";

    public string SingleLevelUrl(DateTime runUtc, int lead, string variable) =>
        $"{Dir(runUtc, variable)}/{Prefix}_regular-lat-lon_single-level_{Run(runUtc)}_{lead:000}_{(SingleLevelToken ? "2d_" : "")}{V(variable)}.grib2.bz2";

    public string ModelLevelUrl(DateTime runUtc, int lead, int level, string variable) =>
        $"{Dir(runUtc, variable)}/{Prefix}_regular-lat-lon_model-level_{Run(runUtc)}_{lead:000}_{level}_{V(variable)}.grib2.bz2";

    public string TimeInvariantUrl(DateTime runUtc, int level, string variable) =>
        $"{Dir(runUtc, variable)}/{Prefix}_regular-lat-lon_time-invariant_{Run(runUtc)}_{(TimeInvariantStepToken ? "000_" : "")}{level}_{V(variable)}.grib2.bz2";

    public override string ToString() => Name;
}
