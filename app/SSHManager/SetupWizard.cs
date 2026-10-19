using System.Diagnostics;
using System.Net;

namespace SSHManager;

/// <summary>
/// First-run setup (and "Change connection…"): pick the PC, make sure this computer has an SSH key the
/// PC accepts (creating one and installing it with a one-time password prompt if needed), and check
/// that SSH Manager is installed there. Runs as a page in the app window; talks to MainForm through
/// page messages.
/// </summary>
internal sealed class SetupWizard(AppSettings settings, Action<string> show, Action done)
{
    string host = "", user = Environment.UserName, alias = "", error = "", step = "pick";
    bool busy;

    public void Open()
    {
        alias = settings.IsConfigured && Ssh.ConfigHosts().Contains(settings.Host) ? settings.Host : "";
        host = alias == "" ? settings.Host : "";
        user = settings.User != "" ? settings.User : Environment.UserName;
        step = "pick";
        error = "";
        Render();
    }

    public async void OnMessage(string type, Func<string, string> field)
    {
        if (busy) return;
        switch (type)
        {
            case "setup-connect":
                alias = field("alias");
                host = field("host").Trim();
                user = field("user").Trim();
                await ConnectAsync();
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
                done();
                break;
        }
    }

    /// <summary>The settings this attempt would use (not saved until it works).</summary>
    AppSettings Candidate() => alias != ""
        ? new AppSettings { Host = alias, LookupName = alias == settings.Host ? settings.LookupName : "" }
        : new AppSettings { Host = host, User = user, KeyFile = File.Exists(Ssh.AppKey) ? Ssh.AppKey : "" };

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
        Render("Checking the connection…");
        try
        {
            if (alias == "" && !File.Exists(Ssh.AppKey))
            {
                Render("Creating a key for this computer…");
                Directory.CreateDirectory(Ssh.SshFolder);
                var (kc, ko) = await Ssh.RunAsync("ssh-keygen.exe",
                    ["-q", "-t", "ed25519", "-N", "", "-C", $"ssh-manager@{Environment.MachineName}", "-f", Ssh.AppKey]);
                if (kc != 0) throw new InvalidOperationException("Couldn't create an SSH key:\n" + ko);
            }
            var c = Candidate();
            var (code, output) = await Ssh.RunAsync("ssh.exe", [.. Ssh.Options(c), c.Target, "echo SSHMGR-OK"], 20000);
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
        Render($"Waiting for the password window… (type {user}'s Windows password for {host} there)");
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
            var c = new AppSettings { Host = host, User = user };
            var args = string.Join(" ", Ssh.Options(c, batch: false).Select(Quote)) + $" {Quote(c.Target)} {Ssh.PowerShell(script)}";
            var psi = new ProcessStartInfo("cmd.exe", $"/c title SSH Manager: log in to {host} & ssh.exe {args} || pause")
            { UseShellExecute = true };
            using (var p = Process.Start(psi)!) await p.WaitForExitAsync();
            Render("Checking the connection…");
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

    async Task FinishAsync(AppSettings c)
    {
        Render("Checking SSH Manager on the PC…");
        var check = Ssh.PowerShell("if (Test-Path (Join-Path $env:ProgramData 'SSHManager\\install-path.txt')) { 'SSHMGR-INSTALLED' } else { 'SSHMGR-MISSING' }");
        var (_, output) = await Ssh.RunAsync("ssh.exe", [.. Ssh.Options(c), c.Target, check], 20000);
        if (!output.Contains("SSHMGR-INSTALLED"))
            throw new InvalidOperationException($"This computer can log in to {c.Host}, but SSH Manager isn't installed there yet. Run Setup.cmd on {c.Host}, then press Connect again.");
        settings.Host = c.Host;
        settings.User = c.User;
        settings.KeyFile = c.KeyFile;
        settings.LookupName = c.LookupName;
        settings.PcName = "";
        settings.Save();
        done();
    }

    static string Quote(string a) => a.Contains(' ') ? $"\"{a}\"" : a;

    void Render(string? working = null)
    {
        var aliases = Ssh.ConfigHosts();
        string Esc(string s) => WebUtility.HtmlEncode(s);
        var body = step switch
        {
            "working" => $"""<div class="center"><span class="spin"></span>{Esc(working ?? "Working…")}</div>""",
            "key" => $$"""
                <h2>Allow this computer on {{Esc(host)}}</h2>
                <p>{{Esc(host)}} answered, but doesn't know this computer yet. SSH Manager made a key for this computer and
                will add it to {{Esc(host)}} for you. A window opens where you type <b>{{Esc(user)}}</b>'s Windows password
                for {{Esc(host)}}. This is needed only once; after that it logs in with the key.</p>
                {{(error != "" ? $"<div class='err'>{Esc(error)}</div>" : "")}}
                <div class="buttons"><button onclick="send('setup-back')">Back</button>
                <button class="primary" onclick="send('setup-install-key')">Open the password window</button></div>
                """,
            _ => $$"""
                <h2>Connect to a PC</h2>
                <p>SSH Manager connects to a Windows PC over SSH. Run <b>Setup.cmd</b> on that PC first (it installs the
                SSH server and SSH Manager), then enter it here.</p>
                {{(error != "" ? $"<div class='err'>{Esc(error)}</div>" : "")}}
                <label>PC name or IP address <input id="host" value="{{Esc(host)}}" placeholder="e.g. DESKTOP-1234 or 192.168.1.20" autofocus></label>
                <label>Your Windows user name on that PC <input id="user" value="{{Esc(user)}}"></label>
                {{(aliases.Count > 0 ? $$"""
                <details {{(alias != "" ? "open" : "")}}><summary>Or use a host from your SSH config</summary>
                <select id="alias"><option value="">(none)</option>{{string.Join("", aliases.Select(a => $"<option {(a == alias ? "selected" : "")}>{Esc(a)}</option>"))}}</select>
                <p class="small">Uses the user name, key and address from <code>~/.ssh/config</code>.</p></details>
                """ : "<input type='hidden' id='alias' value=''>")}}
                <div class="buttons">
                  {{(settings.IsConfigured ? "<button onclick=\"send('setup-cancel')\">Cancel</button>" : "")}}
                  <button class="primary" onclick="send('setup-connect', {host: v('host'), user: v('user'), alias: v('alias')})">Connect</button>
                </div>
                """,
        };
        show($$"""
            <!doctype html><html><head><meta charset="utf-8"><style>
              :root { color-scheme: dark; --bg:#0d1117; --panel:#151b23; --line:#273140; --text:#e6edf3; --muted:#8b98a8; }
              @media (prefers-color-scheme: light) { :root { color-scheme: light; --bg:#f5f7fa; --panel:#fff; --line:#dde3ea; --text:#17202b; --muted:#5f6b7a; } }
              body { margin:0; min-height:100vh; display:flex; align-items:center; justify-content:center; background:var(--bg); color:var(--text);
                     font:14px/1.5 "Segoe UI", system-ui, sans-serif; }
              .box { width:min(520px, 90vw); padding:28px 32px; background:var(--panel); border:1px solid var(--line); border-radius:12px; }
              .logo { display:inline-block; font:700 18px Consolas, monospace; background:#22c55e; color:#052e16; border-radius:8px; padding:6px 10px; }
              h2 { font-size:18px; margin:14px 0 6px; } p { color:var(--muted); margin:0 0 14px; } .small { font-size:12px; margin-top:6px; }
              label { display:flex; flex-direction:column; gap:4px; margin-bottom:12px; color:var(--muted); font-size:13px; }
              input, select { font:inherit; color:var(--text); background:var(--bg); border:1px solid var(--line); border-radius:7px; padding:7px 10px; }
              details { margin:4px 0 12px; } summary { cursor:pointer; color:#3b82f6; margin-bottom:8px; }
              .buttons { display:flex; justify-content:flex-end; gap:8px; margin-top:8px; }
              button { font:inherit; padding:7px 16px; border-radius:7px; border:1px solid var(--line); background:var(--bg); color:var(--text); cursor:pointer; }
              button.primary { background:#3b82f6; border-color:#3b82f6; color:#fff; }
              .err { white-space:pre-wrap; background:color-mix(in srgb, #ef4444 12%, var(--panel)); border:1px solid color-mix(in srgb, #ef4444 50%, transparent);
                     border-radius:8px; padding:10px 12px; margin-bottom:14px; font-size:13px; max-height:220px; overflow:auto; }
              .center { display:flex; align-items:center; gap:10px; justify-content:center; padding:30px 0; }
              .spin { width:18px; height:18px; border:2px solid var(--line); border-top-color:#22c55e; border-radius:50%; display:inline-block; animation:s .9s linear infinite; }
              @keyframes s { to { transform:rotate(360deg); } }
              code { font-family:Consolas, monospace; }
            </style></head><body><div class="box"><div class="logo">&gt;_</div>
            {{body}}
            </div><script>
              const v = id => (document.getElementById(id) || {}).value || "";
              function send(type, data) { chrome.webview.postMessage({ type, ...(data || {}) }); }
              document.addEventListener("keydown", e => { if (e.key === "Enter" && document.getElementById("host")) document.querySelector(".primary").click(); });
            </script></body></html>
            """);
    }
}
