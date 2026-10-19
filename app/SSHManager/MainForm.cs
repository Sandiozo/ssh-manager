using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace SSHManager;

/// <summary>The dashboard (served by the managed PC) in a WebView2 window, plus a tray icon that keeps
/// the connection alive while the window is closed.</summary>
internal sealed class MainForm : Form
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    readonly AppSettings settings;
    readonly Link link;
    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    readonly NotifyIcon tray = new();
    readonly ToolStripMenuItem startupItem = new("Start with Windows");
    bool webReady, exiting, wasConnected, everConnected, showingDashboard, wizardOpen;
    readonly SetupWizard wizard;
    int downloadSeq;
    readonly bool startHidden;

    public MainForm(AppSettings settings, bool startHidden)
    {
        this.settings = settings;
        this.startHidden = startHidden;
        link = new Link(settings);
        wizard = new SetupWizard(settings,
            html => { if (webReady) web.CoreWebView2.NavigateToString(html); },
            () => { wizardOpen = false; link.Reconnect(); });

        Text = "SSH Manager";
        Icon = AppIcon.App;
        MinimumSize = new Size(760, 520);
        BackColor = SystemDark() ? Color.FromArgb(13, 17, 23) : Color.FromArgb(245, 247, 250);
        StartPosition = FormStartPosition.Manual;
        RestoreBounds_();
        Controls.Add(web);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open SSH Manager", null, (_, _) => ShowWindow());
        menu.Items.Add("Reconnect", null, (_, _) => link.Reconnect());
        menu.Items.Add("Open in browser", null, (_, _) => OpenExternal(link.Url));
        menu.Items.Add(new ToolStripSeparator());
        startupItem.Checked = StartsWithWindows();
        startupItem.Click += (_, _) => ToggleStartup();
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        tray.ContextMenuStrip = menu;
        tray.Icon = AppIcon.For(LinkState.Connecting);
        tray.Text = "SSH Manager: connecting";
        tray.Visible = true;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        tray.BalloonTipClicked += (_, _) => ShowWindow();

        link.Changed += (state, message) =>
        {
            if (IsHandleCreated && !IsDisposed) BeginInvoke(() => OnLinkChanged(state, message));
        };
    }

    protected override void SetVisibleCore(bool value)
    {
        // --tray: create the window (so the link and tray work) but keep it hidden
        if (startHidden && !IsHandleCreated)
        {
            CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override async void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (SystemDark())
        {
            var on = 1;
            DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));   // dark title bar
        }
        link.Start();
        try
        {
            // --devtools-port=N (testing only) exposes the page to Chrome DevTools Protocol on 127.0.0.1:N
            var port = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--devtools-port="))?.Split('=')[1];
            var options = new CoreWebView2EnvironmentOptions(port is null ? null : $"--remote-debugging-port={int.Parse(port)}");
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppSettings.Folder, "WebView2"), options);
            await web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show("SSH Manager needs the Microsoft Edge WebView2 Runtime.\n\n" + ex.Message, "SSH Manager",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            ExitApp();
            return;
        }
        var core = web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = true;
        core.NewWindowRequested += (_, a) => { a.Handled = true; OpenExternal(a.Uri); };
        core.WebMessageReceived += (_, a) => OnPageMessage(a.WebMessageAsJson);
        core.DownloadStarting += OnDownloadStarting;
        core.DocumentTitleChanged += (_, _) => Text = wizardOpen ? "SSH Manager: connect to a PC"
            : showingDashboard ? "SSH Manager" : "SSH Manager (not connected)";
        web.NavigationCompleted += async (_, a) =>
        {
            // the server restarted under us: try again shortly while the link is still up
            if (!a.IsSuccess && showingDashboard && link.State == LinkState.Connected)
            {
                await Task.Delay(2000);
                if (link.State == LinkState.Connected) web.CoreWebView2.Navigate(link.Url);
            }
        };
        webReady = true;
        OnLinkChanged(link.State, link.Message);
    }

    void OnLinkChanged(LinkState state, string message)
    {
        tray.Icon = AppIcon.For(state);
        var pc = settings.DisplayName;
        var trayText = state switch
        {
            LinkState.Connected => $"SSH Manager: connected to {pc}",
            LinkState.Connecting => "SSH Manager: connecting",
            _ => "SSH Manager: not connected",
        };
        tray.Text = trayText.Length > 63 ? trayText[..63] : trayText;

        if (state == LinkState.Connected)
        {
            if (!wasConnected && everConnected && settings.NotifyConnection && !IsActive())
                tray.ShowBalloonTip(3000, "SSH Manager", $"Connected to {pc} again.", ToolTipIcon.Info);
            wasConnected = everConnected = true;
        }
        else if (state == LinkState.Disconnected && wasConnected)
        {
            wasConnected = false;
            if (settings.NotifyConnection && !IsActive())
                tray.ShowBalloonTip(5000, "SSH Manager", $"Lost the connection to {pc}. Reconnecting…", ToolTipIcon.Warning);
        }

        if (!webReady || wizardOpen) return;
        if (state == LinkState.NeedsSetup)
        {
            OpenWizard();
        }
        else if (state == LinkState.Connected)
        {
            if (!showingDashboard || web.Source?.Port != link.Port)
            {
                // the server only answers requests that carry its access token
                var cookies = web.CoreWebView2.CookieManager;
                cookies.AddOrUpdateCookie(cookies.CreateCookie("sshmgr_token", link.Token, "localhost", "/"));
                showingDashboard = true;
                web.CoreWebView2.Navigate(link.Url);
            }
        }
        else
        {
            showingDashboard = false;
            web.CoreWebView2.NavigateToString(StatusPage(state, message));
        }
        Text = state == LinkState.Connected ? "SSH Manager" : "SSH Manager (not connected)";
    }

    void OpenWizard()
    {
        wizardOpen = true;
        showingDashboard = false;
        Text = "SSH Manager: connect to a PC";
        wizard.Open();
    }

    bool IsActive() => Visible && WindowState != FormWindowState.Minimized && ActiveForm == this;

    void Post(object message)
    {
        if (webReady && showingDashboard)
            web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    /// <summary>Messages from the dashboard page (see the "desktop app" section of static/app.js).</summary>
    void OnPageMessage(string json)
    {
        JsonElement m;
        try { m = JsonDocument.Parse(json).RootElement; } catch (JsonException) { return; }
        if (m.ValueKind == JsonValueKind.String)
        {
            if (m.GetString() == "retry") link.Reconnect();
            return;
        }
        if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("type", out var t)) return;
        string Str(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        switch (t.GetString())
        {
            case { } type when type.StartsWith("setup-"):
                wizard.OnMessage(type, Str);
                break;
            case "open-setup":
                OpenWizard();
                break;
            case "auth-failed":
                link.Reconnect();
                break;
            case "get-app-settings":
                PostAppSettings();
                break;
            case "set-app-settings" when m.TryGetProperty("settings", out var s):
                ApplyAppSettings(s);
                break;
            case "pick-download-folder":
                using (var dlg = new FolderBrowserDialog { SelectedPath = settings.DownloadFolder, Description = "Save downloads to" })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        settings.DownloadFolder = dlg.SelectedPath;
                        settings.Save();
                    }
                }
                PostAppSettings();
                break;
            case "show-file":
                var path = Str("path");
                if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
                break;
            case "notify":
                var enabled = Str("kind") switch
                {
                    "failed-login" => settings.NotifyFailedLogin,
                    "unknown" => settings.NotifyUnknown,
                    "transfers" => settings.NotifyTransfers,
                    _ => false,
                };
                if (enabled && !IsActive())
                    tray.ShowBalloonTip(6000, Str("title"), Str("text"),
                        Str("kind") == "transfers" ? ToolTipIcon.Info : ToolTipIcon.Warning);
                break;
        }
    }

    void PostAppSettings() => Post(new
    {
        type = "app-settings",
        machine = Environment.MachineName,
        settings = new
        {
            StartWithWindows = StartsWithWindows(),
            settings.NotifyConnection,
            settings.NotifyFailedLogin,
            settings.NotifyUnknown,
            settings.NotifyTransfers,
            settings.DownloadFolder,
            settings.AskWhereToSave,
            Connection = link.IsLocal ? "this PC (local)" : settings.Target,
        },
    });

    void ApplyAppSettings(JsonElement s)
    {
        foreach (var p in s.EnumerateObject())
        {
            bool Bool() => p.Value.ValueKind == JsonValueKind.True;
            switch (p.Name)
            {
                case "StartWithWindows": if (Bool() != StartsWithWindows()) ToggleStartup(); break;
                case "NotifyConnection": settings.NotifyConnection = Bool(); break;
                case "NotifyFailedLogin": settings.NotifyFailedLogin = Bool(); break;
                case "NotifyUnknown": settings.NotifyUnknown = Bool(); break;
                case "NotifyTransfers": settings.NotifyTransfers = Bool(); break;
                case "AskWhereToSave": settings.AskWhereToSave = Bool(); break;
            }
        }
        settings.Save();
        PostAppSettings();
    }

    /// <summary>Save downloads natively (no browser download bar) and report progress to the page,
    /// which shows them in its transfer panel.</summary>
    void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Handled = true;
        var op = e.DownloadOperation;
        var name = Path.GetFileName(e.ResultFilePath);
        if (settings.AskWhereToSave)
        {
            using var dlg = new SaveFileDialog { FileName = name, InitialDirectory = settings.DownloadFolder, OverwritePrompt = true };
            if (dlg.ShowDialog(this) != DialogResult.OK)
            {
                e.Cancel = true;
                return;
            }
            e.ResultFilePath = dlg.FileName;
        }
        else
        {
            Directory.CreateDirectory(settings.DownloadFolder);
            e.ResultFilePath = UniquePath(Path.Combine(settings.DownloadFolder, name));
        }

        var id = ++downloadSeq;
        var target = e.ResultFilePath;
        void Report(string state, string? reason = null) => Post(new
        {
            type = "download", id, name = Path.GetFileName(target), received = op.BytesReceived,
            total = op.TotalBytesToReceive ?? 0, state, path = target, reason,
        });
        op.BytesReceivedChanged += (_, _) => Report("inprogress");
        op.StateChanged += (_, _) =>
        {
            if (op.State == CoreWebView2DownloadState.Completed) Report("completed");
            else if (op.State == CoreWebView2DownloadState.Interrupted) Report("interrupted", op.InterruptReason.ToString());
        };
        BeginInvoke(() => Report("inprogress"));
    }

    static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    static string StatusPage(LinkState state, string message)
    {
        var connecting = state == LinkState.Connecting;
        var lines = message.Split('\n', 2);
        var title = WebUtility.HtmlEncode(lines[0]);
        var detail = lines.Length > 1 ? $"<pre>{WebUtility.HtmlEncode(lines[1].Trim())}</pre>" : "";
        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><style>
              :root { color-scheme: dark; --bg:#0d1117; --panel:#151b23; --line:#273140; --text:#e6edf3; --muted:#8b98a8; }
              @media (prefers-color-scheme: light) { :root { color-scheme: light; --bg:#f5f7fa; --panel:#fff; --line:#dde3ea; --text:#17202b; --muted:#5f6b7a; } }
              body { margin:0; height:100vh; display:flex; align-items:center; justify-content:center; background:var(--bg); color:var(--text);
                     font:14px/1.5 "Segoe UI", system-ui, sans-serif; }
              .box { max-width:560px; padding:28px 32px; background:var(--panel); border:1px solid var(--line); border-radius:12px; text-align:center; }
              .logo { display:inline-block; font:700 18px Consolas, monospace; background:{{(connecting ? "#f59e0b" : "#ef4444")}}; color:#111;
                      border-radius:8px; padding:6px 10px; margin-bottom:14px; }
              h1 { font-size:17px; margin:0 0 6px; } p { color:var(--muted); margin:0 0 16px; }
              pre { text-align:left; white-space:pre-wrap; font:12px Consolas, monospace; color:var(--muted); background:var(--bg);
                    border:1px solid var(--line); border-radius:8px; padding:10px; max-height:180px; overflow:auto; }
              button { font:inherit; padding:7px 18px; border-radius:7px; border:1px solid #3b82f6; background:#3b82f6; color:#fff; cursor:pointer; }
              .spin { width:18px; height:18px; border:2px solid var(--line); border-top-color:#f59e0b; border-radius:50%;
                      display:inline-block; animation:s 0.9s linear infinite; vertical-align:middle; margin-right:8px; }
              @keyframes s { to { transform:rotate(360deg); } }
            </style></head><body><div class="box">
              <div class="logo">&gt;_</div>
              <h1>{{(connecting ? "<span class=\"spin\"></span>" : "")}}{{title}}</h1>
              <p>{{(connecting ? "Opening an SSH tunnel." : "SSH Manager keeps retrying in the background.")}}</p>
              {{detail}}
              {{(connecting ? "" : "<button onclick=\"chrome.webview.postMessage('retry')\">Retry now</button>")}}
            </div></body></html>
            """;
    }

    public void ShowWindow()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = settings.Maximized ? FormWindowState.Maximized : FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveBounds();
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        {
            // closing the window keeps the tunnel and tray icon; Exit is in the tray menu
            e.Cancel = true;
            Hide();
            if (!settings.TrayHintShown)
            {
                tray.ShowBalloonTip(5000, "SSH Manager is still running",
                    "It stays connected in the tray. Right-click the tray icon and choose Exit to close it.", ToolTipIcon.Info);
                settings.TrayHintShown = true;
                settings.Save();
            }
            return;
        }
        base.OnFormClosing(e);
    }

    void ExitApp()
    {
        exiting = true;
        SaveBounds();
        link.Dispose();
        tray.Visible = false;
        tray.Dispose();
        Application.Exit();
    }

    void RestoreBounds_()
    {
        var b = settings.WindowBounds;
        var rect = b is { Length: 4 } ? new Rectangle(b[0], b[1], b[2], b[3]) : Rectangle.Empty;
        if (rect.Width > 0 && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect)))
            Bounds = rect;
        else
        {
            var area = Screen.PrimaryScreen!.WorkingArea;
            Size = new Size(Math.Min(1280, area.Width - 80), Math.Min(860, area.Height - 80));
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        }
        if (settings.Maximized) WindowState = FormWindowState.Maximized;
    }

    void SaveBounds()
    {
        if (!IsHandleCreated) return;
        settings.Maximized = WindowState == FormWindowState.Maximized;
        var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (r.Width > 0) settings.WindowBounds = [r.X, r.Y, r.Width, r.Height];
        settings.Save();
    }

    static bool StartsWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("SSH Manager") is string;
    }

    void ToggleStartup()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (StartsWithWindows()) key.DeleteValue("SSH Manager", false);
        else key.SetValue("SSH Manager", $"\"{Environment.ProcessPath}\" --tray");
        startupItem.Checked = StartsWithWindows();
    }

    static void OpenExternal(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { }
    }

    static bool SystemDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
