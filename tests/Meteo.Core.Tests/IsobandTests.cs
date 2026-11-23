using NetTopologySuite.Geometries;
using Meteo.Core.Geo;
using Meteo.Core.Vector;

namespace Meteo.Core.Tests;

public class IsobandTests
{
    private static readonly GridDefinition G = new(0, 0, 1, 5, 5);

    private static float[] Field(Func<int, int, float> f)
    {
        var v = new float[25];
        for (int j = 0; j < 5; j++) for (int i = 0; i < 5; i++) v[j * 5 + i] = f(i, j);
        return v;
    }

    [Fact]
    public void SinglePeakGivesOneValidPolygon()
    {
        var v = Field((i, j) => i == 2 && j == 2 ? 1f : 0f);
        var g = Isobands.Extract(v, G, 0.5, double.PositiveInfinity);
        Assert.True(g.IsValid);
        Assert.Equal(1, g.NumGeometries);
        // Diamond-ish around (2.5, 2.5): vertices at half distance to neighbours.
        Assert.True(g.Covers(new Point(2.5, 2.5)));
        Assert.False(g.Covers(new Point(1.5, 1.5)));
    }

    [Fact]
    public void RingFieldGivesPolygonWithHole()
    {
        var v = Field((i, j) =>
        {
            if (i == 0 || j == 0 || i == 4 || j == 4) return 0f;
            return i == 2 && j == 2 ? 0f : 1f;
        });
        var g = Isobands.Extract(v, G, 0.5, double.PositiveInfinity);
        Assert.True(g.IsValid);
        var p = Assert.IsType<Polygon>(g.GetGeometryN(0));
        Assert.Equal(1, p.NumInteriorRings);
        Assert.False(g.Covers(new Point(2.5, 2.5)));
        Assert.True(g.Covers(new Point(1.5, 2.5)));
    }

    [Fact]
    public void AdjacentBandsTileWithoutGapsOrOverlaps()
    {
        var rnd = new Random(42);
        var big = new GridDefinition(0, 0, 1, 30, 20);
        var v = new float[30 * 20];
        for (int k = 0; k < v.Length; k++) v[k] = (float)(rnd.NextDouble() * 100);

        var all = Isobands.Extract(v, big, 0, double.PositiveInfinity);
        var a = Isobands.Extract(v, big, 0, 40);
        var b = Isobands.Extract(v, big, 40, 70);
        var c = Isobands.Extract(v, big, 70, double.PositiveInfinity);
        Assert.True(a.IsValid && b.IsValid && c.IsValid);
        Assert.Equal(all.Area, a.Area + b.Area + c.Area, 6);
        Assert.Equal(29 * 19, all.Area, 6);
        Assert.True(a.Intersection(b).Area < 1e-9);
    }

    [Fact]
    public void MissingValuesAreSkipped()
    {
        var v = Field((i, j) => i < 2 ? float.NaN : 1f);
        var g = Isobands.Extract(v, G, 0.5, double.PositiveInfinity);
        Assert.Equal(2 * 4, g.Area, 6);
    }
}
