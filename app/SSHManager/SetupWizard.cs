using System.Diagnostics;
using System.Net;

namespace SSHManager;

/// <summary>
/// Adds a PC (or changes how one is reached): pick it (typed, found on the network, or a host from
/// ~/.ssh/config), make sure this computer has an SSH key the PC accepts (creating one and installing it
/// with a one-time password prompt if needed), and check that SSH Manager is installed there. Runs as a
/// page in the app window; talks to MainForm through page messages.
/// </summary>
internal sealed class SetupWizard(Action<string> show, Action<PcEntry?> done)
{
    PcEntry? editing;
    string host = "", user = Environment.UserName, alias = "", error = "", step = "pick", working = "";
    bool busy, canCancel;
    List<Discovery.Found>? found;

    public void Open(PcEntry? edit, bool canCancel)
    {
        editing = edit;
        this.canCancel = canCancel;
        alias = edit is not null && Ssh.ConfigHosts().Contains(edit.Host) ? edit.Host : "";
        host = alias == "" ? edit?.Host ?? "" : "";
        user = edit is { User: not "" } ? edit.User : Environment.UserName;
        step = "pick";
        error = "";
        found = null;
        Render();
    }

    public async void OnMessage(string type, Func<string, string> field)
    {
        if (busy && type != "setup-cancel") return;
        switch (type)
        {
            case "setup-connect":
                alias = field("alias");
                host = field("host").Trim();
                user = field("user").Trim();
                await ConnectAsync();
                break;
            case "setup-search":
                user = field("user").Trim();
                await SearchAsync();
                break;
            case "setup-pick":
                host = field("host");
                alias = "";
                step = "pick";
                Render();
                break;
            case "setup-install-key":
                await InstallKeyAsync();
                break;
            case "setup-back":
                step = "pick";
                error = "";
                Render();
                break;
            case "setup-cancel":
                busy = false;
                done(null);
                break;
        }
    }

    /// <summary>The PC this attempt would add (not saved until it works).</summary>
    PcEntry Candidate()
    {
        var pc = alias != ""
            ? new PcEntry { Host = alias, LookupName = editing?.Host == alias ? editing.LookupName : "" }
            : new PcEntry { Host = host, User = user, KeyFile = File.Exists(Ssh.AppKey) ? Ssh.AppKey : "" };
        if (editing is not null) pc.Id = editing.Id;
        return pc;
    }

    async Task SearchAsync()
    {
        busy = true;
        step = "working";
        working = "Searching your network for PCs that accept SSH…";
        Render();
        try { found = await Discovery.ScanAsync(); }
        catch (Exception e) { error = "Searching failed: " + e.Message; }
        finally { busy = false; }
        step = "pick";
        Render();
    }

    async Task ConnectAsync()
    {
        if (alias == "" && host == "")
        {
            error = "Enter the name or IP address of the PC.";
            Render();
            return;
        }
        busy = true;
        error = "";
        step = "working";
        working = "Checking the connection…";
        Render();
        try
        {
            if (alias == "" && !File.Exists(Ssh.AppKey))
            {
                working = "Creating a key for this computer…";
                Render();
                Directory.CreateDirectory(Ssh.SshFolder);
                var (kc, ko) = await Ssh.RunAsync("ssh-keygen.exe",
                    ["-q", "-t", "ed25519", "-N", "", "-C", $"ssh-manager@{Environment.MachineName}", "-f", Ssh.AppKey]);
                if (kc != 0) throw new InvalidOperationException("Couldn't create an SSH key:\n" + ko);
            }
            var c = Candidate();
            var (_, output) = await Ssh.RunAsync("ssh.exe", [.. Ssh.Options(c), c.Target, "echo SSHMGR-OK"], 20000);
            if (!output.Contains("SSHMGR-OK"))
            {
                if (alias == "" && output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
                {
                    step = "key";   // reachable, but our key isn't allowed yet
                    Render();
                    return;
                }
                var why = Ssh.Explain(output, c.Host);
                throw new InvalidOperationException((why != "" ? why : "Couldn't connect.") + "\n\n" + output.Trim());
            }
            await FinishAsync(c);
        }
        catch (Exception e)
        {
            step = "pick";
            error = e.Message;
            Render();
        }
        finally { busy = false; }
    }

    /// <summary>Opens a console window where the user types their Windows password for the PC once;
    /// the remote script adds this computer's public key where sshd looks for it.</summary>
    async Task InstallKeyAsync()
    {
        busy = true;
        step = "working";
        working = $"Waiting for the password window… (type {user}'s Windows password for {host} there)";
        Render();
        try
        {
            var pub = File.ReadAllText(Ssh.AppKey + ".pub").Trim().Replace("'", "''");
            var script = $$"""
                $key = '{{pub}}'
                $admin = (whoami /groups) -match 'S-1-5-32-544'
                if ($admin) { $f = "$env:ProgramData\ssh\administrators_authorized_keys" }
                else { New-Item -ItemType Directory -Force "$HOME\.ssh" | Out-Null; $f = "$HOME\.ssh\authorized_keys" }
                if (-not ((Test-Path $f) -and (Select-String -Path $f -SimpleMatch $key -Quiet))) { Add-Content -Path $f -Value $key -Encoding ascii }
                if ($admin) { icacls $f /inheritance:r /grant 'Administrators:F' /grant 'SYSTEM:F' | Out-Null }
                'Key added. This window closes in a moment.'
                Start-Sleep 2
                """;
            var c = new PcEntry { Host = host, User = user };
            var args = string.Join(" ", Ssh.Options(c, batch: false).Select(Quote)) + $" {Quote(c.Target)} {Ssh.PowerShell(script)}";
            var psi = new ProcessStartInfo("cmd.exe", $"/c title SSH Manager: log in to {host} & ssh.exe {args} || pause")
            { UseShellExecute = true };
            using (var p = Process.Start(psi)!) await p.WaitForExitAsync();
            busy = false;
            await ConnectAsync();
        }
        catch (Exception e)
        {
            step = "key";
            error = e.Message;
            Render();
        }
        finally { busy = false; }
    }

    async Task FinishAsync(PcEntry c)
    {
        working = "Checking SSH Manager on the PC…";
        Render();
        var check = Ssh.PowerShell("if (Test-Path (Join-Path $env:ProgramData 'SSHManager\\install-path.txt')) { 'SSHMGR-INSTALLED' } else { 'SSHMGR-MISSING' }");
        var (_, output) = await Ssh.RunAsync("ssh.exe", [.. Ssh.Options(c), c.Target, check], 20000);
        if (!output.Contains("SSHMGR-INSTALLED"))
            throw new InvalidOperationException($"This computer can log in to {c.Host}, but SSH Manager isn't set up there yet. " +
                $"Install SSH Manager on {c.Host} and choose \"Let other PCs manage this PC\", then press Add again.");
        done(c);
    }

    static string Quote(string a) => a.Contains(' ') ? $"\"{a}\"" : a;

    void Render()
    {
        var aliases = Ssh.ConfigHosts();
        string Esc(string s) => WebUtility.HtmlEncode(s);
        var title = editing is null ? "Add a PC" : $"Change how to reach {Esc(editing.DisplayName)}";
        var foundHtml = found is null ? "" : found.Count == 0
            ? "<div class='found'><p class='small'>No PCs accepting SSH were found on your network.</p></div>"
            : "<div class='found'>" + string.Join("", found.Select(f =>
                $"<button class='pick' onclick=\"send('setup-pick', {{host: '{Esc(f.Name != "" ? f.Name : f.Address)}'}})\"><b>{Esc(f.Name != "" ? f.Name : f.Address)}</b> <span>{Esc(f.Address)}</span></button>")) + "</div>";
        var body = step switch
        {
            "working" => $"""<div class="center"><span class="spin"></span>{Esc(working)}</div>""",
            "key" => $$"""
                <h2>Allow this computer on {{Esc(host)}}</h2>
                <p>{{Esc(host)}} answered, but doesn't know this computer yet. SSH Manager made a key for this computer and
                will add it to {{Esc(host)}} for you. A window opens where you type <b>{{Esc(user)}}</b>'s Windows password
                for {{Esc(host)}}. This is needed only once; after that it logs in with the key.</p>
                <p class="small">Sign in to Windows on that PC with a Microsoft account? Use that account's password. If the PC
                only allows PIN / Windows Hello sign-in, turn off "Only allow Windows Hello sign-in" there first
                (Settings → Accounts → Sign-in options).</p>
                {{(error != "" ? $"<div class='err'>{Esc(error)}</div>" : "")}}
                <div class="buttons"><button onclick="send('setup-back')">Back</button>
                <button class="primary" onclick="send('setup-install-key')">Open the password window</button></div>
                """,
            _ => $$"""
                <h2>{{title}}</h2>
                <p>SSH Manager connects to Windows PCs over SSH. Install SSH Manager on that PC first and choose
                <b>Let other PCs manage this PC</b>; then enter it here.</p>
                {{(error != "" ? $"<div class='err'>{Esc(error)}</div>" : "")}}
                <label>PC name or IP address
                  <span class="row"><input id="host" value="{{Esc(host)}}" placeholder="e.g. DESKTOP-1234 or 192.168.1.20" autofocus>
                  <button onclick="send('setup-search', {user: v('user')})">Search my network</button></span></label>
                {{foundHtml}}
                <label>Your Windows user name on that PC <input id="user" value="{{Esc(user)}}"></label>
                {{(aliases.Count > 0 ? $$"""
                <details {{(alias != "" ? "open" : "")}}><summary>Or use a host from your SSH config</summary>
                <select id="alias"><option value="">(none)</option>{{string.Join("", aliases.Select(a => $"<option {(a == alias ? "selected" : "")}>{Esc(a)}</option>"))}}</select>
                <p class="small">Uses the user name, key and address from <code>~/.ssh/config</code>.</p></details>
                """ : "<input type='hidden' id='alias' value=''>")}}
                <div class="buttons">
                  {{(canCancel ? "<button onclick=\"send('setup-cancel')\">Cancel</button>" : "")}}
                  <button class="primary" onclick="send('setup-connect', {host: v('host'), user: v('user'), alias: v('alias')})">{{(editing is null ? "Add PC" : "Save")}}</button>
                </div>
                """,
        };
        const string css = """
            .box { width:min(520px, 100%); padding:20px 22px; background:var(--panel); border:1px solid var(--line); border-radius:10px; }
            h2 { font-size:15px; margin:0 0 6px; } p { color:var(--muted); margin:0 0 12px; font-size:12.5px; } .small { font-size:11.5px; margin-top:6px; }
            label { display:flex; flex-direction:column; gap:4px; margin-bottom:10px; color:var(--muted); font-size:12px; }
            .row { display:flex; gap:6px; } .row input { flex:1; }
            details { margin:2px 0 10px; } summary { cursor:pointer; color:var(--accent); margin-bottom:6px; font-size:12.5px; }
            .buttons { display:flex; justify-content:flex-end; gap:8px; margin-top:6px; }
            .err { white-space:pre-wrap; background:color-mix(in srgb, var(--bad) 10%, var(--panel)); border:1px solid color-mix(in srgb, var(--bad) 45%, transparent);
                   border-radius:6px; padding:8px 10px; margin-bottom:12px; font-size:12px; max-height:200px; overflow:auto; }
            .center { display:flex; align-items:center; gap:10px; justify-content:center; padding:24px 0; }
            .found { display:flex; flex-wrap:wrap; gap:6px; margin:-2px 0 10px; }
            .pick { font-size:12px; height:26px; padding:2px 9px; } .pick span { color:var(--muted); margin-left:4px; }
            """;
        const string script = """
            const v = id => (document.getElementById(id) || {}).value || "";
            function send(type, data) { chrome.webview.postMessage({ type, ...(data || {}) }); }
            document.addEventListener("keydown", e => { if (e.key === "Enter" && e.target.id === "host") document.querySelector(".primary").click(); });
            """;
        show(Ui.Shell(editing is null ? "Add a PC" : editing.DisplayName, css, $"<div class=\"box\">{body}</div>", script, center: true));
    }
}
