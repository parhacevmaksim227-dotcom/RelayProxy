using System.IO;
using System.Text.RegularExpressions;

namespace RelayProxy.Core;

public static class ApplicationRules
{
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "browser", "yandex", "iexplore", "chromium", "waterfox", "librewolf", "arc", "thorium", "maxthon"
    };
    // Programs that browse the web and are therefore steered by the site list.
    public static bool IsBrowser(string path) => Browsers.Contains(System.IO.Path.GetFileNameWithoutExtension(path));

    // Browsers installed on this computer, from their usual folders and the Windows list of web browsers.
    // The site list applies to them automatically, so they never have to be added under "Applications".
    public static string[] InstalledBrowsers()
    {
        string Pf(Environment.SpecialFolder folder, params string[] parts) => System.IO.Path.Combine([Environment.GetFolderPath(folder), .. parts]);
        var candidates = new List<string>
        {
            Pf(Environment.SpecialFolder.ProgramFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Pf(Environment.SpecialFolder.ProgramFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            Pf(Environment.SpecialFolder.LocalApplicationData, "Google", "Chrome", "Application", "chrome.exe"),
            Pf(Environment.SpecialFolder.ProgramFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Pf(Environment.SpecialFolder.ProgramFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
            Pf(Environment.SpecialFolder.ProgramFiles, "Mozilla Firefox", "firefox.exe"),
            Pf(Environment.SpecialFolder.ProgramFilesX86, "Mozilla Firefox", "firefox.exe"),
            Pf(Environment.SpecialFolder.LocalApplicationData, "Yandex", "YandexBrowser", "Application", "browser.exe"),
            Pf(Environment.SpecialFolder.LocalApplicationData, "Programs", "Opera", "opera.exe"),
            Pf(Environment.SpecialFolder.LocalApplicationData, "Programs", "Opera GX", "opera.exe"),
            Pf(Environment.SpecialFolder.ProgramFiles, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
            Pf(Environment.SpecialFolder.LocalApplicationData, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
            Pf(Environment.SpecialFolder.LocalApplicationData, "Vivaldi", "Application", "vivaldi.exe"),
        };
        foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            try
            {
                using var clients = hive.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet");
                foreach (var name in clients?.GetSubKeyNames() ?? [])
                {
                    using var command = clients!.OpenSubKey(name + @"\shell\open\command");
                    if (command?.GetValue(null) is string line && line.Trim().Trim('"').Split('"')[0] is { Length: > 0 } exe) candidates.Add(exe.Trim());
                }
            }
            catch { /* a broken registry entry must not stop the rest */ }
        }
        return candidates.Where(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string[] Patterns(IEnumerable<AppRule> apps) => apps.Where(a => a.Enabled)
        .SelectMany(PatternsFor).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static IEnumerable<string> PatternsFor(AppRule app)
    {
        var path = app.Path.Replace('/', '\\');
        yield return "(?i)^" + VersionSafe(path) + "$";
        var folder = Path.GetDirectoryName(path)?.TrimEnd('\\');
        if (app.IncludeHelpers && folder != null && IsApplicationFolder(folder))
            yield return "(?i)^" + VersionSafe(folder) + @"\\.*\.exe$";
    }

    // Store apps change their versioned package path during an update.
    private static string VersionSafe(string path)
    {
        var package = Regex.Match(path, @"(?i)\\WindowsApps\\[^\\]+?_(\d+\.\d+\.\d+\.\d+)_[^\\]+\\");
        if (!package.Success) return Regex.Escape(path);
        var version = package.Groups[1];
        return Regex.Escape(path[..version.Index]) + @"\d+\.\d+\.\d+\.\d+" + Regex.Escape(path[(version.Index + version.Length)..]);
    }

    private static bool IsApplicationFolder(string folder)
    {
        var blocked = new[] {
            Path.GetPathRoot(folder), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System), Path.GetTempPath(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        };
        return !blocked.Any(p => !string.IsNullOrEmpty(p) && folder.Equals(p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    }
}
