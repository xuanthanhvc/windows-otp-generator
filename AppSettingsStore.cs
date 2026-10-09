using System.IO;
using System.Text.Json;

namespace WinOtp;

/// <summary>Loads and saves window.json (geometry + app preferences).</summary>
internal static class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            AccountStore.EnsureDataDirectory();
            string path = ResolvePath();
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        AccountStore.EnsureDataDirectory();
        AtomicFile.WriteAllText(ResolvePath(), JsonSerializer.Serialize(settings, JsonOptions));
    }

    private static string ResolvePath()
    {
        if (File.Exists(AccountStore.WindowStatePath))
        {
            return AccountStore.WindowStatePath;
        }

        if (File.Exists(AccountStore.PortableWindowStatePath))
        {
            return AccountStore.PortableWindowStatePath;
        }

        return AccountStore.WindowStatePath;
    }
}

/// <summary>Persisted window size, position, and app preferences (window.json).</summary>
public sealed class AppSettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool MinimizeToTray { get; set; }
}
