using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meteo.Core;
using Meteo.Core.Geo;
using Meteo.Core.Model;
using Meteo.Core.Vector;

namespace Meteo.Viewer;

public sealed record DayItem(DateOnly Date, string Label);
public sealed record LayerItem(LayerKind Kind, string Name, string Group);

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly CultureInfo Cz = CultureInfo.GetCultureInfo("cs-CZ");
    private readonly ForecastService _svc = new(CreateOptions());

    /// <summary>Optional command line: --cache <folder> (e.g. for testing with a separate forecast).</summary>
    private static ForecastOptions CreateOptions()
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--cache");
        return i >= 0 && i + 1 < args.Length ? new ForecastOptions { CacheDirectory = args[i + 1] } : new ForecastOptions();
    }
    private CancellationTokenSource? _updateCts;
    private int _layerRequest;

    public MainViewModel()
    {
        var g = _svc.Options.Grid;
        WorldExtent = new Rect(g.X0, g.Y0, g.X1 - g.X0, g.Y1 - g.Y0);
        BorderLayer = _svc.GetBorders();
        _reliefLayer = _svc.GetRelief();
        foreach (var k in _svc.AvailableLayers) Layers.Add(ToItem(k));
        _selectedLayer = Layers[0];

        // optional command line: --igc <file> shows a flight over the forecast
        var args = Environment.GetCommandLineArgs();
        int ig = Array.IndexOf(args, "--igc");
        if (ig >= 0 && ig + 1 < args.Length)
        {
            try { _flight = FlightTrack.Load(args[ig + 1]); }
            catch (Exception ex) { Status = "IGC nelze načíst: " + ex.Message; }
        }

        if (_svc.LoadCached()) LoadDays();
        else Status = "Žádná uložená předpověď – klikněte na „Aktualizovat“.";
    }

    // ---- collections & selection ---------------------------------------------------------------------

    public ObservableCollection<DayItem> Days { get; } = new();
    public ObservableCollection<LayerItem> Layers { get; } = new();
    public ObservableCollection<LegendItem> Legend { get; } = new();

    [ObservableProperty] private DayItem? _selectedDay;
    [ObservableProperty] private IReadOnlyList<int> _hours = Array.Empty<int>();
    [ObservableProperty] private int _hourIndex;
    [ObservableProperty] private LayerItem _selectedLayer;

    partial void OnSelectedDayChanged(DayItem? value)
    {
        int prevHour = CurrentHour ?? 12;
        Hours = value is null ? Array.Empty<int>() : _svc.GetHours(value.Date);
        // raise the slider maximum first so the new index is not clamped by the old range
        OnPropertyChanged(nameof(HourMax));
        OnPropertyChanged(nameof(HourInfo));
        int idx;
        if (_pendingHourIndex is { } p) idx = p < 0 ? Hours.Count - 1 : Math.Min(p, Hours.Count - 1); // ←/→ across days
        else
        {
            // keep the same local hour when switching days if possible
            idx = Hours.ToList().IndexOf(prevHour);
            if (idx < 0) idx = Math.Min(Hours.Count / 2, Math.Max(0, Hours.Count - 1));
        }
        _pendingHourIndex = null;
        HourIndex = Math.Max(0, idx);
        OnPropertyChanged(nameof(HourLabel));
        RefreshLayer();
    }

    partial void OnHourIndexChanged(int value)
    {
        OnPropertyChanged(nameof(HourLabel));
        RefreshLayer();
    }

    partial void OnSelectedLayerChanged(LayerItem value) => RefreshLayer();

    public int HourMax => Math.Max(0, Hours.Count - 1);
    public int? CurrentHour => HourIndex >= 0 && HourIndex < Hours.Count ? Hours[HourIndex] : null;
    public string HourLabel => CurrentHour is { } h ? $"{h:00}:00" : "–";
    public string HourInfo => Hours.Count switch
    {
        0 => "Pro tento den nejsou data.",
        1 => $"V datech je jen {Hours[0]:00}:00 – pro všechny hodiny klikněte na „Aktualizovat předpověď“.",
        _ => Hours.Zip(Hours.Skip(1)).Any(p => p.Second - p.First > 1)
            ? $"{Hours.Count} termíny po 3 h: {string.Join(", ", Hours.Select(h => $"{h}:00"))} (ICON-EU, dál než 3 dny)"
            : $"{Hours.Count} hodin: {Hours[0]:00}:00–{Hours[^1]:00}:00",
    };

    // ---- map layers --------------------------------------------------------------------------------------

    public Rect WorldExtent { get; }
    public VectorLayer BorderLayer { get; }
    private readonly VectorLayer _reliefLayer;

    [ObservableProperty] private VectorLayer? _meteoLayer;
    [ObservableProperty] private VectorLayer? _trackLayer;
    private readonly FlightTrack? _flight;

    /// <summary>Flight track with the part flown within ±30 min of the shown hour highlighted.</summary>
    private void RefreshTrack()
    {
        if (_flight is null || SelectedDay is null || CurrentHour is null) { TrackLayer = null; return; }
        var local = SelectedDay.Date.ToDateTime(new TimeOnly(CurrentHour.Value, 0), DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, _svc.Options.TimeZone);
        TrackLayer = _flight.ToLayer(utc.AddMinutes(-30), utc.AddMinutes(30));
    }
    [ObservableProperty] private bool _showRelief = true;
    [ObservableProperty] private bool _showBorders = true;
    [ObservableProperty] private bool _showArrows = true;
    [ObservableProperty] private string _title = "Meteo ČR";
    [ObservableProperty] private string _subtitle = "";

    public VectorLayer? ReliefLayer => ShowRelief ? _reliefLayer : null;
    public VectorLayer? VisibleBorders => ShowBorders ? BorderLayer : null;
    partial void OnShowReliefChanged(bool value) => OnPropertyChanged(nameof(ReliefLayer));
    partial void OnShowBordersChanged(bool value) => OnPropertyChanged(nameof(VisibleBorders));

    private async void RefreshLayer()
    {
        RefreshTrack();
        if (SelectedDay is null || CurrentHour is null) { MeteoLayer = null; return; }
        var (kind, day, hour) = (SelectedLayer.Kind, SelectedDay.Date, CurrentHour.Value);
        int req = Interlocked.Increment(ref _layerRequest);
        try
        {
            var layer = await Task.Run(() => _svc.GetLayer(kind, day, hour));
            if (req != _layerRequest) return; // a newer request superseded this one
            MeteoLayer = layer;
            Title = layer?.Title ?? kind.DisplayName;
            Subtitle = layer?.Subtitle ?? "";
            Legend.Clear();
            if (layer != null) foreach (var l in layer.Legend) Legend.Add(l);
            UpdatePointerInfo();
            Prefetch(kind, day);
        }
        catch (Exception ex)
        {
            Status = "Chyba vrstvy: " + ex.Message;
        }
    }

    /// <summary>Builds the other hours of the selected layer in the background so the slider is smooth.</summary>
    private void Prefetch(LayerKind kind, DateOnly day)
    {
        var hours = Hours.ToArray();
        Task.Run(() => { foreach (var h in hours) _svc.GetLayer(kind, day, h); });
    }

    // ---- pointer readout --------------------------------------------------------------------------------

    [ObservableProperty] private Point? _pointerWorld;
    [ObservableProperty] private string _pointerInfo = "";
    partial void OnPointerWorldChanged(Point? value) => UpdatePointerInfo();

    private void UpdatePointerInfo()
    {
        if (PointerWorld is not { } p) { PointerInfo = ""; return; }
        var (lon, lat) = UtmProjection.Zone33.Inverse(p.X, p.Y);
        var pos = string.Format(CultureInfo.InvariantCulture, "{0:0.000}° N  {1:0.000}° E", lat, lon);
        var value = SelectedDay is null || CurrentHour is null ? null
            : _svc.DescribePoint(SelectedLayer.Kind, SelectedDay.Date, CurrentHour.Value, p.X, p.Y);
        PointerInfo = value is null ? pos : $"{pos}   ·   {value}";
        if (_flight?.Nearest(p.X, p.Y, 1500) is { } fx)
        {
            var t = TimeZoneInfo.ConvertTimeFromUtc(fx.Utc, _svc.Options.TimeZone);
            PointerInfo += $"{Environment.NewLine}let {_flight.Name}: {t:HH:mm} · {fx.Alt:0} m · vario {fx.Vario:+0.0;-0.0} m/s";
        }
    }

    // ---- update ------------------------------------------------------------------------------------------

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _updateStep = "";
    [ObservableProperty] private string _updateDetail = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(UpdateCommand), nameof(CancelCommand))] private bool _isBusy;

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateAsync()
    {
        IsBusy = true;
        _updateCts = new CancellationTokenSource();
        UpdateStep = "Připojuji se k DWD…";
        UpdateDetail = "";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double startDone = -1, startSec = 0;
        var progress = new Progress<ForecastProgress>(p =>
        {
            Progress = p.Total > 1 ? 100.0 * p.Done / p.Total : 0;
            UpdateStep = p.Message;
            var detail = $"Staženo {p.BytesDownloaded / 1_048_576} MB";
            if (p.Total > 1)
            {
                detail += $" · soubor {p.Done} z {p.Total}";
                // ETA from the download rate since the first data file
                if (startDone < 0 && p.Done > 0) { startDone = p.Done; startSec = sw.Elapsed.TotalSeconds; }
                double rate = startDone >= 0 ? (p.Done - startDone) / Math.Max(1, sw.Elapsed.TotalSeconds - startSec) : 0;
                if (rate > 0 && p.Done - startDone >= 10)
                    detail += $" · zbývá asi {Math.Ceiling((p.Total - p.Done) / rate / 60)} min";
            }
            UpdateDetail = detail;
            Status = $"{p.Message}  ({p.BytesDownloaded / 1_048_576} MB)";
        });
        try
        {
            await Task.Run(() => _svc.UpdateAsync(progress, _updateCts.Token));
            LoadDays();
        }
        catch (OperationCanceledException) { Status = "Aktualizace zrušena."; }
        catch (Exception ex) { Status = "Chyba: " + ex.Message; }
        finally
        {
            IsBusy = false;
            _updateCts.Dispose();
            _updateCts = null;
        }
    }

    private bool CanUpdate() => !IsBusy;

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _updateCts?.Cancel();

    private void LoadDays()
    {
        var prev = SelectedDay?.Date;
        Days.Clear();
        foreach (var d in _svc.AvailableDays)
            Days.Add(new DayItem(d, d.ToString("ddd d. M.", Cz)));
        SelectedDay = Days.FirstOrDefault(d => d.Date == prev)
            ?? (_flight is null ? null : Days.FirstOrDefault(d => d.Date == _flight.DateUtc))
            ?? Days.FirstOrDefault();
        var run = _svc.Current;
        if (run != null)
        {
            var models = string.Join(", ", run.Hours.GroupBy(h => (h.Model, h.RunUtc)).Select(g => $"{g.Key.Model} {g.Key.RunUtc:dd.MM. HH} UTC"));
            Status = $"Předpověď z {run.CreatedUtc.ToLocalTime():d. M. HH:mm} · {models}";
        }
    }

    // ---- navigation / export ----------------------------------------------------------------------------

    [RelayCommand]
    private void NextHour()
    {
        if (HourIndex < HourMax) { HourIndex++; return; }
        // past the last hour → first hour of the next day
        int d = SelectedDay is null ? -1 : Days.IndexOf(SelectedDay);
        if (d >= 0 && d < Days.Count - 1) { _pendingHourIndex = 0; SelectedDay = Days[d + 1]; }
    }

    private int? _pendingHourIndex; // hour index to select after a day change (-1 = last)

    [RelayCommand]
    private void PrevHour()
    {
        if (HourIndex > 0) { HourIndex--; return; }
        // before the first hour → last hour of the previous day
        int d = SelectedDay is null ? -1 : Days.IndexOf(SelectedDay);
        if (d > 0) { _pendingHourIndex = -1; SelectedDay = Days[d - 1]; }
    }

    [RelayCommand]
    private void ExportGeoJson()
    {
        if (MeteoLayer is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "GeoJSON|*.geojson",
            FileName = $"{SelectedLayer.Kind.Type}{(SelectedLayer.Kind.Height > 0 ? SelectedLayer.Kind.Height.ToString() : "")}_{SelectedDay?.Date:yyyyMMdd}_{CurrentHour:00}.geojson",
        };
        if (dlg.ShowDialog() == true)
        {
            File.WriteAllText(dlg.FileName, ForecastService.ToGeoJson(MeteoLayer, indented: false));
            Status = "Uloženo: " + dlg.FileName;
        }
    }

    private static LayerItem ToItem(LayerKind k) => k.Type switch
    {
        LayerType.CloudLow => new(k, "Nízká", "Oblačnost"),
        LayerType.CloudMid => new(k, "Střední", "Oblačnost"),
        LayerType.CloudHigh => new(k, "Vysoká", "Oblačnost"),
        LayerType.Showers => new(k, "Přeháňky a bouřky", "Srážky"),
        LayerType.Temperature2m => new(k, "2 m nad zemí", "Teplota"),
        LayerType.FlightPlan => new(k, "Plánování letu (vše dohromady)", "Termika"),
        LayerType.ThermalStrength => new(k, "Síla (W*)", "Termika"),
        LayerType.ThermalCeiling => new(k, "Výška / základna Cu", "Termika"),
        LayerType.ThermalEnd => new(k, "Konec termiky (celý den)", "Termika"),
        LayerType.Overdevelopment => new(k, "Riziko přerůstání", "Termika"),
        LayerType.Spreading => new(k, "Riziko rozlití (Sc)", "Termika"),
        LayerType.Lee => new(k, "Závětří", "Termika"),
        LayerType.Convergence => new(k, "Konvergence", "Termika"),
        LayerType.SolarRadiation => new(k, "Energie na terén (W/m²)", "Slunce"),
        LayerType.SolarDaily => new(k, "Denní součet (kWh/m²)", "Slunce"),
        LayerType.Wind => new(k, k.Height == 0 ? "10 m nad zemí" : $"{k.Height} m n. m.", "Vítr"),
        _ => new(k, k.DisplayName, ""),
    };

    public void Dispose()
    {
        _updateCts?.Cancel();
        _svc.Dispose();
    }
}
