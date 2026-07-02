# Cockpit ADS-B Display — Claude Code Briefing

## What this project is
A native C#/Avalonia UI app for a Raspberry Pi 3B running Pi OS Lite 64-bit
with X11 + Openbox (no desktop environment). It replaces a working Node.js/
Leaflet/Chromium web app with a fully native app. The display is a WiseCoco
2.8" round 480×480 HDMI touchscreen. Everything must work 100% offline.

## Hardware
- Raspberry Pi 3B (upgrading to Pi 5 soon)
- Stratux ADS-B receiver (dual SDR: 1090MHz + 978MHz UAT)
- VK-162 u-blox 7 USB GPS (handled by Stratux)
- WiseCoco 2.8" round 480×480 HDMI touchscreen (touch via USB HID)

## Stratux WebSocket endpoints (on the Pi at ws://localhost/...)
All data comes from Stratux. Connect directly — no intermediary.

### ws://localhost/situation  (ownship GPS + AHRS)
Key fields (Go/PascalCase JSON):
  GPSLatitude, GPSLongitude    — position (doubles)
  GPSAltitudeMSL               — GPS altitude in METRES (convert ×3.28084 for feet)
  GPSFixQuality                — int, >0 means fix
  GPSSatellites                — int, >0 means fix
  GPSTrueCourse                — track degrees
  GPSGroundSpeed               — m/s (convert ×1.94384 for knots)
  BaroPressureAltitude         — feet, preferred over GPS altitude
  AHRSPitch, AHRSRoll          — degrees
  AHRSMagHeading               — magnetic heading (use if < 3276, else invalid)
  AHRSGyroHeading              — gyro heading fallback (use if < 3276)
Heading priority: AHRSMagHeading > AHRSGyroHeading > GPSTrueCourse

### ws://localhost/traffic  (ADS-B targets, one message per aircraft per update)
Key fields:
  Icao_addr       — uint, ICAO address (hex it for display)
  Tail, Reg       — string, callsign (Tail preferred, then Reg, then hex)
  Lat, Lng        — position doubles
  Alt             — barometric altitude feet
  Track           — heading degrees
  Speed           — knots
  Age             — seconds since last heard; DISCARD if Age > 30
  TargetType      — int; 2 = UAT (978MHz)
  Position_valid  — bool; only show if true
  OnGround        — bool

### ws://localhost/weather  (FIS-B, UAT only)
Only used to set a "WX received" flag. If any non-empty message arrives,
wx is considered fresh for 15 minutes.

## Tile files on Pi (MBTiles/SQLite, fully offline)
/home/pi/tiles/vfr.mbtiles   (~6.2 GB, FAA VFR sectional, WebP tiles)
/home/pi/tiles/ifr.mbtiles   (~435 MB, FAA IFR enroute low, WebP tiles)
/home/pi/tiles/sat.mbtiles   (~3.5 GB, NASA Landsat, JPEG tiles)

### CRITICAL: MBTiles Y-axis flip
MBTiles stores tiles in TMS convention (Y=0 at south pole).
Leaflet/web Mercator uses XYZ (Y=0 at north pole).
When querying: tmsY = (1 << zoom) - 1 - xyzY
Always try XYZ y first, then fall back to TMS y.

SQL query:
  SELECT tile_data FROM tiles
  WHERE zoom_level=? AND tile_column=? AND tile_row=?

## Four map pages (cycle with page button)
Index 0 — SAT   — sat.mbtiles,  maxZoom 12, bg #0a0a1a
Index 1 — VFR   — vfr.mbtiles,  maxZoom 11, bg #1a1a0a
Index 2 — IFR   — ifr.mbtiles,  maxZoom 11, bg #0a0a14
Index 3 — RADAR — no tiles,     maxZoom 13, bg #000000 (blank, traffic only)

RADAR page is ALWAYS track-up. Other pages default to saved orientation.
When leaving RADAR, restore the saved orientation from prefs.

## Map orientation modes
- North-up: map is fixed, north always at top
- Track-up: map rotates so aircraft heading points up (rotate map container
  by -track degrees around center)

## Zoom levels and NM labels
7=50NM, 8=25NM, 9=20NM, 10=15NM, 11=10NM, 12=5NM, 13=2NM
Default zoom: 10

## Range rings (draw around ownship)
2nm, 5nm, 10nm, 20nm — cyan color (#0077aa), label at NE (45°)

## Traffic threat classification
Proximate (RED):  distance ≤ 6nm  AND altitude diff ≤ 1200ft
Advisory (YELLOW): distance ≤ 20nm AND altitude diff ≤ 3000ft
Other (CYAN):     everything else with valid position
Stale: Age > 30 seconds — remove from display

## Altitude tag format (shown above each traffic target)
diffHundreds = round((targetAlt - ownAlt) / 100)
Format: "+5↑" or "-3↓" or "0→"

## Default home position (when no GPS fix)
KTOA Torrance CA: lat=33.8033, lon=-118.3396

## prefs.json (saved next to the binary)
{
  "aircraftIcon": "low_wing",   // icon variant
  "lastPage": 0,                // last active page (0-3)
  "altFilter": 3000,            // altitude filter in feet
  "defaultPage": 0,             // startup page
  "orientation": "north"        // "north" or "track"
}

## UI layout (480×480 circle)
Matches the web app's index.html exactly:

### Left arc strip (GS / ALT / HDG) — left edge of circle
Three semi-transparent black panels with cyan (#00cfff) borders
showing ground speed (kts), baro altitude (ft), magnetic heading (°)

### Right arc strip (TFC / WX / SDR) — right edge of circle
Three panels showing:
  TFC — count of visible traffic targets
  WX  — "OK" (green #00ff88) if UAT weather received in last 15min, else "--" (red #ff3333)
  SDR — "OK" (green) if Stratux WebSocket connected, else "--" (red)

### Bottom bar (center-bottom)
  [⚙]  [zoom label]  [page label]
  ⚙ button opens settings menu (NOT long-press — that was removed)
  Zoom label shows e.g. "15 NM"
  Page label shows "SAT"/"VFR"/"IFR"/"RDR", tap cycles to next page

### North indicator — top center
Two-tone arrow (red tip north, white tail south), rotates in track-up mode

### Alerts
  TRAFFIC banner (red) — shown when any proximate traffic exists
  GPS SIGNAL LOST banner (yellow) — shown when no GPS fix

### Splash screen
Black overlay with cyan star polygon + "COCKPIT ADS-B" + "INITIALIZING..."
Fades out after 1.5 seconds

### Settings menu (overlay, opened by ⚙ button)
  DEFAULT VIEW — buttons: SAT / VFR / IFR / RADAR
  ALTITUDE FILTER — buttons: ±1000 / ±3000 / ±5000 / UNLIM
  MAP ORIENTATION — buttons: N UP / TRK UP
  SELECT AIRCRAFT — opens aircraft icon picker
  ✕ CLOSE

## Colors (match web app exactly)
Primary cyan:    #00cfff
Alert red:       #ff3333
Advisory yellow: #ffcc00
OK green:        #00ff88
Background:      #000000
Panel bg:        rgba(0,0,0,0.88) → #E0000000 in Avalonia

## Font
Montserrat (Bold, Black weights) — matches web app
Falls back to system sans-serif if not available

## Pan behavior
- User can pan the map freely
- After 2 seconds of no input, map re-centers on ownship
- In track-up mode, pan direction must be rotated to compensate for map rotation

## Pinch to zoom
WiseCoco touchscreen supports touch. Pinch in/out changes zoom level.
Zoom range: 7–13

## What's already written (in the project)
- Models.cs — all domain types, Stratux message shapes, prefs
- GeoMath.cs — haversine distance, tile math, TMS Y-flip, threat classification
- StratuxService.cs — WebSocket connections to Stratux, 1Hz traffic push
- MbTilesService.cs — SQLite tile reader with TMS Y-flip
- PrefsService.cs — load/save prefs.json
- MainViewModel.cs — central state, wires services to UI
- MainWindow.axaml/cs — 480×480 window, no chrome, clips to circle
- CockpitView.axaml — UI layout (NEEDS REVIEW — may have issues)
- CockpitView.axaml.cs — SkiaSharp renderer (NEEDS REVIEW)
- Converters.cs — bool→"OK"/color converters
- GlobalUsings.cs — global using directives

## Current build status
Builds with 0 errors after GlobalUsings.cs was added.
CockpitView.axaml and CockpitView.axaml.cs need to be verified and
completed — the SkiaSharp map rendering, pinch gesture, and settings
menu wiring may need fixes.

## Things NOT needed in the native app (removed from web version)
- tileserver-gl-light (tiles read directly from SQLite)
- Node.js / Express server
- Chromium kiosk
- Long-press for settings (replaced by ⚙ button)

## Deploy command (from Windows, cross-compile for Pi)
dotnet publish -r linux-arm64 -c Release
Output goes to: bin\Release\net10.0\linux-arm64\publish\
SCP that folder to /home/pi/cockpit-native/ on the Pi
Run with: DISPLAY=:0 /home/pi/cockpit-native/CockpitDisplay
