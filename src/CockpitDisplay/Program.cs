using System;
using Avalonia;
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

        if (isHeadless || args.Contains("--fbdev"))
        {
            Console.Write("\x1b[?25l");
            BuildAvaloniaApp().StartLinuxFbDev(args);
        }
        else
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseReactiveUI()
            .WithInterFont()
            .LogToTrace();
}
