using Meteo.Core.Dwd;
using Meteo.Core.Grib;

namespace Meteo.Core.Tests;

public class GribTests
{
    [Fact]
    public void DecodesIconD2LowCloudCover()
    {
        using var f = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestData", "icon-d2_clcl_sample.grib2.bz2"));
        var raw = Bz2.Decompress(f);
        var fields = Grib2Reader.Read(raw);
        var fld = Assert.Single(fields);
        Assert.Equal(fld.Ni * fld.Nj, fld.Values.Length);
        Assert.InRange(fld.Lat1, 43, 44);
        Assert.InRange(fld.Lon1, -4.5, -3.5);
        Assert.InRange(fld.Di, 0.019, 0.021);
        var valid = fld.Values.Where(v => !float.IsNaN(v)).ToArray();
        Assert.NotEmpty(valid);
        Assert.InRange(valid.Min(), -0.5, 100.5);
        Assert.InRange(valid.Max(), -0.5, 100.5);
        Assert.Equal(12, fld.ForecastHours);
    }
}

public class CcsdsGribTests
{
    private static Grib2Field Load(string name)
    {
        using var f = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestData", name));
        return Assert.Single(Grib2Reader.Read(Bz2.Decompress(f)));
    }

    [Fact]
    public void DecodesIconEuWeatherCodes()
    {
        var fld = Load("icon-eu_ww_sample.grib2.bz2");
        Assert.Equal(fld.Ni * fld.Nj, fld.Values.Length);
        Assert.All(fld.Values, v =>
        {
            Assert.InRange(v, -0.01f, 99.01f);
            Assert.Equal(Math.Round(v), v, 2); // WW codes are integers
        });
        Assert.Contains(fld.Values, v => v > 0);
    }

    [Fact]
    public void DecodesIconEuTerrainHeightAsSmoothField()
    {
        // lowest half level of HHL = terrain height
        var fld = Load("icon-eu_hsurf_sample.grib2.bz2");
        Assert.InRange(fld.Values.Min(), -450, 10);        // Dead Sea (-430 m) is inside the ICON-EU domain
        Assert.InRange(fld.Values.Max(), 2500, 4900);      // Alps / Caucasus at 6.5 km resolution
        // Real terrain at 6.5 km almost never jumps by more than 1000 m between neighbours (a broken decoder gives noise).
        int big = 0;
        for (int i = 1; i < fld.Values.Length; i++) if (i % fld.Ni != 0 && Math.Abs(fld.Values[i] - fld.Values[i - 1]) > 1000) big++;
        Assert.True(big < 20, $"{big} large jumps");
        // Sněžka region (50.7N, 15.7E) should be mountains, Prague basin lower.
        var g = Meteo.Core.Geo.LatLonGrid.FromGrib(fld);
        Assert.InRange(g.Sample(15.74, 50.74), 700, 1700);
        Assert.InRange(g.Sample(14.42, 50.09), 150, 450);
    }
}
