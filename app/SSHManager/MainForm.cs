using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace SSHManager;

/// <summary>
/// The app window: one WebView2 that shows either a PC's dashboard (served by that PC through its
/// SSH tunnel), the "All PCs" overview, a connection status page or the setup wizard. A tray icon
/// keeps every connection alive while the window is closed.
/// </summary>
internal sealed class MainForm : Form
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string All = "all";

    readonly AppSettings settings;
    readonly List<Link> links = [];
    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    readonly NotifyIcon tray = new();
    readonly ToolStripMenuItem startupItem = new("Start with Windows");
    readonly SetupWizard wizard;
    readonly AppUpdater updater;
    readonly SyncManager sync;
    readonly System.Windows.Forms.Timer poll = new() { Interval = 5000 };
    readonly System.Windows.Forms.Timer clipTimer = new() { Interval = 1500 };
    bool clipBusy;
    readonly bool startHidden;
    readonly Dictionary<string, (HashSet<string> fails, HashSet<string> unknown)> seen = [];
    readonly HashSet<string> everConnected = [], connectedNow = [];
    bool webReady, exiting, wizardOpen, polling;
    string current = "", shown = "";
    int downloadSeq;
    object? lastLocalStats;

    public MainForm(AppSettings settings, bool startHidden)
    {
        this.settings = settings;
        this.startHidden = startHidden;

        Text = "SSH Manager";
        Icon = AppIcon.App;
        MinimumSize = new Size(760, 520);
        BackColor = SystemDark() ? Color.FromArgb(13, 17, 23) : Color.FromArgb(245, 247, 250);
        StartPosition = FormStartPosition.Manual;
        RestoreWindowBounds();
        Controls.Add(web);

        wizard = new SetupWizard(html => { if (webReady) { shown = "wizard"; web.CoreWebView2.NavigateToString(html); } }, OnWizardDone, KnownUser);
        updater = new AppUpdater(settings, (title, text) => Balloon(title, text, ToolTipIcon.Info));
        updater.Changed += () => OnUi(PushOverview);
        sync = new SyncManager(settings, id => links.FirstOrDefault(l => l.Pc.Id == id), () => OnUi(() => Post(sync!.Snapshot())));

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open SSH Manager", null, (_, _) => ShowWindow());
        menu.Items.Add("All PCs", null, (_, _) => { ShowWindow(); SwitchTo(All); });
        menu.Items.Add("Add a PC…", null, (_, _) => { ShowWindow(); OpenWizard(null); });
        menu.Items.Add("Reconnect all", null, (_, _) => links.ForEach(l => l.Reconnect()));
        menu.Items.Add(new ToolStripSeparator());
        startupItem.Checked = StartsWithWindows();
        startupItem.Click += (_, _) => ToggleStartup();
        menu.Items.Add(startupItem);
        menu.Items.Add("Check for updates", null, async (_, _) =>
        {
            await updater.CheckAsync();
            Balloon("SSH Manager", updater.Available ? $"Version {updater.Latest} is available." : updater.Error ?? $"You have the latest version ({updater.Current}).", ToolTipIcon.Info, force: true);
        });
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
        foreach (var pc in settings.Pcs) AddLink(pc);
        current = settings.Selected == All && links.Count > 1 ? All
            : links.Any(l => l.Pc.Id == settings.Selected) ? settings.Selected
            : links.Count > 1 ? All : links.FirstOrDefault()?.Pc.Id ?? "";
        poll.Tick += async (_, _) => await PollAsync();
    }

    Link AddLink(PcEntry pc)
    {
        var port = 8765 + links.Count;
        var link = new Link(pc, port, settings.Save);
        link.Changed += (l, state, message) => OnUi(() => OnLinkChanged(l, state, message));
        links.Add(link);
        if (IsHandleCreated) link.Start();
        return link;
    }

    void OnUi(Action a)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(a);
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
        if (SystemDark())
        {
            var on = 1;
            DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));   // dark title bar
        }
        links.ForEach(l => l.Start());
        updater.Start();
        poll.Start();
        sync.Start();
        AddClipboardFormatListener(Handle);
        clipTimer.Tick += async (_, _) => await PollClipboardAsync();
        clipTimer.Start();
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
            MessageBox.Show("SSH Manager needs the Microsoft Edge WebView2 Runtime. Reinstall SSH Manager to get it.\n\n" + ex.Message,
                "SSH Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ExitApp();
            return;
        }
        var core = web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.NewWindowRequested += (_, a) => { a.Handled = true; OpenExternal(a.Uri); };
        core.WebMessageReceived += (_, a) => OnPageMessage(a.WebMessageAsJson);
        core.DownloadStarting += OnDownloadStarting;
        web.NavigationCompleted += async (_, a) =>
        {
            if (a.IsSuccess)
            {
                PushPcs();
                if (shown == All) PushOverview();
                return;
            }
            // a PC's server restarted under us: try again shortly while its link is still up
            var link = CurrentLink();
            if (link is not null && shown == link.Url && link.State == LinkState.Connected)
            {
                await Task.Delay(2000);
                if (shown == link.Url && link.State == LinkState.Connected) web.CoreWebView2.Navigate(link.Url);
            }
        };
        webReady = true;
        ShowCurrent();
    }

    Link? CurrentLink() => links.FirstOrDefault(l => l.Pc.Id == current);

    void SwitchTo(string id)
    {
        wizardOpen = false;
        current = id;
        settings.Selected = id;
        settings.Save();
        ShowCurrent();
    }

    /// <summary>Puts the right page in the window for what is selected and its connection state.</summary>
    void ShowCurrent()
    {
        if (!webReady || wizardOpen) return;
        if (links.Count == 0)
        {
            OpenWizard(null);
            return;
        }
        if (current == All && links.Count < 2) current = links[0].Pc.Id;   // "All PCs" needs two PCs
        if (current == All)
        {
            Text = "SSH Manager · All PCs";
            if (shown != All)
            {
                shown = All;
                web.CoreWebView2.NavigateToString(Ui.Overview());
            }
            else PushOverview();
            return;
        }
        var link = CurrentLink() ?? links[0];
        current = link.Pc.Id;
        Text = $"SSH Manager · {link.Pc.DisplayName}" + (link.State == LinkState.Connected ? "" : " (not connected)");
        if (link.State == LinkState.Connected)
        {
            if (shown != link.Url)
            {
                // the server only answers requests that carry its access token; one cookie per local port
                var cookies = web.CoreWebView2.CookieManager;
                cookies.AddOrUpdateCookie(cookies.CreateCookie($"sshmgr_token_{link.Port}", link.Token, "localhost", "/"));
                shown = link.Url;
                web.CoreWebView2.Navigate(link.Url);
            }
        }
        else
        {
            var key = $"status:{link.Pc.Id}:{link.State}:{link.Message}";
            if (shown != key)
            {
                shown = key;
                web.CoreWebView2.NavigateToString(Ui.StatusPage(link.Pc, link.State, link.Message));
            }
        }
    }

    void OnLinkChanged(Link link, LinkState state, string message)
    {
        UpdateTray();
        var name = link.Pc.DisplayName;
        if (state == LinkState.Connected)
        {
            if (everConnected.Contains(link.Pc.Id) && !connectedNow.Contains(link.Pc.Id) && settings.NotifyConnection)
                Balloon("SSH Manager", $"Connected to {name} again.", ToolTipIcon.Info);
            everConnected.Add(link.Pc.Id);
            connectedNow.Add(link.Pc.Id);
        }
        else if (state == LinkState.Disconnected && connectedNow.Remove(link.Pc.Id) && settings.NotifyConnection)
            Balloon("SSH Manager", $"Lost the connection to {name}. Reconnecting…", ToolTipIcon.Warning);

        if (link.Pc.Id == current) ShowCurrent();
        PushPcs();
        if (current == All) PushOverview();
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
        if (force || !IsActive()) tray.ShowBalloonTip(5000, title, text, icon);
    }

    bool IsActive() => Visible && WindowState != FormWindowState.Minimized && ActiveForm == this;

    void Post(object message)
    {
        if (webReady) web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    void PushPcs() => Post(new
    {
        type = "pcs",
        current = wizardOpen ? "" : current,
        pcs = links.Select(l => new { id = l.Pc.Id, name = l.Pc.DisplayName, state = l.State.ToString() }),
    });

    /// <summary>A computer that connects to one of the PCs here but has no server of its own (a client),
    /// as that PC reports it: it can't be opened, but All PCs shows its status and stats.</summary>
    sealed record ClientPc(string Name, string State, string Via, JsonElement? Stats, string Mac, string Ip, string Version,
        string Host = "", string Machine = "", string User = "");

    /// <summary>The Windows user name a computer that connects here reported (by name, computer name or address).</summary>
    string? KnownUser(string hostOrIp)
    {
        var key = hostOrIp.Trim();
        if (key == "") return null;
        bool Is(string s) => s != "" && (s.Equals(key, StringComparison.OrdinalIgnoreCase)
                                         || s.Split('.')[0].Equals(key.Split('.')[0], StringComparison.OrdinalIgnoreCase) && !char.IsDigit(key[0]));
        return Clients().FirstOrDefault(c => c.User != "" && (Is(c.Name) || Is(c.Host) || Is(c.Machine) || c.Ip == key))?.User;
    }

    List<ClientPc> Clients()
    {
        static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        static bool B(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
        static JsonElement? O(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;
        static string FirstMac(JsonElement? st) =>
            st is { } x && x.TryGetProperty("macs", out var m) && m.ValueKind == JsonValueKind.Array && m.GetArrayLength() > 0 ? m[0].GetString() ?? "" : "";

        var known = links.Select(l => l.Pc.PcName).Append(Environment.MachineName).Where(n => n != "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new List<ClientPc>();
        bool Seen(string name, string machine) => known.Contains(name) || (machine != "" && known.Contains(machine))
            || list.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        foreach (var l in links.Where(l => l.State == LinkState.Connected && l.Status is not null))
        {
            var s = l.Status!.Value;
            if (s.TryGetProperty("peers", out var peers) && peers.ValueKind == JsonValueKind.Array)
                foreach (var p in peers.EnumerateArray())
                {
                    var st = O(p, "stats");
                    var name = S(p, "name");
                    if (name == "" || Seen(name, st is { } x ? S(x, "machine") : "")) continue;
                    var state = B(p, "connected") ? "Connected" : B(p, "online") ? "Online" : "Offline";
                    var ip = S(p, "resolved_ip") is { Length: > 0 } r ? r : S(p, "ip");
                    list.Add(new(name, state, l.Pc.DisplayName, st, FirstMac(st), ip, st is { } y ? S(y, "app_version") : "",
                        S(p, "host"), st is { } z ? S(z, "machine") : "", st is { } u ? S(u, "user") : ""));
                }
            if (s.TryGetProperty("other_apps", out var others) && others.ValueKind == JsonValueKind.Array)
                foreach (var c in others.EnumerateArray())
                {
                    var name = S(c, "machine");
                    if (name == "" || Seen(name, name)) continue;
                    list.Add(new(name, "Connected", l.Pc.DisplayName, c, FirstMac(c), "", S(c, "app_version"), "", name, S(c, "user")));
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
            lastLocalStats = LocalStats.Collect();
            foreach (var link in links.Where(l => l.State == LinkState.Connected).ToList())
            {
                try
                {
                    if (!link.Pc.IsLocal)
                        using (await Link.Http.SendAsync(link.Request(HttpMethod.Post, "/api/clients/report", JsonContent.Create(lastLocalStats)))) { }
                    using var r = await Link.Http.SendAsync(link.Request(HttpMethod.Get, "/api/status"));
                    if (!r.IsSuccessStatusCode) continue;
                    using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
                    link.Status = doc.RootElement.Clone();
                    CheckAlerts(link);
                }
                catch (Exception) { }
            }
            if (current == All) PushOverview();
        }
        finally { polling = false; }
    }

    /// <summary>Tray notifications for new failed logins and unknown computers on any PC.</summary>
    void CheckAlerts(Link link)
    {
        if (link.Status is not { } s) return;
        var fails = s.GetProperty("failures_24h").EnumerateArray()
            .Select(f => f.GetProperty("ts").GetString() + f.GetProperty("ip").GetString()).ToHashSet();
        var unknown = s.GetProperty("unknown_clients").EnumerateArray()
            .Select(u => u.GetProperty("ip").GetString() + u.GetProperty("last").GetString()).ToHashSet();
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

    void PushOverview()
    {
        if (shown != All) return;
        var pcs = new List<object>();
        foreach (var l in links)
        {
            object? stats = null;
            int? sessions = null, failures = null;
            string? latest = null;
            var canUpdate = false;
            if (l.State == LinkState.Connected && l.Status is { } s)
            {
                var host = s.GetProperty("host");
                var ram = host.GetProperty("ram");
                stats = new
                {
                    cpu = host.GetProperty("cpu").GetDouble(),
                    ram_total = ram.GetProperty("total").GetInt64(),
                    ram_used = ram.GetProperty("used").GetInt64(),
                    disks = host.GetProperty("disks").EnumerateArray().Select(d => new
                    {
                        mount = d.GetProperty("mount").GetString(), total = d.GetProperty("total").GetInt64(), free = d.GetProperty("free").GetInt64(),
                    }).ToArray(),
                    uptime_s = host.GetProperty("uptime_s").GetInt64(),
                };
                sessions = s.GetProperty("sessions").GetArrayLength();
                failures = s.GetProperty("failures_24h").GetArrayLength();
                if (s.TryGetProperty("update", out var u))
                {
                    latest = u.GetProperty("latest").GetString();
                    canUpdate = u.GetProperty("available").GetBoolean() && u.GetProperty("installable").GetBoolean()
                                && u.GetProperty("status").GetString() == "idle";
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
        Post(new { type = "overview", pcs, appUpdate });
    }

    // ---- messages from pages ----
    void OnPageMessage(string json)
    {
        JsonElement m;
        try { m = JsonDocument.Parse(json).RootElement; } catch (JsonException) { return; }
        if (m.ValueKind == JsonValueKind.String)
        {
            if (m.GetString() == "retry") CurrentLink()?.Reconnect();
            return;
        }
        if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("type", out var t)) return;
        string Str(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        Link? ById() => links.FirstOrDefault(l => l.Pc.Id == Str("id"));

        switch (t.GetString())
        {
            case { } type when type.StartsWith("setup-"):
                wizard.OnMessage(type, Str);
                break;
            case "switch-pc":
                SwitchTo(Str("id"));
                break;
            case "wake-mac":
                try
                {
                    Wol.Send(Str("mac"), Str("ip") is { Length: > 0 } wip ? [wip] : []);
                    Balloon("SSH Manager", $"Wake-up signal sent to {Str("name")}.", ToolTipIcon.Info, force: true);
                }
                catch (Exception e) { MessageBox.Show(this, e.Message, "Wake up", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                break;
            case "add-pc":
                OpenWizard(null);
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
            case "retry":
                (ById() ?? CurrentLink())?.Reconnect();
                break;
            case "auth-failed":
                CurrentLink()?.Reconnect();
                break;
            case "update-pc":
                if (ById() is { } up) _ = UpdatePcAsync(up);
                break;
            case "wake-pc":
                if (ById() is { } wake) WakePc(wake);
                break;
            case "sync-get":
                Post(sync.Snapshot());
                break;
            case "sync-add" when m.TryGetProperty("pair", out var pr):
                string P(string n) => pr.TryGetProperty(n, out var v) ? v.GetString() ?? "" : "";
                sync.Add(new SyncPair { Local = P("local"), PcId = P("pcId"), Remote = P("remote"), Mode = P("mode"), Ignore = P("ignore") });
                break;
            case "sync-now":
                sync.SyncNow(Str("id"));
                break;
            case "sync-toggle":
                sync.Toggle(Str("id"));
                break;
            case "sync-remove":
                sync.Remove(Str("id"));
                break;
            case "sync-pick-local":
                using (var dlg = new FolderBrowserDialog { Description = "Folder on this computer to keep in sync" })
                    if (dlg.ShowDialog(this) == DialogResult.OK) Post(new { type = "sync-local-picked", path = dlg.SelectedPath });
                break;
            case "app-update-install":
                _ = updater.InstallAsync();
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
            case "notify" when Str("kind") == "transfers" && settings.NotifyTransfers:
                // failed logins and unknown computers are watched by the app itself (CheckAlerts)
                Balloon(Str("title"), Str("text"), ToolTipIcon.Info);
                break;
        }
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
            Wol.Send(link.Pc.Mac, link.Pc.Ips);
            Balloon("SSH Manager", $"Wake-up signal sent to {link.Pc.DisplayName}. It reconnects by itself once it's up.", ToolTipIcon.Info, force: true);
            link.Reconnect();
        }
        catch (Exception e) { MessageBox.Show(this, e.Message, "Wake up", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    // ---- shared clipboard (text): local copies go to the PCs, copies on a PC come here ----
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x031D) OnLocalClipboardChanged();   // WM_CLIPBOARDUPDATE
        base.WndProc(ref m);
    }

    void OnLocalClipboardChanged()
    {
        if (!settings.ShareClipboard) return;
        string text;
        try
        {
            if (!Clipboard.ContainsText()) return;
            text = Clipboard.GetText();
        }
        catch (Exception) { return; }
        if (text.Length == 0 || text.Length > 1_000_000) return;
        foreach (var l in links.Where(l => l.State == LinkState.Connected && !l.Pc.IsLocal && l.ClipText != text).ToList())
        {
            l.ClipText = text;
            _ = SendClipAsync(l, text);
        }
    }

    static async Task SendClipAsync(Link link, string text)
    {
        try
        {
            using var r = await Link.Http.SendAsync(link.Request(HttpMethod.Post, "/api/clipboard",
                JsonContent.Create(new { text, origin = Environment.MachineName })));
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
                    using var r = await Link.Http.SendAsync(l.Request(HttpMethod.Get, $"/api/clipboard?since={Math.Max(l.ClipSeq, 0)}"));
                    if (!r.IsSuccessStatusCode) continue;
                    using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
                    var root = doc.RootElement;
                    var seq = root.GetProperty("seq").GetInt32();
                    var first = l.ClipSeq < 0 || seq < l.ClipSeq;   // just enabled, or the PC's server restarted
                    l.ClipSeq = seq;
                    if (first || !root.GetProperty("changed").GetBoolean()) continue;
                    var text = root.GetProperty("text").GetString();
                    if (string.IsNullOrEmpty(text) || root.GetProperty("origin").GetString() == Environment.MachineName || text == l.ClipText) continue;
                    l.ClipText = text;
                    try { Clipboard.SetText(text); } catch (Exception) { }
                }
                catch (Exception) { }
            }
        }
        finally { clipBusy = false; }
    }

    [DllImport("user32.dll")]
    static extern bool AddClipboardFormatListener(IntPtr hwnd);

    void OpenWizard(PcEntry? edit)
    {
        if (!webReady) return;
        wizardOpen = true;
        Text = edit is null ? "SSH Manager · Add a PC" : $"SSH Manager · {edit.DisplayName}";
        wizard.Open(edit, canCancel: links.Count > 0);
    }

    void OnWizardDone(PcEntry? pc)
    {
        wizardOpen = false;
        shown = "";
        if (pc is not null)
        {
            var existing = settings.Pcs.FirstOrDefault(p => p.Id == pc.Id);
            if (existing is not null)
            {
                existing.Host = pc.Host;
                existing.User = pc.User;
                existing.KeyFile = pc.KeyFile;
                existing.LookupName = pc.LookupName;
                existing.PcName = "";
                links.FirstOrDefault(l => l.Pc == existing)?.Reconnect();
            }
            else
            {
                settings.Pcs.Add(pc);
                AddLink(pc);
            }
            current = pc.Id;
            settings.Selected = pc.Id;
            settings.Save();
        }
        ShowCurrent();
        PushPcs();
    }

    void RemovePc(Link link)
    {
        if (MessageBox.Show(this, $"Remove {link.Pc.DisplayName} from SSH Manager?\n\nThis only removes it from this app. " +
                "SSH Manager on that PC and this computer's key there stay; remove the key in that PC's Settings → SSH keys if you want to lock this computer out.",
                "Remove PC", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        link.Dispose();
        links.Remove(link);
        settings.Pcs.Remove(link.Pc);
        if (current == link.Pc.Id) current = links.Count > 1 ? All : links.FirstOrDefault()?.Pc.Id ?? "";
        settings.Selected = current;
        settings.Save();
        shown = "";
        UpdateTray();
        ShowCurrent();
        PushPcs();
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
            settings.AutoUpdate,
            settings.DownloadFolder,
            settings.AskWhereToSave,
            settings.ShareClipboard,
            Connection = CurrentLink() is { } l ? (l.Pc.IsLocal ? "this PC (local)" : l.Pc.Target) : "",
            AppVersion = updater.Current,
            ServerUpdatesApp = AppUpdater.ServerUpdatesThisPc(),
        },
    });

    void ApplyAppSettings(JsonElement s)
    {
        foreach (var p in s.EnumerateObject())
        {
            var on = p.Value.ValueKind == JsonValueKind.True;
            switch (p.Name)
            {
                case "StartWithWindows": if (on != StartsWithWindows()) ToggleStartup(); break;
                case "NotifyConnection": settings.NotifyConnection = on; break;
                case "NotifyFailedLogin": settings.NotifyFailedLogin = on; break;
                case "NotifyUnknown": settings.NotifyUnknown = on; break;
                case "NotifyTransfers": settings.NotifyTransfers = on; break;
                case "AutoUpdate": settings.AutoUpdate = on; break;
                case "AskWhereToSave": settings.AskWhereToSave = on; break;
                case "ShareClipboard": settings.ShareClipboard = on; links.ForEach(l => l.ClipSeq = -1); break;
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
        links.ForEach(l => l.Dispose());
        tray.Visible = false;
        tray.Dispose();
        Application.Exit();
    }

    void RestoreWindowBounds()
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
