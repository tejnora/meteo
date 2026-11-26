using Meteo.Core.Dwd;
using Meteo.Core.Export;
using Meteo.Core.Geo;
using Meteo.Core.Model;
using Meteo.Core.Processing;
using Meteo.Core.Storage;
using Meteo.Core.Vector;

namespace Meteo.Core.Tests;

public class WindInterpolationTests
{
    // Two cells: ground 0 m and ground 800 m. Levels: 3 (lowest, 100 m above ground), 2 (1100 m AMSL), 1 (2100 m AMSL).
    private static readonly int[] Levels = { 1, 2, 3 };
    private static readonly Dictionary<int, float[]> Z = new() { [1] = new[] { 2100f, 2100f }, [2] = new[] { 1100f, 1100f }, [3] = new[] { 100f, 900f } };
    private static readonly Dictionary<int, float[]> U = new() { [1] = new[] { 20f, 20f }, [2] = new[] { 10f, 10f }, [3] = new[] { 0f, 4f } };
    private static readonly float[] Ground = { 0f, 800f };

    private static (float[] U, float[] V) At(int h) =>
        WindInterpolation.ToHeight(h, Levels, l => Z[l], l => U[l], l => U[l], Ground);

    [Fact]
    public void InterpolatesLinearlyBetweenLevels()
    {
        var (u, _) = At(600);
        Assert.Equal(5f, u[0], 3);          // halfway between 100 m (0) and 1100 m (10)
        Assert.True(float.IsNaN(u[1]));     // 600 m is below the 800 m terrain
        Assert.Equal(15f, At(1600).U[0], 3);
    }

    [Fact]
    public void UsesLowestLevelBetweenGroundAndFirstLevel()
    {
        var (u, _) = At(850);
        Assert.Equal(4f, u[1], 3); // ground 800, lowest level 900 m
    }

    [Fact]
    public void AboveTopLevelUsesTopLevel()
    {
        Assert.Equal(20f, At(3000).U[0], 3);
    }
}

public class ScheduleTests
{
    private static ForecastBuilder Builder(string dir, int days) =>
        new(new ForecastOptions { CacheDirectory = dir, Days = days }, new DwdOpenDataClient(), new ForecastStore(dir));

    private static ForecastBuilder Builder(string dir) => Builder(dir, 3);

    [Fact]
    public void FiveDaysGiveSixtyWantedHours()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var t = Builder(dir, 5).WantedValidTimes(new DateTime(2026, 11, 22, 20, 0, 0, DateTimeKind.Utc));
        Assert.Equal(60, t.Count);
        Assert.Equal(new DateTime(2026, 11, 27, 18, 0, 0, DateTimeKind.Utc), t[^1]); // Fri 19:00 CET
    }

    [Fact]
    public void IconEuIsThreeHourlyAfter78Hours()
    {
        var eu = IconModel.IconEu;
        Assert.True(eu.HasLead(78));
        Assert.False(eu.HasLead(79));
        Assert.True(eu.HasLead(81));
        Assert.True(eu.HasLead(120));
        Assert.False(eu.HasLead(123));
        Assert.Equal(77, eu.PreviousLead(78));
        Assert.Equal(78, eu.PreviousLead(81));
        Assert.False(IconModel.IconD2.HasLead(49));
    }

    [Fact]
    public void ArchiveUrlsUseDynamicalLayout()
    {
        var eu = IconModel.IconEu.FromArchive("https://data.source.coop/dynamical/dwd-icon-grib/");
        var run = new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal("https://data.source.coop/dynamical/dwd-icon-grib/icon-eu/regular-lat-lon/2026-07-11T00/t_2m/" +
                     "icon-eu_europe_regular-lat-lon_single-level_2026071100_012_T_2M.grib2.bz2", eu.SingleLevelUrl(run, 12, "t_2m"));
        Assert.Equal("https://data.source.coop/dynamical/dwd-icon-grib/icon-eu/regular-lat-lon/2026-07-11T00/hhl/" +
                     "icon-eu_europe_regular-lat-lon_time-invariant_2026071100_61_HHL.grib2.bz2", eu.TimeInvariantUrl(run, 61, "hhl"));
        Assert.StartsWith("https://opendata.dwd.de/weather/nwp/icon-eu/grib/00/t_2m/", IconModel.IconEu.SingleLevelUrl(run, 12, "t_2m"));
    }

    [Fact]
    public void EveningStartsTomorrowWithThreeFullDays()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var now = new DateTime(2026, 11, 22, 20, 0, 0, DateTimeKind.Utc); // 21:00 CET
        var t = Builder(dir).WantedValidTimes(now);
        Assert.Equal(36, t.Count);
        Assert.Equal(new DateTime(2026, 11, 23, 7, 0, 0, DateTimeKind.Utc), t[0]);  // 08:00 CET
        Assert.Equal(new DateTime(2026, 11, 25, 18, 0, 0, DateTimeKind.Utc), t[^1]); // 19:00 CET
    }

    [Fact]
    public void MorningIncludesRestOfToday()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var now = new DateTime(2026, 7, 10, 9, 30, 0, DateTimeKind.Utc); // 11:30 CEST
        var t = Builder(dir).WantedValidTimes(now);
        Assert.Equal(new DateTime(2026, 7, 10, 9, 0, 0, DateTimeKind.Utc), t[0]); // 11:00 CEST (current hour kept)
        Assert.Equal(new DateTime(2026, 7, 12, 17, 0, 0, DateTimeKind.Utc), t[^1]); // 19:00 CEST on day 3
    }

    [Fact]
    public void HandlesDaylightSavingSwitch()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var now = new DateTime(2026, 3, 28, 20, 0, 0, DateTimeKind.Utc); // Sat evening, DST starts Sun 29.3.
        var t = Builder(dir).WantedValidTimes(now);
        Assert.Equal(new DateTime(2026, 3, 29, 6, 0, 0, DateTimeKind.Utc), t[0]); // 08:00 CEST
        Assert.Equal(36, t.Count);
    }
}

public class StoreAndExportTests
{
    [Fact]
    public void ForecastRoundTripsThroughStore()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var store = new ForecastStore(dir);
        var grid = new GridDefinition(0, 0, 1000, 3, 2);
        var run = new ForecastRun { Grid = grid, CreatedUtc = DateTime.UtcNow, WindHeights = new[] { 1000 } };
        run.Hours.Add(new ForecastHour
        {
            ValidUtc = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc), Model = "ICON-D2", RunUtc = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            Fields = { [FieldNames.CloudLow] = new[] { 1f, 2f, float.NaN, 4f, 5f, 6f } },
        });
        store.Save(run);

        var loaded = store.Load()!;
        Assert.Equal(grid, loaded.Grid);
        var h = Assert.Single(loaded.Hours);
        Assert.Equal(DateTimeKind.Utc, h.ValidUtc.Kind);
        Assert.Equal(run.Hours[0].Fields[FieldNames.CloudLow], h.Fields[FieldNames.CloudLow]);
    }

    [Fact]
    public void LayerExportsAsWgs84GeoJson()
    {
        var grid = GridDefinition.CzechRepublic5Km;
        var hour = new ForecastHour { ValidUtc = DateTime.UtcNow, Model = "test", RunUtc = DateTime.UtcNow };
        var n = grid.Nx * grid.Ny;
        var u = new float[n]; var v = new float[n];
        for (int k = 0; k < n; k++) { u[k] = (k % grid.Nx) / 10f; v[k] = -3; }
        hour.Fields[FieldNames.WindU10] = u;
        hour.Fields[FieldNames.WindV10] = v;

        var layer = new LayerBuilder(grid).Build(LayerKind.Wind(0), hour, "test");
        Assert.NotEmpty(layer.Polygons);
        Assert.NotEmpty(layer.Arrows);
        Assert.All(layer.Arrows, a => Assert.InRange(a.FromDirection, 270, 360)); // wind from N/NW quadrant

        var fc = GeoJsonExporter.ToFeatureCollection(layer);
        var env = new NetTopologySuite.Geometries.Envelope();
        foreach (var f in fc) env.ExpandToInclude(f.Geometry.EnvelopeInternal);
        Assert.InRange(env.MinX, 11, 13);
        Assert.InRange(env.MaxX, 18.5, 20);
        Assert.InRange(env.MinY, 48, 49);
        Assert.InRange(env.MaxY, 51, 51.5);
    }

    [Fact]
    public void EmbeddedGeoResourcesLoad()
    {
        var relief = GeoResources.Relief(GridDefinition.CzechRepublic5Km);
        Assert.True(relief.Polygons.Count > 5);
        var cz = GeoResources.CzechRepublic;
        Assert.InRange(cz.Area / 1e6, 76_000, 82_000); // CZ area 78 871 km² (UTM scale distortion < 0.1 %)
        Assert.True(GeoResources.Borders().Lines.Count >= 5);
    }
}
