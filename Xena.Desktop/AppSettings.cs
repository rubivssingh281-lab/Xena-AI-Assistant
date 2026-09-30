using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace Xena.Desktop;

/// <summary>A window's size and place, remembered between sessions.</summary>
internal sealed class WindowBounds
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }

    public static WindowBounds Capture(Window window)
    {
        var r = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
            : window.RestoreBounds;
        return new WindowBounds
        {
            Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height,
            Maximized = window.WindowState == WindowState.Maximized,
        };
    }

    /// <summary>Puts the window back where it was, as long as that spot is still on a screen.</summary>
    public void ApplyTo(Window window)
    {
        if (Width < 200 || Height < 150 || double.IsNaN(Left) || double.IsNaN(Top)) return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var place = new Rect(Left, Top, Width, Height);
        if (!screen.IntersectsWith(place) || place.Top < screen.Top - 10) return;   // e.g. a monitor was unplugged
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = Left;
        window.Top = Top;
        window.Width = System.Math.Max(Width, window.MinWidth);
        window.Height = System.Math.Max(Height, window.MinHeight);
        if (Maximized) window.WindowState = WindowState.Maximized;
    }
}

/// <summary>Main window preferences (%APPDATA%\Xena\main.json).</summary>
internal sealed class MainSettings
{
    public WindowBounds? Window { get; set; }
    public List<string> History { get; set; } = new();
    public bool VoiceOn { get; set; } = true;

    private static string FilePath => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Xena", "main.json");

    public static MainSettings Load()
    {
        try { return JsonSerializer.Deserialize<MainSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* preferences are a convenience */ }
    }
}
