using NetTopologySuite.Geometries;
using NetTopologySuite.Simplify;

namespace Meteo.Core.Vector;

/// <summary>Chaikin smoothing + topology-preserving simplification of polygonal geometries.</summary>
public static class GeometrySmoothing
{
    private static readonly GeometryFactory Factory = new();

    /// <summary>
    /// Smooths rings with Chaikin corner cutting, then simplifies each ring with Douglas–Peucker (map units).
    /// Rings are simplified independently (much faster than a topology-preserving simplifier); callers keep the
    /// tolerance well below the contour spacing (≈ 0.1 grid cell), so neighbouring rings cannot cross.
    /// </summary>
    public static Geometry SmoothAndSimplify(Geometry geom, int chaikinIterations, double simplifyTolerance)
    {
        if (geom.IsEmpty) return geom;
        var g = chaikinIterations > 0 ? Smooth(geom, chaikinIterations) : geom;
        if (simplifyTolerance > 0) g = SimplifyRings(g, simplifyTolerance);
        return g;
    }

    private static Geometry SimplifyRings(Geometry geom, double tol)
    {
        Polygon P(Polygon p) => Factory.CreatePolygon(DpRing(p.Shell, tol),
            Enumerable.Range(0, p.NumInteriorRings).Select(i => DpRing(p.GetInteriorRingN(i), tol)).ToArray());
        return geom switch
        {
            Polygon p => P(p),
            MultiPolygon mp => Factory.CreateMultiPolygon(Enumerable.Range(0, mp.NumGeometries).Select(i => P((Polygon)mp.GetGeometryN(i))).ToArray()),
            _ => geom,
        };
    }

    /// <summary>Douglas–Peucker on a closed ring; keeps the original if it would collapse below 4 points.</summary>
    private static LinearRing DpRing(LineString ring, double tol)
    {
        var c = ring.Coordinates;
        int n = c.Length;
        if (n <= 5) return Factory.CreateLinearRing(c);
        var keep = new bool[n];
        keep[0] = keep[n - 1] = true;
        // split the ring at its farthest point from the start so both halves are proper open polylines
        int far = 1; double best = -1;
        for (int i = 1; i < n - 1; i++) { double d = c[0].Distance(c[i]); if (d > best) { best = d; far = i; } }
        keep[far] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, far)); stack.Push((far, n - 1));
        double tol2 = tol * tol;
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (b - a < 2) continue;
            int idx = -1; double max = tol2;
            for (int i = a + 1; i < b; i++)
            {
                double d = SegDist2(c[i], c[a], c[b]);
                if (d > max) { max = d; idx = i; }
            }
            if (idx < 0) continue;
            keep[idx] = true;
            stack.Push((a, idx)); stack.Push((idx, b));
        }
        var res = new List<Coordinate>();
        for (int i = 0; i < n; i++) if (keep[i]) res.Add(c[i]);
        return res.Count >= 4 ? Factory.CreateLinearRing(res.ToArray()) : Factory.CreateLinearRing(c);
    }

    private static double SegDist2(Coordinate p, Coordinate a, Coordinate b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        double t = len2 > 0 ? Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1) : 0;
        double ex = a.X + t * dx - p.X, ey = a.Y + t * dy - p.Y;
        return ex * ex + ey * ey;
    }

    public static Geometry Smooth(Geometry geom, int iterations)
    {
        switch (geom)
        {
            case Polygon p:
                return SmoothPolygon(p, iterations);
            case MultiPolygon mp:
                var polys = new Polygon[mp.NumGeometries];
                for (int k = 0; k < polys.Length; k++) polys[k] = SmoothPolygon((Polygon)mp.GetGeometryN(k), iterations);
                return Factory.CreateMultiPolygon(polys);
            default:
                return geom;
        }
    }

    private static Polygon SmoothPolygon(Polygon p, int iterations)
    {
        var shell = SmoothRing(p.Shell.Coordinates, iterations);
        var holes = new LinearRing[p.NumInteriorRings];
        for (int k = 0; k < holes.Length; k++) holes[k] = SmoothRing(p.GetInteriorRingN(k).Coordinates, iterations);
        return Factory.CreatePolygon(shell, holes);
    }

    private static LinearRing SmoothRing(Coordinate[] closed, int iterations)
    {
        // Work on the open ring (without the closing point).
        var pts = closed.Take(closed.Length - 1).ToList();
        if (pts.Count < 3) return Factory.CreateLinearRing(closed);
        for (int it = 0; it < iterations; it++)
        {
            var next = new List<Coordinate>(pts.Count * 2);
            for (int k = 0; k < pts.Count; k++)
            {
                var a = pts[k];
                var b = pts[(k + 1) % pts.Count];
                next.Add(new Coordinate(0.75 * a.X + 0.25 * b.X, 0.75 * a.Y + 0.25 * b.Y));
                next.Add(new Coordinate(0.25 * a.X + 0.75 * b.X, 0.25 * a.Y + 0.75 * b.Y));
            }
            pts = next;
        }
        pts.Add(pts[0].Copy());
        return Factory.CreateLinearRing(pts.ToArray());
    }
}
