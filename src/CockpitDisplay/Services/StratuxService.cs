using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using CockpitDisplay.Models;

namespace CockpitDisplay.Services;

/// <summary>
/// Connects directly to Stratux WebSocket endpoints and processes messages.
/// Replaces the Node.js proxy in server/app.js — we own the whole pipeline now.
///
/// Stratux endpoints:
///   ws://localhost/situation  — GPS + AHRS state (ownship)
///   ws://localhost/traffic    — ADS-B traffic targets
///   ws://localhost/weather    — FIS-B weather (UAT 978MHz)
/// </summary>
public class StratuxService : IDisposable
{
    // ── Public state (thread-safe via lock) ────────────────
    private OwnshipState _ownship = new();
    private readonly Dictionary<uint, TrafficTarget> _trafficMap = new();
    private bool _wxReceived;
    private DateTime _wxTimestamp = DateTime.MinValue;

    private readonly Lock _lock = new();

    // ── Events pushed to UI ────────────────────────────────
    public event Action<OwnshipState>? OwnshipUpdated;
    public event Action<IReadOnlyList<TrafficTarget>>? TrafficUpdated;
    public event Action<bool>? ConnectionChanged;

    // ── Connection state ───────────────────────────────────
    private bool _connected;
    public bool IsConnected
    {
        get { lock (_lock) return _connected; }
        private set
        {
            lock (_lock) _connected = value;
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => ConnectionChanged?.Invoke(value));
        }
    }

    private readonly CancellationTokenSource _cts = new();

    // ── Stratux host (localhost in production, overridable for dev) ──
    private readonly string _host;

    public StratuxService(string host = "localhost")
    {
        _host = host;
    }

    // ── Start both WebSocket connections ───────────────────
    public void Start()
    {
        _ = Task.Run(() => ConnectLoop("situation", HandleSituation, _cts.Token));
        _ = Task.Run(() => ConnectLoop("traffic",   HandleTraffic,   _cts.Token));
        _ = Task.Run(() => ConnectLoop("weather",   HandleWeather,   _cts.Token));

        // Push traffic to UI every 1s (matches the Node.js 1s interval)
        _ = Task.Run(() => TrafficPushLoop(_cts.Token));
    }

    // ── Generic reconnecting WebSocket loop ───────────────
    private async Task ConnectLoop(
        string endpoint,
        Action<string> handler,
        CancellationToken ct)
    {
        var uri = new Uri($"ws://{_host}/{endpoint}");
        Console.Error.WriteLine($"[Stratux] Starting {endpoint} connection loop to {uri}");

        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(uri, ct);
                Console.Error.WriteLine($"[Stratux] Connected to {endpoint}");
                if (endpoint == "situation" || endpoint == "traffic")
                    IsConnected = true;

                var buf = new byte[65536];
                var sb  = new StringBuilder();

                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(buf, ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    sb.Append(Encoding.UTF8.GetString(buf, 0, result.Count));

                    if (result.EndOfMessage)
                    {
                        try { handler(sb.ToString()); }
                        catch { /* never crash the loop on bad JSON */ }
                        sb.Clear();
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Console.Error.WriteLine($"[Stratux] {endpoint} error: {ex.GetType().Name}: {ex.Message}"); }

            if (endpoint == "situation") IsConnected = false;

            // Match the 3s retry in app.js
            await Task.Delay(3000, ct).ContinueWith(_ => { });
        }
    }

    // ── /situation handler ─────────────────────────────────
    // Mirrors the ws.on('message') handler in app.js connectSituation()
    private void HandleSituation(string json)
    {
        var s = JsonSerializer.Deserialize<SituationMessage>(json);
        if (s == null) return;

        var state = new OwnshipState();

        // GPS position
        if (s.GPSLatitude != 0 || s.GPSLongitude != 0)
        {
            state.Lat = s.GPSLatitude;
            state.Lon = s.GPSLongitude;
        }

        state.HasFix = s.GPSFixQuality > 0 && s.GPSSatellites > 0;
        state.Track  = s.GPSTrueCourse;

        // Ground speed: Stratux gives m/s, convert to knots (×1.94384)
        state.SpeedKts = s.GPSGroundSpeed > 0
            ? (int)Math.Round(s.GPSGroundSpeed * 1.94384)
            : 0;

        // Baro altitude preferred over GPS MSL (matches app.js logic)
        int baroAlt = s.BaroPressureAltitude != 0
            ? (int)Math.Round(s.BaroPressureAltitude)
            : 0;
        int gpsAlt  = s.GPSAltitudeMSL != 0
            ? (int)Math.Round(s.GPSAltitudeMSL * 3.28084) // metres → feet
            : 0;
        state.AltFt = baroAlt != 0 ? baroAlt : gpsAlt;

        // AHRS
        state.Pitch = s.AHRSPitch;
        state.Roll  = s.AHRSRoll;

        // Heading priority: mag > gyro > track (matches app.js)
        const double InvalidAhrs = 3276.0; // Stratux sentinel value
        if (s.AHRSMagHeading > 0 && s.AHRSMagHeading < InvalidAhrs)
            state.HeadingMag = s.AHRSMagHeading;
        else if (s.AHRSGyroHeading > 0 && s.AHRSGyroHeading < InvalidAhrs)
            state.HeadingMag = s.AHRSGyroHeading;
        else
            state.HeadingMag = s.GPSTrueCourse;

        lock (_lock) _ownship = state;

        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => OwnshipUpdated?.Invoke(state));
    }

    // ── /traffic handler ──────────────────────────────────
    // Mirrors the ws.on('message') handler in app.js connectTraffic()
    private void HandleTraffic(string json)
    {
        Console.Error.WriteLine($"[Stratux] RAW traffic message: {json.Substring(0, Math.Min(200, json.Length))}");
        var t = JsonSerializer.Deserialize<TrafficMessage>(json);
        if (t == null || t.Icao_addr == 0) return;

        lock (_lock)
        {
            // Remove stale targets (matches `if (t.Age > 30) delete`)
            if (t.Age > ThreatThresholds.StaleAgeSec)
            {
                _trafficMap.Remove(t.Icao_addr);
                return;
            }

            if (!t.Position_valid) return;

            string callsign = !string.IsNullOrWhiteSpace(t.Tail) ? t.Tail!.Trim()
                            : !string.IsNullOrWhiteSpace(t.Reg)  ? t.Reg!.Trim()
                            : t.Icao_addr.ToString("x6").ToUpper();

            _trafficMap[t.Icao_addr] = new TrafficTarget
            {
                IcaoAddr      = t.Icao_addr,
                Callsign      = callsign,
                Lat           = t.Lat,
                Lon           = t.Lng,
                AltBaro       = t.Alt,
                Track         = t.Track,
                SpeedKts      = t.Speed,
                Age           = t.Age,
                IsUat         = t.TargetType == 2,
                OnGround      = t.OnGround,
                PositionValid = t.Position_valid,
            };
            Console.Error.WriteLine($"[Stratux] Traffic added: {callsign} at {t.Lat},{t.Lng} alt={t.Alt}");
        }
    }

    // ── /weather handler ──────────────────────────────────
    private void HandleWeather(string json)
    {
        if (json.Length > 2) // non-empty object
        {
            lock (_lock)
            {
                _wxReceived  = true;
                _wxTimestamp = DateTime.UtcNow;
            }
        }
    }

    // ── 1Hz traffic push to UI ────────────────────────────
    // Matches the 1s interval WebSocket push in app.js wss.on('connection')
    private async Task TrafficPushLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct).ContinueWith(_ => { });

            OwnshipState ownship;
            List<TrafficTarget> targets;

            lock (_lock)
            {
                ownship = _ownship;

                // Classify threat level for each target against current ownship
                targets = _trafficMap.Values
                    .Where(t => t.PositionValid)
                    .Select(t =>
                    {
                        if (ownship.HasPosition)
                        {
                            t.ThreatLevel = GeoMath.ClassifyThreat(
                                ownship.DisplayLat, ownship.DisplayLon, ownship.AltFt,
                                t.Lat, t.Lon, t.AltBaro);
                        }
                        return t;
                    })
                    .ToList();

                // Also prune truly ancient entries
                var stale = _trafficMap
                    .Where(kv => kv.Value.Age > ThreatThresholds.StaleAgeSec)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var k in stale)
                    _trafficMap.Remove(k);
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => TrafficUpdated?.Invoke(targets));
        }
    }

    // ── Accessors ─────────────────────────────────────────
    public OwnshipState GetOwnship()
    {
        lock (_lock) return _ownship;
    }

    public bool IsWxFresh()
    {
        lock (_lock)
            return _wxReceived &&
                   (DateTime.UtcNow - _wxTimestamp).TotalMinutes < 15;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
