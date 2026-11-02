using System.IO;
using System.Text.Json;

namespace RelayProxy.Core;

public sealed class DesktopPreferences
{
    // Off by default: the minimize button minimizes to the taskbar like any other window.
    public bool MinimizeToTray { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool StartHidden { get; set; } = true;
    public bool ConnectAtStartup { get; set; } = true;
    public bool ReduceMotion { get; set; }
    // Ask for administrator rights once; later launches elevate silently through a scheduled task.
    public bool AutoElevate { get; set; } = true;
    // Secret "GuSHin Proxy" look, switched on by a code in the settings.
    public bool Sponge { get; set; }
    // The version whose "Что нового?" the user has opened; a newer build highlights the link until then.
    public string LastSeenVersion { get; set; } = "";
    // Layout version of the saved file. Files from before version 2 had "minimize to tray" switched on
    // by default; it is switched off once so the minimize button behaves normally.
    public int Version { get; set; }
    public static string FilePath => Path.Combine(ProfileStore.DirectoryPath, "settings.json");
    public static DesktopPreferences Load(string? path = null)
    {
        path ??= FilePath;
        var loaded = File.Exists(path) ? JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllText(path)) ?? new() : new();
        if (loaded.Version < 2) { loaded.MinimizeToTray = false; loaded.Version = 2; }
        return loaded;
    }
    public void Save(string? path = null)
    {
        path ??= FilePath; Version = 2;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}
