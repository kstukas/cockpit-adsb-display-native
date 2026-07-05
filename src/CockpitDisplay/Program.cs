using System;
using Avalonia;
using Avalonia.LinuxFramebuffer;
using Avalonia.LinuxFramebuffer.Output;
using Avalonia.ReactiveUI;

namespace CockpitDisplay;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        bool isHeadless = OperatingSystem.IsLinux() &&
                          string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) &&
                          string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

        if (isHeadless || args.Contains("--fbdev") || args.Contains("--drm"))
        {
            Console.Write("\x1b[?25l");

            // Prefer DRM/KMS (GPU-accelerated via GBM/EGL) over fbdev
            // (pure software blit). --fbdev forces the old software path.
            if (!args.Contains("--fbdev") && TryCreateDrmOutput() is { } drm)
                BuildAvaloniaApp().StartLinuxDirect(args, drm);
            else
                BuildAvaloniaApp().StartLinuxFbDev(args);
        }
        else
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
    }

    // Probe DRM cards in order; on the Pi the display controller (vc4) and the
    // 3D core (v3d) enumerate as separate cards whose order can vary by boot,
    // so try both. COCKPIT_DRM_CARD overrides (e.g. "/dev/dri/card1").
    private static DrmOutput? TryCreateDrmOutput()
    {
        var candidates = new List<string>();
        string? overrideCard = Environment.GetEnvironmentVariable("COCKPIT_DRM_CARD");
        if (!string.IsNullOrEmpty(overrideCard))
            candidates.Add(overrideCard);
        candidates.Add("/dev/dri/card0");
        candidates.Add("/dev/dri/card1");

        foreach (var card in candidates.Distinct().Where(File.Exists))
        {
            try
            {
                var output = new DrmOutput(card);
                Console.Error.WriteLine($"[Display] Using DRM/KMS output on {card} (GPU-accelerated)");
                return output;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Display] DRM init failed on {card}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.Error.WriteLine("[Display] No usable DRM card — falling back to fbdev (software rendering)");
        return null;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseReactiveUI()
            .WithInterFont()
            .LogToTrace();
}
