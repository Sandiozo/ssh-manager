using System.Text.Json;

namespace SSHManager;

/// <summary>
/// Pairing, approving side: a PC this app is connected to passes on a request from a computer that wants
/// to manage this PC (see sshmgr/pairing.py). The app asks in a prompt that doesn't hold anything up,
/// shows how long the request is still open and closes by itself when it expires or is cancelled there.
/// The prompt is a window of its own, on top of the others: it is there whether the main window is
/// open, minimised or in the tray. It doesn't take the keyboard, and Allow can't be clicked in its first
/// moments, so a prompt that appears (or replaces another) under the user's hands isn't answered by accident.
/// What Allow does is in PairApproval.
/// </summary>
internal sealed class PairRequests(Form owner, AppSettings settings, Func<Link?> ownLink, Action<string, string> balloon)
{
    /// <summary>At most this many prompts at once; more wait for the next check.</summary>
    const int MaxOpen = 3;
    /// <summary>A request lives this long on the server; a longer time from a PC is not believed.</summary>
    const int MaxSeconds = 120;

    readonly Dictionary<string, (Link Link, PairPrompt Prompt)> open = [];
    readonly HashSet<string> seen = [];
    /// <summary>Requests from a computer whose last request was denied are turned down without asking for a while.</summary>
    readonly Dictionary<string, DateTime> deniedUntil = [];

    PairIdentity Me => new(Environment.MachineName, settings.MachineId, Environment.UserName);

    static PairSend To(Link link) => (method, path, body) => Link.Http.SendAsync(link.AppRequest(method, path, PairApproval.Json(body)));

    public async Task CheckAsync(Link link)
    {
        using var r = await Link.Http.SendAsync(link.Request(HttpMethod.Get,
            $"/api/pair/pending?machine={Uri.EscapeDataString(Environment.MachineName)}&machine_id={Uri.EscapeDataString(settings.MachineId)}"));
        if (!r.IsSuccessStatusCode) return;   // an older server without pairing
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var q = J.O(doc.RootElement, "request");
        var id = J.S(q, "id");
        // expired, cancelled on the other computer, or replaced by a newer request: that prompt closes
        foreach (var (_, entry) in open.Where(o => o.Value.Link == link && o.Key != id).ToList())
            entry.Prompt.CloseUnanswered();
        if (id == "" || seen.Contains(id) || open.Count >= MaxOpen) return;
        seen.Add(id);

        var seconds = J.N(q, "expires_in") is { } left ? Math.Min(left, MaxSeconds) : MaxSeconds;
        if (seconds <= 0) return;
        var pcName = PairApproval.Clean(link.Pc.DisplayName, 64);
        var from = PairApproval.Clean(J.S(q, "from"), 64) is { Length: > 0 } f ? f : pcName;
        var address = PairApproval.Clean(J.S(q, "address"), 64);
        var key = J.S(q, "public_key");
        var who = $"{link.Pc.Id}|{from}|{address}".ToLowerInvariant();
        if (deniedUntil.TryGetValue(who, out var until) && DateTime.UtcNow < until)
        {
            _ = PairApproval.DenyAsync(To(link), id, Me, "Denied recently; try again later.");
            return;
        }
        // the fingerprint shown is worked out from the key that would be added, not taken from the PC's word
        if (PairApproval.Fingerprint(key) is not { } fingerprint)
        {
            _ = PairApproval.DenyAsync(To(link), id, Me, "The request didn't carry a usable SSH key.");
            return;
        }

        var prompt = new PairPrompt(from, address, pcName, fingerprint, Environment.UserName, DateTime.UtcNow.AddSeconds(seconds));
        open[id] = (link, prompt);
        prompt.Answered += allowed => _ = AppLog.GuardAsync("Answering a pairing request",
            () => AnswerAsync(link, id, from, pcName, key, fingerprint, who, allowed));
        prompt.FormClosed += (_, _) => open.Remove(id);
        prompt.Show();   // no owner: an owned window hides with a minimised or hidden main window
    }

    async Task AnswerAsync(Link link, string id, string from, string pcName, string key, string fingerprint, string who, bool allowed)
    {
        if (!allowed)
        {
            deniedUntil[who] = DateTime.UtcNow.AddMinutes(5);   // a repeated request doesn't pop up again right away
            await PairApproval.DenyAsync(To(link), id, Me);
            return;
        }
        var own = ownLink() is { State: LinkState.Connected } o ? To(o) : null;
        var added = $" (allowed {DateTime.Now:yyyy-MM-dd})";   // the label the key gets in Settings → SSH keys (60 characters at most)
        var label = (from.Length > 60 - added.Length ? from[..(60 - added.Length)] : from) + added;
        var result = await PairApproval.AllowAsync(To(link), own, id, key, fingerprint, label, Me);
        var keyLine = $"Its fingerprint is {fingerprint}.";
        var problem = result.Outcome switch
        {
            PairOutcome.Allowed => null,
            PairOutcome.Gone => $"The request from {from} is no longer open: it expired or was cancelled there. Nothing was changed on this PC.\n\n" +
                                $"If you still want this, send the request again on {from}.",
            PairOutcome.Unreachable => $"Couldn't reach {pcName} about the request from {from}, so nothing was changed on this PC.",
            PairOutcome.NoServer => "SSH Manager's server isn't running on this PC, so the key could not be added.",
            PairOutcome.KeyNotAdded => $"The key of {from} was not added, and {from} was told so:\n\n{result.Detail}",
            PairOutcome.Late => $"The request from {from} expired or was cancelled just before your answer arrived, so its key was taken off this PC again. " +
                                "Nothing was changed.",
            PairOutcome.Unanswered => $"{from} could not be told that you allowed it ({pcName} didn't answer), so its key was taken off this PC again. " +
                                      $"Nothing was changed.\n\nIf you still want this, send the request again on {from}.",
            PairOutcome.KeyLeft => $"{from} could not be told that you allowed it, and its key could not be taken off this PC again:\n\n{result.Detail}\n\n" +
                                   $"The key is still on this PC: {from} can sign in with it. If you don't want that, remove it in this PC's Settings → SSH keys. {keyLine}",
            _ => $"Adding the key of {from} failed, and SSH Manager couldn't make sure that it wasn't added all the same (this PC's server may also still be adding it):\n\n{result.Detail}\n\n" +
                 $"{from} was told that it failed. In a minute, look in this PC's Settings → SSH keys: if the key is there, {from} can sign in with it; remove it if you don't want that. {keyLine}",
        };
        if (problem is null) balloon("SSH Manager", $"{from} can now manage this PC.");
        else
            MessageBox.Show(owner.Visible ? owner : null, problem, "SSH Manager", MessageBoxButtons.OK,
                result.Outcome is PairOutcome.Gone or PairOutcome.Late or PairOutcome.Unanswered ? MessageBoxIcon.Information
                : result.Outcome is PairOutcome.KeyLeft or PairOutcome.KeyUnsure ? MessageBoxIcon.Warning : MessageBoxIcon.Error);
    }
}

/// <summary>Allow / Deny for one computer that asks to manage this PC. Deny is the default; closing the
/// window denies too. It closes by itself, without an answer, when the request's time is up.</summary>
internal sealed class PairPrompt : Form
{
    /// <summary>Allow can be clicked this long after the prompt appeared.</summary>
    public static readonly TimeSpan AllowAfter = TimeSpan.FromMilliseconds(1500);

    public event Action<bool>? Answered;
    readonly System.Windows.Forms.Timer tick = new() { Interval = 500 };
    readonly System.Windows.Forms.Timer unlock = new() { Interval = (int)AllowAfter.TotalMilliseconds };
    readonly Button deny = new() { Text = "Deny", AutoSize = true, MinimumSize = new Size(88, 30) };
    bool answered;

    /// <summary>Shown without becoming the active window: what the user is typing goes on going where it went.</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= 0x8;   // WS_EX_TOPMOST: above the other windows (the TopMost property would also activate it)
            return p;
        }
    }

    public PairPrompt(string from, string address, string pc, string fingerprint, string user, DateTime deadlineUtc)
    {
        Text = "SSH Manager: allow access?";
        Icon = AppIcon.App;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        Font = new Font("Segoe UI", 9.5f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        // UseMnemonic off: an "&" in a name a PC supplied is shown as it is
        Label Line(string text, bool bold = false, int below = 10) => new()
        {
            AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 0, 0, below), UseMnemonic = false,
            Font = bold ? new Font(Font, FontStyle.Bold) : Font, Text = text,
        };
        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(Line($"{from} asks to be allowed to manage this PC.", bold: true));
        layout.Controls.Add(Line($"If you allow it, {from} can sign in to this PC as {user} over SSH, with the same rights as you: " +
                                 "open and change files, run commands and view the screen."));
        layout.Controls.Add(Line($"Requested by: {from}\nIts address: {(address != "" ? address : "unknown")}\n" +
                                 $"Through the connection to: {pc}\nKey fingerprint: {fingerprint}"));
        layout.Controls.Add(Line($"Allow only if you started this yourself on {from} just now. You can take it back at any time in " +
                                 "Settings → SSH keys.", below: 0));
        var countdown = new Label { AutoSize = true, Margin = new Padding(0, 10, 0, 0), ForeColor = SystemColors.GrayText };
        layout.Controls.Add(countdown);
        var allow = new Button { Text = "Allow", AutoSize = true, MinimumSize = new Size(88, 30), Enabled = false };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(deny);
        buttons.Controls.Add(allow);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = deny;
        CancelButton = deny;
        ActiveControl = deny;   // where the keyboard is once the user turns to this window

        // counted from the server's expiry, so a busy window or a sleeping PC doesn't stretch it
        int Left() => (int)Math.Ceiling((deadlineUtc - DateTime.UtcNow).TotalSeconds);
        void ShowLeft() => countdown.Text = $"The request expires in {Math.Max(0, Left())} s. Nothing changes unless you click Allow.";
        ShowLeft();
        tick.Tick += (_, _) =>
        {
            if (Left() <= 0) CloseUnanswered();
            else ShowLeft();
        };
        allow.Click += (_, _) =>
        {
            if (Left() > 0) Answer(true);
            else CloseUnanswered();
        };
        deny.Click += (_, _) => Answer(false);
        unlock.Tick += (_, _) =>
        {
            unlock.Stop();
            allow.Enabled = true;
        };
        Shown += (_, _) => { tick.Start(); unlock.Start(); };
        // closing the window with its X is a No
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) Answer(false, close: false); };
        FormClosed += (_, _) => { tick.Dispose(); unlock.Dispose(); };
    }

    void Answer(bool allowed, bool close = true)
    {
        if (!answered)
        {
            answered = true;
            Answered?.Invoke(allowed);
        }
        if (close) Close();
    }

    /// <summary>The request is no longer open (expired, or cancelled on the other computer): close, answering nothing.</summary>
    public void CloseUnanswered()
    {
        answered = true;
        Close();
    }
}
