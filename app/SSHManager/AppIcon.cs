using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace SSHManager;

/// <summary>Tray icons: the app's ">_" mark, colored by connection state.</summary>
internal static class AppIcon
{
    static readonly Dictionary<LinkState, Icon> Cache = [];

    public static Icon App => Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;

    public static Icon For(LinkState state)
    {
        if (Cache.TryGetValue(state, out var icon)) return icon;
        var color = state switch
        {
            LinkState.Connected => Color.FromArgb(34, 197, 94),
            LinkState.Connecting => Color.FromArgb(245, 158, 11),
            _ => Color.FromArgb(239, 68, 68),
        };
        var size = SystemInformation.SmallIconSize.Width * 2 > 32 ? 32 : SystemInformation.SmallIconSize.Width * 2;
        using var bmp = Draw(Math.Max(16, size), color);
        var handle = bmp.GetHicon();
        icon = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        return Cache[state] = icon;
    }

    static Bitmap Draw(int s, Color bg)
    {
        var bmp = new Bitmap(s, s);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var r = Math.Max(2, (int)(s * 0.22));
        var d = r * 2;
        using var path = new GraphicsPath();
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(s - d - 1, 0, d, d, 270, 90);
        path.AddArc(s - d - 1, s - d - 1, d, d, 0, 90);
        path.AddArc(0, s - d - 1, d, d, 90, 90);
        path.CloseFigure();
        using (var b = new SolidBrush(bg)) g.FillPath(b, path);
        using var font = new Font("Consolas", s * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var fg = new SolidBrush(Color.FromArgb(10, 20, 14));
        g.DrawString(">_", font, fg, new RectangleF(0, 0, s, s), fmt);
        return bmp;
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
