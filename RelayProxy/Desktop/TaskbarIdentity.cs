using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RelayProxy.Desktop;

public static class TaskbarIdentity
{
    public const string AppId = "RelayProxy.Desktop.Glass";
    public static void InitializeProcess() => Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(App.IsTestMode ? AppId + ".Tests" : AppId));
    // The icon file for the current look: the bundled Relay icon, or the sponge drawn in code.
    // Both are cached under %LOCALAPPDATA% because the taskbar needs a file path.
    public static string IconFile(bool sponge)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelayProxy", "Assets");
        Directory.CreateDirectory(folder);
        var iconPath = Path.Combine(folder, sponge ? "gushin-2.0.ico" : "relay-1.8.ico");
        byte[] bytes;
        if (sponge) bytes = SpongeLogo.IconBytes();
        else
        {
            using var source = Application.GetResourceStream(new Uri("pack://application:,,,/RelayProxy;component/relay.ico")).Stream;
            using var memory = new MemoryStream(); source.CopyTo(memory); bytes = memory.ToArray();
        }
        // Refresh the cached copy whenever the icon changes, so a redesign reaches the taskbar.
        if (!File.Exists(iconPath) || !File.ReadAllBytes(iconPath).AsSpan().SequenceEqual(bytes)) File.WriteAllBytes(iconPath, bytes);
        return iconPath;
    }
    public static void Apply(Window window, bool sponge = false)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var iconPath = IconFile(sponge);
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(handle, ref iid, out var store));
        try
        {
            // Set relaunch metadata before the ID (Microsoft's required ordering).
            Set(store, 2, $"\"{Environment.ProcessPath}\"");
            Set(store, 3, iconPath + ",0");
            Set(store, 4, sponge ? "GuSHin Proxy" : "Relay");
            Set(store, 5, App.IsTestMode ? AppId + ".Tests" : AppId);
            Marshal.ThrowExceptionForHR(store.Commit());
        }
        finally { Marshal.ReleaseComObject(store); }
    }
    public static string ReadIconResource(Window window)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(new WindowInteropHelper(window).Handle, ref iid, out var store));
        var key = new PropertyKey(3);
        try
        {
            Marshal.ThrowExceptionForHR(store.GetValue(ref key, out var value));
            try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) ?? "" : ""; }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }
    private static void Set(IPropertyStore store, uint id, string text)
    {
        var key = new PropertyKey(id);
        var value = new PropVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(text) };
        try { Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value)); }
        finally { Marshal.FreeCoTaskMem(value.Pointer); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(uint id)
    {
        public Guid Format = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        public uint Id = id;
    }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr handle, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
}
