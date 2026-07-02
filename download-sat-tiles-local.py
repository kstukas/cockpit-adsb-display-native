#!/usr/bin/env python3
"""
Download NASA GIBS Landsat tiles for a 200nm radius around Redondo Beach, CA
Zoom levels 0-12
Resumable — skips tiles already downloaded
"""

import sqlite3
import urllib.request
import math
import time

MBTILES = '/home/pi/tiles/sat.mbtiles'

TILE_URL = 'https://gibs.earthdata.nasa.gov/wmts/epsg3857/best/Landsat_WELD_CorrectedReflectance_TrueColor_Global_Annual/default/GoogleMapsCompatible_Level12/{z}/{y}/{x}.jpg'

# 200nm radius around Redondo Beach, CA (33.8492, -118.3884)
BOUNDS = (-122.4020, 30.5159, -114.3748, 37.1825)

MIN_ZOOM = 0
MAX_ZOOM = 12

def lon_to_tile_x(lon, zoom):
    return int((lon + 180.0) / 360.0 * (2 ** zoom))

def lat_to_tile_y(lat, zoom):
    lat_r = math.radians(lat)
    return int((1.0 - math.log(math.tan(lat_r) + 1.0 / math.cos(lat_r)) / math.pi) / 2.0 * (2 ** zoom))

def count_tiles():
    total = 0
    for z in range(MIN_ZOOM, MAX_ZOOM + 1):
        x_min = lon_to_tile_x(BOUNDS[0], z)
        x_max = lon_to_tile_x(BOUNDS[2], z)
        y_min = lat_to_tile_y(BOUNDS[3], z)
        y_max = lat_to_tile_y(BOUNDS[1], z)
        total += (x_max - x_min + 1) * (y_max - y_min + 1)
    return total

def init_db(db):
    db.execute('''CREATE TABLE IF NOT EXISTS tiles (
        zoom_level INTEGER, tile_column INTEGER, tile_row INTEGER, tile_data BLOB,
        PRIMARY KEY (zoom_level, tile_column, tile_row))''')
    db.execute('''CREATE TABLE IF NOT EXISTS metadata (name TEXT, value TEXT)''')
    db.execute("INSERT OR REPLACE INTO metadata VALUES ('name', 'NASA Landsat Redondo Beach 200nm')")
    db.execute("INSERT OR REPLACE INTO metadata VALUES ('format', 'jpg')")
    db.execute("INSERT OR REPLACE INTO metadata VALUES ('minzoom', '0')")
    db.execute("INSERT OR REPLACE INTO metadata VALUES ('maxzoom', '12')")
    db.execute("INSERT OR REPLACE INTO metadata VALUES ('bounds', '-122.4020,30.5159,-114.3748,37.1825')")
    db.execute("INSERT OR REPLACE INTO metadata VALUES ('attribution', 'NASA GIBS Landsat')")
    db.commit()

def download_tiles():
    db = sqlite3.connect(MBTILES)
    init_db(db)

    total = count_tiles()
    done = 0
    errors = 0
    start = time.time()

    print(f'Downloading {total} tiles, 200nm radius around Redondo Beach, zoom {MIN_ZOOM}-{MAX_ZOOM}')
    print(f'Output: {MBTILES}')
    print('Resumable — already-downloaded tiles are skipped')
    print()

    for z in range(MIN_ZOOM, MAX_ZOOM + 1):
        x_min = lon_to_tile_x(BOUNDS[0], z)
        x_max = lon_to_tile_x(BOUNDS[2], z)
        y_min = lat_to_tile_y(BOUNDS[3], z)
        y_max = lat_to_tile_y(BOUNDS[1], z)

        for x in range(x_min, x_max + 1):
            for y in range(y_min, y_max + 1):
                existing = db.execute(
                    'SELECT 1 FROM tiles WHERE zoom_level=? AND tile_column=? AND tile_row=?',
                    (z, x, y)
                ).fetchone()

                if existing:
                    done += 1
                    continue

                url = TILE_URL.format(z=z, x=x, y=y)
                try:
                    req = urllib.request.Request(url, headers={
                        'User-Agent': 'CockpitADSB/1.0 (aviation display)'
                    })
                    with urllib.request.urlopen(req, timeout=15) as resp:
                        data = resp.read()
                    db.execute(
                        'INSERT OR REPLACE INTO tiles VALUES (?,?,?,?)',
                        (z, x, y, data)
                    )
                    done += 1
                except Exception as e:
                    errors += 1
                    done += 1

                if done % 200 == 0:
                    db.commit()
                    elapsed = time.time() - start
                    rate = done / elapsed if elapsed > 0 else 0
                    remaining = (total - done) / rate if rate > 0 else 0
                    print(f'Z{z} [{done}/{total}] {rate:.1f} tiles/sec ETA {remaining/3600:.1f}hrs errors={errors}')

    db.commit()
    db.close()
    print('Download complete!')

if __name__ == '__main__':
    download_tiles()
