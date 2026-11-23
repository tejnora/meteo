using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Nts = NetTopologySuite.Geometries;

namespace Meteo.Viewer.Map;

/// <summary>Converts NetTopologySuite geometries (world metres) to frozen WPF geometries, with caching.</summary>
internal static class NtsToWpf
{
    private static readonly ConditionalWeakTable<Nts.Geometry, StreamGeometry> Cache = new();

    public static StreamGeometry Convert(Nts.Geometry g) => Cache.GetValue(g, Build);

    private static StreamGeometry Build(Nts.Geometry g)
    {
        var sg = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var ctx = sg.Open()) Add(ctx, g);
        sg.Freeze();
        return sg;
    }

    private static void Add(StreamGeometryContext ctx, Nts.Geometry g)
    {
        switch (g)
        {
            case Nts.Polygon p:
                AddRing(ctx, p.ExteriorRing.Coordinates, closed: true);
                for (int i = 0; i < p.NumInteriorRings; i++) AddRing(ctx, p.GetInteriorRingN(i).Coordinates, closed: true);
                break;
            case Nts.LineString ls:
                AddRing(ctx, ls.Coordinates, closed: ls.IsClosed && ls is Nts.LinearRing);
                break;
            case Nts.GeometryCollection gc:
                for (int i = 0; i < gc.NumGeometries; i++) Add(ctx, gc.GetGeometryN(i));
                break;
        }
    }

    private static void AddRing(StreamGeometryContext ctx, Nts.Coordinate[] coords, bool closed)
    {
        if (coords.Length < 2) return;
        ctx.BeginFigure(new Point(coords[0].X, coords[0].Y), isFilled: closed, isClosed: closed);
        var pts = new Point[coords.Length - 1];
        for (int i = 1; i < coords.Length; i++) pts[i - 1] = new Point(coords[i].X, coords[i].Y);
        ctx.PolyLineTo(pts, isStroked: true, isSmoothJoin: false);
    }

    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();

    public static SolidColorBrush Brush(string hex)
    {
        lock (Brushes)
        {
            if (!Brushes.TryGetValue(hex, out var b))
            {
                b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                b.Freeze();
                Brushes[hex] = b;
            }
            return b;
        }
    }
}
