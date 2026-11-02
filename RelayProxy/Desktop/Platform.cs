namespace RelayProxy.Desktop;

// Which Windows the app runs on. Windows 10 (1809 and later) is supported alongside Windows 11;
// the few features that only exist on Windows 11 are skipped there instead of failing.
public static class Platform
{
    public static int Build { get; } = Environment.OSVersion.Version.Build;
    public static bool IsWindows11 => Build >= 22000;
    public static string Name => IsWindows11 ? "Windows 11" : "Windows 10";
    // Segoe UI Variable ships with Windows 11 only; Windows 10 falls back to Segoe UI.
    public static bool HasVariableFont => System.Windows.Media.Fonts.SystemFontFamilies.Any(f => f.Source.StartsWith("Segoe UI Variable", StringComparison.OrdinalIgnoreCase));
}
