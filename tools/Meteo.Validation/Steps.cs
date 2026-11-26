using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meteo.Core;
using Meteo.Core.Dwd;
using Meteo.Core.Geo;
using Meteo.Core.Model;
using Meteo.Core.Processing;
using Meteo.Core.Storage;

namespace Meteo.Validation;

public sealed class ValidationContext(string outDir)
{
    public const string ArchiveRoot = "https://data.source.coop/dynamical/dwd-icon-grib";
    public string OutDir { get; } = outDir;
    public string PathFor(params string[] parts) { var p = Path.Combine([OutDir, .. parts]); Directory.CreateDirectory(Path.GetDirectoryName(p)!); return p; }

    public static DateTime RunOf(DateOnly day) => day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    /// <summary>Forecast of the day as available in the morning: ICON-EU 00 UTC run (ICON-D2 is not archived).</summary>
    public ForecastOptions Options(DateOnly day) => new()
    {
        IconD2 = null,
        IconEu = IconModel.IconEu.FromArchive(ArchiveRoot),
        IconEuRun = RunOf(day),
        Days = 1,
        CacheDirectory = Path.Combine(OutDir, "forecast", day.ToString("yyyy-MM-dd")),
    };

    private readonly Dictionary<DateOnly, ForecastService> _services = new();
    public ForecastService? Service(DateOnly day)
    {
        if (_services.TryGetValue(day, out var s)) return s;
        s = new ForecastService(Options(day));
        if (!s.LoadCached()) { s.Dispose(); return null; }
        return _services[day] = s;
    }

    public static readonly HttpClient Http = CreateHttp();
    private static HttpClient CreateHttp()
    {
        var h = new HttpClient(new HttpClientHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromMinutes(2) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Meteo validation)");
        return h;
    }
}

public static class Replay
{
    public static async Task RunAsync(ValidationContext ctx, DateOnly day)
    {
        var opt = ctx.Options(day);
        var store = new ForecastStore(opt.CacheDirectory);
        if (store.Load() is { Hours.Count: >= 12 }) { Console.WriteLine($"{day}: předpověď už je v cache"); return; }

        using var client = new DwdOpenDataClient(8);
        var run = ValidationContext.RunOf(day);
        var builder = new ForecastBuilder(opt, client, store, () => run.AddHours(4.5)); // as published that morning
        int lastPct = -1;
        var progress = new Progress<ForecastProgress>(p =>
        {
            int pct = p.Total > 0 ? 100 * p.Done / p.Total : 0;
            if (pct / 10 != lastPct / 10) { lastPct = pct; Console.WriteLine($"{day}: {pct,3} %  {p.Message}  ({p.BytesDownloaded / 1_048_576} MB)"); }
        });
        var r = await builder.BuildAsync(progress, CancellationToken.None);
        Console.WriteLine($"{day}: hotovo, {r.Hours.Count} hodin ({r.Hours[0].Model} běh {r.Hours[0].RunUtc:yyyy-MM-dd HH}Z)");
    }
}

public static class Satellite
{
    /// <summary>Every 3rd cell of the 5 km grid inside the Czech Republic (~15 km spacing).</summary>
    public static List<(int Cell, double Lat, double Lon)> Points()
    {
        var g = GridDefinition.CzechRepublic5Km;
        var cz = GeoResources.CzechRepublic;
        var gf = new NetTopologySuite.Geometries.GeometryFactory();
        var res = new List<(int, double, double)>();
        for (int j = 1; j < g.Ny; j += 3)
            for (int i = 1; i < g.Nx; i += 3)
            {
                if (!cz.Contains(gf.CreatePoint(new NetTopologySuite.Geometries.Coordinate(g.CenterX(i), g.CenterY(j))))) continue;
                var (lon, lat) = UtmProjection.Zone33.Inverse(g.CenterX(i), g.CenterY(j));
                res.Add((j * g.Nx + i, lat, lon));
            }
        return res;
    }

    public static async Task RunAsync(ValidationContext ctx, DateOnly day)
    {
        var file = ctx.PathFor("satellite", $"{day:yyyy-MM-dd}.csv");
        if (File.Exists(file)) { Console.WriteLine($"{day}: satelit už je stažený"); return; }
        var svc = ctx.Service(day);
        if (svc is null) { Console.WriteLine($"{day}: chybí předpověď, satelit přeskočen"); return; }

        var pts = Points();
        var sb = new StringBuilder("valid_utc,cell,lat,lon,fc_ghi,fc_direct,fc_diffuse,sat_ghi,sat_direct,sat_diffuse,fc_clcl,fc_clcm,fc_clch\n");
        foreach (var batch in pts.Chunk(60))
        {
            string F(Func<(int, double Lat, double Lon), double> sel) => string.Join(",", batch.Select(p => sel(p).ToString("0.0000")));
            var url = "https://satellite-api.open-meteo.com/v1/archive?latitude=" + F(p => p.Lat) + "&longitude=" + F(p => p.Lon) +
                      $"&start_date={day:yyyy-MM-dd}&end_date={day:yyyy-MM-dd}&hourly=shortwave_radiation,direct_radiation,diffuse_radiation" +
                      "&models=satellite_radiation_seamless&timezone=GMT";
            using var doc = JsonDocument.Parse(await GetWithRetry(url));
            var locs = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
            for (int k = 0; k < batch.Length; k++)
            {
                var hourly = locs[k].GetProperty("hourly");
                var times = hourly.GetProperty("time").EnumerateArray().Select(t => DateTime.SpecifyKind(DateTime.Parse(t.GetString()!, CultureInfo.InvariantCulture), DateTimeKind.Utc)).ToList();
                double V(string name, int idx) => hourly.GetProperty(name)[idx].ValueKind == JsonValueKind.Number ? hourly.GetProperty(name)[idx].GetDouble() : double.NaN;
                foreach (var hour in svc.Current!.Hours)
                {
                    // both are means of the preceding hour, labelled with the end of the hour
                    int idx = times.IndexOf(hour.ValidUtc);
                    if (idx < 0) continue;
                    int cell = batch[k].Item1;
                    float Fc(string name) => hour.Get(name) is { } a ? a[cell] : float.NaN;
                    double dir = Fc(FieldNames.RadDirect), dif = Fc(FieldNames.RadDiffuse);
                    sb.Append(CultureInfo.InvariantCulture, $"{hour.ValidUtc:yyyy-MM-ddTHH:mm},{cell},{batch[k].Lat:0.0000},{batch[k].Lon:0.0000},{dir + dif:0.0},{dir:0.0},{dif:0.0},");
                    sb.Append(CultureInfo.InvariantCulture, $"{V("shortwave_radiation", idx):0.0},{V("direct_radiation", idx):0.0},{V("diffuse_radiation", idx):0.0},");
                    sb.Append(CultureInfo.InvariantCulture, $"{Fc(FieldNames.CloudLow):0},{Fc(FieldNames.CloudMid):0},{Fc(FieldNames.CloudHigh):0}\n");
                }
            }
            await Task.Delay(7000); // Open-Meteo counts each location as a call (600 / min)
        }
        File.WriteAllText(file, sb.ToString());
        Console.WriteLine($"{day}: satelit {pts.Count} bodů");
    }

    internal static async Task<string> GetWithRetry(string url)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return await ValidationContext.Http.GetStringAsync(url); }
            catch (HttpRequestException e) when (attempt < 6)
            {
                await Task.Delay(e.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? 65_000 : 5000 * attempt);
            }
        }
    }
}

public static partial class Flights
{
    private const string Base = "https://www.cpska.cz/public/index3.php";

    public sealed record Listed(string Id, double Km);

    /// <summary>cpska.cz allows 20 downloads per 24 h; once hit, no further requests are made in this run.</summary>
    public static bool LimitReached { get; private set; }

    /// <summary>Public daily list of flights (distance, flight id).</summary>
    public static async Task<List<Listed>> ListAsync(ValidationContext ctx, DateOnly day)
    {
        var file = ctx.PathFor("cpska", $"{day:yyyy-MM-dd}.html");
        if (!File.Exists(file))
        {
            var bytes = await ValidationContext.Http.GetByteArrayAsync($"{Base}?lpg=sezlet&obdobi_rok={day.Year}&obdobi_mesic={day.Month}&obdobi_den={day.Day}");
            File.WriteAllBytes(file, bytes);
        }
        var html = File.ReadAllText(file, Encoding.Latin1);
        var res = new List<Listed>();
        foreach (var row in html.Split("<tr"))
        {
            var km = KmRegex().Match(row); var id = IdRegex().Match(row);
            if (km.Success && id.Success) res.Add(new Listed(id.Groups[1].Value, double.Parse(km.Groups[1].Value, CultureInfo.InvariantCulture)));
        }
        return res.DistinctBy(l => l.Id).ToList();
    }

    public static async Task RunAsync(ValidationContext ctx, DateOnly day, string? cookieFile, int maxFlights)
    {
        var dir = Path.GetDirectoryName(ctx.PathFor("igc", $"{day:yyyy-MM-dd}", "x"))!;
        var listed = await ListAsync(ctx, day);
        // a spread over the ranking (not only the best pilots), flights ≥ 50 km
        var cand = listed.Where(l => l.Km >= 50).OrderByDescending(l => l.Km).ToList();
        var pick = cand.Count <= maxFlights ? cand : Enumerable.Range(0, maxFlights).Select(k => cand[k * cand.Count / maxFlights]).ToList();

        string? cookie = cookieFile != null && File.Exists(cookieFile) && !LimitReached ? File.ReadAllText(cookieFile).Trim() : null;
        int downloaded = 0, have = 0;
        foreach (var f in pick)
        {
            var path = Path.Combine(dir, f.Id + ".igc");
            if (File.Exists(path)) { have++; continue; }
            if (cookie is null) continue;
            var bytes = await GetAsync($"{Base}?lpg=zobraz_let&let_id={f.Id}&rezim=stahnout", cookie);
            var text = ExtractIgc(bytes);
            // logged in: the page links a temporary copy of the log (../temp_igc/xxx.igc)
            if (text is null && TempIgcRegex().Match(Encoding.Latin1.GetString(bytes)) is { Success: true } link)
            {
                await Task.Delay(500);
                text = ExtractIgc(await GetAsync(new Uri(new Uri(Base), link.Groups[1].Value).ToString(), cookie));
            }
            if (text is null)
            {
                File.WriteAllBytes(Path.Combine(dir, f.Id + ".response.html"), bytes);
                var s = Encoding.Latin1.GetString(bytes);
                if (s.Contains("omezeno na", StringComparison.Ordinal))
                {
                    File.Delete(Path.Combine(dir, f.Id + ".response.html"));
                    Console.WriteLine($"{day}: cpska.cz – vyčerpán limit stahování (20 letů / 24 h), pokračuj zítra");
                    LimitReached = true;
                    break;
                }
                if (s.Contains("pouze p", StringComparison.Ordinal) && s.Contains("ihl", StringComparison.Ordinal))
                    throw new InvalidOperationException("cpska.cz: stahování IGC odmítnuto – cookie není platná (přihlas se znovu a obnov soubor s cookie).");
                Console.WriteLine($"{day}: let {f.Id} – odpověď není IGC (uloženo .response.html)");
                continue;
            }
            File.WriteAllText(path, text);
            downloaded++;
            await Task.Delay(1000); // be gentle with the server
        }
        Console.WriteLine($"{day}: lety ≥ 50 km {cand.Count}, vybráno {pick.Count}, IGC staženo {downloaded}, už bylo {have}" + (cookie is null ? " (bez cookie se nestahuje)" : ""));

        // thermals of all available logs of the day
        var sb = new StringBuilder("flight,start_utc,end_utc,lat,lon,bottom,top,climb\n");
        int nThermals = 0;
        foreach (var igc in Directory.EnumerateFiles(dir, "*.igc"))
        {
            var id = Path.GetFileNameWithoutExtension(igc);
            var fixes = IgcReader.Read(File.ReadAllText(igc, Encoding.Latin1));
            foreach (var t in ThermalDetector.Detect(id, fixes))
            {
                sb.Append(CultureInfo.InvariantCulture, $"{id},{t.StartUtc:yyyy-MM-ddTHH:mm:ss},{t.EndUtc:yyyy-MM-ddTHH:mm:ss},{t.Lat:0.00000},{t.Lon:0.00000},{t.BottomAlt:0},{t.TopAlt:0},{t.ClimbRate:0.00}\n");
                nThermals++;
            }
        }
        File.WriteAllText(ctx.PathFor("thermals", $"{day:yyyy-MM-dd}.csv"), sb.ToString());
        if (nThermals > 0) Console.WriteLine($"{day}: {nThermals} stoupání");
    }

    /// <summary>The response may be the IGC text itself or a zip containing it.</summary>
    private static string? ExtractIgc(byte[] bytes)
    {
        if (bytes.Length > 4 && bytes[0] == 'P' && bytes[1] == 'K')
        {
            using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
            var e = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".igc", StringComparison.OrdinalIgnoreCase));
            if (e is null) return null;
            using var r = new StreamReader(e.Open(), Encoding.Latin1);
            return r.ReadToEnd();
        }
        var s = Encoding.Latin1.GetString(bytes);
        return s.TrimStart().StartsWith('A') && s.Contains("\nB", StringComparison.Ordinal) ? s : null;
    }

    [GeneratedRegex(@"cllNumeric"">([\d.]+)&#8239;<span[^>]*>km<")]
    private static partial Regex KmRegex();
    private static async Task<byte[]> GetAsync(string url, string cookie)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Cookie", cookie);
        using var resp = await ValidationContext.Http.SendAsync(req);
        return await resp.Content.ReadAsByteArrayAsync();
    }

    [GeneratedRegex(@"href=""(\.\./temp_igc/[^""]+\.igc)""")]
    private static partial Regex TempIgcRegex();
    [GeneratedRegex(@"let_id=(\d+)")]
    private static partial Regex IdRegex();
}
