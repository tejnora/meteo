using System.Globalization;
using System.Text.Json;
using Meteo.Core.Geo;
using Meteo.Core.Model;

namespace Meteo.Core.Storage;

/// <summary>
/// Disk cache of processed forecasts: one binary file per valid hour plus a JSON index.
/// Layout: {root}/forecast/index.json, {root}/forecast/{yyyyMMddHH}.bin; {root}/static/{model}_hhl_{run}.bin
/// </summary>
public sealed class ForecastStore
{
    private const int Magic = 0x544D4631; // "TMF1"
    private readonly string _root;

    public ForecastStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(ForecastDir);
        Directory.CreateDirectory(StaticDir);
    }

    private string ForecastDir => Path.Combine(_root, "forecast");
    private string StaticDir => Path.Combine(_root, "static");

    private sealed record Index(DateTime CreatedUtc, GridDefinition Grid, int[] WindHeights, List<HourEntry> Hours);
    private sealed record HourEntry(DateTime ValidUtc, string Model, DateTime RunUtc);

    public void Save(ForecastRun run)
    {
        foreach (var f in Directory.EnumerateFiles(ForecastDir, "*.bin")) File.Delete(f);
        foreach (var h in run.Hours)
            WriteFields(Path.Combine(ForecastDir, h.ValidUtc.ToString("yyyyMMddHH", CultureInfo.InvariantCulture) + ".bin"), h.Fields);
        var idx = new Index(run.CreatedUtc, run.Grid, run.WindHeights.ToArray(),
            run.Hours.Select(h => new HourEntry(h.ValidUtc, h.Model, h.RunUtc)).ToList());
        File.WriteAllText(Path.Combine(ForecastDir, "index.json"), JsonSerializer.Serialize(idx, new JsonSerializerOptions { WriteIndented = true }));
    }

    public ForecastRun? Load()
    {
        var idxPath = Path.Combine(ForecastDir, "index.json");
        if (!File.Exists(idxPath)) return null;
        var idx = JsonSerializer.Deserialize<Index>(File.ReadAllText(idxPath));
        if (idx is null) return null;
        var run = new ForecastRun { Grid = idx.Grid, CreatedUtc = idx.CreatedUtc, WindHeights = idx.WindHeights };
        foreach (var e in idx.Hours)
        {
            var path = Path.Combine(ForecastDir, e.ValidUtc.ToString("yyyyMMddHH", CultureInfo.InvariantCulture) + ".bin");
            if (!File.Exists(path)) continue;
            run.Hours.Add(new ForecastHour { ValidUtc = DateTime.SpecifyKind(e.ValidUtc, DateTimeKind.Utc), Model = e.Model, RunUtc = e.RunUtc, Fields = ReadFields(path) });
        }
        return run;
    }

    // ---- static (time-invariant) fields such as resampled HHL --------------------------------

    public Dictionary<string, float[]>? LoadStatic(string key)
    {
        var path = Path.Combine(StaticDir, key + ".bin");
        return File.Exists(path) ? ReadFields(path) : null;
    }

    public void SaveStatic(string key, Dictionary<string, float[]> fields) =>
        WriteFields(Path.Combine(StaticDir, key + ".bin"), fields);

    // ---- binary format -------------------------------------------------------------------------

    private static void WriteFields(string path, Dictionary<string, float[]> fields)
    {
        var tmp = path + ".tmp";
        using (var bw = new BinaryWriter(File.Create(tmp)))
        {
            bw.Write(Magic);
            bw.Write(fields.Count);
            foreach (var (name, values) in fields)
            {
                bw.Write(name);
                bw.Write(values.Length);
                var bytes = new byte[values.Length * sizeof(float)];
                Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
                bw.Write(bytes);
            }
        }
        File.Move(tmp, path, true);
    }

    private static Dictionary<string, float[]> ReadFields(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        if (br.ReadInt32() != Magic) throw new InvalidDataException($"Not a Meteo field file: {path}");
        int n = br.ReadInt32();
        var res = new Dictionary<string, float[]>(n);
        for (int k = 0; k < n; k++)
        {
            var name = br.ReadString();
            int len = br.ReadInt32();
            var bytes = br.ReadBytes(len * sizeof(float));
            var values = new float[len];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            res[name] = values;
        }
        return res;
    }
}
