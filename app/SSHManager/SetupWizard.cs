using System.Net;

namespace SSHManager;

/// <summary>
/// Adds a PC (or changes how one is reached): pick it (typed, found on the network, or a host from
/// ~/.ssh/config) and make sure this computer can log in to it with a key. A key that already works
/// (the app's own, or one of the user's in ~/.ssh) is used as it is. Otherwise the user allows this
/// computer once, in one of three ways: approve the request on the other PC (when its app is connected
/// to this PC), sign in with a Windows password (typed on the page, never stored), or copy the public
/// key over by hand. Finally it checks that SSH Manager is installed there and saves the host key the PC
/// showed as its identity (see <see cref="HostKeys"/>). Runs as a page in the app window; talks to MainForm
/// through page messages.
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

    // The host keys the PC is held to while it is being added or changed: the identity saved for the PC
    // being changed, else what ssh already has on record for that name. With neither, the first contact
    // records the key it sees. ssh checks them in a file of the wizard's own (the user's known_hosts is only
    // read), and they become the PC's saved identity when the wizard finishes.
    List<string> trusted = [];
    string newId = "", trustFor = "";
    /// <summary>The key was on record before this wizard met the PC, or the user confirmed it here.</summary>
    bool keyKnown;
    /// <summary>The user said Yes to another key than the one on record.</summary>
    bool keyReplaced;
    /// <summary>Counts opening and cancelling: an attempt still running from before then saves and shows nothing.</summary>
    int opened;
    string TrustFile => Path.Combine(HostKeys.Folder, $"adding-{opened}");
    string TrustName => HostKeys.Alias(editing?.Id ?? newId);

    public void Open(PcEntry? edit, bool canCancel)
    {
        opened++;
        busy = false;
        editing = edit;
        this.canCancel = canCancel;
        isOpen = true;
        alias = edit is not null && Ssh.ConfigHosts().Contains(edit.Host) ? edit.Host : "";
        host = alias == "" ? edit?.Host ?? "" : "";
        user = edit is { User: not "" } ? edit.User : Environment.UserName;
        step = "pick";
        error = "";
        found = null;
        newId = new PcEntry().Id;
        ForgetTrust();
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
                opened++;
                busy = false;
                isOpen = false;
                ForgetTrust();
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
        pc.Id = editing?.Id ?? newId;
        return pc;
    }

    /// <summary>Stops an attempt that was running when the wizard was cancelled or opened again.</summary>
    void StillOpen(int session)
    {
        if (session != opened) throw new OperationCanceledException();
    }

    /// <summary>Looks up which keys the PC is held to, once per PC tried.</summary>
    async Task PrepareTrustAsync(PcEntry c, int session)
    {
        var target = alias != "" ? "config:" + alias : host;
        if (trustFor.Equals(target, StringComparison.OrdinalIgnoreCase)) return;
        ForgetTrust();
        var keys = editing is null ? [] : HostKeys.Read(HostKeys.FileFor(editing.Id));
        if (keys.Count == 0) keys = await HostKeys.OnRecordAsync(c);
        StillOpen(session);
        trusted = keys;
        keyKnown = keys.Count > 0;
        trustFor = target;
    }

    void ForgetTrust()
    {
        trustFor = "";
        trusted = [];
        keyKnown = keyReplaced = false;
        try
        {
            foreach (var f in Directory.GetFiles(HostKeys.Folder, "adding-*")) File.Delete(f);
        }
        catch (Exception) { }
    }

    /// <summary>Runs ssh for the PC being added, held to the keys this wizard has for it. With none yet, the
    /// first contact records the key it sees. Only a host from the user's ssh config with nothing on record
    /// is left to the user's own ssh settings.</summary>
    async Task<string> SshAsync(int session, PcEntry c, IEnumerable<string> more, int timeoutMs = 20000, bool batch = true,
        IDictionary<string, string>? env = null)
    {
        StillOpen(session);
        HostTrust? trust = null;
        if (trusted.Count > 0 || alias == "")
        {
            // written anew each time: ssh must never find it missing or changed and take that for "no key yet"
            HostKeys.Write(TrustFile, TrustName, trusted);
            trust = new(TrustName, TrustFile, AcceptNew: trusted.Count == 0);
        }
        var (_, output) = await Ssh.RunAsync("ssh.exe", [.. Ssh.Options(c, trust, batch), .. more], timeoutMs, env);
        StillOpen(session);
        if (trust is { AcceptNew: true }) trusted = HostKeys.Read(trust.File);
        return output;
    }

    /// <summary>What went wrong in an ssh run, for the page.</summary>
    string Problem(string output, string name, string otherwise = "")
    {
        var why = Ssh.KeyRefused(output) && trusted.Count > 0
            ? $"{name} answered with a different identity (host key) than the one on record. Nothing was changed."
            : Ssh.Explain(output, name, ownConfig: true);
        if (why == "") why = otherwise;
        return (why != "" ? why + "\n\n" : "") + Ssh.Shown(output);
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
        if (alias != "" ? !Ssh.SafeName(alias) || !Ssh.ConfigHosts().Contains(alias) : !Ssh.SafeName(host))
        {
            error = "That name or address isn't valid: it can't start with \"-\" or contain spaces.";
            step = "pick";
            Render();
            return;
        }
        if (alias == "" && user != "" && !Ssh.SafeName(user, allowSpaces: true))
        {
            error = "That user name isn't valid.";
            step = "pick";
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
        var session = opened;
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
            StillOpen(session);
            var c = Candidate();
            await PrepareTrustAsync(c, session);
            var output = await SshAsync(session, c, ["--", c.Target, "echo SSHMGR-OK"]);
            if (trusted.Count > 0 && Ssh.KeyRefused(output))
            {
                // not the key on record: go on with the one it shows now only when the user says it is that PC
                var shown = await ShownKeysAsync(session, c);
                if (shown.Count == 0)
                    throw new InvalidOperationException($"{c.Host} answered with a different identity (host key) than the one on record, " +
                        "and the one it shows now couldn't be read. Nothing was changed.");
                if (!AskChangedKey(c, shown))
                    throw new InvalidOperationException($"{c.Host} answered with a different identity (host key) than the one on record, " +
                        "and the new one was not accepted. Nothing was changed.");
                trusted = shown;
                keyKnown = keyReplaced = true;
                output = await SshAsync(session, c, ["--", c.Target, "echo SSHMGR-OK"]);
            }
            if (!output.Contains("SSHMGR-OK") && alias == "" && Denied(output))
            {
                // maybe one of the user's own keys (~/.ssh, ssh config) is already allowed there
                var own = new PcEntry { Id = c.Id, Host = host, User = user };
                var output2 = await SshAsync(session, own, ["--", own.Target, "echo SSHMGR-OK"]);
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
                throw new InvalidOperationException(Problem(output, c.Host, "Couldn't connect."));
            }
            await FinishAsync(c, session);
        }
        catch (Exception e)
        {
            if (session != opened) return;   // cancelled or opened again meanwhile: that attempt shows nothing
            step = "pick";
            error = e.Message;
            Render();
        }
        finally
        {
            if (session == opened) busy = false;
        }
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
        var session = opened;
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
                if (session != opened) return;   // the wizard was opened again meanwhile
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
        catch (Exception e)
        {
            if (session != opened) return;
            error = e.Message;
            pairId = "";
        }
        finally
        {
            if (session == opened) busy = false;
        }
        if (session != opened) return;
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
        if (!Ssh.SafeName(user, allowSpaces: true) || !Ssh.SafeName(host))
        {
            error = "That user name or PC name isn't valid.";
            Render();
            return;
        }
        var session = opened;
        busy = true;   // no second click while the identity question is open
        bool confirmed;
        try { confirmed = await ConfirmHostKeyAsync(session); }
        catch (Exception e) { error = e.Message; confirmed = false; }
        if (session != opened) return;
        busy = false;
        if (!confirmed)
        {
            if (error == "") error = $"The password was not sent. Check {host}'s identity first, then try again.";
            step = "key";
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
                if ($admin) { icacls $f /inheritance:r /grant '*S-1-5-32-544:F' /grant '*S-1-5-18:F' | Out-Null }
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
            // held to the key that was on record or that the user just confirmed, like every other step
            var output = await SshAsync(session, c, [.. once, "--", c.Target, Ssh.PowerShell(script)], 30000, batch: false, env);
            if (!output.Contains("SSHMGR-KEY-ADDED"))
            {
                step = "key";
                error = Denied(output) ? $"{host} did not accept the user name \"{user}\" with that password."
                    : Ssh.KeyRefused(output) ? $"{host} answered with a different identity (host key) than a moment ago, so the password was not sent."
                    : Problem(output, host);
                Render();
                return;
            }
            busy = false;
            await ConnectAsync();
        }
        catch (Exception e)
        {
            if (session != opened) return;
            step = "key";
            error = e.Message;
            Render();
        }
        finally
        {
            if (session == opened) busy = false;
        }
    }

    const string ShowFingerprints = "Get-ChildItem $env:ProgramData\\ssh\\ssh_host_*_key.pub | ForEach-Object { ssh-keygen -lf $_ }";

    /// <summary>Before a password goes to a PC this wizard met for the first time (its key was accepted on
    /// that first contact), the user compares its host key fingerprint: a wrong one means another computer
    /// answers under that name and would receive the password.</summary>
    async Task<bool> ConfirmHostKeyAsync(int session)
    {
        error = "";
        var probe = new PcEntry { Host = host, User = user };
        await PrepareTrustAsync(probe, session);
        if (keyKnown) return true;
        // not met yet (e.g. the first try timed out): one contact without a password records the key
        if (trusted.Count == 0) await SshAsync(session, probe, ["-o", "PreferredAuthentications=none", "--", probe.Target, "exit"]);
        if (trusted.Count == 0)
        {
            error = $"Couldn't read {host}'s identity (host key), so the password was not sent.";
            return false;
        }
        var owner = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
        var answer = MessageBox.Show(owner,
            $"This is the first time this computer connects to {host}. Before your password is sent, make sure it is really that PC.\n\n" +
            $"{host} identifies itself with:\n{string.Join("\n", HostKeys.Fingerprints(trusted))}\n\n" +
            $"On {host}, run this in PowerShell to see its fingerprints:\n{ShowFingerprints}\n\n" +
            "Send the password only if one of them matches. (On your own home network this is usually fine.)",
            "SSH Manager: check the PC's identity", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            ForgetTrust();   // not trusted: asked again next time
            return false;
        }
        keyKnown = true;
        return true;
    }

    /// <summary>The key a PC shows right now: one contact that sends nothing, checked against nothing,
    /// recorded in an empty file of its own.</summary>
    async Task<List<string>> ShownKeysAsync(int session, PcEntry c)
    {
        StillOpen(session);
        var (file, name) = (TrustFile + ".seen", TrustName);
        HostKeys.Write(file, name, []);
        await Ssh.RunAsync("ssh.exe", [.. Ssh.Options(c, new HostTrust(name, file, AcceptNew: true)), "-o", "PreferredAuthentications=none", "--", c.Target, "exit"], 20000);
        StillOpen(session);
        var keys = HostKeys.Read(file);
        HostKeys.Write(file, name, []);
        return keys;
    }

    /// <summary>The PC answers with another host key than the one on record for it. Only when the user says
    /// Yes here does the new key replace it: the way through after a PC was reinstalled.</summary>
    bool AskChangedKey(PcEntry c, List<string> keys)
    {
        if (keys.Count == 0) return false;
        var who = editing is not null && editing.DisplayName != c.Host ? $"{editing.DisplayName} ({c.Host})" : c.Host;
        var owner = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
        return MessageBox.Show(owner,
            $"{who} does not identify itself the way it did before.\n\n" +
            "That is expected after Windows or OpenSSH Server was reinstalled on that PC, or when this address now belongs to " +
            "another PC that you want to use instead. If neither is the case, another computer may be answering in its place: " +
            "it would receive what SSH Manager sends to this PC.\n\n" +
            $"It now identifies itself with:\n{string.Join("\n", HostKeys.Fingerprints(keys))}\n\n" +
            $"On {c.Host}, run this in PowerShell to see its fingerprints:\n{ShowFingerprints}\n\n" +
            "Trust this identity from now on? Choose Yes only if one of them matches.",
            "SSH Manager: the PC's identity changed", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    async Task FinishAsync(PcEntry c, int session)
    {
        working = "Checking SSH Manager on the PC…";
        Render();
        var check = Ssh.PowerShell("if (Test-Path (Join-Path $env:ProgramData 'SSHManager\\install-path.txt')) { 'SSHMGR-INSTALLED' } else { 'SSHMGR-MISSING' }");
        var output = await SshAsync(session, c, ["--", c.Target, check]);
        if (!output.Contains("SSHMGR-INSTALLED"))
            throw new InvalidOperationException($"This computer can sign in to {c.Host}, but SSH Manager's server isn't installed there. " +
                $"Install SSH Manager on {c.Host}, choose \"Client and server\", then click Add PC again.");
        SaveIdentity(c);
        isOpen = false;
        ForgetTrust();
        done(c);
    }

    /// <summary>Saves the keys the PC was held to in this wizard as its identity: from now on the app only
    /// talks to a server that shows one of them, at whatever address the PC is found.</summary>
    void SaveIdentity(PcEntry c)
    {
        if (trusted.Count == 0)
        {
            // only a host from the ssh config that the user's own ssh settings checked, with no key on record
            // anywhere: its first connection records the key
            if (alias == "") throw new InvalidOperationException($"Couldn't save {c.Host}'s identity (host key) on this computer. Try again.");
            return;
        }
        // a key saved for this PC while the wizard was open (by its connection in the background), and it is
        // another one: only with the user's word
        var saved = HostKeys.Read(HostKeys.FileFor(c.Id));
        if (saved.Count > 0 && !keyReplaced && !saved.Intersect(trusted).Any() && !AskChangedKey(c, trusted))
            throw new InvalidOperationException($"{c.Host} shows a different identity (host key) than the one saved for it, " +
                "and the new one was not accepted. Nothing was changed.");
        HostKeys.Write(HostKeys.FileFor(c.Id), HostKeys.Alias(c.Id), trusted);
        c.KeySaved = true;
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
                $"<button class='pick' data-act='setup-pick' data-host='{Esc(f.Name != "" ? f.Name : f.Address)}' data-address='{Esc(f.Address)}'><b>{Esc(f.Name != "" ? f.Name : f.Address)}</b> <span>{Esc(f.Address)}</span></button>")) + "</div>";
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
                <div class="buttons"><button data-act="setup-approve-cancel">Cancel</button></div>
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
                  <div class="buttons"><button class="primary" data-act="setup-approve">Send request</button></div>
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
                  <div class="buttons"><button id="pw-btn" class="{{(canApprove ? "" : "primary")}}" data-act="setup-install-key" data-fields="user,password">Sign in</button></div>
                </div>
                <details class="opt">
                  <summary>Copy this computer's key instead</summary>
                  <p class="small">For PCs that only allow PIN or Windows Hello sign-in. On {{Esc(host)}}, open SSH Manager →
                  Settings (⚙) → SSH keys → <b>Allow a new key</b>, paste the key and click <b>Allow key</b>. Then click
                  <b>Try again</b> here. This is a public key, so it is safe to send by email or chat.</p>
                  <textarea readonly rows="3">{{Esc(pub)}}</textarea>
                  <div class="buttons"><button data-act="setup-copy-key" data-after="Copied">Copy key</button>
                  <button data-act="setup-retry">Try again</button></div>
                </details>
                <div class="buttons"><button data-act="setup-back">Back</button></div>
                """,
            _ => $$"""
                <h2>{{title}}</h2>
                <p>SSH Manager connects to Windows PCs over SSH. Install SSH Manager on that PC first and choose
                <b>Client and server</b>; then enter it here.</p>
                {{err}}
                <label>PC name or IP address
                  <span class="row"><input id="host" value="{{Esc(host)}}" placeholder="e.g. DESKTOP-1234 or 192.168.1.20" autofocus>
                  <button data-act="setup-search" data-fields="user">Search my network</button></span></label>
                {{foundHtml}}
                <label>Your Windows user name on that PC <input id="user" value="{{Esc(user)}}"></label>
                {{(aliases.Count > 0 ? $$"""
                <details {{(alias != "" ? "open" : "")}}><summary>Or use a host from your SSH config</summary>
                <select id="alias"><option value="">(none)</option>{{string.Join("", aliases.Select(a => $"<option {(a == alias ? "selected" : "")}>{Esc(a)}</option>"))}}</select>
                <p class="small">Uses the user name, key and address from <code>~/.ssh/config</code>.</p></details>
                """ : "<input type='hidden' id='alias' value=''>")}}
                <div class="buttons">
                  {{(canCancel ? "<button data-act=\"setup-cancel\">Cancel</button>" : "")}}
                  <button class="primary" id="go" data-act="setup-connect" data-fields="host,user,alias">{{(editing is null ? "Add PC" : "Save")}}</button>
                </div>
                """,
        };
        const string css = """
            .box { width:min(560px, 100%); padding:22px 24px; background:var(--surface); border:1px solid var(--rule); border-radius:var(--r); }
            h2 { font-size:16px; margin:0 0 6px; font-weight:600; letter-spacing:-.01em; } p { color:var(--muted); margin:0 0 14px; font-size:12.5px; }
            .small { font-size:11.5px; margin-top:6px; }
            label { display:flex; flex-direction:column; gap:5px; margin-bottom:12px; color:var(--ink-2); font-size:12px; }
            .row { display:flex; gap:6px; } .row input { flex:1; min-width:0; }
            details { margin:2px 0 12px; } summary { cursor:pointer; color:var(--accent); margin-bottom:6px; font-size:12.5px; }
            .buttons { display:flex; justify-content:flex-end; gap:8px; margin-top:8px; }
            .err { white-space:pre-wrap; background:color-mix(in srgb, var(--critical) 8%, var(--surface)); border:1px solid color-mix(in srgb, var(--critical) 45%, transparent);
                   border-left:3px solid var(--critical); border-radius:var(--r-sm); padding:8px 10px; margin-bottom:12px; font-size:12px; max-height:200px; overflow:auto; }
            .center { display:flex; align-items:center; gap:10px; justify-content:center; padding:24px 0 8px; }
            .found { display:flex; flex-wrap:wrap; gap:6px; margin:-4px 0 12px; }
            .pick { font-size:12px; height:26px; padding:0 9px; } .pick span { color:var(--muted); margin-left:4px; }
            .opt { border:1px solid var(--rule); border-radius:var(--r); padding:12px 14px; margin-bottom:10px; background:var(--raised); }
            .opt.rec { border-left:3px solid var(--accent); }
            .opt p { margin:4px 0 8px; } .opt .buttons { margin-top:0; }
            .opt summary { margin-bottom:0; } details.opt[open] summary { margin-bottom:6px; }
            .opt-head { display:flex; align-items:center; gap:8px; font-size:13px; }
            .opt-head .tag { margin-left:auto; color:var(--accent); border-color:var(--accent-line); }
            .two { display:grid; grid-template-columns:1fr 1fr; gap:10px; margin-top:8px; } .two label { margin-bottom:0; }
            textarea { width:100%; font:11.5px var(--mono); color:var(--ink); background:var(--sunken); border:1px solid var(--rule-2);
                       border-radius:var(--r-sm); padding:7px 9px; resize:none; margin-bottom:8px; }
            """;
        const string script = """
            document.addEventListener("keydown", e => {
              if (e.key !== "Enter") return;
              if (e.target.id === "password" || (e.target.id === "user" && document.getElementById("pw-btn"))) document.getElementById("pw-btn").click();
              else if (e.target.id === "host") document.getElementById("go")?.click();
            });
            """;
        show(Ui.Shell(editing is null ? "Add a PC" : editing.DisplayName, css, $"<div class=\"box\">{body}</div>", script, center: true));
    }
}
