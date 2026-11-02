using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace RelayProxy.Core;

public static class ProfileStore
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelayProxy");
    public static string FilePath => Path.Combine(DirectoryPath, "profile.json");
    public static Profile Load()
    {
        if (!File.Exists(FilePath)) return new();
        var p = JsonSerializer.Deserialize<Profile>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException("Профиль повреждён.");
        if (p.ProtectedPassword.Length > 0) p.Password = Encoding.UTF8.GetString(Protect(Convert.FromBase64String(p.ProtectedPassword), false));
        return p;
    }
    public static void Save(Profile p)
    {
        Directory.CreateDirectory(DirectoryPath);
        p.ProtectedPassword = p.Password.Length == 0 ? "" : Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(p.Password), true));
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
    private static byte[] Protect(byte[] bytes, bool encrypt)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var ok = encrypt ? CryptProtectData(ref input, "RelayProxy", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Не удалось обработать пароль Windows DPAPI.");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { Marshal.Copy(new byte[bytes.Length], 0, input.Data, bytes.Length); Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); Array.Clear(bytes); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
