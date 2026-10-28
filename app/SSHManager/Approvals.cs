using System.Net.Http.Json;
using System.Text.Json;

namespace SSHManager;

/// <summary>
/// Approvals for AI agents: a quick action marked "ask first" that an agent starts waits on that PC's
/// server until someone allows it in an SSH Manager app (on that PC, or on the computer the agent works
/// from). Each app asks every PC it is connected to for approvals meant for its computer and shows a
/// prompt; the prompt closes by itself when it is answered elsewhere or expires. See sshmgr/agents.py.
/// Answers carry the app secret (only the app itself can answer, not an agent holding the token).
/// </summary>
internal sealed class Approvals(Form owner, AppSettings settings, Action<string, string> balloon)
{
    /// <summary>At most this many prompts at once; more wait for the next check.</summary>
    const int MaxOpen = 3;
    /// <summary>The server sends at most this much of the script.</summary>
    const int ServerScriptLimit = 2000;

    readonly Dictionary<string, (Link Link, ApprovalPrompt Prompt)> open = [];
    readonly HashSet<string> done = [];

    public async Task CheckAsync(Link link)
    {
        using var r = await Link.Http.SendAsync(link.Request(HttpMethod.Get,
            $"/api/approvals/pending?machine={Uri.EscapeDataString(Environment.MachineName)}&machine_id={Uri.EscapeDataString(settings.MachineId)}"));
        if (!r.IsSuccessStatusCode) return;   // an older server without approvals
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("approvals", out var list) || list.ValueKind != JsonValueKind.Array) return;
        var pending = new HashSet<string>();
        foreach (var q in list.EnumerateArray())
        {
            if (q.ValueKind != JsonValueKind.Object) continue;
            string S(string n) => q.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var id = S("id");
            if (id == "") continue;
            pending.Add(id);
            if (open.ContainsKey(id) || done.Contains(id) || open.Count >= MaxOpen) continue;
            var expires = q.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 60;
            if (expires <= 0) continue;
            var script = S("script");
            var shortened = (q.TryGetProperty("script_truncated", out var t) && t.ValueKind == JsonValueKind.True) || script.Length >= ServerScriptLimit;
            var pcName = S("pc") is { Length: > 0 } pc ? pc : link.Pc.DisplayName;
            var prompt = new ApprovalPrompt(S("agent"), S("title"), pcName, script, shortened, DateTime.UtcNow.AddSeconds(expires));
            open[id] = (link, prompt);
            prompt.Answered += allowed => _ = AnswerAsync(link, id, allowed, S("title"), pcName);
            prompt.FormClosed += (_, _) => { open.Remove(id); done.Add(id); };
            prompt.Show(owner.Visible ? owner : null);
            prompt.Activate();
            balloon("AI agent needs approval", $"{S("agent")} wants to run \"{S("title")}\" on {link.Pc.DisplayName}.");
        }
        // answered on another computer, or expired: close the prompt
        foreach (var (id, entry) in open.Where(o => o.Value.Link == link && !pending.Contains(o.Key)).ToList())
            entry.Prompt.Close();
    }

    async Task AnswerAsync(Link link, string id, bool allowed, string title, string pc)
    {
        string? problem = null;
        try
        {
            using var r = await Link.Http.SendAsync(link.AppRequest(HttpMethod.Post, $"/api/approvals/{Uri.EscapeDataString(id)}/answer",
                JsonContent.Create(new { machine = Environment.MachineName, machine_id = settings.MachineId, allowed })));
            if (!r.IsSuccessStatusCode)
                problem = Pairing.Detail(await r.Content.ReadAsStringAsync()) ?? $"{pc} answered {(int)r.StatusCode}.";
        }
        catch (Exception e) { problem = e.Message; }
        if (problem is not null && allowed)
            balloon("Approval not delivered", $"Your answer for \"{title}\" on {pc} didn't arrive (it may have expired or been answered elsewhere): {problem}");
    }
}

/// <summary>Allow / Deny for one action an AI agent wants to run. Deny is the default; it closes (denied)
/// when the time is up.</summary>
internal sealed class ApprovalPrompt : Form
{
    public event Action<bool>? Answered;
    readonly System.Windows.Forms.Timer tick = new() { Interval = 500 };
    readonly Button deny = new() { Text = "Deny", AutoSize = true, MinimumSize = new Size(88, 30), DialogResult = DialogResult.No };

    public ApprovalPrompt(string agent, string title, string pc, string script, bool shortened, DateTime deadlineUtc)
    {
        Text = "SSH Manager: allow this action?";
        Icon = AppIcon.App;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        Font = new Font("Segoe UI", 9.5f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 0, 0, 10),
            Font = new Font(Font, FontStyle.Bold),
            Text = $"{(agent != "" ? agent : "An AI agent")} wants to run the quick action \"{title}\" on {pc}.",
        });
        layout.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 0, 0, 6),
            Text = "This action is marked \"ask first\". Allow it only if you expect it. Its script" +
                   (shortened ? " (shortened: only the beginning is shown):" : ":"),
        });
        layout.Controls.Add(new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = false, Width = 520, Height = 120,
            Font = new Font("Cascadia Mono", 9f),
            Text = script.Replace("\r\n", "\n").Replace("\n", "\r\n") + (shortened ? "\r\n… (shortened)" : ""), TabStop = false,
        });
        var countdown = new Label { AutoSize = true, Margin = new Padding(0, 10, 0, 0), ForeColor = SystemColors.GrayText };
        layout.Controls.Add(countdown);
        var allow = new Button { Text = "Allow", AutoSize = true, MinimumSize = new Size(88, 30), DialogResult = DialogResult.Yes };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(deny);
        buttons.Controls.Add(allow);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = deny;
        CancelButton = deny;

        // counted from the server's expiry, so a busy window or a sleeping PC doesn't stretch it
        int Left() => (int)Math.Ceiling((deadlineUtc - DateTime.UtcNow).TotalSeconds);
        void ShowLeft() => countdown.Text = $"Not run unless allowed within {Math.Max(0, Left())} s.";
        ShowLeft();
        tick.Tick += (_, _) =>
        {
            if (Left() <= 0) Close();
            else ShowLeft();
        };
        var answered = false;
        allow.Click += (_, _) => { if (!answered) { answered = true; Answered?.Invoke(true); } Close(); };
        deny.Click += (_, _) => { if (!answered) { answered = true; Answered?.Invoke(false); } Close(); };
        Shown += (_, _) => { deny.Focus(); tick.Start(); };
        FormClosed += (_, _) => tick.Dispose();
    }
}
