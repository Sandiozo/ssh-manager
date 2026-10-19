using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SSHManager;

/// <summary>The app's icon, and the tray icons: the icon with a dot showing the connection state.</summary>
internal static class AppIcon
{
    static readonly Dictionary<LinkState, Icon> Cache = [];
    static Image? logo;

    public static Icon App => Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;

    /// <summary>The icon as a PNG (embedded resource), e.g. for the app's own pages.</summary>
    public static byte[] LogoPng()
    {
        using var s = typeof(AppIcon).Assembly.GetManifestResourceStream("icon-64.png")!;
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    static Image Logo => logo ??= Image.FromStream(new MemoryStream(LogoPng()));

    public static Icon For(LinkState state)
    {
        if (Cache.TryGetValue(state, out var icon)) return icon;
        var color = state switch
        {
            LinkState.Connected => Color.FromArgb(52, 199, 123),
            LinkState.Connecting => Color.FromArgb(232, 163, 23),
            _ => Color.FromArgb(239, 83, 80),
        };
        var size = Math.Clamp(SystemInformation.SmallIconSize.Width * 2, 16, 32);
        using var bmp = Draw(size, color);
        var handle = bmp.GetHicon();
        icon = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        return Cache[state] = icon;
    }

    static Bitmap Draw(int s, Color dot)
    {
        var bmp = new Bitmap(s, s);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(Logo, 0, 0, s, s);
        // status dot in the bottom-right corner, with a dark ring so it reads on any taskbar
        var d = s * 0.44f;
        var ring = Math.Max(1.5f, s / 14f);
        var rect = new RectangleF(s - d, s - d, d, d);
        using (var b = new SolidBrush(Color.FromArgb(20, 22, 24))) g.FillEllipse(b, rect);
        rect.Inflate(-ring, -ring);
        using (var b = new SolidBrush(dot)) g.FillEllipse(b, rect);
        return bmp;
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
