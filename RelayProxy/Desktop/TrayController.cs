using System.IO;
using System.Drawing;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace RelayProxy.Desktop;

public sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private Icon idle = null!, busyIcon = null!, live = null!;
    private readonly Action restore, toggle, settings, exit;
    private bool connected, chromeOnly, busy;
    private TrayMenu? menu;

    // Idle, busy and live variants of the tray icon for the current look (Relay or the sponge theme).
    private void BuildIcons(bool sponge)
    {
        byte[] bytes;
        if (sponge) bytes = SpongeLogo.IconBytes();
        else
        {
            using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/RelayProxy;component/relay.ico")).Stream;
            using var copy = new MemoryStream(); stream.CopyTo(copy); bytes = copy.ToArray();
        }
        using var memory = new MemoryStream(bytes);
        using var baseIcon = new Icon(memory, Forms.SystemInformation.SmallIconSize);
        var old = new[] { idle, busyIcon, live };
        idle = (Icon)baseIcon.Clone();
        busyIcon = Badge(baseIcon, Color.FromArgb(160, 160, 160));
        live = Badge(baseIcon, Color.FromArgb(240, 240, 240));
        foreach (var icon in old) icon?.Dispose();
    }

    public TrayController(Action restore, Action toggle, Action settings, Action exit)
    {
        this.restore = restore; this.toggle = toggle; this.settings = settings; this.exit = exit;
        BuildIcons(Brand.Sponge);
        icon = new Forms.NotifyIcon { Icon = idle, Text = Brand.Name + " · прокси выключен", Visible = true };
        icon.DoubleClick += (_, _) => restore(); icon.BalloonTipClicked += (_, _) => restore();
        icon.MouseUp += (_, e) => { if (e.Button == Forms.MouseButtons.Right) ShowMenu(); };
    }

    // The menu is a WPF window created on demand and thrown away when it closes, so it costs no memory while idle.
    private void ShowMenu()
    {
        if (menu is { IsLoaded: true }) { menu.Close(); }
        var text = connected ? chromeOnly ? "Только Chrome" : "Приложения подключены" : busy ? "Подключение…" : "Прокси выключен";
        menu = new TrayMenu(Forms.Cursor.Position, text, connected, busy, connected && !chromeOnly, restore, toggle, settings, exit);
        menu.Closed += (_, _) => menu = null;
        menu.Show();
    }

    public void Update(bool connected, bool chromeOnly, bool busy)
    {
        this.connected = connected; this.chromeOnly = chromeOnly; this.busy = busy;
        icon.Text = Brand.Name + (connected ? chromeOnly ? " · только Chrome" : " · приложения подключены" : " · прокси выключен");
        icon.Icon = busy ? busyIcon : connected ? live : idle;
    }
    // The look changed: rebuild the icons (the NotifyIcon must stop using the old ones first).
    public void Rebrand()
    {
        icon.Icon = null; BuildIcons(Brand.Sponge); Update(connected, chromeOnly, busy);
    }
    public void Notify(string message) { icon.BalloonTipTitle = Brand.Name; icon.BalloonTipText = message; icon.ShowBalloonTip(4000); }
    public void Dispose()
    {
        menu?.Close();
        icon.Visible = false; icon.Dispose();
        idle.Dispose(); busyIcon.Dispose(); live.Dispose();
    }

    // A status dot in the lower right corner shows the connection state at a glance.
    private static Icon Badge(Icon source, Color color)
    {
        using var bitmap = source.ToBitmap();
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var d = Math.Max(6, bitmap.Width * .46f); var x = bitmap.Width - d - .5f; var y = bitmap.Height - d - .5f;
            using var ring = new SolidBrush(Color.FromArgb(235, 12, 12, 14)); using var fill = new SolidBrush(color);
            g.FillEllipse(ring, x - 1.2f, y - 1.2f, d + 2.4f, d + 2.4f); g.FillEllipse(fill, x, y, d, d);
        }
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); } finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
}
