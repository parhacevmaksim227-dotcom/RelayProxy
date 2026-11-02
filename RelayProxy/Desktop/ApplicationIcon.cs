using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;

namespace RelayProxy.Desktop;

public sealed class ApplicationIcon : IValueConverter
{
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    public static ImageSource Get(string path) => Cache.GetOrAdd(path, Extract);
    private static ImageSource Extract(string path)
    {
        try
        {
            if (PackageIcon(path) is ImageSource packageIcon) return packageIcon;
            // Never launch the executable; Windows extracts its icon resource.
            if (File.Exists(path))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                {
                    var image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                    image.Freeze(); return image;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException or NotSupportedException) { }
        var fallback = new DrawingImage(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(197, 197, 197)), new Pen(Brushes.White, 1), new RectangleGeometry(new Rect(3, 3, 26, 26), 7, 7)));
        fallback.Freeze(); return fallback;
    }
    private static ImageSource? PackageIcon(string path)
    {
        if (!path.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var directory = new DirectoryInfo(Path.GetDirectoryName(path)!);
            while (directory.Parent != null && !directory.Parent.Name.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)) directory = directory.Parent;
            var manifest = Path.Combine(directory.FullName, "AppxManifest.xml");
            if (!File.Exists(manifest)) return null;
            using var reader = XmlReader.Create(manifest, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var app = XDocument.Load(reader).Descendants().FirstOrDefault(e => e.Name.LocalName == "Application" &&
                string.Equals((string?)e.Attribute("Executable"), Path.GetRelativePath(directory.FullName, path), StringComparison.OrdinalIgnoreCase));
            var logo = app?.Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualElements")?.Attribute("Square44x44Logo")?.Value;
            if (logo == null) return null;
            var asset = Path.GetFullPath(Path.Combine(directory.FullName, logo));
            if (!asset.StartsWith(directory.FullName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            var candidates = Directory.GetFiles(Path.GetDirectoryName(asset)!, Path.GetFileNameWithoutExtension(asset) + "*.png");
            var selected = candidates.OrderByDescending(p => p.Contains("altform-unplated", StringComparison.OrdinalIgnoreCase) && (p.Contains("targetsize-48", StringComparison.OrdinalIgnoreCase) || p.Contains("targetsize-44", StringComparison.OrdinalIgnoreCase)))
                .ThenByDescending(p => p.Contains("scale-200", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
            if (selected == null) return null;
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 64; bitmap.UriSource = new Uri(selected); bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or ArgumentException or NotSupportedException) { return null; }
    }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Get(value as string ?? "");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
