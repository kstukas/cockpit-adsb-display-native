using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CockpitDisplay.Models;
using CockpitDisplay.ViewModels;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;

namespace CockpitDisplay.Views;

public partial class CockpitView : UserControl
{
    private MainViewModel? _vm;

    // ── SkiaSharp canvas control ───────────────────────────
    private SkiaCanvas? _skiaCanvas;

    // ── Cached ownship bitmap (reloaded when icon changes) ─
    private SKBitmap? _ownshipBitmap;
    private string _loadedIconId = "";

    // ── Pan state ─────────────────────────────────────────
    private bool _userPanning;
    private Point _panStart;
    private double _panStartLat;
    private double _panStartLon;
    private DispatcherTimer? _recenterTimer;

    // ── Map center (follows ownship unless panning) ───────
    private double _centerLat = 33.8033;
    private double _centerLon = -118.3396;

    public CockpitView()
    {
        InitializeComponent();

        // Splash fade after 1.5s
        DispatcherTimer.RunOnce(() =>
        {
            if (SplashScreen != null)
                SplashScreen.IsVisible = false;
        }, TimeSpan.FromMilliseconds(1500));

        DataContextChanged += (_, _) =>
        {
            _vm = DataContext as MainViewModel;
            if (_vm != null) OnVmAttached();
        };
    }

    private void OnVmAttached()
    {
        if (_vm == null) return;

        // Insert SkiaSharp canvas as first child of the Panel (behind HUD overlays)
        if (Content is Panel panel)
        {
            _skiaCanvas = new SkiaCanvas(this) { Width = 480, Height = 480 };
            panel.Children.Insert(0, _skiaCanvas);
        }

        // Track ownship position for map centering
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Ownship) && !_userPanning)
            {
                _centerLat = _vm.Ownship.DisplayLat;
                _centerLon = _vm.Ownship.DisplayLon;
            }
        };

        // 30fps render loop
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (_, _) =>
        {
            _skiaCanvas?.InvalidateVisual();
            string label = Models.ZoomLabels.Get(_fractionalZoom, _centerLat);
            var parts = label.Split(' ');
            ZoomNumberText.Text = parts.Length > 0 ? parts[0] : label;
            ZoomUnitText.Text   = parts.Length > 1 ? parts[1] : "";
            if (_vm != null && NorthIndicator.RenderTransform is Avalonia.Media.RotateTransform northRt)
            {
                northRt.Angle = _vm.Orientation == MapOrientation.Track ? -_vm.Ownship.Track : 0;
            }
        };
        timer.Start();

        // Touch pan
        AddHandler(PointerPressedEvent,  OnPointerPressed,  handledEventsToo: true);
        AddHandler(PointerMovedEvent,    OnPointerMoved,    handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPointerReleased, handledEventsToo: true);
    }

    // ── SkiaSharp render (called by SkiaCanvas) ───────────
    private double _fractionalZoom = 10.0;

    internal void Render(SKCanvas canvas, int width, int height)
    {
        canvas.Clear(SKColors.Transparent);
        if (_vm == null) return;

        float cx = width  / 2f;
        float cy = height / 2f;

        float rotDeg = _vm.Orientation == MapOrientation.Track
            ? -(float)_vm.Ownship.Track : 0f;

        canvas.Save();
        canvas.Translate(cx, cy);
        canvas.RotateDegrees(rotDeg);
        canvas.Translate(-cx, -cy);

        DrawTiles(canvas, cx, cy, width, height);
        DrawRangeRings(canvas, cx, cy, rotDeg);
        DrawTraffic(canvas, cx, cy, rotDeg);
        DrawOwnship(canvas, cx, cy);

        canvas.Restore();
    }

    // ── Tile rendering ────────────────────────────────────
    private readonly Dictionary<string, SKBitmap?> _tileCache = new();

    private void DrawTiles(SKCanvas canvas, float cx, float cy, int w, int h)
    {
        if (_vm == null || _vm.CurrentPage == MapPage.Radar) return;

        int tileZoom = (int)Math.Round(_fractionalZoom);
        tileZoom = Math.Clamp(tileZoom, 7, 13);

        // Visual scale factor between the fractional zoom and the snapped tile zoom.
        // 2^(fractional - snapped) smoothly scales the tile bitmap during a pinch
        // so motion feels continuous even though tiles are fixed-resolution.
        double scale = Math.Pow(2.0, _fractionalZoom - tileZoom);
        float scaledTileSize = (float)(256 * scale);

        var (fracTx, fracTy) = GeoMath.LatLonToTile(_centerLat, _centerLon, tileZoom);
        int baseTx = (int)Math.Floor(fracTx);
        int baseTy = (int)Math.Floor(fracTy);
        float offX = (float)((fracTx - baseTx) * scaledTileSize);
        float offY = (float)((fracTy - baseTy) * scaledTileSize);
        int maxTile = 1 << tileZoom;

        for (int dy = -3; dy <= 3; dy++)
        for (int dx = -3; dx <= 3; dx++)
        {
            int tx = ((baseTx + dx) % maxTile + maxTile) % maxTile;
            int ty = baseTy + dy;
            if (ty < 0 || ty >= maxTile) continue;

            float px = cx - offX + dx * scaledTileSize;
            float py = cy - offY + dy * scaledTileSize;
            if (px + scaledTileSize < 0 || px > w || py + scaledTileSize < 0 || py > h) continue;

            var bmp = GetTileBitmap(tileZoom, tx, ty);
            if (bmp != null)
                canvas.DrawBitmap(bmp, new SKRect(px, py, px + scaledTileSize, py + scaledTileSize));
        }
    }

    private SKBitmap? GetTileBitmap(int z, int x, int y)
    {
        string key = $"{(int)(_vm?.CurrentPage ?? 0)}:{z}/{x}/{y}";
        if (_tileCache.TryGetValue(key, out var cached)) return cached;
        var data = _vm?.GetTile(z, x, y);
        SKBitmap? bmp = null;
        if (data != null) try { bmp = SKBitmap.Decode(data); } catch { }
        if (_tileCache.Count > 256) _tileCache.Clear();
        _tileCache[key] = bmp;
        return bmp;
    }

    // ── Range rings ───────────────────────────────────────
    // From map.js drawRangeRings(): color #0077aa, weight 3, opacity 0.6
    // Labels: Montserrat 19px bold, white text, black bg, cyan border, at 45° NE
    private readonly SKPaint _ringPaint = new()
    {
        Style = SKPaintStyle.Stroke,
        Color = new SKColor(0x00, 0x77, 0xaa, 0x99), // #0077aa at 60% opacity
        StrokeWidth = 3,
        IsAntialias = true,
    };
    private readonly SKPaint _ringLabelBg = new()
    {
        Style = SKPaintStyle.Fill,
        Color = new SKColor(0, 0, 0, 192),
    };
    private readonly SKPaint _ringLabelBorder = new()
    {
        Style = SKPaintStyle.Stroke,
        Color = new SKColor(0x00, 0xcf, 0xff, 255),
        StrokeWidth = 1,
        IsAntialias = true,
    };
    private readonly SKPaint _ringLabelText = new()
    {
        Color = new SKColor(255, 255, 255, 230),
        TextSize = 19,
        IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold),
    };

    private void DrawRangeRings(SKCanvas canvas, float cx, float cy, float canvasRotDeg)
    {
        if (_vm == null) return;

        double pxPerM = (256.0 * Math.Pow(2, _fractionalZoom)) / (2 * Math.PI * 6378137.0)
                        / Math.Cos(GeoMath.ToRad(_centerLat));
        double[] rings = { 2, 5, 10, 20 };

        foreach (double nm in rings)
        {
            float radiusPx = (float)(nm * 1852.0 * pxPerM);
            canvas.DrawCircle(cx, cy, radiusPx, _ringPaint);

            double bearingDeg = _vm.Orientation == MapOrientation.Track
                ? 45.0 + _vm.Ownship.Track
                : 45.0;
            double brg = bearingDeg * Math.PI / 180.0;
            float lx = cx + (float)(radiusPx * Math.Sin(brg));
            float ly = cy - (float)(radiusPx * Math.Cos(brg));
            string label = $"{nm}nm";

            var bounds = new SKRect();
            _ringLabelText.MeasureText(label, ref bounds);
            float lw = bounds.Width + 6;
            float lh = 22;

            // Counter-rotate by exactly -canvasRotDeg to cancel the outer canvas
            // rotation, keeping text upright while position stays geographically fixed
            canvas.Save();
            canvas.Translate(lx, ly);
            canvas.RotateDegrees(-canvasRotDeg);
            float lLeft = -lw / 2;
            float lTop  = -lh / 2;
            canvas.DrawRoundRect(lLeft, lTop, lw, lh, 6, 6, _ringLabelBg);
            canvas.DrawRoundRect(lLeft, lTop, lw, lh, 6, 6, _ringLabelBorder);
            canvas.DrawText(label, lLeft + 3, lTop + lh - 5, _ringLabelText);
            canvas.Restore();
        }
    }

    // ── Ownship icon ──────────────────────────────────────
    // From map.js placeOwnship(): 60×60 image centered on position, rotated by track
    private readonly SKPaint _bitmapPaint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.High };

    private void DrawOwnship(SKCanvas canvas, float cx, float cy)
    {
        if (_vm == null) return;

        // Reload bitmap if icon selection changed
        string iconId = _vm.AircraftIcon;
        if (iconId != _loadedIconId)
        {
            _ownshipBitmap?.Dispose();
            _ownshipBitmap = LoadIconBitmap(iconId);
            _loadedIconId = iconId;
        }

        if (_ownshipBitmap == null) return;

        // 60×60, centered on ownship position (cx,cy when not panned)
        float size = 60;
        float track = (float)_vm.Ownship.Track;

        canvas.Save();
        canvas.Translate(cx, cy);
        canvas.RotateDegrees(track); // rotate by track heading
        canvas.DrawBitmap(
            _ownshipBitmap,
            new SKRect(-size / 2, -size / 2, size / 2, size / 2),
            _bitmapPaint);
        canvas.Restore();
    }

    private SKBitmap? LoadIconBitmap(string iconId)
    {
        try
        {
            var uri = new Uri($"avares://CockpitDisplay/Assets/icons/{iconId}.png");
            using var stream = AssetLoader.Open(uri);
            return SKBitmap.Decode(stream);
        }
        catch { return null; }
    }

    // ── Traffic markers ───────────────────────────────────
    // From map.js buildTrafficIcon(): arrow polygon, alt tag, callsign
    // Colors: proximate=#ff3333, advisory=#ffcc00, other=#00cfff
    private readonly SKPaint _proxPaint  = new() { Color = new SKColor(0xff,0x33,0x33), IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _advPaint   = new() { Color = new SKColor(0xff,0xcc,0x00), IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _otherPaint = new() { Color = new SKColor(0x00,0xcf,0xff), IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _arrowStroke = new() { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 0.75f, IsAntialias = true };
    private readonly SKPaint _trafficTextProx = new()
    {
        Color = new SKColor(0xff, 0x33, 0x33), TextSize = 19, IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold),
    };
    private readonly SKPaint _trafficTextAdv = new()
    {
        Color = new SKColor(0xff, 0xcc, 0x00), TextSize = 19, IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold),
    };
    private readonly SKPaint _trafficTextOther = new()
    {
        Color = new SKColor(0x00, 0xcf, 0xff), TextSize = 19, IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold),
    };
    private readonly SKPaint _trafficTextStroke = new()
    {
        Color = SKColors.Black, TextSize = 19, IsAntialias = true,
        Style = SKPaintStyle.Stroke, StrokeWidth = 3,
        Typeface = SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold),
    };

    private void DrawTraffic(SKCanvas canvas, float cx, float cy, float canvasRotDeg)
    {
        if (_vm == null) return;

        double pxPerDegLon = 256.0 * Math.Pow(2, _fractionalZoom) / 360.0;
        double pxPerDegLat = pxPerDegLon / Math.Cos(GeoMath.ToRad(_centerLat));

        foreach (var t in _vm.Traffic)
        {
            if (!t.PositionValid) continue;

            float sx = cx + (float)((t.Lon - _centerLon) * pxPerDegLon);
            float sy = cy - (float)((t.Lat - _centerLat) * pxPerDegLat);
            if (sx < -60 || sx > 540 || sy < -60 || sy > 540) continue;

            var fill = t.ThreatLevel switch
            {
                ThreatLevel.Proximate => _proxPaint,
                ThreatLevel.Advisory  => _advPaint,
                _                     => _otherPaint,
            };
            var textPaint = t.ThreatLevel switch
            {
                ThreatLevel.Proximate => _trafficTextProx,
                ThreatLevel.Advisory  => _trafficTextAdv,
                _                     => _trafficTextOther,
            };

            // Arrow — rotates with track, same as before
            float track = (float)t.Track;
            canvas.Save();
            canvas.Translate(sx, sy);
            canvas.RotateDegrees(track);
            using var arrow = new SKPath();
            arrow.MoveTo(0,    -12.6f);
            arrow.LineTo(10.8f, 12.6f);
            arrow.LineTo(0,     3.6f);
            arrow.LineTo(-10.8f,12.6f);
            arrow.Close();
            canvas.DrawPath(arrow, fill);
            canvas.DrawPath(arrow, _arrowStroke);
            canvas.Restore();

            // Labels counter-rotated by -canvasRotDeg to stay upright on screen
            string altTag   = GeoMath.AltTag(t.AltBaro, _vm.Ownship.AltFt);
            string callsign = t.Callsign;

            if (!string.IsNullOrEmpty(altTag))
            {
                var ab = new SKRect();
                textPaint.MeasureText(altTag, ref ab);
                float ax = -ab.Width / 2 - ab.Left;

                // Rotate around the arrow's own center (sx,sy) at a fixed 22px radius above it.
                // This guarantees constant clearance from the arrow at every heading.
                canvas.Save();
                canvas.Translate(sx, sy);
                canvas.RotateDegrees(-canvasRotDeg);
                canvas.DrawText(altTag, ax, -22, _trafficTextStroke);
                canvas.DrawText(altTag, ax, -22, textPaint);
                canvas.Restore();
            }
            if (!string.IsNullOrEmpty(callsign))
            {
                var cb = new SKRect();
                textPaint.MeasureText(callsign, ref cb);
                float cx2 = -cb.Width / 2 - cb.Left;

                // Rotate around the arrow's own center (sx,sy) at a fixed 32px radius below it.
                canvas.Save();
                canvas.Translate(sx, sy);
                canvas.RotateDegrees(-canvasRotDeg);
                canvas.DrawText(callsign, cx2, 32, _trafficTextStroke);
                canvas.DrawText(callsign, cx2, 32, textPaint);
                canvas.Restore();
            }
        }
    }

    // ── Pan / pinch-zoom input ────────────────────────────
    private readonly Dictionary<int, Point> _touchPoints = new();
    private double _pinchStartDistance;
    private double _pinchStartZoom;
    private bool _isPinching;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pt = e.GetPosition(this);
        int id = (int)e.Pointer.Id;
        _touchPoints[id] = pt;

        if (_touchPoints.Count == 1)
        {
            _panStart    = pt;
            _panStartLat = _centerLat;
            _panStartLon = _centerLon;
            _userPanning = true;
            _isPinching  = false;
            _recenterTimer?.Stop();
        }
        else if (_touchPoints.Count == 2)
        {
            _isPinching  = true;
            _userPanning = false;
            var pts = _touchPoints.Values.ToArray();
            _pinchStartDistance = Distance(pts[0], pts[1]);
            _pinchStartZoom = _fractionalZoom;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        int id = (int)e.Pointer.Id;
        if (!_touchPoints.ContainsKey(id)) return;

        var pt = e.GetPosition(this);
        _touchPoints[id] = pt;

        if (_isPinching && _touchPoints.Count == 2)
        {
            var pts = _touchPoints.Values.ToArray();
            double dist = Distance(pts[0], pts[1]);
            double ratio = dist / _pinchStartDistance;

            // log2(ratio) gives continuous zoom delta: doubling distance = +1 zoom level
            double zoomDelta = Math.Log2(Math.Max(ratio, 0.01));
            _fractionalZoom = Math.Clamp(_pinchStartZoom + zoomDelta, 7.0, 13.0);
        }
        else if (_userPanning && _touchPoints.Count == 1)
        {
            double pxPerDegLon = 256.0 * Math.Pow(2, _fractionalZoom) / 360.0;
            double pxPerDegLat = pxPerDegLon / Math.Cos(GeoMath.ToRad(_centerLat));
            _centerLon = _panStartLon - (pt.X - _panStart.X) / pxPerDegLon;
            _centerLat = _panStartLat + (pt.Y - _panStart.Y) / pxPerDegLat;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        int id = (int)e.Pointer.Id;
        _touchPoints.Remove(id);

        if (_touchPoints.Count == 0)
        {
            _isPinching = false;

            if (_userPanning)
            {
                _recenterTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _recenterTimer.Tick += (_, _) =>
                {
                    _recenterTimer?.Stop();
                    _userPanning = false;
                    if (_vm?.Ownship.HasPosition == true)
                    {
                        _centerLat = _vm.Ownship.DisplayLat;
                        _centerLon = _vm.Ownship.DisplayLon;
                    }
                };
                _recenterTimer.Start();
            }
        }
    }

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

}

// ── SkiaSharp canvas control ──────────────────────────────
// Renders the map/ownship/traffic/rings layer behind the AXAML HUD
internal class SkiaCanvas : Control
{
    private readonly CockpitView _parent;

    public SkiaCanvas(CockpitView parent)
    {
        _parent = parent;
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        int w = (int)bounds.Width;
        int h = (int)bounds.Height;
        if (w <= 0 || h <= 0) return;

        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface == null) return;

        _parent.Render(surface.Canvas, w, h);

        using var skImage = surface.Snapshot();
        using var skData  = skImage.Encode(SKEncodedImageFormat.Png, 100);
        using var ms      = new MemoryStream(skData.ToArray());
        var bmp = new Avalonia.Media.Imaging.Bitmap(ms);
        context.DrawImage(bmp, new Rect(0, 0, w, h));
    }
}
