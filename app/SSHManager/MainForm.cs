using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace SSHManager;

/// <summary>Reading JSON from PCs without trusting its shape: a missing or odd value gives a default.</summary>
internal static class J
{
    public static bool Has(JsonElement e, string n, out JsonElement v)
    {
        v = default;
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out v);
    }

    public static string S(JsonElement e, string n) => Has(e, n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    public static bool B(JsonElement e, string n) => Has(e, n, out var v) && v.ValueKind == JsonValueKind.True;
    public static int I(JsonElement e, string n) => Has(e, n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var x) ? x : 0;
    public static long L(JsonElement e, string n) => Has(e, n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var x) ? x : 0;
    public static double D(JsonElement e, string n) => Has(e, n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var x) ? x : 0;
    public static JsonElement O(JsonElement e, string n) => Has(e, n, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
    public static IEnumerable<JsonElement> A(JsonElement e, string n) =>
        Has(e, n, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
    public static int Len(JsonElement e, string n) => Has(e, n, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;
}

/// <summary>
/// The app window: one WebView2 that shows either a PC's dashboard (served by that PC through its
/// SSH tunnel), the "All PCs" overview, a connection status page or the setup wizard. A tray icon
/// keeps every connection alive while the window is closed.
///
/// Trust: a PC's dashboard is served by that PC, so messages from PC views are treated as coming from
/// that PC. Anything that changes this app (settings, PCs, setup, updates, waking PCs) is accepted only
/// from the app's own shared view; a PC view can only manage its own sync pairs (each new pair confirmed
/// in a native dialog), switch PCs, and open the app's own pages.
/// </summary>
internal sealed class MainForm : Form
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string All = "all";
    const string AppSettingsView = "app-settings";
    /// <summary>PC dashboards kept loaded at most; the least recently used one is closed beyond that.</summary>
    const int MaxPcViews = 6;
    /// <summary>A hidden PC dashboard is suspended (stops running script) after this long.</summary>
    static readonly TimeSpan SuspendAfter = TimeSpan.FromMinutes(10);

    readonly AppSettings settings;
    readonly List<Link> links = [];
    /// <summary>The links for other threads (CLI pipe, folder sync); replaced whenever the list changes.</summary>
    volatile Link[] linkSnapshot = [];
    /// <summary>The shared view: All PCs, connection status pages and the setup pages.</summary>
    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    /// <summary>A PC's dashboard, kept loaded while the PC is connected so switching between PCs is
    /// instant and each keeps its page and scroll position.</summary>
    sealed class PcView(WebView2 view, string url, string token, int port)
    {
        public readonly WebView2 View = view;
        public readonly string Url = url, Token = token;
        public readonly int Port = port;
        public bool Ready, Busy, Suspended;
        public DateTime LastShown = DateTime.UtcNow;
        public DateTime? HiddenSince;
    }
    readonly Dictionary<string, PcView> pcViews = [];
    WebView2? visible;
    bool mainPending, webWantsSync;
    CoreWebView2Environment? webEnv;
    readonly NotifyIcon tray = new();
    readonly ToolStripMenuItem startupItem = new("Start with Windows");
    readonly SetupWizard wizard;
    readonly AppUpdater updater;
    readonly SyncManager sync;
    readonly Pairing pairing;
    readonly CliPipe cliPipe;
    readonly Approvals approvals;
    /// <summary>Recent activity of each PC (by PC id), for the combined timeline in All PCs.</summary>
    readonly Dictionary<string, (long last, List<JsonElement> items)> feeds = [];
    readonly HashSet<string> pairSeen = [];
    /// <summary>Pairing requests from a computer whose last request was denied are ignored for a while.</summary>
    readonly Dictionary<string, DateTime> pairDeniedUntil = [];
    bool pairPrompting;
    readonly System.Windows.Forms.Timer poll = new() { Interval = 5000 };
    readonly System.Windows.Forms.Timer clipTimer = new() { Interval = 1500 };
    bool clipBusy;
    /// <summary>The text last exchanged with any PC (sent, or received and put on this clipboard): never sent
    /// on again, so a copy doesn't bounce between PCs that manage each other.</summary>
    string? clipExchanged;
    DateTime localClipChangedUtc = DateTime.MinValue;
    readonly bool startHidden;
    readonly Dictionary<string, (HashSet<string> fails, HashSet<string> unknown)> seen = [];
    readonly HashSet<string> everConnected = [], connectedNow = [];
    /// <summary>Files this app saved from a PC's page: the only ones a page may show in Explorer.</summary>
    readonly HashSet<string> downloaded = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> mergeAsked = [];
    DateTime muteBalloonsUntil = DateTime.MinValue;
    bool webReady, exiting, wizardOpen, appSettingsOpen, polling, darkNow, syncAddOpen;
    string current = "", mainShown = "";
    int downloadSeq;
    object? lastLocalStats;
    System.Threading.Timer? networkSettle;

    public MainForm(AppSettings settings, bool startHidden)
    {
        this.settings = settings;
        this.startHidden = startHidden;

        Text = "SSH Manager";
        Icon = AppIcon.App;
        MinimumSize = new Size(760, 520);
        darkNow = EffectiveDark();
        SetUiTheme(settings.Theme);
        BackColor = darkNow ? Color.FromArgb(13, 17, 23) : Color.FromArgb(245, 247, 250);
        web.DefaultBackgroundColor = PageBack();
        StartPosition = FormStartPosition.Manual;
        RestoreWindowBounds();
        Controls.Add(web);

        pairing = new Pairing(() => links.FirstOrDefault(l => l.Pc.IsLocal));
        wizard = new SetupWizard(html => { if (webReady) { mainShown = "wizard"; NavigateMain(html); } }, OnWizardDone, FindPeer, pairing);
        updater = new AppUpdater(settings, (title, text) => Balloon(title, text, ToolTipIcon.Info));
        updater.Changed += () => OnUi(PushOverview);
        sync = new SyncManager(settings, id => linkSnapshot.FirstOrDefault(l => l.Pc.Id == id), () => OnUi(PostSyncState));
        approvals = new Approvals(this, settings, (title, text) => Balloon(title, text, ToolTipIcon.Warning, force: true));
        cliPipe = new CliPipe(() => linkSnapshot.ToList());

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open SSH Manager", null, (_, _) => ShowWindow());
        menu.Items.Add("All PCs", null, (_, _) => { ShowWindow(); SwitchTo(All); });
        menu.Items.Add("Add a PC…", null, (_, _) => { ShowWindow(); OpenWizard(null); });
        menu.Items.Add("App settings", null, (_, _) => { ShowWindow(); OpenAppSettings(); });
        menu.Items.Add("Reconnect all", null, (_, _) => links.ForEach(l => l.Reconnect(resetBackoff: true)));
        menu.Items.Add(new ToolStripSeparator());
        startupItem.Checked = StartsWithWindows();
        startupItem.Click += (_, _) => AppLog.Guard("Start with Windows", () => { ToggleStartup(); PostAppSettings(); });
        menu.Items.Add(startupItem);
        menu.Items.Add("Check for updates", null, async (_, _) => await AppLog.GuardAsync("Check for updates", async () =>
        {
            await updater.CheckAsync();
            Balloon("SSH Manager", updater.Available ? $"Version {updater.Latest} is available." : updater.Error ?? $"You have the latest version ({updater.Current}).", ToolTipIcon.Info, force: true);
        }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        tray.ContextMenuStrip = menu;
        tray.Icon = AppIcon.For(LinkState.Connecting);
        tray.Text = "SSH Manager";
        tray.Visible = true;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        tray.BalloonTipClicked += (_, _) => ShowWindow();

        if (Ssh.LocalInstallFolder() is not null)
            AddLink(new PcEntry { Id = PcEntry.LocalId, PcName = Environment.MachineName });
        foreach (var pc in settings.Pcs.ToList())
        {
            // an address saved by an older version that ssh would read as an option is not used
            if (!Ssh.SafeName(pc.Host.Trim())) AppLog.Write($"PC {pc.Id} has an unusable address; it shows as not connected until it's changed.");
            AddLink(pc);
        }
        current = settings.Selected == All && links.Count > 1 ? All
            : links.Any(l => l.Pc.Id == settings.Selected) ? settings.Selected
            : links.Count > 1 ? All : links.FirstOrDefault()?.Pc.Id ?? "";
        poll.Tick += async (_, _) => await AppLog.GuardAsync("Polling the PCs", PollAsync);
    }

    /// <summary>The lowest local port from 8765 up that no other connection uses (stable, no collisions after removals).</summary>
    int NextPort()
    {
        var used = links.SelectMany(l => new[] { l.PreferredPort, l.Port }).ToHashSet();
        var port = 8765;
        while (used.Contains(port)) port++;
        return port;
    }

    Link AddLink(PcEntry pc)
    {
        var link = new Link(pc, NextPort(), settings.Save);
        link.Changed += (l, state, message) => OnUi(() => OnLinkChanged(l, state, message));
        links.Add(link);
        linkSnapshot = links.ToArray();
        if (IsHandleCreated) link.Start();
        return link;
    }

    void OnUi(Action a)
    {
        if (IsHandleCreated && !IsDisposed && !exiting)
        {
            try { BeginInvoke(() => AppLog.Guard("Window update", a)); }
            catch (InvalidOperationException) { }   // the window is closing
        }
    }

    protected override void SetVisibleCore(bool value)
    {
        // --tray: create the window (so links and the tray work) but keep it hidden
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
        try { await StartAsync(); }
        catch (Exception ex) { AppLog.Write("Starting the window", ex); }
    }

    async Task StartAsync()
    {
        ApplyTitleBar();
        links.ForEach(l => l.Start());
        updater.Start();
        poll.Start();
        sync.Start();
        cliPipe.Start();
        AddClipboardFormatListener(Handle);
        clipTimer.Tick += async (_, _) => await AppLog.GuardAsync("Shared clipboard", PollClipboardAsync);
        clipTimer.Start();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        var record = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SSHManager", "install-path.txt");
        if (File.Exists(record) && Ssh.LocalInstallFolder() is null && !Ssh.OwnedByAdmins(record))
        {
            AppLog.Write($"{record} isn't owned by Administrators; this PC's own server is not used.");
            Balloon("SSH Manager", "This PC's SSH Manager install record wasn't written by an administrator, so it isn't used. Reinstall SSH Manager to fix it.",
                ToolTipIcon.Warning, force: true);
        }
        try
        {
            // --devtools-port=N (testing only) exposes the page to Chrome DevTools Protocol on 127.0.0.1:N
            var port = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--devtools-port="))?.Split('=')[1];
            var options = new CoreWebView2EnvironmentOptions(port is null ? null : $"--remote-debugging-port={int.Parse(port)}");
            webEnv = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppSettings.Folder, "WebView2"), options);
            await web.EnsureCoreWebView2Async(webEnv);
        }
        catch (Exception ex)
        {
            MessageBox.Show("SSH Manager needs the Microsoft Edge WebView2 Runtime. Reinstall SSH Manager to get it.\n\n" + ex.Message,
                "SSH Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ExitApp();
            return;
        }
        Configure(web, null);
        ApplyColorScheme();
        visible = web;
        web.NavigationCompleted += (_, a) => AppLog.Guard("Page loaded", () =>
        {
            if (!a.IsSuccess) return;
            PushPcs();
            if (mainShown == All) PushOverview();
            if (mainShown == AppSettingsView) PostAppSettings();
            if (mainPending && WantsMain()) ShowView(web);   // shown once drawn, not while loading
            mainPending = false;
        });
        webReady = true;
        ShowCurrent();
    }

    Link? CurrentLink() => links.FirstOrDefault(l => l.Pc.Id == current);

    // ---- theme: "system" follows Windows; the pages get {type:"theme"} when it changes ----
    bool EffectiveDark() => settings.Theme switch { "dark" => true, "light" => false, _ => SystemDark() };

    Color PageBack() => darkNow ? Color.FromArgb(11, 14, 19) : Color.FromArgb(244, 246, 249);

    void ApplyTitleBar()
    {
        if (!IsHandleCreated) return;
        var on = darkNow ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));   // dark title bar
    }

    void ApplyColorScheme()
    {
        if (web.CoreWebView2 is not { } core) return;
        try
        {
            core.Profile.PreferredColorScheme = settings.Theme switch
            {
                "dark" => CoreWebView2PreferredColorScheme.Dark,
                "light" => CoreWebView2PreferredColorScheme.Light,
                _ => CoreWebView2PreferredColorScheme.Auto,
            };
        }
        catch (Exception) { }   // older WebView2 runtimes
    }

    void ApplyTheme()
    {
        darkNow = EffectiveDark();
        SetUiTheme(settings.Theme);
        ApplyTitleBar();
        BackColor = darkNow ? Color.FromArgb(13, 17, 23) : Color.FromArgb(245, 247, 250);
        foreach (var v in AllViews()) v.DefaultBackgroundColor = PageBack();
        ApplyColorScheme();
        Post(ThemeMessage());
    }

    object ThemeMessage() => new { type = "theme", theme = darkNow ? "dark" : "light", setting = settings.Theme };

    /// <summary>Ui.Theme (the theme of pages drawn from now on), when this build's Ui has it.</summary>
    static void SetUiTheme(string theme)
    {
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            if (typeof(Ui).GetProperty("Theme", flags) is { CanWrite: true } p) p.SetValue(null, theme);
            else typeof(Ui).GetField("Theme", flags)?.SetValue(null, theme);
        }
        catch (Exception e) { AppLog.Write("Setting the page theme", e); }
    }

    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) => OnUi(() =>
    {
        if (EffectiveDark() != darkNow) ApplyTheme();
    });

    // ---- sleep, resume and network changes ----
    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        OnUi(() =>
        {
            // every tunnel is dead after sleep: reconnect now instead of after the backoff, quietly
            muteBalloonsUntil = DateTime.UtcNow.AddSeconds(30);
            links.ForEach(l => l.Reconnect(resetBackoff: true));
        });
    }

    void OnNetworkChanged(object? sender, EventArgs e)
    {
        // addresses change in bursts (Wi-Fi, VPN): act once things settle
        networkSettle?.Dispose();
        networkSettle = new System.Threading.Timer(_ => OnUi(() => _ = AppLog.GuardAsync("Network change", ReconnectAfterNetworkChangeAsync)), null, 3000, Timeout.Infinite);
    }

    async Task ReconnectAfterNetworkChangeAsync()
    {
        muteBalloonsUntil = DateTime.UtcNow.AddSeconds(30);
        foreach (var l in links.ToList())
        {
            if (l.State != LinkState.Connected)
            {
                l.Reconnect(resetBackoff: true);
                continue;
            }
            try
            {
                using var r = await Link.Http.SendAsync(l.Request(HttpMethod.Get, "/api/ping"));
                if (!r.IsSuccessStatusCode) l.Reconnect(resetBackoff: true);
            }
            catch (Exception) { l.Reconnect(resetBackoff: true); }
        }
    }

    // ---- views: one per connected PC plus the shared one; only the one in front is visible ----
    void Configure(WebView2 v, Link? link)
    {
        var core = v.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.NewWindowRequested += (_, a) =>
        {
            a.Handled = true;
            var uri = a.Uri;
            OnUi(() => ConfirmOpenExternal(uri));
        };
        core.NavigationStarting += (_, a) =>
        {
            if (NavigationAllowed(v, a.Uri)) return;
            a.Cancel = true;
            var uri = a.Uri;
            OnUi(() => ConfirmOpenExternal(uri));
        };
        core.WebMessageReceived += (_, a) => AppLog.Guard("Page message", () => OnPageMessage(a.WebMessageAsJson, v, a.Source));
        core.DownloadStarting += (s, a) => AppLog.Guard("Download", () => OnDownloadStarting(s, a));
    }

    /// <summary>A PC's view stays on its own server (http://localhost:port/); the app's own pages are drawn
    /// from strings (about:blank / data:). Everything else is cancelled.</summary>
    bool NavigationAllowed(WebView2 v, string uri)
    {
        if (v == web) return uri.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
        var pv = pcViews.Values.FirstOrDefault(p => p.View == v);
        return pv is not null && SameOrigin(uri, pv.Port);
    }

    static bool SameOrigin(string uri, int port) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttp
        && u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && u.Port == port;

    IEnumerable<WebView2> AllViews() => pcViews.Values.Select(p => p.View).Prepend(web);

    /// <summary>The shared view is wanted: setup, app settings, All PCs, or a PC that isn't connected.</summary>
    bool WantsMain() => wizardOpen || appSettingsOpen || current == All || CurrentLink() is not { State: LinkState.Connected };

    void ShowView(WebView2 v)
    {
        if (visible == v) return;
        v.Visible = true;
        v.BringToFront();
        visible = v;
        var now = DateTime.UtcNow;
        // the others stop painting while hidden (they keep their page); ones still loading stay behind
        foreach (var p in pcViews.Values)
        {
            if (p.View == v)
            {
                p.LastShown = now;
                p.HiddenSince = null;
                p.Suspended = false;   // WebView2 resumes a suspended view when it is shown
            }
            else if (p.Ready && p.View.Visible)
            {
                p.View.Visible = false;
                p.HiddenSince = now;
            }
        }
        if (v != web && !mainPending) web.Visible = false;
        PushPcs();
    }

    /// <summary>Closes the least recently used dashboards beyond <see cref="MaxPcViews"/> and suspends ones hidden
    /// for a while (unless a page says it is busy, e.g. with a transfer).</summary>
    void TrimViews()
    {
        var hidden = pcViews.Where(p => p.Value.View != visible && !p.Value.Busy).OrderBy(p => p.Value.LastShown).ToList();
        foreach (var (id, _) in hidden.Take(Math.Max(0, pcViews.Count - MaxPcViews)).ToList()) DropPcView(id);
        foreach (var (_, pv) in pcViews.Where(p => p.Value.View != visible && !p.Value.Busy && !p.Value.Suspended
                                                   && p.Value.HiddenSince is { } h && DateTime.UtcNow - h > SuspendAfter).ToList())
        {
            pv.Suspended = true;
            _ = SuspendAsync(pv);
        }
    }

    static async Task SuspendAsync(PcView pv)
    {
        try
        {
            if (pv.View.CoreWebView2 is { } core && !pv.View.Visible && !await core.TrySuspendAsync()) pv.Suspended = false;
        }
        catch (Exception) { pv.Suspended = false; }
    }

    /// <summary>Loads a page in the shared view behind the one in front; it comes forward when drawn.</summary>
    void NavigateMain(string html)
    {
        mainPending = true;
        webWantsSync = false;
        if (visible != web)
        {
            web.Visible = true;   // draws behind the page in front
            web.SendToBack();
        }
        web.CoreWebView2.NavigateToString(html);
    }

    /// <summary>Shows a connected PC's dashboard: right away when it's already loaded, otherwise it loads
    /// behind the current page and comes forward once its first data is drawn.</summary>
    async void OpenPcView(Link link)
    {
        try { await OpenPcViewAsync(link); }
        catch (Exception e) { AppLog.Write($"Opening {link.Pc.DisplayName}'s page", e); }
    }

    async Task OpenPcViewAsync(Link link)
    {
        var id = link.Pc.Id;
        if (pcViews.TryGetValue(id, out var pv) && pv.Url == link.Url && pv.Token == link.Token)
        {
            if (pv.Ready) ShowView(pv.View);
            return;
        }
        if (pv is not null) DropPcView(id);
        // the server only answers requests that carry its access token; one cookie per local port
        // (the cookie store is shared by all views); page script can't read it
        var cookies = web.CoreWebView2.CookieManager;
        var cookie = cookies.CreateCookie($"sshmgr_token_{link.Port}", link.Token, "localhost", "/");
        cookie.IsHttpOnly = true;
        cookie.SameSite = CoreWebView2CookieSameSiteKind.Strict;
        cookies.AddOrUpdateCookie(cookie);
        var v = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = PageBack() };
        pv = new PcView(v, link.Url, link.Token, link.Port);
        pcViews[id] = pv;
        Controls.Add(v);
        v.SendToBack();   // load behind the page that is showing now
        await v.EnsureCoreWebView2Async(webEnv);
        if (!pcViews.TryGetValue(id, out var still) || still != pv) return;   // dropped while starting
        Configure(v, link);
        v.NavigationCompleted += async (_, a) => await AppLog.GuardAsync("PC page loaded", async () =>
        {
            if (a.IsSuccess)
            {
                PushPcs();
                Post(ThemeMessage(), v.CoreWebView2);
                await Task.Delay(1500);   // pages from older servers don't say when they're drawn
                Reveal(id, pv);
                return;
            }
            // the PC's server restarted under us: try again shortly while its link is still up
            await Task.Delay(2000);
            if (pcViews.TryGetValue(id, out var cur) && cur == pv && link.State == LinkState.Connected)
                v.CoreWebView2.Navigate(pv.Url);
        });
        v.CoreWebView2.Navigate(link.Url);
        TrimViews();
    }

    void Reveal(string id, PcView pv)
    {
        if (!pcViews.TryGetValue(id, out var cur) || cur != pv || pv.Ready) return;
        pv.Ready = true;
        if (current == id && !WantsMain()) ShowView(pv.View);
        else
        {
            pv.View.Visible = false;
            pv.HiddenSince = DateTime.UtcNow;
        }
    }

    void DropPcView(string id)
    {
        if (!pcViews.Remove(id, out var pv)) return;
        if (visible == pv.View)
        {
            visible = null;
            ShowView(web);
        }
        Controls.Remove(pv.View);
        pv.View.Dispose();
    }

    void SwitchTo(string id)
    {
        wizardOpen = false;
        appSettingsOpen = false;
        current = id;
        settings.Selected = id;
        settings.Save();
        ShowCurrent();
    }

    /// <summary>Puts the right page in the window for what is selected and its connection state.</summary>
    void ShowCurrent()
    {
        if (!webReady || wizardOpen || appSettingsOpen) return;
        if (links.Count == 0)
        {
            OpenWizard(null);
            return;
        }
        if (current == All && links.Count < 2) current = links[0].Pc.Id;   // "All PCs" needs two PCs
        if (current == All)
        {
            Text = "SSH Manager · All PCs";
            if (mainShown != All)
            {
                mainShown = All;
                NavigateMain(Ui.Overview());
                _ = AppLog.GuardAsync("Polling the PCs", PollAsync);   // fetch everyone's activity now rather than at the next poll
            }
            else
            {
                ShowView(web);
                PushOverview();
            }
            return;
        }
        var link = CurrentLink() ?? links[0];
        current = link.Pc.Id;
        Text = $"SSH Manager · {link.Pc.DisplayName}" + (link.State == LinkState.Connected ? "" : " (not connected)");
        if (link.State == LinkState.Connected)
            OpenPcView(link);
        else
        {
            var key = $"status:{link.Pc.Id}:{link.State}:{link.Message}";
            if (mainShown != key)
            {
                mainShown = key;
                NavigateMain(Ui.StatusPage(link.Pc, link.State, link.Message));
            }
            else ShowView(web);
        }
    }

    void OnLinkChanged(Link link, LinkState state, string message)
    {
        if (!links.Contains(link)) return;   // removed meanwhile
        UpdateTray();
        var name = link.Pc.DisplayName;
        var quiet = DateTime.UtcNow < muteBalloonsUntil;
        if (state == LinkState.Connected)
        {
            if (everConnected.Contains(link.Pc.Id) && !connectedNow.Contains(link.Pc.Id) && settings.NotifyConnection && !quiet)
                Balloon("SSH Manager", $"Connected to {name} again.", ToolTipIcon.Info);
            everConnected.Add(link.Pc.Id);
            connectedNow.Add(link.Pc.Id);
            link.ClipSeq = -1;   // a clipboard copy from before the reconnect is not applied over a newer one here
            OfferMerge(link);
        }
        else if (state == LinkState.Disconnected && connectedNow.Remove(link.Pc.Id) && settings.NotifyConnection && !quiet)
            Balloon("SSH Manager", $"Lost the connection to {name}. Reconnecting…", ToolTipIcon.Warning);
        else if (state == LinkState.Disconnected) connectedNow.Remove(link.Pc.Id);

        if (state != LinkState.Connected) DropPcView(link.Pc.Id);   // reloaded when it's back
        if (link.Pc.Id == current) ShowCurrent();
        PushPcs();
        if (current == All) PushOverview();
    }

    /// <summary>The same PC added twice (e.g. by name and by address): noticed by its network card or computer
    /// name once both are connected; offers to keep one entry.</summary>
    void OfferMerge(Link link)
    {
        if (link.Pc.IsLocal) return;
        var twin = links.FirstOrDefault(o => o != link && !o.Pc.IsLocal && o.State == LinkState.Connected
            && ((o.Pc.Mac != "" && o.Pc.Mac.Equals(link.Pc.Mac, StringComparison.OrdinalIgnoreCase))
                || (o.Computer != "" && o.Computer.Equals(link.Computer, StringComparison.OrdinalIgnoreCase))));
        if (twin is null) return;
        var key = string.Join("|", new[] { twin.Pc.Id, link.Pc.Id }.Order());
        if (!mergeAsked.Add(key)) return;
        var (keep, drop) = links.IndexOf(twin) < links.IndexOf(link) ? (twin, link) : (link, twin);
        BeginInvoke(() => AppLog.Guard("Merge PCs", () =>
        {
            if (!links.Contains(keep) || !links.Contains(drop)) return;
            if (MessageBox.Show(this, $"{keep.Pc.DisplayName} is in SSH Manager twice: as \"{keep.Pc.Host}\" and as \"{drop.Pc.Host}\".\n\n" +
                    $"Keep only the first entry? Folder syncs of the second one move to it.",
                    "SSH Manager: same PC twice", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            sync.MovePc(drop.Pc.Id, keep.Pc.Id);
            RemovePc(drop, ask: false);
        }));
    }

    void UpdateTray()
    {
        var up = links.Count(l => l.State == LinkState.Connected);
        var state = links.Count == 0 ? LinkState.Connecting
            : up == links.Count ? LinkState.Connected
            : links.Any(l => l.State == LinkState.Disconnected) ? LinkState.Disconnected : LinkState.Connecting;
        tray.Icon = AppIcon.For(state);
        var text = links.Count == 1 ? $"SSH Manager: {links[0].Pc.DisplayName} {(up == 1 ? "connected" : "not connected")}"
            : $"SSH Manager: {up} of {links.Count} PCs connected";
        tray.Text = text.Length > 63 ? text[..63] : text;
    }

    void Balloon(string title, string text, ToolTipIcon icon, bool force = false)
    {
        if (exiting) return;
        if (InvokeRequired)
        {
            OnUi(() => Balloon(title, text, icon, force));
            return;
        }
        if (force || !IsActive()) tray.ShowBalloonTip(5000, title, text, icon);
    }

    bool IsActive() => Visible && WindowState != FormWindowState.Minimized && ActiveForm == this;

    /// <summary>A message to the pages: to one view, or to all of them.</summary>
    void Post(object message, CoreWebView2? to = null)
    {
        if (!webReady) return;
        var json = JsonSerializer.Serialize(message);
        foreach (var core in to is not null ? [to] : AllViews().Select(v => v.CoreWebView2).Where(c => c is not null))
        {
            try { core.PostWebMessageAsJson(json); } catch (Exception) { }   // a view that is just closing
        }
    }

    /// <summary>The PC switcher in every page: only each PC's id, name and state.</summary>
    void PushPcs() => Post(new
    {
        type = "pcs",
        current = wizardOpen || appSettingsOpen ? "" : current,
        pcs = links.Select(l => new { id = l.Pc.Id, name = l.Pc.DisplayName, state = l.State.ToString() }),
    });

    /// <summary>Sync pairs to the views that show them: each PC's dashboard gets only its own pairs.</summary>
    void PostSyncState()
    {
        foreach (var (id, pv) in pcViews)
            if (pv.View.CoreWebView2 is { } core) Post(sync.Snapshot(id), core);
        if (webWantsSync) Post(sync.Snapshot(), web.CoreWebView2);
    }

    /// <summary>A computer that connects to one of the PCs here but has no server of its own (a client),
    /// as that PC reports it: it can't be opened, but All PCs shows its status and stats.</summary>
    sealed record ClientPc(string Name, string State, string Via, JsonElement? Stats, string Mac, string Ip, string Version,
        string Host = "", string Machine = "", string User = "", bool ViaLocal = false, bool CanPair = false);

    /// <summary>For the setup page: a computer whose app is connected to a PC here, found by its name, computer
    /// name or address. It can approve a pairing request when it is connected to this PC's own server.</summary>
    SetupWizard.Peer? FindPeer(string hostOrIp)
    {
        var key = hostOrIp.Trim();
        if (key == "") return null;
        bool Is(string s) => s != "" && (s.Equals(key, StringComparison.OrdinalIgnoreCase)
                                         || s.Split('.')[0].Equals(key.Split('.')[0], StringComparison.OrdinalIgnoreCase) && !char.IsDigit(key[0]));
        var c = Clients().Where(c => Is(c.Name) || Is(c.Host) || Is(c.Machine) || c.Ip == key)
            .OrderByDescending(c => c.ViaLocal).FirstOrDefault();
        return c is null ? null
            : new(c.Machine != "" ? c.Machine : c.Name, c.User, c.ViaLocal && c.CanPair && c.State == "Connected");
    }

    List<ClientPc> Clients()
    {
        static JsonElement? Obj(JsonElement e, string n) => J.O(e, n) is { ValueKind: JsonValueKind.Object } o ? o : null;
        static string FirstMac(JsonElement? st) =>
            st is { } x ? J.A(x, "macs").Where(m => m.ValueKind == JsonValueKind.String).Select(m => m.GetString() ?? "").FirstOrDefault() ?? "" : "";
        static bool Pairs(JsonElement? st) =>
            st is { } x && J.A(x, "features").Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == "pair");

        var known = links.Select(l => l.Pc.PcName).Append(Environment.MachineName).Where(n => n != "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new List<ClientPc>();
        bool Seen(string name, string machine) => known.Contains(name) || (machine != "" && known.Contains(machine))
            || list.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        foreach (var l in links.Where(l => l.State == LinkState.Connected && l.Status is not null))
        {
            var s = l.Status!.Value;
            foreach (var p in J.A(s, "peers"))
            {
                var st = Obj(p, "stats");
                var name = J.S(p, "name");
                if (name == "" || Seen(name, st is { } x ? J.S(x, "machine") : "")) continue;
                var state = J.B(p, "connected") ? "Connected" : J.B(p, "online") ? "Online" : "Offline";
                var ip = J.S(p, "resolved_ip") is { Length: > 0 } r ? r : J.S(p, "ip");
                list.Add(new(name, state, l.Pc.DisplayName, st, FirstMac(st), ip, st is { } y ? J.S(y, "app_version") : "",
                    J.S(p, "host"), st is { } z ? J.S(z, "machine") : "", st is { } u ? J.S(u, "user") : "", l.Pc.IsLocal, Pairs(st)));
            }
            foreach (var c in J.A(s, "other_apps"))
            {
                if (c.ValueKind != JsonValueKind.Object) continue;
                var name = J.S(c, "machine");
                if (name == "" || Seen(name, name)) continue;
                list.Add(new(name, "Connected", l.Pc.DisplayName, c, FirstMac(c), "", J.S(c, "app_version"), "", name, J.S(c, "user"),
                    l.Pc.IsLocal, Pairs(c)));
            }
        }
        return list;
    }

    // ---- polling: this computer's stats to each PC, each PC's status for the overview and alerts ----
    async Task PollAsync()
    {
        if (polling) return;
        polling = true;
        try
        {
            lastLocalStats = LocalStats.Collect(settings.MachineId);
            // all PCs at once: one slow PC doesn't hold up the others
            await Task.WhenAll(links.Where(l => l.State == LinkState.Connected).ToList().Select(PollOneAsync));
            if (current == All) PushOverview();
            TrimViews();
        }
        finally { polling = false; }
    }

    async Task PollOneAsync(Link link)
    {
        try
        {
            if (!link.Pc.IsLocal)
                using (await Link.Http.SendAsync(link.Request(HttpMethod.Post, "/api/clients/report", JsonContent.Create(lastLocalStats)))) { }
            using var r = await Link.Http.SendAsync(link.Request(HttpMethod.Get, "/api/status"));
            if (!r.IsSuccessStatusCode) return;
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            link.Status = doc.RootElement.Clone();
            CheckAlerts(link);
            if (!link.Pc.IsLocal && !pairPrompting) await CheckPairRequestAsync(link);
            await approvals.CheckAsync(link);
            if (current == All) await FetchActivityAsync(link);
        }
        catch (Exception) { }   // the PC dropped or answered something odd; the next poll tries again
    }

    /// <summary>Tray notifications for new failed logins and unknown computers on any PC.</summary>
    void CheckAlerts(Link link)
    {
        if (link.Status is not { } s) return;
        var fails = J.A(s, "failures_24h").Select(f => J.S(f, "ts") + J.S(f, "ip")).ToHashSet();
        var unknown = J.A(s, "unknown_clients").Select(u => J.S(u, "ip") + J.S(u, "last")).ToHashSet();
        if (seen.TryGetValue(link.Pc.Id, out var before))
        {
            var newFails = fails.Count(f => !before.fails.Contains(f));
            if (newFails > 0 && settings.NotifyFailedLogin)
                Balloon("Failed SSH login", $"{newFails} failed login attempt(s) on {link.Pc.DisplayName}.", ToolTipIcon.Warning, force: link.Pc.Id != current);
            if (unknown.Any(u => !before.unknown.Contains(u)) && settings.NotifyUnknown)
                Balloon("Unknown computer logged in", $"A computer that isn't in {link.Pc.DisplayName}'s list logged in. See Settings → Computers there.", ToolTipIcon.Warning, force: link.Pc.Id != current);
        }
        seen[link.Pc.Id] = (fails, unknown);
    }

    /// <summary>New activity entries of a PC since the last fetch (the newest 300 the first time).</summary>
    async Task FetchActivityAsync(Link link)
    {
        feeds.TryGetValue(link.Pc.Id, out var f);
        f.items ??= [];
        using var r = await Link.Http.SendAsync(link.Request(HttpMethod.Get, $"/api/activity?after={f.last}&limit=300"));
        if (!r.IsSuccessStatusCode) return;
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        foreach (var it in J.A(doc.RootElement, "items"))
        {
            if (it.ValueKind != JsonValueKind.Object) continue;
            f.items.Add(it.Clone());
            if (J.L(it, "id") is var n && n > f.last) f.last = n;
        }
        if (f.items.Count > 400) f.items.RemoveRange(0, f.items.Count - 400);
        feeds[link.Pc.Id] = f;
    }

    /// <summary>Totals, logins per hour, live sessions and a merged activity timeline over all connected PCs.</summary>
    object Combined()
    {
        static JsonElement A(JsonElement s) => J.O(s, "activity_24h");

        var up = links.Where(l => l.State == LinkState.Connected && l.Status is not null).Select(l => (l, s: l.Status!.Value)).ToList();
        var summary = new
        {
            online = links.Count(l => l.State == LinkState.Connected), total = links.Count,
            sessions = up.Sum(x => J.Len(x.s, "sessions")),
            logins = up.Sum(x => J.I(x.s, "logins_24h")),
            failures = up.Sum(x => J.Len(x.s, "failures_24h")),
            commands = up.Sum(x => J.I(A(x.s), "commands")),
            actions = up.Sum(x => J.I(A(x.s), "actions")),
            files = up.Sum(x => J.I(A(x.s), "files")),
            bytes = up.Sum(x => J.L(A(x.s), "bytes")),
        };
        var series = up.Where(x => J.Len(x.s, "hours") > 0)
            .Select(x => new { name = x.l.Pc.DisplayName, counts = J.A(x.s, "hours").Select(h => J.I(h, "count")).ToArray() })
            .ToList();
        var labels = up.Select(x => x.s).Where(s => J.Len(s, "hours") > 0)
            .Select(s => J.A(s, "hours").Select(h => J.S(h, "hour")).ToArray()).FirstOrDefault() ?? [];
        var sessions = up.SelectMany(x => J.A(x.s, "sessions").Select(se => (object)new
        {
            pc = x.l.Pc.DisplayName, client = J.S(se, "client"), user = J.S(se, "user"), ip = J.S(se, "ip"), start = J.S(se, "start"),
            tunnel = J.B(se, "tunnel"),
            agents = J.A(se, "agents").Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString() ?? "").ToArray(),
            app = J.A(se, "commands").Any(c => J.S(c, "kind") == "dashboard"),
            commands = J.A(se, "commands").Where(c => J.S(c, "kind") != "dashboard")
                .Select(c => new { kind = J.S(c, "kind"), summary = J.S(c, "summary"), start = J.S(c, "start") }).ToArray(),
        })).ToList();
        var activity = links.Where(l => feeds.ContainsKey(l.Pc.Id))
            .SelectMany(l => feeds[l.Pc.Id].items.Select(it =>
            {
                var o = System.Text.Json.Nodes.JsonNode.Parse(it.GetRawText())!.AsObject();
                foreach (var big in new[] { "script", "output" })
                    if (o[big] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 4000)
                        o[big] = text[..4000] + "…";
                o["pc"] = l.Pc.DisplayName;
                return o;
            }))
            .OrderByDescending(o => o["ts"] is System.Text.Json.Nodes.JsonValue tv && tv.TryGetValue<string>(out var ts)
                                    && DateTimeOffset.TryParse(ts, out var t) ? t : DateTimeOffset.MinValue)
            .Take(250).ToList();
        return new { summary, hours = new { labels, series }, sessions, activity };
    }

    void PushOverview()
    {
        if (mainShown != All) return;
        var pcs = new List<object>();
        foreach (var l in links)
        {
            object? stats = null;
            int? sessions = null, failures = null;
            string? latest = null;
            var canUpdate = false;
            if (l.State == LinkState.Connected && l.Status is { } s)
            {
                var host = J.O(s, "host");
                var ram = J.O(host, "ram");
                stats = new
                {
                    cpu = J.D(host, "cpu"),
                    ram_total = J.L(ram, "total"),
                    ram_used = J.L(ram, "used"),
                    disks = J.A(host, "disks").Select(d => new { mount = J.S(d, "mount"), total = J.L(d, "total"), free = J.L(d, "free") }).ToArray(),
                    uptime_s = J.L(host, "uptime_s"),
                };
                sessions = J.Len(s, "sessions");
                failures = J.Len(s, "failures_24h");
                if (J.O(s, "update") is { ValueKind: JsonValueKind.Object } u)
                {
                    latest = J.S(u, "latest");
                    canUpdate = J.B(u, "available") && J.B(u, "installable") && J.S(u, "status") == "idle";
                }
            }
            pcs.Add(new
            {
                id = l.Pc.Id, name = l.Pc.DisplayName, state = l.State.ToString(), message = l.Message.Split('\n')[0],
                version = l.Version, stats, sessions, failures, latest, canUpdate, local = l.Pc.IsLocal, kind = "pc",
                mac = l.Pc.Mac,
            });
        }
        if (!links.Any(l => l.Pc.IsLocal) && lastLocalStats is not null)
            pcs.Add(new { id = "", name = Environment.MachineName, state = "Connected", kind = "this", stats = lastLocalStats });
        foreach (var c in Clients())
            pcs.Add(new { id = "client:" + c.Name, name = c.Name, state = c.State, kind = "client", stats = c.Stats, via = c.Via,
                          mac = c.Mac, ip = c.Ip, version = c.Version });

        object? appUpdate = null;
        if (!AppUpdater.ServerUpdatesThisPc())
        {
            if (updater.Status != "idle")
                appUpdate = new { text = updater.Status == "downloading" ? "Downloading the SSH Manager update…" : "Installing the update…", canInstall = false };
            else if (updater.Available)
                appUpdate = new { text = $"SSH Manager {updater.Latest} is available (you have {updater.Current}).", canInstall = AppUpdater.IsInstalled() };
            else if (updater.Error is { } err)
                appUpdate = new { text = err, canInstall = false };
        }
        Post(new { type = "overview", pcs, appUpdate, combined = Combined() }, web.CoreWebView2);
    }

    // ---- messages from pages ----
    void OnPageMessage(string json, WebView2 from, string source)
    {
        // who is asking: the app's own shared view, or the dashboard of one PC (served by that PC)
        var shared = from == web;
        string? fromPc = null;
        if (shared)
        {
            if (!(source.StartsWith("about:", StringComparison.OrdinalIgnoreCase) || source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))) return;
        }
        else
        {
            var entry = pcViews.FirstOrDefault(p => p.Value.View == from);
            if (entry.Value is null || !SameOrigin(source, entry.Value.Port)) return;
            fromPc = entry.Key;
        }
        var reply = from.CoreWebView2;
        Link? Own() => fromPc is null ? null : links.FirstOrDefault(l => l.Pc.Id == fromPc);

        JsonElement m;
        try { m = JsonDocument.Parse(json).RootElement; } catch (JsonException) { return; }
        if (m.ValueKind == JsonValueKind.String)
        {
            if (m.GetString() == "retry") (shared ? CurrentLink() : Own())?.Reconnect(resetBackoff: true);
            return;
        }
        if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) return;
        string Str(string name) => J.S(m, name);
        Link? ById() => links.FirstOrDefault(l => l.Pc.Id == Str("id"));
        var type = t.GetString() ?? "";

        // ---- from any view ----
        switch (type)
        {
            case "page-ready":   // a PC's dashboard has drawn its first data: it can come forward
                if (fromPc is not null && pcViews.TryGetValue(fromPc, out var ready)) Reveal(fromPc, ready);
                return;
            case "busy":   // a PC's dashboard is transferring files: keep it running while hidden
                if (fromPc is not null && pcViews.TryGetValue(fromPc, out var busy)) busy.Busy = J.B(m, "busy");
                return;
            case "switch-pc":
                if (Str("id") is var to && (to == All || links.Any(l => l.Pc.Id == to))) SwitchTo(to);
                return;
            case "add-pc":   // only opens the app's own add-a-PC page
                OpenWizard(null);
                return;
            case "open-app-settings":
                OpenAppSettings();
                return;
            case "get-theme":
                Post(ThemeMessage(), reply);
                return;
            case "retry":
                (shared ? ById() ?? CurrentLink() : Own())?.Reconnect(resetBackoff: true);
                return;
            case "auth-failed":   // the page's token stopped working: fetch a fresh one
                (shared ? null : Own())?.Reconnect();
                return;
            case "sync-get":
                if (shared) webWantsSync = true;
                Post(sync.Snapshot(fromPc), reply);
                return;
            case "sync-add" when m.TryGetProperty("pair", out var pr) && pr.ValueKind == JsonValueKind.Object:
                if (syncAddOpen) return;   // one question at a time
                syncAddOpen = true;
                try { AddSyncPair(pr, fromPc, reply); }
                finally { syncAddOpen = false; }
                return;
            case "sync-now" or "sync-toggle" or "sync-remove":
                var pairId = Str("id");
                if (fromPc is not null && !sync.Owns(pairId, fromPc)) return;   // a PC manages only its own pairs
                if (type == "sync-now") sync.SyncNow(pairId);
                else if (type == "sync-toggle") sync.Toggle(pairId);
                else sync.Remove(pairId);
                return;
            case "sync-pick-local":
                using (var dlg = new FolderBrowserDialog { Description = "Folder on this computer to keep in sync" })
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        if (SyncManager.SensitiveLocal(dlg.SelectedPath) is { } why)
                            MessageBox.Show(this, why, "Folder sync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        else Post(new { type = "sync-local-picked", path = dlg.SelectedPath }, reply);
                    }
                return;
            case "show-file":   // only a file this app saved from a page
                var path = Str("path");
                lock (downloaded) if (!downloaded.Contains(path)) return;
                if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
                return;
            case "notify" when Str("kind") == "transfers" && settings.NotifyTransfers:
                // failed logins and unknown computers are watched by the app itself (CheckAlerts)
                Balloon(Str("title"), Str("text"), ToolTipIcon.Info);
                return;
        }

        // ---- from the app's own pages only: they change this app ----
        if (!shared) return;
        switch (type)
        {
            case not null when type.StartsWith("setup-"):
                if (wizardOpen) wizard.OnMessage(type, Str);
                break;
            case "wake-mac":
                try
                {
                    Wol.Send(Str("mac"), Ssh.CleanIp(Str("ip")) is { } wip ? [wip] : []);
                    Balloon("SSH Manager", $"Wake-up signal sent to {Str("name")}.", ToolTipIcon.Info, force: true);
                }
                catch (Exception e) { MessageBox.Show(this, e.Message, "Wake up", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                break;
            case "edit-pc":
                if (ById() is { Pc.IsLocal: false } edit) OpenWizard(edit.Pc);
                break;
            case "open-setup":
                if (CurrentLink() is { } cur && !cur.Pc.IsLocal) OpenWizard(cur.Pc);
                else OpenWizard(null);
                break;
            case "remove-pc":
                if (ById() is { Pc.IsLocal: false } rm) RemovePc(rm);
                break;
            case "update-pc":
                if (ById() is { } up) _ = UpdatePcAsync(up);
                break;
            case "wake-pc":
                if (ById() is { } wake) WakePc(wake);
                break;
            case "app-update-install":
                _ = AppLog.GuardAsync("Installing the app update", updater.InstallAsync);
                break;
            case "get-app-settings":
                PostAppSettings();
                break;
            case "set-app-settings" when m.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object:
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
        }
    }

    /// <summary>A new sync pair. From a PC's dashboard it is always for that PC. Always confirmed here, in a
    /// window the page can't draw, showing exactly which folders and which direction.</summary>
    void AddSyncPair(JsonElement pr, string? fromPc, CoreWebView2 reply)
    {
        string P(string n) => J.S(pr, n);
        var pair = new SyncPair { Local = P("local"), PcId = fromPc ?? P("pcId"), Remote = P("remote"), Mode = P("mode"), Ignore = P("ignore") };
        if (links.FirstOrDefault(l => l.Pc.Id == pair.PcId) is not { } link) return;
        if (sync.Validate(pair) is { } problem)
        {
            MessageBox.Show(this, problem, "Folder sync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Post(sync.Snapshot(fromPc), reply);
            return;
        }
        var direction = pair.Mode switch
        {
            "to-pc" => $"from this computer to {link.Pc.DisplayName} (files here are copied there)",
            "from-pc" => $"from {link.Pc.DisplayName} to this computer (files there are copied here)",
            _ => "both ways (changes on either side are copied to the other)",
        };
        ShowWindow();
        if (MessageBox.Show(this,
                $"Keep these folders in sync?\n\nOn this computer:\n{pair.Local}\n\nOn {link.Pc.DisplayName}:\n{pair.Remote}\n\nDirection: {direction}\n\n" +
                (fromPc is not null ? $"Requested from {link.Pc.DisplayName}'s page. " : "") +
                "Click Yes only if you set this up yourself.",
                "SSH Manager: add a folder sync?", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            Post(sync.Snapshot(fromPc), reply);
            return;
        }
        sync.Add(pair);
    }

    async Task UpdatePcAsync(Link link)
    {
        try
        {
            using var r = await Link.Http.SendAsync(link.Request(HttpMethod.Post, "/api/update/install"));
            if (!r.IsSuccessStatusCode) throw new InvalidOperationException(await r.Content.ReadAsStringAsync());
            Balloon("SSH Manager", $"Updating {link.Pc.DisplayName}. It reconnects by itself when done.", ToolTipIcon.Info, force: true);
        }
        catch (Exception e)
        {
            MessageBox.Show(this, $"Couldn't start the update on {link.Pc.DisplayName}:\n\n{e.Message}", "SSH Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void WakePc(Link link)
    {
        if (link.Pc.Mac == "")
        {
            MessageBox.Show(this, $"SSH Manager doesn't know {link.Pc.DisplayName}'s network card yet. It learns it the next time it's connected.",
                "Wake up", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            Wol.Send(link.Pc.Mac, link.Pc.Ips.Select(Ssh.CleanIp).OfType<string>().ToList());
            Balloon("SSH Manager", $"Wake-up signal sent to {link.Pc.DisplayName}. It reconnects by itself once it's up.", ToolTipIcon.Info, force: true);
            link.Reconnect(resetBackoff: true);
        }
        catch (Exception e) { MessageBox.Show(this, e.Message, "Wake up", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    // ---- shared clipboard (text): local copies go to the PCs, copies on a PC come here ----
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x031D) AppLog.Guard("Clipboard change", OnLocalClipboardChanged);   // WM_CLIPBOARDUPDATE
        base.WndProc(ref m);
    }

    /// <summary>Password managers mark secrets so clipboard tools leave them alone; those are never shared.</summary>
    static bool PrivateClipboard()
    {
        var data = Clipboard.GetDataObject();
        if (data is null) return false;
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || data.GetDataPresent("Clipboard Viewer Ignore")) return true;
        if (data.GetDataPresent("CanIncludeInClipboardHistory") && data.GetData("CanIncludeInClipboardHistory") is MemoryStream ms)
        {
            var b = ms.ToArray();
            if (b.Length >= 4 && BitConverter.ToInt32(b, 0) == 0) return true;
        }
        return false;
    }

    void OnLocalClipboardChanged()
    {
        localClipChangedUtc = DateTime.UtcNow;
        if (!settings.ShareClipboard) return;
        string text;
        try
        {
            if (PrivateClipboard() || !Clipboard.ContainsText()) return;
            text = Clipboard.GetText();
        }
        catch (Exception) { return; }
        if (text.Length == 0 || text.Length > 1_000_000 || text == clipExchanged) return;   // nothing new, or it came from a PC
        foreach (var l in links.Where(l => l.State == LinkState.Connected && !l.Pc.IsLocal).ToList())
            _ = SendClipAsync(l, text);
    }

    async Task SendClipAsync(Link link, string text)
    {
        try
        {
            using var r = await Link.Http.SendAsync(link.Request(HttpMethod.Post, "/api/clipboard",
                JsonContent.Create(new { text, origin = Environment.MachineName })));
            if (r.IsSuccessStatusCode) clipExchanged = text;
        }
        catch (Exception) { }
    }

    async Task PollClipboardAsync()
    {
        if (!settings.ShareClipboard || clipBusy) return;
        clipBusy = true;
        try
        {
            foreach (var l in links.Where(l => l.State == LinkState.Connected && !l.Pc.IsLocal).ToList())
            {
                try
                {
                    var asked = DateTime.UtcNow;
                    using var r = await Link.Http.SendAsync(l.Request(HttpMethod.Get, $"/api/clipboard?since={Math.Max(l.ClipSeq, 0)}"));
                    if (!r.IsSuccessStatusCode) continue;
                    using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
                    var root = doc.RootElement;
                    if (!J.Has(root, "seq", out var sv) || !sv.TryGetInt32(out var seq)) continue;
                    var first = l.ClipSeq < 0 || seq < l.ClipSeq;   // just enabled, reconnected, or the PC's server restarted
                    l.ClipSeq = seq;
                    if (first || !J.B(root, "changed")) continue;
                    var text = J.S(root, "text");
                    if (text == "" || J.S(root, "origin") == Environment.MachineName || text == clipExchanged) continue;
                    // a server that says when the copy was made: never put an older copy over a newer one here
                    if (J.D(root, "age_s") is var age && age > 0 && asked.AddSeconds(-age) < localClipChangedUtc) continue;
                    try
                    {
                        clipExchanged = text;   // before setting, so our own clipboard event doesn't send it back
                        Clipboard.SetText(text);
                    }
                    catch (Exception) { clipExchanged = null; }
                }
                catch (Exception) { }
            }
        }
        finally { clipBusy = false; }
    }

    [DllImport("user32.dll")]
    static extern bool AddClipboardFormatListener(IntPtr hwnd);

    // ---- pairing, approving side: a PC this app is connected to asks to be allowed to manage this PC ----
    async Task CheckPairRequestAsync(Link link)
    {
        using var r = await Link.Http.SendAsync(link.Request(HttpMethod.Get,
            $"/api/pair/pending?machine={Uri.EscapeDataString(Environment.MachineName)}&machine_id={Uri.EscapeDataString(settings.MachineId)}"));
        if (!r.IsSuccessStatusCode) return;   // an older server without pairing
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        if (J.O(doc.RootElement, "request") is not { ValueKind: JsonValueKind.Object } q) return;
        var id = J.S(q, "id");
        if (id == "" || !pairSeen.Add(id)) return;
        var from = J.S(q, "from") is { Length: > 0 } f ? f : link.Pc.DisplayName;
        var address = J.S(q, "address");
        var key = J.S(q, "public_key");
        var fp = J.S(q, "fingerprint");
        var who = $"{link.Pc.Id}|{from}|{address}".ToLowerInvariant();
        if (pairDeniedUntil.TryGetValue(who, out var until) && DateTime.UtcNow < until)
        {
            _ = AnswerPairAsync(link, id, false, "Denied recently; try again later.");
            return;
        }
        if (key == "") return;
        pairPrompting = true;
        _ = AnswerPairRequestAsync(link, id, from, address, key, fp, who).ContinueWith(_ => pairPrompting = false, TaskScheduler.FromCurrentSynchronizationContext());
    }

    async Task AnswerPairAsync(Link link, string id, bool allowed, string error = "")
    {
        try
        {
            using (await Link.Http.SendAsync(link.AppRequest(HttpMethod.Post, $"/api/pair/{Uri.EscapeDataString(id)}/answer",
                JsonContent.Create(new { machine = Environment.MachineName, machine_id = settings.MachineId, allowed,
                                         user = allowed ? Environment.UserName : "", error })))) { }
        }
        catch (Exception) { }
    }

    async Task AnswerPairRequestAsync(Link link, string id, string from, string address, string key, string fingerprint, string who)
    {
        ShowWindow();
        var choice = MessageBox.Show(this,
            $"{from} asks to be allowed to manage this PC.\n\n" +
            $"If you allow it, {from} can sign in to this PC as {Environment.UserName} over SSH, with the same rights as you: " +
            "open and change files, run commands and view the screen.\n\n" +
            $"Requested by: {from}\nIts address: {(address != "" ? address : "unknown")}\nThrough the connection to: {link.Pc.DisplayName}\n" +
            $"Key fingerprint: {(fingerprint != "" ? fingerprint : "unknown")}\n\n" +
            $"Allow only if you started this yourself on {from} just now. You can take it back at any time in Settings → SSH keys.",
            "SSH Manager: allow access?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (choice != DialogResult.Yes)
        {
            pairDeniedUntil[who] = DateTime.UtcNow.AddMinutes(5);   // a repeated request doesn't pop up again right away
            await AnswerPairAsync(link, id, false);
            return;
        }
        if (links.FirstOrDefault(l => l.Pc.IsLocal) is not { State: LinkState.Connected } own)
        {
            await AnswerPairAsync(link, id, false, "SSH Manager's server isn't running on this PC.");
            MessageBox.Show(this, "SSH Manager's server isn't running on this PC, so the key could not be added.",
                "SSH Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        try
        {
            using var r = await Link.Http.SendAsync(own.AppRequest(HttpMethod.Post, "/api/keys/add",
                JsonContent.Create(new { public_key = key, label = $"{from} (allowed {DateTime.Now:yyyy-MM-dd})" })));
            if (!r.IsSuccessStatusCode)
                throw new InvalidOperationException(Pairing.Detail(await r.Content.ReadAsStringAsync()) ?? "The key could not be added.");
            await AnswerPairAsync(link, id, true);
            Balloon("SSH Manager", $"{from} can now manage this PC.", ToolTipIcon.Info, force: true);
        }
        catch (Exception e)
        {
            await AnswerPairAsync(link, id, false, e.Message);
            MessageBox.Show(this, e.Message, "SSH Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void OpenWizard(PcEntry? edit)
    {
        if (!webReady) return;
        appSettingsOpen = false;
        wizardOpen = true;
        Text = edit is null ? "SSH Manager · Add a PC" : $"SSH Manager · {edit.DisplayName}";
        wizard.Open(edit, canCancel: links.Count > 0);
    }

    /// <summary>The app's own settings page (Ui.AppSettingsPage), shown in the shared view.</summary>
    void OpenAppSettings()
    {
        if (!webReady) return;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        string? html;
        try { html = typeof(Ui).GetMethod("AppSettingsPage", flags, Type.EmptyTypes)?.Invoke(null, null) as string; }
        catch (Exception e) { AppLog.Write("Drawing the app settings page", e); return; }
        if (html is null) return;   // this build has no settings page
        wizardOpen = false;
        appSettingsOpen = true;
        Text = "SSH Manager · App settings";
        mainShown = AppSettingsView;
        NavigateMain(html);
        PushPcs();
    }

    void OnWizardDone(PcEntry? pc)
    {
        wizardOpen = false;
        mainShown = "";
        if (pc is not null)
        {
            // adding a PC that is already in the list updates that entry instead of adding it twice
            var existing = settings.Pcs.FirstOrDefault(p => p.Id == pc.Id)
                ?? settings.Pcs.FirstOrDefault(p => p.Host.Equals(pc.Host, StringComparison.OrdinalIgnoreCase)
                                                    && p.User.Equals(pc.User, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) pc.Id = existing.Id;
            if (existing is not null)
            {
                lock (settings.Gate)
                {
                    existing.Host = pc.Host;
                    existing.User = pc.User;
                    existing.KeyFile = pc.KeyFile;
                    existing.LookupName = pc.LookupName;
                    existing.PcName = "";
                }
                links.FirstOrDefault(l => l.Pc == existing)?.Reconnect(resetBackoff: true);
            }
            else
            {
                lock (settings.Gate) settings.Pcs.Add(pc);
                AddLink(pc);
            }
            current = pc.Id;
            settings.Selected = pc.Id;
            settings.Save();
        }
        ShowCurrent();
        PushPcs();
    }

    void RemovePc(Link link, bool ask = true)
    {
        var pairs = settings.Syncs.Count(p => p.PcId == link.Pc.Id);
        if (ask && MessageBox.Show(this, $"Remove {link.Pc.DisplayName} from SSH Manager?\n\nThis only removes it from this app. " +
                (pairs > 0 ? $"Its {pairs} folder sync{(pairs == 1 ? "" : "s")} stop{(pairs == 1 ? "s" : "")} too (no files are deleted). " : "") +
                "SSH Manager on that PC and this computer's key there stay; remove the key in that PC's Settings → SSH keys if you want to lock this computer out.",
                "Remove PC", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        DropPcView(link.Pc.Id);
        link.Dispose();
        links.Remove(link);
        linkSnapshot = links.ToArray();
        lock (settings.Gate) settings.Pcs.Remove(link.Pc);
        sync.RemoveForPc(link.Pc.Id);
        feeds.Remove(link.Pc.Id);
        seen.Remove(link.Pc.Id);
        connectedNow.Remove(link.Pc.Id);
        if (current == link.Pc.Id) current = links.Count > 1 ? All : links.FirstOrDefault()?.Pc.Id ?? "";
        settings.Selected = current;
        settings.Save();
        mainShown = "";
        UpdateTray();
        ShowCurrent();
        PushPcs();
    }

    /// <summary>The app's settings, to the shared view only (a PC's page never sees local paths).</summary>
    void PostAppSettings()
    {
        if (!webReady) return;
        Post(new
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
                settings.AutoUpdate,
                settings.DownloadFolder,
                settings.AskWhereToSave,
                settings.ShareClipboard,
                settings.Theme,
                EffectiveTheme = darkNow ? "dark" : "light",
                Connection = CurrentLink() is { } l ? (l.Pc.IsLocal ? "this PC (local)" : l.Pc.Target) : "",
                AppVersion = updater.Current,
                ServerUpdatesApp = AppUpdater.ServerUpdatesThisPc(),
            },
        }, web.CoreWebView2);
    }

    bool Confirm(string text, string title) =>
        MessageBox.Show(this, text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    void ApplyAppSettings(JsonElement s)
    {
        foreach (var p in s.EnumerateObject())
        {
            if (p.Name == "Theme")
            {
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is "system" or "light" or "dark" && p.Value.GetString() != settings.Theme)
                {
                    settings.Theme = p.Value.GetString()!;
                    ApplyTheme();
                }
                continue;
            }
            if (p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;   // unknown or odd: ignored
            var on = p.Value.ValueKind == JsonValueKind.True;
            switch (p.Name)
            {
                case "StartWithWindows" when on != StartsWithWindows():
                    if (!on || Confirm("Start SSH Manager automatically when you sign in to Windows?", "SSH Manager")) ToggleStartup();
                    break;
                case "NotifyConnection": settings.NotifyConnection = on; break;
                case "NotifyFailedLogin": settings.NotifyFailedLogin = on; break;
                case "NotifyUnknown": settings.NotifyUnknown = on; break;
                case "NotifyTransfers": settings.NotifyTransfers = on; break;
                case "AutoUpdate": settings.AutoUpdate = on; break;
                case "AskWhereToSave": settings.AskWhereToSave = on; break;
                case "ShareClipboard" when on != settings.ShareClipboard:
                    if (on && !Confirm("Share the clipboard with your PCs?\n\nText you copy here is sent to every connected PC, and text " +
                                       "copied there comes here. Passwords copied from password managers that mark them as private are not shared.",
                            "SSH Manager: share the clipboard?")) break;
                    settings.ShareClipboard = on;
                    links.ForEach(l => l.ClipSeq = -1);
                    clipExchanged = null;
                    break;
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
        var page = sender as CoreWebView2;   // progress goes to the page that started the download
        long lastReport = 0;
        void Report(string state, string? reason = null) => Post(new
        {
            type = "download", id, name = Path.GetFileName(target), received = op.BytesReceived,
            total = op.TotalBytesToReceive ?? 0, state, path = target, reason,
        }, page);
        op.BytesReceivedChanged += (_, _) =>
        {
            if (Environment.TickCount64 - lastReport < 250) return;   // a few updates a second are enough
            lastReport = Environment.TickCount64;
            AppLog.Guard("Download progress", () => Report("inprogress"));
        };
        op.StateChanged += (_, _) => AppLog.Guard("Download state", () =>
        {
            if (op.State == CoreWebView2DownloadState.Completed)
            {
                lock (downloaded) downloaded.Add(target);
                Report("completed");
            }
            else if (op.State == CoreWebView2DownloadState.Interrupted) Report("interrupted", op.InterruptReason.ToString());
        });
        BeginInvoke(() => AppLog.Guard("Download progress", () => Report("inprogress")));
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

    // ---- window and tray ----
    public void ShowWindow()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = settings.Maximized ? FormWindowState.Maximized : FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveWindowBounds();
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        {
            // closing the window keeps the connections and the tray icon; Exit is in the tray menu
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
        SaveWindowBounds();
        poll.Stop();
        clipTimer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        networkSettle?.Dispose();
        cliPipe.Dispose();
        links.ForEach(l => l.Dispose());
        tray.Visible = false;
        tray.Dispose();
        Application.Exit();
    }

    void RestoreWindowBounds()
    {
        var b = settings.WindowBounds;
        var rect = b is { Length: 4 } ? new Rectangle(b[0], b[1], b[2], b[3]) : Rectangle.Empty;
        // the title bar must be on a screen enough to grab it (a monitor may have been removed since)
        var title = new Rectangle(rect.X, rect.Y, rect.Width, 40);
        if (rect.Width >= MinimumSize.Width / 2 && rect.Height >= 200
            && Screen.AllScreens.Any(s => Rectangle.Intersect(s.WorkingArea, title) is { Width: >= 150, Height: >= 20 }))
            Bounds = rect;
        else
        {
            var area = Screen.PrimaryScreen!.WorkingArea;
            Size = new Size(Math.Min(1280, area.Width - 80), Math.Min(860, area.Height - 80));
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        }
        if (settings.Maximized) WindowState = FormWindowState.Maximized;
    }

    void SaveWindowBounds()
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

    /// <summary>Links from pages open in the browser, but only web links and only after asking: a page must
    /// not be able to start programs or open files through Windows.</summary>
    void ConfirmOpenExternal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
        {
            AppLog.Write($"A page tried to open {(url.Length > 200 ? url[..200] : url)}; ignored.");
            return;
        }
        if (u.IsLoopback) return;   // another PC's tunnel or this PC's server: never through the browser
        var shown = u.AbsoluteUri.Length > 300 ? u.AbsoluteUri[..300] + "…" : u.AbsoluteUri;
        if (MessageBox.Show(this, $"Open this link in your web browser?\n\n{shown}", "SSH Manager",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); } catch (Exception) { }
    }

    static bool SystemDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
