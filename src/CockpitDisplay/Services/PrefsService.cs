using System;
using System.IO;
using System.Text.Json;
using CockpitDisplay.Models;

namespace CockpitDisplay.Services;

/// <summary>
/// Loads and saves user preferences from prefs.json.
/// Maintains the same file format as the web app so preferences
/// are portable between the web and native versions.
///
/// prefs.json fields (from web app):
///   aircraftIcon  — icon variant id (e.g. "low_wing", "high_wing")
///   lastPage      — last active page index (0=SAT, 1=VFR, 2=IFR, 3=RADAR)
///   altFilter     — altitude filter in feet (e.g. 3000)
///   defaultPage   — default startup page index
///   orientation   — "north" or "track"
/// </summary>
public class PrefsService
{
    private readonly string _path;
    private UserPrefs _prefs;

    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        WriteIndented               = false,
    };

    public PrefsService(string? path = null)
    {
        _path = path ?? DefaultPath();
        _prefs = Load();
    }

    public UserPrefs Current => _prefs;

    public void Save(Action<UserPrefs> mutate)
    {
        try
        {
            mutate(_prefs);
            File.WriteAllText(_path, JsonSerializer.Serialize(_prefs, _opts));
            Console.Error.WriteLine($"[Prefs] Saved to {_path}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Prefs] Save failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private UserPrefs Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<UserPrefs>(json, _opts)
                       ?? new UserPrefs();
            }
        }
        catch { }
        return new UserPrefs();
    }

    private static string DefaultPath()
    {
        // prefs.json lives next to the app binary (matches old web app behavior)
        var appDir = AppContext.BaseDirectory;
        return Path.Combine(appDir, "prefs.json");
    }
}
