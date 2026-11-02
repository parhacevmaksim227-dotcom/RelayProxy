using System.IO;

namespace RelayProxy.Core;

public static class DiagnosticLog
{
    private static readonly object Gate = new();
    public static string FilePath => Path.Combine(ProfileStore.DirectoryPath, "relay.log");
    public static void Write(string text)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(ProfileStore.DirectoryPath);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1024 * 1024)
                    File.Move(FilePath, FilePath + ".previous", true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
