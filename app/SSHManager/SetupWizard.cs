using System.Net;

namespace SSHManager;

/// <summary>
/// Adds a PC (or changes how one is reached): pick it (typed, found on the network, or a host from
/// ~/.ssh/config) and make sure this computer can log in to it with a key. A key that already works
/// (the app's own, or one of the user's in ~/.ssh) is used as it is. Otherwise the user allows this
/// computer once, in one of three ways: approve the request on the other PC (when its app is connected
/// to this PC), sign in with a Windows password (typed on the page, never stored), or copy the public
/// key over by hand. Finally it checks that SSH Manager is installed there. Runs as a page in the app
/// window; talks to MainForm through page messages.
/// </summary>
internal sealed class SetupWizard(Action<string> show, Action<PcEntry?> done, Func<string, SetupWizard.Peer?> findPeer, Pairing pairing)
{
    /// <summary>A computer whose app is connected to this PC's server: its name, Windows user, and whether
    /// its app can approve a pairing request.</summary>
    public sealed record Peer(string Machine, string User, bool CanApprove);

    PcEntry? editing;
    string host = "", user = Environment.UserName, alias = "", error = "", step = "pick", working = "", pairId = "";
    bool busy, canCancel, isOpen;
    List<Discovery.Found>? found;

    public void Open(PcEntry? edit, bool canCancel)
    {
        editing = edit;
        this.canCancel = canCancel;
        isOpen = true;
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
        if (busy && type is not ("setup-cancel" or "setup-approve-cancel")) return;
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
                if ((findPeer(host) ?? findPeer(field("address"))) is { User: not "" } p) user = p.User;
                alias = "";
                step = "pick";
                Render();
                break;
            case "setup-install-key":
                user = field("user").Trim();
                await InstallKeyAsync(field("password"));
                break;
            case "setup-approve":
                await ApproveAsync();
                break;
            case "setup-approve-cancel":
                if (pairId != "") await pairing.CancelAsync(pairId);
                pairId = "";
                break;
            case "setup-copy-key":
                try { Clipboard.SetText(File.ReadAllText(Ssh.AppKey + ".pub").Trim()); } catch (Exception) { }
                break;
            case "setup-retry":
                await ConnectAsync();
                break;
            case "setup-back":
                step = "pick";
                error = "";
                Render();
                break;
            case "setup-cancel":
                if (pairId != "") await pairing.CancelAsync(pairId);
                pairId = "";
                busy = false;
                isOpen = false;
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

    static bool IsThisPc(string h) =>
        h.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) || h is "localhost" or "127.0.0.1" or "::1";

    async Task ConnectAsync()
    {
        if (alias == "" && host == "")
        {
            error = "Enter the name or IP address of the PC.";
            Render();
            return;
        }
        if (alias == "" && IsThisPc(host))
        {
            error = "That is the PC this app runs on. It is already in the list when SSH Manager's server is installed here.";
            step = "pick";
            Render();
            return;
        }
        if (alias == "" && user == Environment.UserName && findPeer(host) is { User: not "" } peer) user = peer.User;
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
            if (!output.Contains("SSHMGR-OK") && alias == "" && Denied(output))
            {
                // maybe one of the user's own keys (~/.ssh, ssh config) is already allowed there
                var own = new PcEntry { Id = c.Id, Host = host, User = user };
                var (_, output2) = await Ssh.RunAsync("ssh.exe", [.. Ssh.Options(own), own.Target, "echo SSHMGR-OK"], 20000);
                if (output2.Contains("SSHMGR-OK")) (c, output) = (own, output2);
            }
            if (!output.Contains("SSHMGR-OK"))
            {
                if (alias == "" && Denied(output))
                {
                    step = "key";   // reachable, but this computer isn't allowed yet
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

    static bool Denied(string output) => output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase);

    /// <summary>Asks the other PC's app to allow this computer's key; waits for the click there.</summary>
    async Task ApproveAsync()
    {
        if (findPeer(host) is not { CanApprove: true } peer || !pairing.Available)
        {
            error = $"{host} can't be asked right now: its SSH Manager app has to be running and connected to this PC.";
            Render();
            return;
        }
        busy = true;
        error = "";
        try
        {
            pairId = await pairing.RequestAsync(peer.Machine, File.ReadAllText(Ssh.AppKey + ".pub").Trim());
            var id = pairId;
            step = "approve-wait";
            Render();
            while (pairId == id)
            {
                await Task.Delay(1500);
                if (pairId != id) break;   // cancelled
                var s = await pairing.StatusAsync(id);
                if (s.State == "pending") continue;
                pairId = "";
                if (s.State == "allowed")
                {
                    if (s.User != "") user = s.User;
                    busy = false;
                    await ConnectAsync();
                    return;
                }
                error = s.State == "denied"
                    ? (s.Error != "" ? $"{host} could not allow this computer: {s.Error}" : $"The request was denied on {host}.")
                    : $"Nobody answered on {host} within 2 minutes. Send a new request and click Allow on {host}.";
                break;
            }
        }
        catch (Exception e) { error = e.Message; pairId = ""; }
        finally { busy = false; }
        step = "key";
        Render();
    }

    /// <summary>Logs in once with the Windows password typed on the page (ssh gets it from this app through
    /// SSH_ASKPASS, it is not stored) and adds this computer's public key where sshd looks for it.</summary>
    async Task InstallKeyAsync(string password)
    {
        if (user == "" || password == "")
        {
            error = "Enter the user name and the password.";
            Render();
            return;
        }
        busy = true;
        error = "";
        step = "working";
        working = $"Signing in to {host} as {user}…";
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
                'SSHMGR-KEY-ADDED'
                """;
            var c = new PcEntry { Host = host, User = user };
            var env = new Dictionary<string, string>
            {
                ["SSH_ASKPASS"] = Environment.ProcessPath ?? Application.ExecutablePath,
                ["SSH_ASKPASS_REQUIRE"] = "force",
                ["SSHMGR_ASKPASS"] = password,
            };
            string[] once = ["-o", "NumberOfPasswordPrompts=1", "-o", "PubkeyAuthentication=no",
                             "-o", "PreferredAuthentications=password,keyboard-interactive"];
            var (_, output) = await Ssh.RunAsync("ssh.exe", [.. Ssh.Options(c, batch: false), .. once, c.Target, Ssh.PowerShell(script)], 30000, env);
            if (!output.Contains("SSHMGR-KEY-ADDED"))
            {
                step = "key";
                error = Denied(output)
                    ? $"{host} did not accept the user name \"{user}\" with that password."
                    : (Ssh.Explain(output, host) is { Length: > 0 } why ? why + "\n\n" : "") + output.Trim();
                Render();
                return;
            }
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
            throw new InvalidOperationException($"This computer can sign in to {c.Host}, but SSH Manager's server isn't installed there. " +
                $"Install SSH Manager on {c.Host}, choose \"Client and server\", then click Add PC again.");
        isOpen = false;
        done(c);
    }

    void Render()
    {
        if (!isOpen) return;   // closed while something was still running
        var aliases = Ssh.ConfigHosts();
        string Esc(string s) => WebUtility.HtmlEncode(s);
        var title = editing is null ? "Add a PC" : $"Change how to reach {Esc(editing.DisplayName)}";
        var err = error != "" ? $"<div class='err'>{Esc(error)}</div>" : "";
        var foundHtml = found is null ? "" : found.Count == 0
            ? "<div class='found'><p class='small'>No PCs accepting SSH were found on your network.</p></div>"
            : "<div class='found'>" + string.Join("", found.Select(f =>
                $"<button class='pick' onclick=\"send('setup-pick', {{host: '{Esc(f.Name != "" ? f.Name : f.Address)}', address: '{Esc(f.Address)}'}})\"><b>{Esc(f.Name != "" ? f.Name : f.Address)}</b> <span>{Esc(f.Address)}</span></button>")) + "</div>";
        var peer = step is "key" or "approve-wait" ? findPeer(host) : null;
        var canApprove = peer is { CanApprove: true } && pairing.Available;
        var pub = File.Exists(Ssh.AppKey + ".pub") ? File.ReadAllText(Ssh.AppKey + ".pub").Trim() : "";
        var body = step switch
        {
            "working" => $"""<div class="center"><span class="spin"></span>{Esc(working)}</div>""",
            "approve-wait" => $"""
                <h2>Waiting for {Esc(host)}</h2>
                <div class="center"><span class="spin"></span>Click <b>Allow</b> in SSH Manager on {Esc(host)}.</div>
                <p class="small" style="text-align:center">The request expires in 2 minutes.</p>
                <div class="buttons"><button onclick="send('setup-approve-cancel')">Cancel</button></div>
                """,
            "key" => $$"""
                <h2>Allow this computer on {{Esc(host)}}</h2>
                <p>{{Esc(host)}} is reachable but does not accept this computer yet. Allow it once in one of these ways;
                after that it signs in with its key.</p>
                {{err}}
                {{(canApprove ? $$"""
                <div class="opt rec">
                  <div class="opt-head"><b>Approve on {{Esc(host)}}</b><span class="tag">Recommended</span></div>
                  <p>SSH Manager on {{Esc(host)}} is connected to this PC. Send a request, then click <b>Allow</b> on {{Esc(host)}}.
                  No password is needed.</p>
                  <div class="buttons"><button class="primary" onclick="send('setup-approve')">Send request</button></div>
                </div>
                """ : "")}}
                <div class="opt">
                  <div class="opt-head"><b>Sign in with a password</b></div>
                  <div class="two">
                    <label>Windows user name on {{Esc(host)}} <input id="user" value="{{Esc(user)}}" spellcheck="false"></label>
                    <label>Password <input id="password" type="password" {{(canApprove ? "" : "autofocus")}}></label>
                  </div>
                  <p class="small">The user name is the folder name under <code>C:\Users</code> on that PC. For a Microsoft
                  account, use the account password, not the PIN. The password is used once and not stored.</p>
                  <div class="buttons"><button id="pw-btn" class="{{(canApprove ? "" : "primary")}}"
                    onclick="send('setup-install-key', {user: v('user'), password: v('password')})">Sign in</button></div>
                </div>
                <details class="opt">
                  <summary>Copy this computer's key instead</summary>
                  <p class="small">For PCs that only allow PIN or Windows Hello sign-in. On {{Esc(host)}}, open SSH Manager →
                  Settings (⚙) → SSH keys → <b>Allow a new key</b>, paste the key and click <b>Allow key</b>. Then click
                  <b>Try again</b> here. This is a public key, so it is safe to send by email or chat.</p>
                  <textarea readonly rows="3">{{Esc(pub)}}</textarea>
                  <div class="buttons"><button onclick="send('setup-copy-key'); this.textContent='Copied'">Copy key</button>
                  <button onclick="send('setup-retry')">Try again</button></div>
                </details>
                <div class="buttons"><button onclick="send('setup-back')">Back</button></div>
                """,
            _ => $$"""
                <h2>{{title}}</h2>
                <p>SSH Manager connects to Windows PCs over SSH. Install SSH Manager on that PC first and choose
                <b>Client and server</b>; then enter it here.</p>
                {{err}}
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
            .box { width:min(560px, 100%); padding:20px 22px; background:var(--panel); border:1px solid var(--line); border-radius:10px; }
            h2 { font-size:15px; margin:0 0 6px; } p { color:var(--muted); margin:0 0 12px; font-size:12.5px; } .small { font-size:11.5px; margin-top:6px; }
            label { display:flex; flex-direction:column; gap:4px; margin-bottom:10px; color:var(--muted); font-size:12px; }
            .row { display:flex; gap:6px; } .row input { flex:1; }
            details { margin:2px 0 10px; } summary { cursor:pointer; color:var(--accent); margin-bottom:6px; font-size:12.5px; }
            .buttons { display:flex; justify-content:flex-end; gap:8px; margin-top:6px; }
            .err { white-space:pre-wrap; background:color-mix(in srgb, var(--bad) 10%, var(--panel)); border:1px solid color-mix(in srgb, var(--bad) 45%, transparent);
                   border-radius:6px; padding:8px 10px; margin-bottom:12px; font-size:12px; max-height:200px; overflow:auto; }
            .center { display:flex; align-items:center; gap:10px; justify-content:center; padding:24px 0 8px; }
            .found { display:flex; flex-wrap:wrap; gap:6px; margin:-2px 0 10px; }
            .pick { font-size:12px; height:26px; padding:2px 9px; } .pick span { color:var(--muted); margin-left:4px; }
            .opt { border:1px solid var(--line); border-radius:8px; padding:11px 13px; margin-bottom:10px; background:var(--panel-2); }
            .opt.rec { border-color:color-mix(in srgb, var(--accent) 45%, transparent); }
            .opt p { margin:4px 0 8px; } .opt .buttons { margin-top:0; }
            .opt summary { margin-bottom:0; } details.opt[open] summary { margin-bottom:6px; }
            .opt-head { display:flex; align-items:center; gap:8px; font-size:13px; }
            .tag { margin-left:auto; font-size:10.5px; padding:0 6px; line-height:17px; border-radius:4px; color:var(--accent);
                   border:1px solid color-mix(in srgb, var(--accent) 45%, transparent); }
            .two { display:grid; grid-template-columns:1fr 1fr; gap:10px; margin-top:8px; } .two label { margin-bottom:0; }
            textarea { width:100%; font:11.5px var(--mono); color:var(--text); background:var(--bg); border:1px solid var(--line-2);
                       border-radius:6px; padding:7px 9px; resize:none; margin-bottom:8px; }
            """;
        const string script = """
            const v = id => (document.getElementById(id) || {}).value || "";
            function send(type, data) { chrome.webview.postMessage({ type, ...(data || {}) }); }
            document.addEventListener("keydown", e => {
              if (e.key !== "Enter") return;
              if (e.target.id === "password" || (e.target.id === "user" && document.getElementById("pw-btn"))) document.getElementById("pw-btn").click();
              else if (e.target.id === "host") document.querySelector(".primary").click();
            });
            """;
        show(Ui.Shell(editing is null ? "Add a PC" : editing.DisplayName, css, $"<div class=\"box\">{body}</div>", script, center: true));
    }
}
