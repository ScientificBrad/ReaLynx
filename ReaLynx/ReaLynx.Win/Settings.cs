using System.Text.Json;

namespace ReaLynx.Win;

public enum RotationOverrideMode
{
    Auto = 0,
    None = 1,
    Left = 2,
    Right = 3,
}

public sealed class AppSettings
{
    public string? BootRomPath { get; set; }
    public List<string> RecentFiles { get; set; } = [];
    public float Volume { get; set; } = 0.8f;
    public bool Muted { get; set; }
    public bool IntegerScaling { get; set; } = true;
    public RotationOverrideMode RotationMode { get; set; } = RotationOverrideMode.Auto;
    public Dictionary<string, int> PerCartRotation { get; set; } = [];

    public Dictionary<string, Keys> KeyBindings { get; set; } = new()
    {
        ["Up"] = Keys.Up,
        ["Down"] = Keys.Down,
        ["Left"] = Keys.Left,
        ["Right"] = Keys.Right,
        ["A"] = Keys.Z,
        ["B"] = Keys.X,
        ["Option1"] = Keys.A,
        ["Option2"] = Keys.S,
        ["Pause"] = Keys.Enter,
    };

    private static string SettingsFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReaLynx", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                {
                    loaded.EnsureDefaultBindings();
                    return loaded;
                }
            }
        }
        catch
        {
            // Fall back to defaults on corrupt settings file
        }
        var defaults = new AppSettings();
        defaults.TryAutoDiscoverBootRom();
        return defaults;
    }

    public void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
            // Ignore settings write errors
        }
    }

    public void AddRecentFile(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 10)
            RecentFiles.RemoveRange(10, RecentFiles.Count - 10);
        Save();
    }

    public void EnsureDefaultBindings()
    {
        var defaults = new AppSettings().KeyBindings;
        foreach (var kv in defaults)
            KeyBindings.TryAdd(kv.Key, kv.Value);
    }

    public void TryAutoDiscoverBootRom()
    {
        if (!string.IsNullOrEmpty(BootRomPath) && File.Exists(BootRomPath))
            return;

        // Check common workspace relative locations for legally supplied lynxboot.img
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "lynxboot.img"),
            Path.Combine(baseDir, "BOOTROM", "lynxboot.img"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "BOOTROM", "lynxboot.img")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "BOOTROM", "lynxboot.img")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "BOOTROM", "lynxboot.img")),
        ];

        foreach (string c in candidates)
        {
            if (File.Exists(c) && new FileInfo(c).Length == 512)
            {
                BootRomPath = c;
                return;
            }
        }
    }
}
