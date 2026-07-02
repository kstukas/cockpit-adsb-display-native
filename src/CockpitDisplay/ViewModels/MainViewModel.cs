using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CockpitDisplay.Models;
using CockpitDisplay.Services;

namespace CockpitDisplay.ViewModels;

/// <summary>
/// Central view model. Mirrors the state held across traffic.js,
/// map.js, and config.js in the web app — unified in one place.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly MbTilesService  _tiles;
    private readonly StratuxService  _stratux;
    private readonly PrefsService    _prefs;

    // ── Ownship ───────────────────────────────────────────
    [ObservableProperty] private OwnshipState _ownship = new();
    [ObservableProperty] private bool _hasFix;

    // HUD strips — bound directly in AXAML
    [ObservableProperty] private string _altDisplay  = "----";
    [ObservableProperty] private string _gsDisplay   = "---";
    [ObservableProperty] private string _hdgDisplay  = "---";
    [ObservableProperty] private int    _trafficCount;

    // Status indicators
    [ObservableProperty] private bool _stratuxConnected;
    [ObservableProperty] private bool _wxFresh;

    // ── Map state ─────────────────────────────────────────
    [ObservableProperty] private MapPage       _currentPage;
    [ObservableProperty] private MapOrientation _orientation;
    [ObservableProperty] private int           _zoomLevel = 10;
    [ObservableProperty] private string        _zoomLabel  = "15 NM";
    [ObservableProperty] private string        _zoomNumber = "15";
    [ObservableProperty] private string        _zoomUnit   = "NM";
    [ObservableProperty] private string        _pageLabel = "SAT";

    // ── Traffic ───────────────────────────────────────────
    public ObservableCollection<TrafficTarget> Traffic { get; } = new();

    // ── Alerts ───────────────────────────────────────────
    [ObservableProperty] private bool _proximateAlert;

    // ── Settings ──────────────────────────────────────────
    [ObservableProperty] private bool   _settingsVisible;
    [ObservableProperty] private int    _altFilterFt;
    [ObservableProperty] private string _aircraftIcon = "low_wing";

    // ── Active settings values (for button highlight bindings) ────
    [ObservableProperty] private int    _activeAltFilterValue;
    [ObservableProperty] private int    _activeDefaultPageValue;
    [ObservableProperty] private string _activeOrientationValue = "north";

    // ── Tile availability ─────────────────────────────────
    [ObservableProperty] private bool _satAvailable;
    [ObservableProperty] private bool _vfrAvailable;
    [ObservableProperty] private bool _ifrAvailable;

    public MainViewModel(
        MbTilesService  tiles,
        StratuxService  stratux,
        PrefsService    prefs)
    {
        _tiles   = tiles;
        _stratux = stratux;
        _prefs   = prefs;

        // Load saved prefs
        var p = prefs.Current;
        _currentPage  = p.ActivePage;
        _orientation  = p.MapOrientation;
        _altFilterFt  = p.AltFilter;
        _aircraftIcon = p.AircraftIcon;
        _activeAltFilterValue   = p.AltFilter;
        _activeDefaultPageValue = p.DefaultPage;
        _activeOrientationValue = p.Orientation;
        _pageLabel    = MapPageInfo.Label(_currentPage);
        _zoomLabel  = ZoomLabels.Get(_zoomLevel);
        var initParts = _zoomLabel.Split(' ');
        _zoomNumber = initParts.Length > 0 ? initParts[0] : _zoomLabel;
        _zoomUnit   = initParts.Length > 1 ? initParts[1] : "";

        // RADAR is always track-up (matches map.js loadPage())
        if (_currentPage == MapPage.Radar)
            _orientation = MapOrientation.Track;

        // Check tile availability
        _satAvailable = _tiles.IsAvailable(MapPage.Sat);
        _vfrAvailable = _tiles.IsAvailable(MapPage.Vfr);
        _ifrAvailable = _tiles.IsAvailable(MapPage.Ifr);

        // Wire Stratux events
        _stratux.OwnshipUpdated    += OnOwnshipUpdated;
        _stratux.TrafficUpdated    += OnTrafficUpdated;
        _stratux.ConnectionChanged += connected => StratuxConnected = connected;

        // Start Stratux (with 2s delay to let system settle, matches app.js)
        _ = Task.Delay(2000).ContinueWith(_ => _stratux.Start());
    }

    // ── Ownship update ────────────────────────────────────
    private void OnOwnshipUpdated(OwnshipState state)
    {
        Ownship    = state;
        HasFix     = state.HasFix;
        AltDisplay = state.AltFt > 0
            ? state.AltFt.ToString("N0")
            : "----";
        GsDisplay  = state.SpeedKts > 0
            ? state.SpeedKts.ToString()
            : "---";
        HdgDisplay = ((int)Math.Round(state.HeadingMag))
            .ToString().PadLeft(3, '0');
    }

    // ── Traffic update ────────────────────────────────────
    private void OnTrafficUpdated(IReadOnlyList<TrafficTarget> targets)
    {
        // Apply altitude filter (matches traffic.js filter logic)
        var filtered = targets
            .Where(t => Math.Abs(t.AltBaro - Ownship.AltFt) <= AltFilterFt)
            .ToList();

        Traffic.Clear();
        foreach (var t in filtered)
            Traffic.Add(t);

        TrafficCount   = filtered.Count;
        ProximateAlert = filtered.Any(t => t.ThreatLevel == ThreatLevel.Proximate);
        WxFresh        = _stratux.IsWxFresh();
    }

    // ── Page navigation ───────────────────────────────────
    // Matches nextPage() / prevPage() in map.js
    [RelayCommand]
    public void NextPage()
    {
        var next = (MapPage)(((int)CurrentPage + 1) % 4);
        SetPage(next);
    }

    [RelayCommand]
    public void PrevPage()
    {
        var prev = (MapPage)(((int)CurrentPage + 3) % 4);
        SetPage(prev);
    }

    private void SetPage(MapPage page)
    {
        bool wasRadar = CurrentPage == MapPage.Radar;
        CurrentPage   = page;
        PageLabel     = MapPageInfo.Label(page);

        // RADAR always track-up; leaving RADAR restores saved orientation
        if (page == MapPage.Radar)
        {
            Orientation = MapOrientation.Track;
        }
        else if (wasRadar)
        {
            Orientation = _prefs.Current.MapOrientation;
        }

        _prefs.Save(p => p.LastPage = (int)page);
    }

    // ── Zoom ──────────────────────────────────────────────
    [RelayCommand]
    public void ZoomIn()  => SetZoom(Math.Min(ZoomLevel + 1, 13));

    [RelayCommand]
    public void ZoomOut() => SetZoom(Math.Max(ZoomLevel - 1, 7));

    private void SetZoom(int z)
    {
        ZoomLevel = z;
        var label = ZoomLabels.Get(z);
        ZoomLabel = label;
        // Split "15 NM" into number + unit for two-line display (matches map.js updateZoomLabel())
        var parts = label.Split(' ');
        ZoomNumber = parts.Length > 0 ? parts[0] : label;
        ZoomUnit   = parts.Length > 1 ? parts[1] : "";
        _tiles.ClearCache();
    }

    // ── Orientation ───────────────────────────────────────
    [RelayCommand]
    public void SetNorthUp()
    {
        if (CurrentPage == MapPage.Radar) return; // RADAR always track-up
        Orientation = MapOrientation.North;
        ActiveOrientationValue = "north";
        _prefs.Save(p => p.Orientation = "north");
    }

    [RelayCommand]
    public void SetTrackUp()
    {
        Orientation = MapOrientation.Track;
        ActiveOrientationValue = "track";
        _prefs.Save(p => p.Orientation = "track");
    }

    // ── Settings ──────────────────────────────────────────
    [RelayCommand]
    public void ToggleSettings() => SettingsVisible = !SettingsVisible;

    [RelayCommand]
    public void CloseSettings()  => SettingsVisible = false;

    // Opens aircraft icon picker (shows selector overlay)
    [ObservableProperty] private bool _aircraftSelectorVisible;

    [RelayCommand]
    public void SelectAircraft()
    {
        SettingsVisible = false;
        AircraftSelectorVisible = true;
    }

    [RelayCommand]
    public void CloseAircraftSelector() => AircraftSelectorVisible = false;

    [RelayCommand]
    public void SetAltFilter(object? param)
    {
        if (param is not string s || !int.TryParse(s, out int feet)) return;
        AltFilterFt = feet;
        ActiveAltFilterValue = feet;
        _prefs.Save(p => p.AltFilter = feet);
    }

    [RelayCommand]
    public void SetDefaultPage(object? param)
    {
        if (param is not string s || !int.TryParse(s, out int index)) return;
        ActiveDefaultPageValue = index;
        _prefs.Save(p => p.DefaultPage = index);
    }

    [RelayCommand]
    public void SelectAircraftIcon(object? param)
    {
        if (param is not string iconId) return;
        AircraftIcon = iconId;
        _prefs.Save(p => p.AircraftIcon = iconId);
        AircraftSelectorVisible = false;
    }

    // ── Tile access for the map renderer ──────────────────
    public byte[]? GetTile(int z, int x, int y) =>
        _tiles.GetTile(CurrentPage, z, x, y);
}
