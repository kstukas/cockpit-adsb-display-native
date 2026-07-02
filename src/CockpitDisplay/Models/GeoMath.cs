using System;

namespace CockpitDisplay.Models;

/// <summary>
/// Geographic math helpers.
/// All functions mirror the JS implementations in traffic.js and map.js exactly.
/// </summary>
public static class GeoMath
{
    // ── Haversine distance ─────────────────────────────────
    // Matches distanceNm() in traffic.js
    public static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 3440.065; // Earth radius in nautical miles
        double dLat = ToRad(lat2 - lat1);
        double dLon = ToRad(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2))
                 * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    // ── Threat classification ─────────────────────────────
    // Matches threatLevel() in traffic.js
    public static ThreatLevel ClassifyThreat(
        double ownLat, double ownLon, double ownAlt,
        double tgtLat, double tgtLon, double tgtAlt)
    {
        double dist    = DistanceNm(ownLat, ownLon, tgtLat, tgtLon);
        double altDiff = Math.Abs(tgtAlt - ownAlt);

        if (dist    <= ThreatThresholds.ProximateNm &&
            altDiff <= ThreatThresholds.ProximateAltFt)
            return ThreatLevel.Proximate;

        if (dist    <= ThreatThresholds.AdvisoryNm &&
            altDiff <= ThreatThresholds.AdvisoryAltFt)
            return ThreatLevel.Advisory;

        return ThreatLevel.Other;
    }

    // ── Relative altitude tag ─────────────────────────────
    // Matches altTag() in traffic.js — e.g. "+5↑" or "-3↓"
    public static string AltTag(double aircraftAlt, double ownAlt)
    {
        if (aircraftAlt == 0) return "";
        int diffHundreds = (int)Math.Round((aircraftAlt - ownAlt) / 100.0);
        string sign  = diffHundreds >= 0 ? "+" : "";
        string arrow = diffHundreds > 0 ? "↑" : diffHundreds < 0 ? "↓" : "→";
        return $"{sign}{diffHundreds}{arrow}";
    }

    // ── Web Mercator tile math ────────────────────────────

    /// <summary>Convert lat/lon to fractional tile coordinates at given zoom.</summary>
    public static (double X, double Y) LatLonToTile(double lat, double lon, int zoom)
    {
        double n = Math.Pow(2, zoom);
        double x = (lon + 180.0) / 360.0 * n;
        double latRad = ToRad(lat);
        double y = (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n;
        return (x, y);
    }

    /// <summary>Convert tile X/Y to lat/lon (top-left corner of tile).</summary>
    public static (double Lat, double Lon) TileToLatLon(int tileX, int tileY, int zoom)
    {
        double n = Math.Pow(2, zoom);
        double lon = tileX / n * 360.0 - 180.0;
        double latRad = Math.Atan(Math.Sinh(Math.PI * (1 - 2.0 * tileY / n)));
        return (ToDeg(latRad), lon);
    }

    /// <summary>
    /// Convert lat/lon to pixel offset from map origin (top-left at zoom level),
    /// using 256px tiles.
    /// </summary>
    public static (double Px, double Py) LatLonToPixel(double lat, double lon, int zoom)
    {
        var (tx, ty) = LatLonToTile(lat, lon, zoom);
        return (tx * 256.0, ty * 256.0);
    }

    /// <summary>
    /// Convert pixel offset back to lat/lon.
    /// </summary>
    public static (double Lat, double Lon) PixelToLatLon(double px, double py, int zoom)
    {
        double n = Math.Pow(2, zoom);
        double tileX = px / 256.0;
        double tileY = py / 256.0;
        double lon = tileX / n * 360.0 - 180.0;
        double latRad = Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * tileY / n)));
        return (ToDeg(latRad), lon);
    }

    /// <summary>
    /// Offset in pixels from center of screen to a lat/lon point,
    /// given the center lat/lon and current zoom.
    /// Positive X = east, positive Y = south (screen coords).
    /// </summary>
    public static (double Dx, double Dy) LatLonToScreenOffset(
        double centerLat, double centerLon,
        double targetLat, double targetLon,
        int zoom)
    {
        var (cx, cy) = LatLonToPixel(centerLat, centerLon, zoom);
        var (tx, ty) = LatLonToPixel(targetLat, targetLon, zoom);
        return (tx - cx, ty - cy);
    }

    /// <summary>
    /// Convert nautical miles distance at a given latitude to degrees latitude.
    /// Used for placing range ring labels.
    /// </summary>
    public static double NmToDegreesLat(double nm) => nm / 60.0;

    /// <summary>
    /// Convert nautical miles distance at a given latitude to degrees longitude.
    /// </summary>
    public static double NmToDegreesLon(double nm, double lat) =>
        nm / (60.0 * Math.Cos(ToRad(lat)));

    // ── TMS Y-flip ─────────────────────────────────────────
    // MBTiles stores tiles in TMS convention (Y=0 at south pole).
    // Leaflet/web Mercator uses XYZ (Y=0 at north pole).
    // Matches the Y-flip logic in server/app.js serveMBTiles().
    public static int XyzToTmsY(int y, int zoom) => (1 << zoom) - 1 - y;

    // ── Helpers ────────────────────────────────────────────
    public static double ToRad(double degrees) => degrees * Math.PI / 180.0;
    public static double ToDeg(double radians) => radians * 180.0 / Math.PI;

    /// <summary>Normalize heading to 0-359.</summary>
    public static double NormalizeHeading(double h) => ((h % 360) + 360) % 360;

    /// <summary>
    /// Bearing offset for placing range ring labels at 45° NE of ownship.
    /// Matches the effectiveBearing calculation in map.js drawRangeRings().
    /// </summary>
    public static (double DLat, double DLon) RingLabelOffset(
        double centerLat, double nm, double bearingDeg)
    {
        double brg     = ToRad(bearingDeg);
        double dMeters = nm * 1852.0;
        double dLat    = (dMeters * Math.Cos(brg)) / 111320.0;
        double dLon    = (dMeters * Math.Sin(brg)) / (111320.0 * Math.Cos(ToRad(centerLat)));
        return (dLat, dLon);
    }
}
