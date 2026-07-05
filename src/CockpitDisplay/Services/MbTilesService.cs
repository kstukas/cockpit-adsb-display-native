using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;
using CockpitDisplay.Models;

namespace CockpitDisplay.Services;

/// <summary>
/// Reads tiles from MBTiles (SQLite) files.
///
/// Directly ports the serveMBTiles() function from server/app.js:
///   - Tries XYZ Y coordinate first
///   - Falls back to TMS Y-flip: tmsY = (1 << z) - 1 - y
///   - Returns raw bytes (PNG/WebP/JPEG) for rendering with SkiaSharp
///
/// Tile files (from TILES.md):
///   /home/pi/tiles/vfr.mbtiles   (~6.2 GB, FAA sectional)
///   /home/pi/tiles/ifr.mbtiles   (~435 MB, enroute low)
///   /home/pi/tiles/sat.mbtiles   (~3.5 GB, NASA Landsat)
/// </summary>
public class MbTilesService : IDisposable
{
    // One open connection per file, kept alive for performance
    private readonly Dictionary<string, SqliteConnection> _connections = new();
    private readonly Dictionary<string, SqliteCommand> _tileCommands = new();
    private readonly Lock _lock = new();

    // Simple in-memory tile cache to avoid re-reading hot tiles.
    // Key: "path:z/x/y", Value: raw bytes (null = empty tile at this coord).
    // Kept small — the view layer caches decoded bitmaps in front of this,
    // so this mostly serves null results (TAC misses) and re-decodes.
    private readonly Dictionary<string, byte[]?> _cache = new();
    private const int MaxCacheEntries = 128;

    // ── Get tile bytes for a map page ─────────────────────
    public byte[]? GetTile(MapPage page, int z, int x, int y)
    {
        // For VFR page, try TAC first (higher-resolution terminal area chart).
        // Falls back to the sectional chart if TAC has no coverage at this location.
        string? tacPath = MapPageInfo.TacPath(page);
        if (tacPath != null)
        {
            byte[]? tacTile = GetTile(tacPath, z, x, y);
            if (tacTile != null)
                return tacTile;
        }
        string? path = MapPageInfo.TilePath(page);
        if (path == null) return null;
        return GetTile(path, z, x, y);
    }

    // ── Core tile fetch — mirrors serveMBTiles() in app.js ──
    public byte[]? GetTile(string filePath, int z, int x, int y)
    {
        string cacheKey = $"{filePath}:{z}/{x}/{y}";

        lock (_lock)
        {
            if (_cache.TryGetValue(cacheKey, out var cached))
                return cached;

            try
            {
                var db = GetOrOpenDb(filePath);

                // Try XYZ y first (some MBTiles are stored this way)
                byte[]? data = QueryTile(db, z, x, y);

                // Fall back to TMS y-flip (most MBTiles use TMS convention)
                if (data == null)
                {
                    int tmsY = GeoMath.XyzToTmsY(y, z);
                    data = QueryTile(db, z, x, tmsY);
                }

                // Cache result (even null, so we don't re-query missing tiles)
                if (_cache.Count >= MaxCacheEntries)
                    _cache.Clear(); // simple eviction
                _cache[cacheKey] = data;

                return data;
            }
            catch
            {
                return null;
            }
        }
    }

    private byte[]? QueryTile(SqliteConnection db, int z, int x, int y)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT tile_data FROM tiles " +
            "WHERE zoom_level=@z AND tile_column=@x AND tile_row=@y";
        cmd.Parameters.AddWithValue("@z", z);
        cmd.Parameters.AddWithValue("@x", x);
        cmd.Parameters.AddWithValue("@y", y);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0))
            return null;

        return (byte[])reader[0];
    }

    // ── Read MBTiles metadata ──────────────────────────────
    public string? GetMetadata(string filePath, string key)
    {
        lock (_lock)
        {
            try
            {
                var db = GetOrOpenDb(filePath);
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT value FROM metadata WHERE name=@key";
                cmd.Parameters.AddWithValue("@key", key);
                return cmd.ExecuteScalar()?.ToString();
            }
            catch { return null; }
        }
    }

    // ── Check if a tile file exists and is readable ───────
    public bool IsAvailable(MapPage page)
    {
        string? path = MapPageInfo.TilePath(page);
        if (path == null || !File.Exists(path)) return false;
        try
        {
            GetOrOpenDb(path);
            return true;
        }
        catch { return false; }
    }

    // ── Lazy open DB connection ────────────────────────────
    private SqliteConnection GetOrOpenDb(string filePath)
    {
        if (_connections.TryGetValue(filePath, out var existing))
            return existing;

        var conn = new SqliteConnection($"Data Source={filePath};Mode=ReadOnly");
        conn.Open();
        _connections[filePath] = conn;
        return conn;
    }

    // ── Clear cache (call on zoom change to free memory) ──
    public void ClearCache()
    {
        lock (_lock) _cache.Clear();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var cmd in _tileCommands.Values)
                cmd.Dispose();
            foreach (var conn in _connections.Values)
                conn.Dispose();
            _connections.Clear();
            _tileCommands.Clear();
            _cache.Clear();
        }
    }
}
