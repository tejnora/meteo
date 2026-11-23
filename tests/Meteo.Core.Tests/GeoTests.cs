using Meteo.Core.Geo;

namespace Meteo.Core.Tests;

public class GeoTests
{
    [Fact]
    public void UtmOnCentralMeridianEqualsScaledMeridianArc()
    {
        // WGS84 meridian arc length from equator to 50° N is 5 540 847.04 m; UTM scales it by k0 = 0.9996.
        var (x, y) = UtmProjection.Zone33.Forward(15.0, 50.0);
        Assert.Equal(500_000.0, x, 3);
        Assert.InRange(y, 0.9996 * 5_540_847.04 - 1, 0.9996 * 5_540_847.04 + 1);
    }

    [Fact]
    public void UtmIsSymmetricAroundCentralMeridian()
    {
        var (xw, yw) = UtmProjection.Zone33.Forward(12.0, 49.5);
        var (xe, ye) = UtmProjection.Zone33.Forward(18.0, 49.5);
        Assert.Equal(1_000_000.0, xw + xe, 3);
        Assert.Equal(yw, ye, 3);
    }

    [Fact]
    public void UtmRoundTrip()
    {
        var p = UtmProjection.Zone33;
        foreach (var (lon, lat) in new[] { (12.1, 48.5), (18.9, 51.1), (15.0, 50.0), (14.42, 50.09) })
        {
            var (x, y) = p.Forward(lon, lat);
            var (lon2, lat2) = p.Inverse(x, y);
            Assert.Equal(lon, lon2, 7);
            Assert.Equal(lat, lat2, 7);
        }
    }

    [Fact]
    public void GridCoversCzechRepublicExtremes()
    {
        var g = GridDefinition.CzechRepublic5Km;
        var p = UtmProjection.Zone33;
        // westernmost, easternmost, southernmost, northernmost points of CZ (approx.)
        foreach (var (lon, lat) in new[] { (12.09, 50.25), (18.86, 49.52), (14.33, 48.55), (14.31, 51.06) })
        {
            var (x, y) = p.Forward(lon, lat);
            Assert.InRange(x, g.X0 + 5_000, g.X1 - 5_000);
            Assert.InRange(y, g.Y0 + 5_000, g.Y1 - 5_000);
        }
    }

    [Fact]
    public void BilinearSampleInterpolates()
    {
        var src = new LatLonGrid(2, 2, 50, 14, 1, 1, new float[] { 0, 10, 20, 30 });
        Assert.Equal(15f, src.Sample(14.5, 50.5), 3);
        Assert.Equal(10f, src.Sample(15, 50), 3);
        Assert.True(float.IsNaN(src.Sample(13, 50)));
    }
}
