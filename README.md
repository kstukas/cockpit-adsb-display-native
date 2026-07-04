# Cockpit ADS-B Display — Native App

A native C#/Avalonia UI ADS-B cockpit display for Raspberry Pi (5 recommended, 3B supported), running on Raspberry Pi OS Lite 64-bit with no desktop environment. Fully offline, GPU-accelerated via DRM/framebuffer, no browser or Node.js required.

Replaces the original web-based (Node.js/Leaflet/Chromium kiosk) version of this project with a native app that boots faster, uses less RAM, and runs directly on the Linux framebuffer.

---

## Hardware

- Raspberry Pi 5 (4GB+, recommended) or Raspberry Pi 3B (supported, slower rendering)
- Stratux ADS-B receiver (dual SDR: RTL2838 for 1090MHz ES, Stratux UATRadio for 978MHz UAT)
- VK-162 u-blox 7 USB GPS (or equivalent NMEA GPS handled by Stratux)
- WiseCoco 2.8" round 480×480 HDMI touchscreen (or similar round HDMI touch panel)
- Official Pi 5 power supply (5V/5A USB-C) — underpowering causes instability with dual SDRs attached

---

## Software Stack

- **OS:** Raspberry Pi OS Lite 64-bit (Debian 13 "trixie")
- **ADS-B backend:** Stratux (installed via `.deb` package, not the full Stratux OS image)
- **Display app:** .NET 10 + Avalonia UI 11.2.3, rendering directly to `/dev/fb0` via `StartLinuxFbDev` (no X11, no Wayland)
- **Map rendering:** SkiaSharp, reading tiles directly from MBTiles (SQLite) files
- **Tile generation:** [chartmaker](https://github.com/N129BZ/chartmaker) (Docker), pulling official FAA raster charts

---

## Part 1 — Raspberry Pi OS Setup

1. Flash **Raspberry Pi OS Lite (64-bit)** using Raspberry Pi Imager.
2. In the Imager's settings (gear icon) before writing:
   - Set hostname (e.g. `stratux-display`)
   - Enable SSH
   - Set username `pi` and a password
   - Configure WiFi (for development — see note on static IP below)
3. Boot the Pi, SSH in, and update:
   ```bash
   sudo apt update && sudo apt upgrade -y
   ```
4. Set a static IP (recommended for consistent SCP/SSH access during development):
   ```bash
   sudo nmcli con show   # find your WiFi connection name
   sudo nmcli con mod "<connection-name>" ipv4.addresses 192.168.0.198/24
   sudo nmcli con mod "<connection-name>" ipv4.gateway 192.168.0.1
   sudo nmcli con mod "<connection-name>" ipv4.dns "192.168.0.1 8.8.8.8"
   sudo nmcli con mod "<connection-name>" ipv4.method manual
   sudo nmcli con up "<connection-name>"
   ```

---

## Part 2 — Install Stratux

Stratux is installed as a `.deb` package on plain Pi OS Lite — **not** the full Stratux OS image. This lets it run alongside our native app with no conflicts.

```bash
cd /tmp
wget https://github.com/stratux/stratux/releases/download/v2.0-pre4/stratux-2.0-pre4-arm64.deb
sudo dpkg -i stratux-2.0-pre4-arm64.deb
sudo apt install -f -y
```

Verify it's running and both SDRs are detected:
```bash
sudo systemctl status stratux
curl http://localhost/getStatus
```

Look for `"Devices":2` (both SDRs) and `"GPS_connected":true` once your GPS has a fix.

**Note on the Pi 5 fan:** Stratux's built-in `fancontrol` binary does **not** work on Pi 5 — it uses legacy direct GPIO memory access, which the Pi 5's new RP1 southbridge chip doesn't support. Disable it and use the Pi's native fan control instead:
```bash
sudo systemctl stop stratux_fancontrol
sudo systemctl disable stratux_fancontrol
sudo raspi-config   # Performance Options → Fan → configure GPIO pin + temp threshold
```

---

## Part 3 — Generate Offline Map Tiles

All tiles are generated using **chartmaker**, a Dockerized tool that downloads official FAA digital raster charts and converts them to MBTiles (SQLite) format. This must be run on a Windows/Mac/Linux machine with Docker — **not** on the Pi itself.

### 3.1 — Install Docker Desktop

Download and install [Docker Desktop](https://www.docker.com/products/docker-desktop/). Make sure it's fully running before continuing.

### 3.2 — Pull and run chartmaker

```powershell
docker pull n129bz/chartmaker:latest
docker run -it -p 1962:1962 -p 1970:1970 n129bz/chartmaker:latest
```

Inside the container shell that opens:
```bash
cd /chartmaker
node make
```

Open **http://localhost:1962** in your browser — this is the chartmaker web GUI.

### 3.3 — Configure zoom level and quality (optional, recommended)

By default chartmaker uses `zoomrange: 0-11` and `tileimagequality: 40` (fairly low quality/detail). To generate significantly higher-resolution tiles — especially valuable for **TAC** (Terminal Area Charts) and **Sectional**, which have genuine high-resolution source imagery — edit the config **before** triggering any downloads:

```bash
nano /chartmaker/settings.json
```
Change:
```json
"tileimagequality" : 40,
"zoomrange" : "0-11",
```
to:
```json
"tileimagequality" : 85,
"zoomrange" : "0-13",
```

**Important finding from testing:** IFR Enroute Low charts do **not** benefit from zoom levels beyond 11 — the FAA's source scans for enroute charts are lower native resolution (mostly line/text art, not dense photographic-style detail), so higher zoom just produces pixelated upscaling with no real detail gain, while adding ~1.4GB of pure overhead. **Recommendation: leave IFR at the default `zoomrange: 0-11`.** TAC and Sectional charts, by contrast, have genuine high-resolution source scans and benefit significantly from `zoomrange: 0-13` (TAC) or at least `0-12` (Sectional, to manage file size — see size table below).

### 3.4 — Download each chart type

In the web GUI, trigger each of these one at a time (do **not** run them concurrently — each is CPU-intensive):

| Chart Type | GUI Option | Approx. processing time (zoom 0-11) |
|---|---|---|
| VFR Sectional | "Sectional" (full/all charts) | ~30 min |
| IFR Enroute Low | "DDECUS Enroute Low" | ~45 min |
| Terminal Area Charts | "Terminal" (full/all charts) | ~45 min |

**Known issue — New York TAC crash:** The Terminal chart download will crash partway through with `TypeError: Cannot read properties of undefined (reading 'coordinates')` when it reaches `new_york_vfr_plannings`. This is a broken/orphaned file in the FAA's Terminal zip (a VFR Flyway Planning chart with no matching clip shapefile in chartmaker). Fix:
```bash
# From a separate terminal (not inside the container), find the container ID:
docker ps
# Remove the broken file from the downloaded zip:
docker exec <container_id> apt-get update && docker exec <container_id> apt-get install -y zip
docker exec <container_id> zip -d /chartmaker/chartcache/Terminal-*.zip "New York TAC VFR Planning Charts.tif"
docker exec <container_id> rm -f /chartmaker/workarea/Terminal/1_unzipped/new_york_vfr_plannings.tif
```
Then re-trigger the Terminal download from the web GUI — it will resume/retry and skip past the broken file.

### 3.5 — File size reference (measured)

| Chart | Zoom Range | File Size |
|---|---|---|
| Sectional | 0-11 (default) | ~6.2 GB |
| Sectional | 0-12 | ~15-23 GB (est.) |
| IFR Enroute Low | 0-11 (recommended — do not increase) | ~461 MB |
| Terminal (TAC) | 0-11 (default) | ~323 MB |
| Terminal (TAC) | 0-13 (recommended) | ~3.2 GB |
| SAT (NASA Landsat, separate script — see below) | 0-12, local 200nm | ~106 MB |
| SAT (NASA Landsat, full CONUS) | 0-12 | ~3.5 GB |

**CPU/thermal warning:** Running chartmaker unthrottled can pull 1500%+ CPU (all cores) and has been observed to push a modern gaming laptop to 105°C. **Strongly recommend capping Docker's CPU usage:**
```powershell
docker update --cpus="4" --memory="6g" --memory-swap="6g" <container_id>
```
4 cores holds steady around 55-70°C on typical hardware; scale up cautiously while monitoring actual CPU temperature, and back off immediately if it climbs past ~80°C.

### 3.6 — Extract the finished tile databases

Each finished chart produces a `.db` file (note: some chart types, like IFR Enroute Low, output as a literally unnamed `.db` file — rename it before copying out):
```powershell
docker ps   # get container ID
docker exec <container_id> mv /chartmaker/charts/.db /chartmaker/charts/ifr.db   # if unnamed

docker cp <container_id>:/chartmaker/charts/Sectional.db .\vfr.mbtiles
docker cp <container_id>:/chartmaker/charts/ifr.db .\ifr.mbtiles
docker cp <container_id>:/chartmaker/charts/Terminal.db .\tac.mbtiles
```

### 3.7 — SAT (satellite) tiles — separate script, not chartmaker

SAT imagery comes from NASA GIBS Landsat WELD, downloaded via a standalone Python script (`scripts/download-sat-conus.py` in this repo), **not** chartmaker. This is a plain HTTP tile-by-tile downloader — run it directly on the Pi (or any machine, then copy the resulting `.mbtiles` file over):

```bash
python3 scripts/download-sat-conus.py
```

It's resumable (skips already-downloaded tiles) and writes progress to stdout. For full CONUS coverage at zoom 0-12, expect **300,000+ tiles** and **20+ hours** depending on GIBS server response times — run it in the background with `nohup`:
```bash
nohup python3 -u scripts/download-sat-conus.py > sat-download.log 2>&1 &
disown
```
Check progress:
```bash
tail -20 sat-download.log
```
A ~10-17% tile failure rate is normal (ocean/remote areas without full zoom-12 GIBS coverage) and does not indicate a problem.

---

## Part 4 — Deploy Tiles to the Pi

Copy all four `.mbtiles` files to the Pi's tile directory:
```powershell
scp vfr.mbtiles pi@192.168.0.198:/home/pi/tiles/vfr.mbtiles
scp ifr.mbtiles pi@192.168.0.198:/home/pi/tiles/ifr.mbtiles
scp tac.mbtiles pi@192.168.0.198:/home/pi/tiles/tac.mbtiles
scp sat.mbtiles pi@192.168.0.198:/home/pi/tiles/sat.mbtiles
```

The app expects these exact paths (see `Models/Models.cs` → `MapPageInfo`):
```
/home/pi/tiles/vfr.mbtiles
/home/pi/tiles/ifr.mbtiles
/home/pi/tiles/tac.mbtiles   (optional — VFR page falls back to sectional if absent)
/home/pi/tiles/sat.mbtiles
```

**TAC-first fallback:** When TAC tiles exist for a requested location (e.g. near a major metro airport), the VFR page automatically prefers TAC's higher resolution over the standard sectional. Areas outside TAC coverage fall back to the sectional chart seamlessly, with no user-facing toggle.

If you generated TAC or Sectional at a higher zoom level than the app's configured max, update `MapPageInfo.MaxZoom()` in `Models/Models.cs` to match (e.g. `MapPage.Vfr => 13` if TAC was generated at zoom 0-13).

---

## Part 5 — Build and Deploy the App

### 5.1 — Prerequisites (on your Windows/dev machine)

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Git

### 5.2 — Clone and build

```powershell
git clone https://github.com/kstukas/cockpit-adsb-display-native.git
cd cockpit-adsb-display-native
dotnet publish src\CockpitDisplay -r linux-arm64 -c Release --self-contained true
```

### 5.3 — Deploy to Pi

```powershell
scp -C src\CockpitDisplay\bin\Release\net10.0\linux-arm64\publish\* pi@192.168.0.198:/home/pi/cockpit-native/
```

On the Pi, install runtime dependencies:
```bash
sudo apt install -y libicu-dev libssl-dev libgbm1 libgl1-mesa-dri libegl1-mesa-dev libinput10 libfontconfig1
chmod +x /home/pi/cockpit-native/CockpitDisplay
```

Test run manually first:
```bash
LD_LIBRARY_PATH=/home/pi/cockpit-native /home/pi/cockpit-native/CockpitDisplay
```

### 5.4 — Set up autolaunch (systemd)

```bash
sudo nano /etc/systemd/system/cockpit-native.service
```
```ini
[Unit]
Description=Cockpit ADS-B Native Display
After=stratux.service
Wants=stratux.service

[Service]
ExecStart=/home/pi/cockpit-native/CockpitDisplay
Environment=LD_LIBRARY_PATH=/home/pi/cockpit-native
WorkingDirectory=/home/pi/cockpit-native
Restart=always
RestartSec=3
User=pi

[Install]
WantedBy=multi-user.target
```
```bash
sudo systemctl daemon-reload
sudo systemctl enable cockpit-native.service
sudo systemctl start cockpit-native.service
```

---

## Part 6 — Boot Time Optimization (Optional but Recommended)

These steps take total boot time from ~13s down to ~6s (systemd portion) and eliminate visible boot text/login prompts on the display:

```bash
# Disable services not needed for this offline, single-purpose device
sudo systemctl disable NetworkManager-wait-online.service
sudo systemctl disable cloud-init.service cloud-init-local.service cloud-config.service cloud-final.service
sudo touch /etc/cloud/cloud-init.disabled
sudo systemctl disable bluetooth.service

# Suppress the login prompt on the physical display
sudo systemctl disable getty@tty1.service
sudo systemctl mask getty@tty1.service

# Quiet kernel boot messages
sudo nano /boot/firmware/cmdline.txt
```
Add to the **end of the single existing line** (do not add a new line):
```
quiet loglevel=0 logo.nologo vt.global_cursor_default=0 consoleblank=0
```

**Disable the Pi 5 bootloader diagnostics/QR code screen** (shown on cold boot only — a real fix, unlike the unavoidable earliest Pi logo which cannot currently be replaced on Pi 5):
```bash
sudo -E rpi-eeprom-config --edit
```
Add:
```
DISABLE_HDMI=1
```

**Note:** Disabling the diagnostics screen means boot failures (bad SD card, corrupt OS) will show a blank screen instead of a helpful diagnostic message. Fine for a device with SSH/physical access for maintenance; reconsider for a fully sealed production unit.

---

## Part 7 — Known Limitations

- **Pi 5 boot logo:** The very first Raspberry Pi logo shown on cold boot is rendered by the bootloader firmware itself, before Linux starts. As of this writing there is no supported way to replace it with a custom image on Pi 5 (confirmed via official Raspberry Pi forums).
- **WiseCoco display OSD:** The HDMI resolution/mode toast shown briefly on signal change is generated by the display panel's own firmware, not by the Pi. WiseCoco has confirmed this can be disabled at the factory but not by the end user — contact them directly if building multiple units.
- **Not yet implemented:** Airport dots with tap-to-view METAR; Stratux WiFi hotspot mode for simultaneous tablet/EFB traffic sharing (currently the Pi's WiFi is client-mode only, for development).

---

## Repository Structure

```
src/CockpitDisplay/          — Avalonia app source
  Models/                    — Domain types, Stratux message shapes, geo math
  Services/                  — Stratux WebSocket client, MBTiles reader, prefs
  ViewModels/                — MainViewModel (app state)
  Views/                     — AXAML UI + SkiaSharp map rendering
  Assets/                    — Icons, splash image, fonts
scripts/
  download-sat-conus.py      — NASA GIBS Landsat tile downloader (SAT layer)
```

---

## Credits

- [chartmaker](https://github.com/N129BZ/chartmaker) by N129BZ — FAA chart tile generation
- [Stratux](https://github.com/stratux/stratux) — ADS-B receiver software
- NASA GIBS — Landsat WELD satellite imagery
