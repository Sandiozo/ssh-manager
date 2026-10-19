using System.Net.Http.Json;
using System.Text.Json;

namespace SSHManager;

/// <summary>
/// Approvals for AI agents: a quick action marked "ask first" that an agent starts waits on that PC's
/// server until someone allows it in an SSH Manager app (on that PC, or on the computer the agent works
/// from). Each app asks every PC it is connected to for approvals meant for its computer and shows a
/// prompt; the prompt closes by itself when it is answered elsewhere or expires. See sshmgr/agents.py.
/// </summary>
internal sealed class Approvals(Form owner, Action<string, string> balloon)
{
    readonly Dictionary<string, (Link Link, ApprovalPrompt Prompt)> open = [];
    readonly HashSet<string> done = [];

    public async Task CheckAsync(Link link)
    {
        using var r = await Link.Http.SendAsync(link.Request(HttpMethod.Get,
            "/api/approvals/pending?machine=" + Uri.EscapeDataString(Environment.MachineName)));
        if (!r.IsSuccessStatusCode) return;   // an older server without approvals
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var pending = new HashSet<string>();
        foreach (var q in doc.RootElement.GetProperty("approvals").EnumerateArray())
        {
            string S(string n) => q.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var id = S("id");
            if (id == "") continue;
            pending.Add(id);
            if (open.ContainsKey(id) || done.Contains(id)) continue;
            var expires = q.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 60;
            var prompt = new ApprovalPrompt(S("agent"), S("title"), S("pc") is { Length: > 0 } pc ? pc : link.Pc.DisplayName, S("script"), expires);
            open[id] = (link, prompt);
            prompt.Answered += allowed => _ = AnswerAsync(link, id, allowed);
            prompt.FormClosed += (_, _) => { open.Remove(id); done.Add(id); };
            prompt.Show(owner.Visible ? owner : null);
            prompt.Activate();
            balloon("AI agent needs approval", $"{S("agent")} wants to run \"{S("title")}\" on {link.Pc.DisplayName}.");
        }
        // answered on another computer, or expired: close the prompt
        foreach (var (id, entry) in open.Where(o => o.Value.Link == link && !pending.Contains(o.Key)).ToList())
            entry.Prompt.Close();
    }

    static async Task AnswerAsync(Link link, string id, bool allowed)
    {
        try
        {
            using (await Link.Http.SendAsync(link.Request(HttpMethod.Post, $"/api/approvals/{Uri.EscapeDataString(id)}/answer",
                JsonContent.Create(new { machine = Environment.MachineName, allowed })))) { }
        }
        catch (Exception) { }
    }
}

/// <summary>Allow / Deny for one action an AI agent wants to run. Deny is the default; it closes (denied)
/// when the time is up.</summary>
internal sealed class ApprovalPrompt : Form
{
    public event Action<bool>? Answered;
    readonly System.Windows.Forms.Timer tick = new() { Interval = 1000 };
    readonly Button deny = new() { Text = "Deny", AutoSize = true, MinimumSize = new Size(88, 30), DialogResult = DialogResult.No };
    int left;

    public ApprovalPrompt(string agent, string title, string pc, string script, int expiresIn)
    {
        left = Math.Max(1, expiresIn);
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
            Text = "This action is marked \"ask first\". Allow it only if you expect it. Its script:",
        });
        layout.Controls.Add(new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = false, Width = 520, Height = 120,
            Font = new Font("Cascadia Mono", 9f), Text = script.Replace("\r\n", "\n").Replace("\n", "\r\n"), TabStop = false,
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

        void ShowLeft() => countdown.Text = $"Not run unless allowed within {left} s.";
        ShowLeft();
        tick.Tick += (_, _) =>
        {
            if (--left <= 0) Close();
            else ShowLeft();
        };
        allow.Click += (_, _) => { Answered?.Invoke(true); Close(); };
        deny.Click += (_, _) => { Answered?.Invoke(false); Close(); };
        Shown += (_, _) => { deny.Focus(); tick.Start(); };
        FormClosed += (_, _) => tick.Dispose();
    }
}
