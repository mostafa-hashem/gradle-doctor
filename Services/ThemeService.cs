using System.Windows;
using Microsoft.Win32;

namespace GradleDoctor;

public enum ThemeMode { Light, Dark, System }

public static class ThemeService
{
    static ResourceDictionary? _applied;
    public static ThemeMode Current { get; private set; } = ThemeMode.Light;
    public static event Action<ThemeMode>? ThemeChanged;

    public static ThemeMode Parse(string? value) =>
        Enum.TryParse<ThemeMode>(value, true, out var mode) ? mode : ThemeMode.Light;

    public static ThemeMode Resolve(ThemeMode mode) =>
        mode == ThemeMode.System ? (IsSystemDark() ? ThemeMode.Dark : ThemeMode.Light) : mode;

    public static void Apply(ThemeMode mode)
    {
        var app = Application.Current;
        if (app == null) return;

        var resolved = Resolve(mode);
        var dict = new ResourceDictionary
        {
            Source = new Uri($"Themes/{resolved}.xaml", UriKind.Relative)
        };

        if (_applied != null)
            app.Resources.MergedDictionaries.Remove(_applied);

        for (var i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            var source = app.Resources.MergedDictionaries[i].Source?.OriginalString ?? "";
            if (source.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                source.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase))
                app.Resources.MergedDictionaries.RemoveAt(i);
        }

        app.Resources.MergedDictionaries.Add(dict);
        _applied = dict;
        Current = mode;
        ThemeChanged?.Invoke(mode);
    }

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch { return false; }
    }

    public static string Label(ThemeMode mode) => mode switch
    {
        ThemeMode.Dark => "Dark",
        ThemeMode.System => "System",
        _ => "Light"
    };
}
