// GeoPrep: one-off preparation of static geo resources for Meteo.Core.
//  - Czech border from Natural Earth 10m admin_0 countries (public domain)
//  - Relief (hypsometric bands) from Copernicus GLO-90 DEM (© DLR/Airbus, provided under COPERNICUS licence)
// Output (UTM 33N metres, rounded to 1 m) is written to src/Meteo.Core/Resources and embedded into the DLL.
//
// Usage: dotnet run --project tools/Meteo.GeoPrep [-- <output dir>]

using System.Globalization;
using BitMiracle.LibTiff.Classic;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Precision;
using Meteo.Core.Export;
using Meteo.Core.Geo;
using Meteo.Core.Vector;

var outDir = args.Length > 0 ? args[0] : FindRepoResources();
Directory.CreateDirectory(outDir);
var cacheDir = Path.Combine(Path.GetTempPath(), "meteo-geoprep");
Directory.CreateDirectory(cacheDir);
Console.WriteLine($"Output: {outDir}\nCache:  {cacheDir}");

Tiff.SetErrorHandler(new QuietTiffHandler());
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Meteo-GeoPrep/1.0");
var proj = UtmProjection.Zone33;
var reducer = new GeometryPrecisionReducer(new PrecisionModel(1.0));

// ---------------- Border ----------------
var neFile = await Download("https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/geojson/ne_10m_admin_0_countries.geojson",
    Path.Combine(cacheDir, "ne_10m_admin_0_countries.geojson"));
FeatureCollection countries;
using (var fs = File.OpenRead(neFile)) countries = GeoJson.ReadFeatures(fs);

var borderFc = new FeatureCollection();
foreach (var code in new[] { "CZE", "DEU", "POL", "AUT", "SVK" })
{
    var f = countries.FirstOrDefault(x => (x.Attributes["ADM0_A3"] as string) == code)
        ?? throw new InvalidOperationException($"Country {code} not found in Natural Earth data.");
    var projected = ProjectGeometry(f.Geometry, proj);
    // Keep only what is near the forecast grid.
    var g = GridDefinition.CzechRepublic5Km;
    var clip = new GeometryFactory().ToGeometry(new Envelope(g.X0 - 50_000, g.X1 + 50_000, g.Y0 - 50_000, g.Y1 + 50_000));
    var clipped = reducer.Reduce(projected.Intersection(clip));
    borderFc.Add(GeoJson.Feature(clipped, ("code", code), ("main", code == "CZE")));
    Console.WriteLine($"Border {code}: {clipped.NumPoints} points");
}
File.WriteAllText(Path.Combine(outDir, "borders.geojson"), GeoJson.Write(borderFc));

// ---------------- DEM ----------------
var reliefGrid = GridDefinition.CzechRepublic5Km.WithCellSize(500);
var sum = new double[reliefGrid.Nx * reliefGrid.Ny];
var cnt = new int[sum.Length];
var fineGrid = GridDefinition.CzechRepublic5Km.WithCellSize(1000);
var fine = new FineAcc(fineGrid.Nx * fineGrid.Ny);
for (int lat = 48; lat <= 51; lat++)
    for (int lon = 11; lon <= 19; lon++)
    {
        string name = $"Copernicus_DSM_COG_30_N{lat:00}_00_E{lon:000}_00_DEM";
        string url = $"https://copernicus-dem-90m.s3.amazonaws.com/{name}/{name}.tif";
        string file;
        try { file = await Download(url, Path.Combine(cacheDir, name + ".tif")); }
        catch (HttpRequestException ex) { Console.WriteLine($"  skip {name}: {ex.Message}"); continue; }
        AccumulateTile(file, reliefGrid, proj, sum, cnt, fineGrid, fine);
    }

var dem = new float[sum.Length];
for (int k = 0; k < dem.Length; k++) dem[k] = cnt[k] > 0 ? (float)(sum[k] / cnt[k]) : float.NaN;
Console.WriteLine($"DEM grid {reliefGrid.Nx}x{reliefGrid.Ny}, min {dem.Where(float.IsFinite).Min():F0} m, max {dem.Where(float.IsFinite).Max():F0} m");

// ---------------- Relief bands ----------------
double[] levels = { 200, 300, 400, 500, 600, 700, 800, 900, 1000, 1200, 1400 };
var reliefFc = new FeatureCollection();
foreach (var level in levels)
{
    var band = Isobands.Extract(dem, reliefGrid, level, double.PositiveInfinity);
    band = GeometrySmoothing.SmoothAndSimplify(band, 1, 120);
    band = reducer.Reduce(band);
    reliefFc.Add(GeoJson.Feature(band, ("min", level)));
    Console.WriteLine($"Relief >= {level} m: {band.NumGeometries} polygons, {band.NumPoints} points");
}
var reliefPath = Path.Combine(outDir, "relief.geojson");
File.WriteAllText(reliefPath, GeoJson.Write(reliefFc));
Console.WriteLine($"relief.geojson: {new FileInfo(reliefPath).Length / 1024} kB");

// ---------------- Land cover (ESA WorldCover 2021, CC-BY 4.0) ----------------
var classCounts = new Dictionary<int, int>[fineGrid.Nx * fineGrid.Ny];
for (int k = 0; k < classCounts.Length; k++) classCounts[k] = new Dictionary<int, int>(4);
foreach (int tLat in new[] { 48, 51 })
    foreach (int tLon in new[] { 9, 12, 15, 18 })
    {
        string tname = $"ESA_WorldCover_10m_2021_v200_N{tLat:00}E{tLon:000}_Map";
        string file;
        try { file = await Download($"https://esa-worldcover.s3.eu-central-1.amazonaws.com/v200/2021/map/{tname}.tif", Path.Combine(cacheDir, tname + ".tif")); }
        catch (HttpRequestException ex) { Console.WriteLine($"  skip {tname}: {ex.Message}"); continue; }
        AccumulateLandCover(file, tLat, tLon, fineGrid, proj, classCounts);
    }

// ---------------- Surface file ----------------
int nFine = fineGrid.Nx * fineGrid.Ny;
var surface = new SurfaceData
{
    Grid = fineGrid,
    Elevation = new float[nFine], NormalE = new float[nFine], NormalN = new float[nFine], NormalU = new float[nFine],
    Albedo = new float[nFine], Efficiency = new float[nFine], Water = new float[nFine],
};
for (int k = 0; k < nFine; k++)
{
    if (fine.Count[k] > 0)
    {
        surface.Elevation[k] = (float)(fine.Elev[k] / fine.Count[k]);
        surface.NormalE[k] = (float)(fine.NE[k] / fine.Count[k]);
        surface.NormalN[k] = (float)(fine.NN[k] / fine.Count[k]);
        surface.NormalU[k] = (float)(fine.NU[k] / fine.Count[k]);
    }
    else { surface.Elevation[k] = float.NaN; surface.NormalU[k] = 1; }

    int total = classCounts[k].Values.Sum();
    if (total == 0) { var (a0, e0) = SurfaceData.LandCoverProperties(0); surface.Albedo[k] = a0; surface.Efficiency[k] = e0; continue; }
    double alb = 0, eff = 0, water = 0;
    foreach (var (cls, c) in classCounts[k])
    {
        var (a, e) = SurfaceData.LandCoverProperties(cls);
        alb += a * c; eff += e * c;
        if (cls == 80) water += c;
    }
    surface.Albedo[k] = (float)(alb / total);
    surface.Efficiency[k] = (float)(eff / total);
    surface.Water[k] = (float)(water / total);
}
using (var fs = File.Create(Path.Combine(outDir, "surface_1km.bin"))) surface.Write(fs);
Console.WriteLine($"surface_1km.bin: {fineGrid.Nx}x{fineGrid.Ny}, mean efficiency {surface.Efficiency.Average():F2}, " +
                  $"water cells {surface.Water.Count(w => w > 0.5)}, max slope {surface.NormalU.Min():F2} (cos)");
Console.WriteLine("Done.");

// ======================================================================================

async Task<string> Download(string url, string target)
{
    if (File.Exists(target) && new FileInfo(target).Length > 0) return target;
    Console.WriteLine($"Downloading {url}");
    using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
    resp.EnsureSuccessStatusCode();
    var tmp = target + ".part";
    await using (var fs = File.Create(tmp)) await resp.Content.CopyToAsync(fs);
    File.Move(tmp, target, true);
    return target;
}

static Geometry ProjectGeometry(Geometry g, UtmProjection proj)
{
    var copy = g.Copy();
    copy.Apply(new ProjectFilter(proj));
    copy.GeometryChanged();
    return copy.IsValid ? copy : NetTopologySuite.Geometries.Utilities.GeometryFixer.Fix(copy);
}

static void AccumulateTile(string file, GridDefinition grid, UtmProjection proj, double[] sum, int[] cnt, GridDefinition fineGrid, FineAcc fine)
{
    using var tif = Tiff.Open(file, "r") ?? throw new InvalidOperationException($"Cannot open {file}");
    int width = tif.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
    int height = tif.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
    var scale = tif.GetField((TiffTag)33550)[1].ToDoubleArray();   // ModelPixelScale
    var tie = tif.GetField((TiffTag)33922)[1].ToDoubleArray();     // ModelTiepoint (i,j,k,x,y,z)
    double lon0 = tie[3], lat0 = tie[4], dLon = scale[0], dLat = scale[1];
    int bits = tif.GetField(TiffTag.BITSPERSAMPLE)[0].ToInt();
    if (bits != 32) throw new NotSupportedException($"{file}: {bits} bits per sample");

    var raster = new float[width * height];
    if (tif.IsTiled())
    {
        int tw = tif.GetField(TiffTag.TILEWIDTH)[0].ToInt();
        int th = tif.GetField(TiffTag.TILELENGTH)[0].ToInt();
        var buf = new byte[tif.TileSize()];
        for (int ty = 0; ty < height; ty += th)
            for (int tx = 0; tx < width; tx += tw)
            {
                tif.ReadTile(buf, 0, tx, ty, 0, 0);
                for (int y = 0; y < th && ty + y < height; y++)
                    for (int x = 0; x < tw && tx + x < width; x++)
                        raster[(ty + y) * width + tx + x] = BitConverter.ToSingle(buf, (y * tw + x) * 4);
            }
    }
    else
    {
        var buf = new byte[tif.ScanlineSize()];
        for (int y = 0; y < height; y++)
        {
            tif.ReadScanline(buf, y);
            for (int x = 0; x < width; x++) raster[y * width + x] = BitConverter.ToSingle(buf, x * 4);
        }
    }

    // Accumulate each DEM pixel (subsampled by 2 for speed) into the target grid cell (box mean).
    int used = 0;
    for (int y = 0; y < height; y += 2)
    {
        double lat = lat0 - (y + 0.5) * dLat;
        for (int x = 0; x < width; x += 2)
        {
            float v = raster[y * width + x];
            if (!float.IsFinite(v) || v < -500) continue;
            double lon = lon0 + (x + 0.5) * dLon;
            var (px, py) = proj.Forward(lon, lat);
            int i = (int)Math.Floor((px - grid.X0) / grid.CellSize);
            int j = (int)Math.Floor((py - grid.Y0) / grid.CellSize);
            if (i < 0 || j < 0 || i >= grid.Nx || j >= grid.Ny) continue;
            sum[j * grid.Nx + i] += v;
            cnt[j * grid.Nx + i]++;
            used++;

            // Terrain normal from central differences on the ~90 m raster (east, north, up).
            if (x < 1 || y < 1 || x >= width - 1 || y >= height - 1) continue;
            float ve = raster[y * width + x + 1], vw = raster[y * width + x - 1];
            float vn = raster[(y - 1) * width + x], vs = raster[(y + 1) * width + x];
            if (!float.IsFinite(ve) || !float.IsFinite(vw) || !float.IsFinite(vn) || !float.IsFinite(vs)) continue;
            double dx = 2 * dLon * 111_320 * Math.Cos(lat * Math.PI / 180);
            double dy = 2 * dLat * 111_320;
            double gx = (ve - vw) / dx, gy = (vn - vs) / dy;
            double norm = Math.Sqrt(gx * gx + gy * gy + 1);
            int fi = (int)Math.Floor((px - fineGrid.X0) / fineGrid.CellSize);
            int fj = (int)Math.Floor((py - fineGrid.Y0) / fineGrid.CellSize);
            if (fi < 0 || fj < 0 || fi >= fineGrid.Nx || fj >= fineGrid.Ny) continue;
            int fk = fj * fineGrid.Nx + fi;
            fine.NE[fk] += -gx / norm;
            fine.NN[fk] += -gy / norm;
            fine.NU[fk] += 1 / norm;
            fine.Elev[fk] += v;
            fine.Count[fk]++;
        }
    }
    Console.WriteLine($"  {Path.GetFileName(file)} {width}x{height}, {used} samples used");
}

/// <summary>
/// Counts WorldCover classes per fine grid cell. Uses the COG overview closest to ~60 m pixels
/// (the full 10 m raster is not needed for 1 km fractions).
/// </summary>
static void AccumulateLandCover(string file, int tileLat, int tileLon, GridDefinition grid, UtmProjection proj, Dictionary<int, int>[] counts)
{
    using var tif = Tiff.Open(file, "r") ?? throw new InvalidOperationException($"Cannot open {file}");
    // pick the smallest directory (overview) that still has <= ~60 m pixels
    short best = 0; int bestW = tif.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
    for (short d = 1; d < tif.NumberOfDirectories(); d++)
    {
        tif.SetDirectory(d);
        int w = tif.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        if (w >= 3 * 1800 && w < bestW) { best = d; bestW = w; }
    }
    tif.SetDirectory(best);
    int width = tif.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
    int height = tif.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
    double step = 3.0 / width; // tile spans 3 x 3 degrees, north-west corner = (tileLat + 3, tileLon)
    if (!tif.IsTiled()) throw new NotSupportedException($"{file}: expected tiled COG");
    int tw = tif.GetField(TiffTag.TILEWIDTH)[0].ToInt();
    int th = tif.GetField(TiffTag.TILELENGTH)[0].ToInt();
    var buf = new byte[tif.TileSize()];
    long used = 0;
    for (int ty = 0; ty < height; ty += th)
    {
        double latTop = tileLat + 3 - ty * step;
        if (latTop - th * step > 51.5 || latTop < 48.1) continue;
        for (int tx = 0; tx < width; tx += tw)
        {
            double lonLeft = tileLon + tx * step;
            if (lonLeft > 19.6 || lonLeft + tw * step < 11.4) continue;
            tif.ReadTile(buf, 0, tx, ty, 0, 0);
            for (int y = 0; y < th && ty + y < height; y++)
            {
                double lat = tileLat + 3 - (ty + y + 0.5) * step;
                for (int x = 0; x < tw && tx + x < width; x++)
                {
                    int cls = buf[y * tw + x];
                    if (cls == 0) continue;
                    double lon = tileLon + (tx + x + 0.5) * step;
                    var (px, py) = proj.Forward(lon, lat);
                    int i = (int)Math.Floor((px - grid.X0) / grid.CellSize);
                    int j = (int)Math.Floor((py - grid.Y0) / grid.CellSize);
                    if (i < 0 || j < 0 || i >= grid.Nx || j >= grid.Ny) continue;
                    var d = counts[j * grid.Nx + i];
                    d[cls] = d.GetValueOrDefault(cls) + 1;
                    used++;
                }
            }
        }
    }
    Console.WriteLine($"  {Path.GetFileName(file)} overview {width}x{height} (~{step * 111_000:F0} m), {used} samples used");
}


static string FindRepoResources()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Meteo.slnx"))) dir = dir.Parent;
    if (dir == null) throw new InvalidOperationException("Repository root (Meteo.slnx) not found; pass output dir as argument.");
    return Path.Combine(dir.FullName, "src", "Meteo.Core", "Resources");
}

sealed class ProjectFilter(UtmProjection proj) : ICoordinateSequenceFilter
{
    public bool Done => false;
    public bool GeometryChanged => true;
    public void Filter(CoordinateSequence seq, int i)
    {
        var (x, y) = proj.Forward(seq.GetX(i), seq.GetY(i));
        seq.SetX(i, x);
        seq.SetY(i, y);
    }
}

sealed class QuietTiffHandler : TiffErrorHandler
{
    public override void WarningHandler(Tiff tif, string method, string format, params object[] args) { }
    public override void WarningHandlerExt(Tiff tif, object clientData, string method, string format, params object[] args) { }
}

sealed class FineAcc(int n)
{
    public double[] NE = new double[n], NN = new double[n], NU = new double[n], Elev = new double[n];
    public int[] Count = new int[n];
}
