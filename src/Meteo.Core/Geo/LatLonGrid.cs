using Meteo.Core.Grib;

namespace Meteo.Core.Geo;

/// <summary>Regular lat/lon source grid with bilinear sampling.</summary>
public sealed class LatLonGrid
{
    public int Ni { get; }
    public int Nj { get; }
    public double Lat0 { get; }   // latitude of row 0
    public double Lon0 { get; }   // longitude of column 0
    public double DLat { get; }   // signed step between rows
    public double DLon { get; }
    public float[] Values { get; }

    public LatLonGrid(int ni, int nj, double lat0, double lon0, double dLat, double dLon, float[] values)
    {
        Ni = ni; Nj = nj; Lat0 = lat0; Lon0 = lon0; DLat = dLat; DLon = dLon; Values = values;
    }

    public static LatLonGrid FromGrib(Grib2Field f)
    {
        // Scan mode bit 0x40: rows go south -> north. Bit 0x80 (-i) and 0x20 (column-major) are not used by ICON.
        if ((f.ScanMode & 0x80) != 0 || (f.ScanMode & 0x20) != 0)
            throw new NotSupportedException($"GRIB scan mode {f.ScanMode} not supported.");
        double dLat = (f.ScanMode & 0x40) != 0 ? f.Dj : -f.Dj;
        return new LatLonGrid(f.Ni, f.Nj, f.Lat1, f.Lon1, dLat, f.Di, f.Values);
    }

    /// <summary>Bilinear sample; NaN outside the grid or when a corner is missing.</summary>
    public float Sample(double lon, double lat)
    {
        double fi = (lon - Lon0) / DLon;
        double fj = (lat - Lat0) / DLat;
        int i0 = (int)Math.Floor(fi), j0 = (int)Math.Floor(fj);
        if (i0 < 0 || j0 < 0 || i0 >= Ni || j0 >= Nj) return float.NaN;
        int i1 = Math.Min(i0 + 1, Ni - 1), j1 = Math.Min(j0 + 1, Nj - 1);
        double tx = fi - i0, ty = fj - j0;
        float v00 = Values[j0 * Ni + i0], v10 = Values[j0 * Ni + i1];
        float v01 = Values[j1 * Ni + i0], v11 = Values[j1 * Ni + i1];
        return (float)((1 - ty) * ((1 - tx) * v00 + tx * v10) + ty * ((1 - tx) * v01 + tx * v11));
    }
}
