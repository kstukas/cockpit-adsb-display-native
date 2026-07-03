using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CockpitDisplay.Models;

// ── Stratux /situation WebSocket message ───────────────────
// Field names match Stratux JSON exactly (PascalCase from Go)
public class SituationMessage
{
    [JsonPropertyName("GPSLatitude")]       public double GPSLatitude { get; set; }
    [JsonPropertyName("GPSLongitude")]      public double GPSLongitude { get; set; }
    [JsonPropertyName("GPSAltitudeMSL")]    public double GPSAltitudeMSL { get; set; }
    [JsonPropertyName("GPSFixQuality")]     public int GPSFixQuality { get; set; }
    [JsonPropertyName("GPSSatellites")]     public int GPSSatellites { get; set; }
    [JsonPropertyName("GPSTrueCourse")]     public double GPSTrueCourse { get; set; }
    [JsonPropertyName("GPSGroundSpeed")]    public double GPSGroundSpeed { get; set; }
    [JsonPropertyName("BaroPressureAltitude")] public double BaroPressureAltitude { get; set; }
    [JsonPropertyName("AHRSPitch")]         public double AHRSPitch { get; set; }
    [JsonPropertyName("AHRSRoll")]          public double AHRSRoll { get; set; }
    [JsonPropertyName("AHRSMagHeading")]    public double AHRSMagHeading { get; set; }
    [JsonPropertyName("AHRSGyroHeading")]   public double AHRSGyroHeading { get; set; }
}

// ── Stratux /traffic WebSocket message ────────────────────
public class TrafficMessage
{
    [JsonPropertyName("Icao_addr")]         public uint Icao_addr { get; set; }
    [JsonPropertyName("Tail")]              public string? Tail { get; set; }
    [JsonPropertyName("Reg")]               public string? Reg { get; set; }
    [JsonPropertyName("Lat")]              public double Lat { get; set; }
    [JsonPropertyName("Lng")]              public double Lng { get; set; }
    [JsonPropertyName("Alt")]              public int Alt { get; set; }
    [JsonPropertyName("Track")]            public double Track { get; set; }
    [JsonPropertyName("Speed")]            public double Speed { get; set; }
    [JsonPropertyName("Age")]              public double Age { get; set; }
    [JsonPropertyName("TargetType")]       public int TargetType { get; set; }  // 2 = UAT
    [JsonPropertyName("Squawk")]           public int Squawk { get; set; }
    [JsonPropertyName("Emitter_category")] public int Emitter_category { get; set; }
    [JsonPropertyName("OnGround")]         public bool OnGround { get; set; }
    [JsonPropertyName("Position_valid")]   public bool Position_valid { get; set; }
}

// ── Processed ownship state ────────────────────────────────
public class OwnshipState
{
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public int AltFt { get; set; }          // baro preferred, GPS fallback
    public int SpeedKts { get; set; }       // ground speed in knots
    public double Track { get; set; }       // true course degrees
    public double HeadingMag { get; set; }  // mag heading (AHRS preferred)
    public double Pitch { get; set; }
    public double Roll { get; set; }
    public bool HasFix { get; set; }

    public bool HasPosition => Lat.HasValue && Lon.HasValue;

    // Default home: KTOA Torrance CA (matches config.js)
    public double DisplayLat => Lat ?? 33.8033;
    public double DisplayLon => Lon ?? -118.3396;
}

// ── Processed traffic target ───────────────────────────────
public class TrafficTarget
{
    public uint IcaoAddr { get; set; }
    public string Hex => IcaoAddr.ToString("x6");
    public string Callsign { get; set; } = "";   // Tail ?? Reg ?? Hex
    public double Lat { get; set; }
    public double Lon { get; set; }
    public int AltBaro { get; set; }
    public double Track { get; set; }
    public double SpeedKts { get; set; }
    public double Age { get; set; }
    public bool IsUat { get; set; }
    public bool OnGround { get; set; }
    public bool PositionValid { get; set; }

    // Computed threat level vs ownship
    public ThreatLevel ThreatLevel { get; set; } = ThreatLevel.Other;
}

public enum ThreatLevel { None, Other, Advisory, Proximate }

// ── Map pages (mirrors config.js pages array) ──────────────
public enum MapPage { Sat = 0, Vfr = 1, Ifr = 2, Radar = 3 }

public static class MapPageInfo
{
    public static readonly string[] Labels = { "SAT", "VFR", "IFR", "RDR" };

    public static string Label(MapPage p) => Labels[(int)p];

    // Tile file paths on Pi — matches TILES.md
    public static string? TilePath(MapPage p) => p switch
    {
        MapPage.Sat   => "/home/pi/tiles/sat.mbtiles",
        MapPage.Vfr   => "/home/pi/tiles/vfr.mbtiles",
        MapPage.Ifr   => "/home/pi/tiles/ifr.mbtiles",
        MapPage.Radar => null,
        _             => null,
    };

    public static string? TacPath(MapPage p) => p switch
    {
        MapPage.Vfr => "/home/pi/tiles/tac.mbtiles",
        _           => null,
    };

    public static int MaxZoom(MapPage p) => p switch
    {
        MapPage.Sat   => 12,
        MapPage.Vfr   => 11,
        MapPage.Ifr   => 11,
        MapPage.Radar => 13,
        _             => 11,
    };
}

// ── Map orientation ────────────────────────────────────────
public enum MapOrientation { North, Track }

// ── Zoom level to NM label ─────────────────────────────────
public static class ZoomLabels
{
    // Computes the true nautical-mile radius from map center to screen edge
    // at the given zoom level and latitude, using correct Web Mercator math.
    // Screen radius is 240px (half of 480px display).
    public static string Get(double zoom, double latitude = 33.85)
    {
        const double ScreenRadiusPx = 240.0;
        const double EarthRadiusM = 6378137.0;

        double circumference = 2 * Math.PI * EarthRadiusM;
        double metersPerPixel = (circumference / (256.0 * Math.Pow(2, zoom))) * Math.Cos(latitude * Math.PI / 180.0);
        double metersAtEdge = ScreenRadiusPx * metersPerPixel;
        double nmAtEdge = metersAtEdge / 1852.0;

        return $"{Math.Round(nmAtEdge)} NM";
    }
}

// ── Range rings (matches config.js rangeRings) ─────────────
public static class RangeRings
{
    public static readonly double[] NauticalMiles = { 2, 5, 10, 20 };
}

// ── Threat thresholds (matches config.js) ─────────────────
public static class ThreatThresholds
{
    public const double ProximateNm     = 6;
    public const double ProximateAltFt  = 1200;
    public const double AdvisoryNm      = 20;
    public const double AdvisoryAltFt   = 3000;
    public const double StaleAgeSec     = 30;
}

// ── User preferences (matches prefs.json) ─────────────────
public class UserPrefs
{
    public string AircraftIcon  { get; set; } = "low_wing";
    public int    LastPage      { get; set; } = 0;
    public int    AltFilter     { get; set; } = 99999;
    public int    DefaultPage   { get; set; } = 0;
    public string Orientation   { get; set; } = "north";

    public MapPage ActivePage   => (MapPage)Math.Clamp(LastPage, 0, 3);
    public MapOrientation MapOrientation =>
        Orientation == "track" ? MapOrientation.Track : MapOrientation.North;
}
