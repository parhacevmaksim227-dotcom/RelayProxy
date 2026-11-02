using System.Diagnostics;
using System.IO;

namespace RelayProxy.Core;

public static class ChromeLauncher
{
    // Stable port lets an existing Relay Chrome window resume after reconnecting.
    public const int Port = 19871;
    public static string? Find() => new[] {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
    }.FirstOrDefault(File.Exists);

    public static void Open()
    {
        Process.Start(CreateStartInfo(Path.Combine(ProfileStore.DirectoryPath, "Chrome")));
    }

    public static ProcessStartInfo CreateStartInfo(string profileDirectory)
    {
        var path = Find() ?? throw new FileNotFoundException("Google Chrome не найден. Установите Chrome или добавьте его .exe в список приложений для режима TUN.");
        var psi = new ProcessStartInfo(path) { UseShellExecute = true };
        foreach (var arg in new[] {
            "--user-data-dir=" + profileDirectory,
            $"--proxy-server=http://127.0.0.1:{Port}", "--disable-quic", "--no-first-run", "--no-default-browser-check", "--new-window", "https://api.ipify.org"
        }) psi.ArgumentList.Add(arg);
        return psi;
    }
}
