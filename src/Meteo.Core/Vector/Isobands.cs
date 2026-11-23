using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using Meteo.Core.Geo;

namespace Meteo.Core.Vector;

/// <summary>
/// Extracts filled contour polygons (isobands) from a scalar field sampled at grid cell centres.
/// Every grid cell is split into two triangles; inside a triangle the field is linear, so the band region
/// is the triangle clipped by the half-planes v &gt;= lo and v &lt; hi. Edges shared by two neighbouring pieces
/// cancel out, the remaining edges are chained into rings. Vertex identity is topological (node or
/// edge+level), so neighbouring pieces match exactly and the result has no gaps or slivers.
/// </summary>
public static class Isobands
{
    private static readonly GeometryFactory Factory = new();

    /// <summary>Region where lo &lt;= v &lt; hi (use +inf for hi to get the cumulative region v &gt;= lo).</summary>
    public static Geometry Extract(float[] values, GridDefinition grid, double lo, double hi)
    {
        if (values.Length != grid.Nx * grid.Ny) throw new ArgumentException("Value count does not match grid.");
        var levels = new[] { lo, hi };
        var v = Perturb(values, levels);

        var edges = new Dictionary<(VKey From, VKey To), int>();
        var pts = new Dictionary<VKey, Coordinate>();
        int nx = grid.Nx;

        for (int j = 0; j < grid.Ny - 1; j++)
            for (int i = 0; i < nx - 1; i++)
            {
                int a = j * nx + i, b = a + 1, c = a + nx + 1, d = a + nx;
                if (float.IsNaN(v[a]) || float.IsNaN(v[b]) || float.IsNaN(v[c]) || float.IsNaN(v[d])) continue;
                ClipTriangle(a, b, c, v, lo, hi, edges);
                ClipTriangle(a, c, d, v, lo, hi, edges);
            }

        // Remaining directed edges form the boundary rings.
        var outgoing = new Dictionary<VKey, List<VKey>>();
        foreach (var e in edges.Keys)
        {
            if (!outgoing.TryGetValue(e.From, out var list)) outgoing[e.From] = list = new List<VKey>(1);
            list.Add(e.To);
        }

        var rings = new List<Coordinate[]>();
        while (outgoing.Count > 0)
        {
            var start = outgoing.Keys.First();
            var ring = new List<Coordinate>();
            var cur = start;
            while (true)
            {
                ring.Add(Position(cur, grid, v, levels, pts));
                if (!outgoing.TryGetValue(cur, out var nexts)) break; // open chain (should not happen)
                var next = nexts[^1];
                nexts.RemoveAt(nexts.Count - 1);
                if (nexts.Count == 0) outgoing.Remove(cur);
                cur = next;
                if (cur.Equals(start)) break;
            }
            var cleaned = RemoveDuplicates(ring);
            if (cleaned.Count >= 3)
            {
                cleaned.Add(cleaned[0].Copy());
                rings.Add(cleaned.ToArray());
            }
        }
        return Assemble(rings);
    }

    /// <summary>Cumulative regions v &gt;= level for each level (stack them in ascending order when drawing).</summary>
    public static IReadOnlyList<Geometry> ExtractCumulative(float[] values, GridDefinition grid, IReadOnlyList<double> levels)
        => levels.Select(l => Extract(values, grid, l, double.PositiveInfinity)).ToList();

    // ---- triangle clipping -------------------------------------------------------------------

    /// <summary>Vertex identity: either a grid node (N1 == N2, Level = -1) or a point on edge N1-N2 at level index.</summary>
    private readonly record struct VKey(int N1, int N2, int Level)
    {
        public static VKey Node(int n) => new(n, n, -1);
        public static VKey OnEdge(int a, int b, int level) => a < b ? new(a, b, level) : new(b, a, level);
        public bool IsNode => Level < 0;
    }

    private static void ClipTriangle(int a, int b, int c, float[] v, double lo, double hi, Dictionary<(VKey, VKey), int> edges)
    {
        if (v[a] < lo && v[b] < lo && v[c] < lo) return;
        if (v[a] >= hi && v[b] >= hi && v[c] >= hi) return;

        var poly = new List<VKey>(6) { VKey.Node(a), VKey.Node(b), VKey.Node(c) };
        poly = ClipHalfPlane(poly, v, lo, 0, above: true, lo, hi);
        if (poly.Count < 3) return;
        if (!double.IsPositiveInfinity(hi))
        {
            poly = ClipHalfPlane(poly, v, hi, 1, above: false, lo, hi);
            if (poly.Count < 3) return;
        }

        for (int k = 0; k < poly.Count; k++)
        {
            var from = poly[k];
            var to = poly[(k + 1) % poly.Count];
            if (from.Equals(to)) continue;
            if (!edges.Remove((to, from))) edges[(from, to)] = 0;
        }
    }

    private static double ValueOf(VKey k, float[] v, double lo, double hi) =>
        k.IsNode ? v[k.N1] : (k.Level == 0 ? lo : hi);

    /// <summary>Sutherland–Hodgman clip of a convex polygon against v &gt;= level (above) or v &lt; level.</summary>
    private static List<VKey> ClipHalfPlane(List<VKey> poly, float[] v, double level, int levelIndex, bool above, double lo, double hi)
    {
        var res = new List<VKey>(poly.Count + 2);
        for (int k = 0; k < poly.Count; k++)
        {
            var cur = poly[k];
            var nxt = poly[(k + 1) % poly.Count];
            double vc = ValueOf(cur, v, lo, hi), vn = ValueOf(nxt, v, lo, hi);
            bool inC = above ? vc >= level : vc < level;
            bool inN = above ? vn >= level : vn < level;
            if (inC) res.Add(cur);
            if (inC != inN)
            {
                var (e1, e2) = CommonEdge(cur, nxt);
                res.Add(VKey.OnEdge(e1, e2, levelIndex));
            }
        }
        return res;
    }

    /// <summary>The original grid edge on which the segment cur-nxt lies (a crossing only happens on such segments).</summary>
    private static (int, int) CommonEdge(VKey p, VKey q)
    {
        if (p.IsNode && q.IsNode) return (p.N1, q.N1);
        if (p.IsNode) return (q.N1, q.N2);
        if (q.IsNode) return (p.N1, p.N2);
        return (p.N1, p.N2); // both on the same edge
    }

    private static Coordinate Position(VKey k, GridDefinition g, float[] v, double[] levels, Dictionary<VKey, Coordinate> cache)
    {
        if (cache.TryGetValue(k, out var c)) return c;
        int nx = g.Nx;
        if (k.IsNode)
        {
            c = new Coordinate(g.CenterX(k.N1 % nx), g.CenterY(k.N1 / nx));
        }
        else
        {
            double va = v[k.N1], vb = v[k.N2];
            double t = (levels[k.Level] - va) / (vb - va);
            t = Math.Clamp(t, 0, 1);
            double xa = g.CenterX(k.N1 % nx), ya = g.CenterY(k.N1 / nx);
            double xb = g.CenterX(k.N2 % nx), yb = g.CenterY(k.N2 / nx);
            c = new Coordinate(xa + t * (xb - xa), ya + t * (yb - ya));
        }
        cache[k] = c;
        return c;
    }

    // ---- assembly ------------------------------------------------------------------------------

    private static float[] Perturb(float[] values, double[] levels)
    {
        // Values exactly on a level create zero-length edges; nudge them slightly upwards.
        var v = (float[])values.Clone();
        for (int k = 0; k < v.Length; k++)
            foreach (var l in levels)
                if (v[k] == l) v[k] = (float)(l + Math.Max(1e-4, Math.Abs(l) * 1e-6));
        return v;
    }

    private static List<Coordinate> RemoveDuplicates(List<Coordinate> ring)
    {
        var res = new List<Coordinate>(ring.Count);
        foreach (var c in ring)
            if (res.Count == 0 || !res[^1].Equals2D(c)) res.Add(c);
        while (res.Count > 1 && res[0].Equals2D(res[^1])) res.RemoveAt(res.Count - 1);
        return res;
    }

    private static Geometry Assemble(List<Coordinate[]> rings)
    {
        var shells = new List<(LinearRing Ring, Polygon Poly, double Area, List<LinearRing> Holes)>();
        var holes = new List<LinearRing>();
        foreach (var r in rings)
        {
            var lr = Factory.CreateLinearRing(r);
            double area = Area.OfRingSigned(r); // NTS: negative for CCW
            if (area < 0) shells.Add((lr, Factory.CreatePolygon(lr), -area, new List<LinearRing>()));
            else if (area > 0) holes.Add(lr);
        }
        shells.Sort((x, y) => x.Area.CompareTo(y.Area));

        // Assign each hole to the smallest shell containing it. Spatial index on shell envelopes + indexed
        // point-in-polygon locators keep this fast for noisy fields with hundreds of holes.
        var index = new NetTopologySuite.Index.Strtree.STRtree<int>();
        for (int s = 0; s < shells.Count; s++) index.Insert(shells[s].Ring.EnvelopeInternal, s);
        var locators = new NetTopologySuite.Algorithm.Locate.IndexedPointInAreaLocator?[shells.Count];
        foreach (var h in holes)
        {
            var env = h.EnvelopeInternal;
            var p = h.Coordinates[0];
            foreach (int s in index.Query(env).Where(s => shells[s].Ring.EnvelopeInternal.Contains(env)).OrderBy(s => s))
            {
                locators[s] ??= new NetTopologySuite.Algorithm.Locate.IndexedPointInAreaLocator(shells[s].Poly);
                if (locators[s]!.Locate(p) != Location.Exterior)
                {
                    shells[s].Holes.Add(h);
                    break;
                }
            }
        }
        var polys = shells.Select(s => Factory.CreatePolygon(s.Ring, s.Holes.ToArray())).ToArray();
        return Factory.CreateMultiPolygon(polys);
    }
}
