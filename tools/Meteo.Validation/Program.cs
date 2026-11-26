// Replays the forecast for past days from the ICON-EU archive and compares it with reality:
//   replay     – forecast of the 00 UTC ICON-EU run (archive on source.coop) through the normal Meteo.Core pipeline
//   satellite  – forecast global radiation vs satellite-derived radiation (Open-Meteo satellite archive, EUMETSAT)
//   igc        – downloads flights of the day from cpska.cz (needs a session cookie) and extracts thermals
//   analyze    – thermals vs forecast W*, thermal top / cloud base, end of thermals → CSV + summary
// Usage: dotnet run --project tools/Meteo.Validation -- [all|replay|satellite|igc|analyze] [--out DIR] [--cookie FILE] [yyyy-MM-dd ...]
using System.Globalization;
using Meteo.Validation;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var argList = args.ToList();
string Opt(string name, string def)
{
    int i = argList.IndexOf(name);
    if (i < 0 || i + 1 >= argList.Count) return def;
    var v = argList[i + 1];
    argList.RemoveRange(i, 2);
    return v;
}

string outDir = Opt("--out", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meteo", "validation"));
string? cookieFile = Opt("--cookie", "") is { Length: > 0 } c ? c : null;
int maxFlights = int.Parse(Opt("--flights", "25"));
string command = argList.Count > 0 && !char.IsDigit(argList[0][0]) ? argList[0] : "all";
var days = argList.Where(a => char.IsDigit(a[0])).Select(a => DateOnly.ParseExact(a, "yyyy-MM-dd")).ToList();
if (days.Count == 0)
    days = new[] { "2026-05-09", "2026-05-23", "2026-05-26", "2026-06-14", "2026-06-20", "2026-06-25",
                   "2026-07-09", "2026-07-11", "2026-07-18", "2026-08-05", "2026-08-18", "2026-08-23" }
        .Select(s => DateOnly.ParseExact(s, "yyyy-MM-dd")).ToList();

Directory.CreateDirectory(outDir);
Console.WriteLine($"Výstup: {outDir}");
var ctx = new ValidationContext(outDir);

if (command == "thermals") // diagnostics: thermals of IGC files
{
    foreach (var file in argList.Skip(1))
        foreach (var t in ThermalDetector.Detect(Path.GetFileName(file), IgcReader.Read(File.ReadAllText(file, System.Text.Encoding.Latin1))))
            Console.WriteLine($"{t.StartUtc:HH:mm:ss}–{t.EndUtc:HH:mm:ss}  {t.Lat:0.0000} {t.Lon:0.0000}  {t.BottomAlt:0}→{t.TopAlt:0} m  {t.ClimbRate:0.00} m/s");
    return;
}

foreach (var day in days)
{
    if (command is "all" or "replay") await Replay.RunAsync(ctx, day);
    if (command is "all" or "satellite") await Satellite.RunAsync(ctx, day);
    if (command is "all" or "igc") await Flights.RunAsync(ctx, day, cookieFile, maxFlights);
}
if (command is "all" or "analyze") Analysis.Run(ctx, days);
