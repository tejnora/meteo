namespace Meteo.Core.Geo;

/// <summary>
/// Resamples lat/lon fields onto a projected <see cref="GridDefinition"/>.
/// Lon/lat of target cells (and of 3x3 sub-samples) and the local true-north/east directions are cached.
/// </summary>
public sealed class Resampler
{
    private const int Sub = 3;
    private readonly double[] _lon, _lat;          // cell centres
    private readonly double[] _subLon, _subLat;    // Sub x Sub samples per cell
    private readonly double[] _eastX, _eastY, _northX, _northY; // unit vectors of true east/north in map coords

    public Resampler(GridDefinition grid, UtmProjection? projection = null)
    {
        Grid = grid;
        var proj = projection ?? UtmProjection.Zone33;
        int n = grid.Nx * grid.Ny;
        _lon = new double[n]; _lat = new double[n];
        _subLon = new double[n * Sub * Sub]; _subLat = new double[n * Sub * Sub];
        _eastX = new double[n]; _eastY = new double[n]; _northX = new double[n]; _northY = new double[n];

        for (int j = 0; j < grid.Ny; j++)
            for (int i = 0; i < grid.Nx; i++)
            {
                int k = j * grid.Nx + i;
                double cx = grid.CenterX(i), cy = grid.CenterY(j);
                var (lon, lat) = proj.Inverse(cx, cy);
                _lon[k] = lon; _lat[k] = lat;

                for (int sj = 0; sj < Sub; sj++)
                    for (int si = 0; si < Sub; si++)
                    {
                        double x = cx + ((si + 0.5) / Sub - 0.5) * grid.CellSize;
                        double y = cy + ((sj + 0.5) / Sub - 0.5) * grid.CellSize;
                        var (slon, slat) = proj.Inverse(x, y);
                        int s = k * Sub * Sub + sj * Sub + si;
                        _subLon[s] = slon; _subLat[s] = slat;
                    }

                const double d = 0.01;
                var (ex, ey) = proj.Forward(lon + d, lat);
                var (nx, ny) = proj.Forward(lon, lat + d);
                double el = Math.Sqrt((ex - cx) * (ex - cx) + (ey - cy) * (ey - cy));
                double nl = Math.Sqrt((nx - cx) * (nx - cx) + (ny - cy) * (ny - cy));
                _eastX[k] = (ex - cx) / el; _eastY[k] = (ey - cy) / el;
                _northX[k] = (nx - cx) / nl; _northY[k] = (ny - cy) / nl;
            }
    }

    public GridDefinition Grid { get; }

    /// <summary>Bilinear sample at cell centres.</summary>
    public float[] Resample(LatLonGrid src)
    {
        var dst = new float[_lon.Length];
        for (int k = 0; k < dst.Length; k++) dst[k] = src.Sample(_lon[k], _lat[k]);
        return dst;
    }

    /// <summary>Mean of 3x3 bilinear sub-samples per cell (area average; better for precipitation).</summary>
    public float[] ResampleMean(LatLonGrid src)
    {
        var dst = new float[_lon.Length];
        for (int k = 0; k < dst.Length; k++)
        {
            double s = 0; int n = 0;
            for (int q = 0; q < Sub * Sub; q++)
            {
                float v = src.Sample(_subLon[k * Sub * Sub + q], _subLat[k * Sub * Sub + q]);
                if (!float.IsNaN(v)) { s += v; n++; }
            }
            dst[k] = n > 0 ? (float)(s / n) : float.NaN;
        }
        return dst;
    }

    /// <summary>Fraction (0..1) of 3x3 nearest-neighbour sub-samples whose value satisfies the predicate (categorical fields).</summary>
    public float[] ResampleFraction(LatLonGrid src, Func<float, bool> predicate)
    {
        var dst = new float[_lon.Length];
        for (int k = 0; k < dst.Length; k++)
        {
            int hit = 0, n = 0;
            for (int q = 0; q < Sub * Sub; q++)
            {
                float v = SampleNearest(src, _subLon[k * Sub * Sub + q], _subLat[k * Sub * Sub + q]);
                if (float.IsNaN(v)) continue;
                n++;
                if (predicate(v)) hit++;
            }
            dst[k] = n > 0 ? (float)hit / n : float.NaN;
        }
        return dst;
    }

    /// <summary>Mean of the 3x3 nearest-neighbour sub-samples that satisfy the predicate (NaN when none does).</summary>
    public float[] ResampleMeanWhere(LatLonGrid src, Func<float, bool> predicate)
    {
        var dst = new float[_lon.Length];
        for (int k = 0; k < dst.Length; k++)
        {
            double s = 0; int n = 0;
            for (int q = 0; q < Sub * Sub; q++)
            {
                float v = SampleNearest(src, _subLon[k * Sub * Sub + q], _subLat[k * Sub * Sub + q]);
                if (!float.IsNaN(v) && predicate(v)) { s += v; n++; }
            }
            dst[k] = n > 0 ? (float)(s / n) : float.NaN;
        }
        return dst;
    }

    /// <summary>Rotates earth-relative (east/north) wind components into map (grid x/y) components, in place.</summary>
    public void RotateToMap(float[] u, float[] v)
    {
        for (int k = 0; k < u.Length; k++)
        {
            double ue = u[k], vn = v[k];
            u[k] = (float)(ue * _eastX[k] + vn * _northX[k]);
            v[k] = (float)(ue * _eastY[k] + vn * _northY[k]);
        }
    }

    private static float SampleNearest(LatLonGrid g, double lon, double lat)
    {
        int i = (int)Math.Round((lon - g.Lon0) / g.DLon);
        int j = (int)Math.Round((lat - g.Lat0) / g.DLat);
        if (i < 0 || j < 0 || i >= g.Ni || j >= g.Nj) return float.NaN;
        return g.Values[j * g.Ni + i];
    }
}
