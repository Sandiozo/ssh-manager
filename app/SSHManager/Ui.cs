using System.Net;

namespace SSHManager;

/// <summary>Pages the app draws itself (the overview, connection status, app settings), styled like the
/// dashboard: a left rail with the PC list (filled by the app's "pcs" message) and a main area.
/// Buttons carry data-act (the message type) and data-* values; one click handler posts them, so no
/// value from a PC ever ends up inside script.</summary>
internal static class Ui
{
    /// <summary>"system", "light" or "dark"; set by the app from its settings.</summary>
    public static string Theme { get; set; } = "system";

    public const string BaseCss = """
        :root { color-scheme: dark;
          --page:#121412; --surface:#191c1a; --raised:#1f2320; --hover:#242925; --sunken:#0f110f; --rule:#2a2f2b; --rule-2:#3a403b;
          --ink:#e9e6dd; --ink-2:#b6b3a8; --muted:#8b8a80; --faint:#6d6d64;
          --accent:#d08447; --accent-ink:#1b1209; --accent-wash:rgba(208,132,71,.14); --accent-line:rgba(208,132,71,.45); --teal:#5fa898;
          --good:#0ca30c; --warning:#fab219; --critical:#d03b3b;
          --s1:#3987e5; --s2:#d95926; --s3:#199e70; --s4:#c98500; --grid:#2b2f2c; --axis:#3c413d;
          --shadow:0 10px 30px rgba(0,0,0,.45); --r:6px; --r-sm:4px;
          --font:"Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif; --mono:"Cascadia Mono", "Cascadia Code", Consolas, monospace; }
        @media (prefers-color-scheme: light) { :root:where(:not([data-theme="dark"])) { color-scheme: light;
          --page:#efebe2; --surface:#fbfaf6; --raised:#f4f1e9; --hover:#ebe6da; --sunken:#f7f5ef; --rule:#ddd7ca; --rule-2:#c9c2b3;
          --ink:#1c1f1d; --ink-2:#4c4f48; --muted:#75756b; --faint:#9c9a8f;
          --accent:#ad5f27; --accent-ink:#fff; --accent-wash:rgba(173,95,39,.10); --accent-line:rgba(173,95,39,.40); --teal:#2f7466;
          --s1:#2a78d6; --s2:#eb6834; --s3:#1baf7a; --s4:#eda100; --grid:#e7e2d7; --axis:#c9c3b6; --shadow:0 10px 30px rgba(40,30,10,.14); } }
        :root[data-theme="light"] { color-scheme: light;
          --page:#efebe2; --surface:#fbfaf6; --raised:#f4f1e9; --hover:#ebe6da; --sunken:#f7f5ef; --rule:#ddd7ca; --rule-2:#c9c2b3;
          --ink:#1c1f1d; --ink-2:#4c4f48; --muted:#75756b; --faint:#9c9a8f;
          --accent:#ad5f27; --accent-ink:#fff; --accent-wash:rgba(173,95,39,.10); --accent-line:rgba(173,95,39,.40); --teal:#2f7466;
          --s1:#2a78d6; --s2:#eb6834; --s3:#1baf7a; --s4:#eda100; --grid:#e7e2d7; --axis:#c9c3b6; --shadow:0 10px 30px rgba(40,30,10,.14); }
        * { box-sizing:border-box; }
        html, body { margin:0; height:100%; background:var(--page); color:var(--ink); font:13px/1.45 var(--font); }
        button { font:inherit; color:var(--ink); background:var(--surface); border:1px solid var(--rule-2); border-radius:var(--r-sm); padding:0 12px; height:30px;
                 cursor:pointer; white-space:nowrap; transition:background .12s, border-color .12s; }
        button:hover { background:var(--hover); }
        button.primary { background:var(--accent); border-color:var(--accent); color:var(--accent-ink); font-weight:600; }
        button.primary:hover { background:var(--accent); filter:brightness(1.07); }
        button.link { background:none; border:0; color:var(--accent); padding:0; height:auto; font-size:12px; }
        button.link:hover { text-decoration:underline; }
        input, select { font:inherit; color:var(--ink); background:var(--sunken); border:1px solid var(--rule-2); border-radius:var(--r-sm); padding:0 9px; height:30px; }
        input:focus, select:focus { outline:none; border-color:var(--accent); box-shadow:0 0 0 3px var(--accent-wash); }
        :focus-visible { outline:2px solid var(--accent); outline-offset:1px; }
        code, pre { font-family:var(--mono); font-size:12px; }
        .muted { color:var(--muted); } .small { font-size:11.5px; } .num { font-variant-numeric:tabular-nums; } .dim { color:var(--muted); }
        .dot { width:7px; height:7px; border-radius:50%; background:var(--faint); display:inline-block; flex:none; }
        .dot.ok { background:var(--good); } .dot.warn { background:var(--warning); } .dot.bad { background:var(--critical); }
        .tag { font-size:10.5px; padding:0 5px; line-height:17px; border-radius:3px; border:1px solid var(--rule-2); color:var(--ink-2); font-weight:400; white-space:nowrap; }
        .tag.accent { color:var(--accent); border-color:var(--accent-line); }
        .spin { width:16px; height:16px; border:2px solid var(--rule-2); border-top-color:var(--accent); border-radius:50%; display:inline-block; animation:s .9s linear infinite; }
        @keyframes s { to { transform:rotate(360deg); } }
        .app { display:grid; grid-template-columns:232px minmax(0, 1fr); min-height:100vh; }
        .rail { position:sticky; top:0; height:100vh; display:flex; flex-direction:column; background:var(--surface); border-right:1px solid var(--rule); padding:14px 0 10px; overflow-y:auto; }
        .brand { display:flex; align-items:center; gap:10px; padding:0 16px 14px; font-weight:600; font-size:14px; letter-spacing:-.01em; }
        .logo { width:26px; height:26px; flex:none; }
        .rail-label { font-size:10.5px; font-weight:600; letter-spacing:.08em; text-transform:uppercase; color:var(--faint); padding:10px 16px 6px; border-top:1px solid var(--rule); }
        .pc-list { display:flex; flex-direction:column; }
        .pc-list button { display:flex; align-items:center; gap:10px; width:100%; height:34px; padding:0 16px; background:none; border:0; border-radius:0; color:var(--ink-2); text-align:left; }
        .pc-list button:hover { background:var(--hover); color:var(--ink); }
        .pc-list button.on { background:var(--accent-wash); color:var(--ink); font-weight:600; box-shadow:inset 3px 0 0 var(--accent); }
        .pc-list .add { color:var(--accent); }
        .pc-list .name { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
        .rail-foot { margin-top:auto; padding:10px 8px 0; border-top:1px solid var(--rule); }
        .rail-foot button { display:flex; align-items:center; gap:10px; width:100%; height:32px; padding:0 10px; background:none; border:0; color:var(--ink-2); }
        .rail-foot button:hover, .rail-foot button.on { background:var(--raised); color:var(--ink); }
        .rail-foot button.on { box-shadow:inset 2px 0 0 var(--accent); font-weight:600; }
        .rail-foot svg { width:16px; height:16px; fill:none; stroke:currentColor; stroke-width:1.7; stroke-linecap:round; stroke-linejoin:round; }
        .main { min-width:0; display:flex; flex-direction:column; }
        .topbar { position:sticky; top:0; z-index:6; display:flex; align-items:center; gap:16px; height:56px; padding:0 28px;
                  background:color-mix(in srgb, var(--page) 92%, transparent); backdrop-filter:blur(6px); border-bottom:1px solid var(--rule); }
        .topbar h1 { margin:0; font-size:17px; font-weight:600; letter-spacing:-.01em; }
        .content { padding:20px 28px 48px; flex:1; width:100%; max-width:1480px; display:flex; flex-direction:column; gap:22px; }
        .content.center { align-items:center; justify-content:center; }
        .panel { background:var(--surface); border:1px solid var(--rule); border-radius:var(--r); padding:14px 16px; min-width:0; }
        .panel-head { display:flex; align-items:baseline; justify-content:space-between; gap:12px; padding-bottom:8px; margin-bottom:4px; border-bottom:1px solid var(--rule); flex-wrap:wrap; }
        .panel h2 { font-size:13px; margin:0; font-weight:600; }
        .none { color:var(--muted); padding:10px 2px; font-size:12.5px; }
        .viz-tip { position:fixed; z-index:40; pointer-events:none; background:var(--raised); border:1px solid var(--rule-2); border-radius:var(--r-sm);
                   padding:5px 8px; font-size:11.5px; color:var(--ink); box-shadow:var(--shadow); white-space:pre; display:none; }
        @media (max-width: 860px) { .app { grid-template-columns:56px minmax(0, 1fr); } .brand span, .rail-label, .pc-list .name, .rail-foot span { display:none; }
          .pc-list button, .rail-foot button { justify-content:center; padding:0; } .brand { justify-content:center; padding:0 0 14px; } .content { padding:16px; } .topbar { padding:0 16px; } }
        """;

    /// <summary>The app icon for pages shown with NavigateToString (no URL to load a file from).</summary>
    static readonly string LogoUri = "data:image/png;base64," + Convert.ToBase64String(AppIcon.LogoPng());

    /// <summary>Fills the PC list from the app's "pcs" message, follows theme changes, posts [data-act]
    /// clicks to the app and shows [data-tip] hover details.</summary>
    public const string PcBarScript = """
        const esc = s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
        const send = (type, data = {}) => chrome.webview.postMessage({ type, ...data });
        chrome.webview.addEventListener("message", e => {
          const m = e.data;
          if (m?.type === "theme") {
            if (m.theme === "light" || m.theme === "dark") document.documentElement.dataset.theme = m.theme;
            else delete document.documentElement.dataset.theme;
          }
          if (m?.type !== "pcs") return;
          const st = p => p.state === "Connected" ? "ok" : p.state === "Connecting" ? "warn" : "bad";
          document.getElementById("pcbar").innerHTML =
            (m.pcs.length > 1 ? `<button data-act="switch-pc" data-id="all" class="${m.current === "all" ? "on" : ""}"><span class="dot" style="background:var(--accent)"></span><span class="name">All PCs</span></button>` : "") +
            m.pcs.map(p => `<button data-act="switch-pc" data-id="${esc(p.id)}" class="${p.id === m.current ? "on" : ""}" title="${esc(p.name)}"><span class="dot ${st(p)}"></span><span class="name">${esc(p.name)}</span></button>`).join("") +
            `<button class="add" data-act="add-pc"><span class="dot" style="background:none;color:var(--accent)">+</span><span class="name">Add PC</span></button>`;
          if (window.onPcs) window.onPcs(m);
        });
        document.addEventListener("click", e => {
          const b = e.target.closest("[data-act]");
          if (!b || b.disabled) return;
          // data-fields names inputs whose values go along; data-after relabels the button once clicked
          const { act, fields, after, ...data } = b.dataset;
          for (const f of (fields || "").split(",").filter(Boolean)) data[f] = document.getElementById(f)?.value ?? "";
          if (after) b.textContent = after;
          send(act, data);
        });
        document.addEventListener("mousemove", e => {
          const t = e.target.closest?.("[data-tip]"), tip = document.getElementById("viz-tip");
          if (!tip) return;
          if (!t) { tip.style.display = "none"; return; }
          tip.textContent = t.dataset.tip; tip.style.display = "block";
          tip.style.left = Math.min(innerWidth - tip.offsetWidth - 8, e.clientX + 12) + "px"; tip.style.top = (e.clientY - tip.offsetHeight - 10) + "px";
        });
        """;

    const string GearIcon = """<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="3"/><path d="M12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9l2.1 2.1M17 17l2.1 2.1M4.9 19.1 7 17M17 7l2.1-2.1"/></svg>""";

    /// <summary>A whole page: rail with the PC list, a top bar with the title, and the content.</summary>
    public static string Shell(string title, string css, string content, string script = "", bool center = false, bool settingsPage = false)
    {
        var theme = Theme is "light" or "dark" ? $" data-theme=\"{Theme}\"" : "";
        return $$"""
            <!doctype html><html lang="en"{{theme}}><head><meta charset="utf-8"><title>SSH Manager · {{Esc(title)}}</title><style>{{BaseCss}}{{css}}</style></head>
            <body><div class="app">
              <aside class="rail">
                <div class="brand"><img class="logo" src="{{LogoUri}}" alt=""><span>SSH Manager</span></div>
                <div class="rail-label">PCs</div>
                <div class="pc-list" id="pcbar"></div>
                <div class="rail-foot"><button data-act="open-app-settings" class="{{(settingsPage ? "on" : "")}}">{{GearIcon}}<span>App settings</span></button></div>
              </aside>
              <div class="main">
                <header class="topbar"><h1>{{Esc(title)}}</h1></header>
                <div class="content{{(center ? " center" : "")}}">{{content}}</div>
              </div>
            </div><div class="viz-tip" id="viz-tip"></div><script>{{PcBarScript}}{{script}}</script></body></html>
            """;
    }

    static string Esc(string s) => WebUtility.HtmlEncode(s);

    public static string StatusPage(PcEntry pc, LinkState state, string message)
    {
        var connecting = state == LinkState.Connecting;
        var lines = message.Split('\n', 2);
        var detail = lines.Length > 1 ? $"<pre>{Esc(lines[1].Trim())}</pre>" : "";
        var id = Esc(pc.Id);
        var buttons = connecting ? "" :
            $"<button class=\"primary\" data-act=\"retry\" data-id=\"{id}\">Retry now</button>" +
            (pc.Mac != "" && !pc.IsLocal ? $"<button data-act=\"wake-pc\" data-id=\"{id}\">Wake up PC</button>" : "") +
            (pc.IsLocal ? "" : $"<button data-act=\"edit-pc\" data-id=\"{id}\">Change connection…</button>");
        const string css = """
            .box { width:min(560px, 100%); padding:22px 24px; background:var(--surface); border:1px solid var(--rule); border-radius:var(--r);
                   border-left:3px solid var(--critical); }
            .box.busy { border-left-color:var(--accent); }
            .state { display:flex; align-items:center; gap:10px; font-size:14px; font-weight:600; margin-bottom:4px; }
            p { color:var(--muted); margin:0 0 14px; }
            pre { white-space:pre-wrap; font-size:11.5px; color:var(--ink-2); background:var(--sunken); border:1px solid var(--rule);
                  border-radius:var(--r-sm); padding:9px; max-height:180px; overflow:auto; margin:0 0 14px; }
            .buttons { display:flex; gap:8px; flex-wrap:wrap; }
            """;
        var icon = connecting ? "<span class=\"spin\"></span>" : "<span class=\"dot bad\"></span>";
        return Shell(pc.DisplayName, css, $"""
            <div class="box{(connecting ? " busy" : "")}">
              <div class="state">{icon}{Esc(lines[0])}</div>
              <p>{(connecting ? "Opening an SSH connection." : "SSH Manager keeps retrying in the background.")}</p>
              {detail}
              <div class="buttons">{buttons}</div>
            </div>
            """, center: true);
    }

    /// <summary>The app's own settings. Changes are posted as "set-app-settings"; the app answers with
    /// "app-settings", which refreshes the controls.</summary>
    public static string AppSettingsPage() => Shell("App settings", """
        .content { max-width:820px; }
        .set-head p { margin:0; color:var(--muted); font-size:12.5px; }
        .set-sub { font-size:10.5px; font-weight:600; text-transform:uppercase; letter-spacing:.08em; color:var(--faint); margin:0 2px -14px; }
        .set-group { background:var(--surface); border:1px solid var(--rule); border-radius:var(--r); }
        .set-row { display:flex; align-items:center; justify-content:space-between; gap:16px; min-height:52px; padding:9px 16px; border-bottom:1px solid var(--rule); }
        .set-row:last-child { border-bottom:0; } .set-row[hidden] { display:none; }
        .set-label { display:flex; flex-direction:column; font-size:13px; min-width:0; }
        .set-label small { color:var(--muted); font-size:11.5px; margin-top:1px; }
        .inline { display:flex; gap:6px; width:min(420px, 60%); } .inline input { flex:1; min-width:0; font-family:var(--mono); font-size:12px; }
        .switch { display:inline-flex; cursor:pointer; }
        .switch input { appearance:none; width:32px; height:18px; border-radius:9px; background:var(--rule-2); position:relative; cursor:pointer; padding:0; border:0; flex:none; transition:background .15s; }
        .switch input::after { content:""; position:absolute; top:2px; left:2px; width:14px; height:14px; border-radius:50%; background:#fff; transition:left .15s; }
        .switch input:checked { background:var(--accent); } .switch input:checked::after { left:16px; }
        .seg { display:inline-flex; border:1px solid var(--rule-2); border-radius:var(--r-sm); overflow:hidden; height:30px; }
        .seg button { border:0; border-radius:0; background:none; color:var(--muted); height:28px; border-left:1px solid var(--rule); }
        .seg button:first-child { border-left:0; }
        .seg button.on { background:var(--accent-wash); color:var(--ink); font-weight:600; }
        """, """
        <div class="set-head"><p id="where">The SSH Manager app on this computer.</p></div>
        <div class="set-sub">General</div>
        <div class="set-group">
          <div class="set-row"><div class="set-label">Appearance<small>Light or dark, or follow Windows.</small></div>
            <div class="seg" id="theme"><button data-theme-v="system">Windows</button><button data-theme-v="light">Light</button><button data-theme-v="dark">Dark</button></div></div>
          <div class="set-row"><div class="set-label">Start with Windows<small>Starts in the notification area.</small></div>
            <label class="switch"><input type="checkbox" data-app="StartWithWindows"></label></div>
          <div class="set-row" id="autoupdate-row"><div class="set-label">Automatic app updates<small>Install new versions of the app automatically.</small></div>
            <label class="switch"><input type="checkbox" data-app="AutoUpdate"></label></div>
          <div class="set-row"><div class="set-label">Shared clipboard<small>Copied text is available on the PCs this app is connected to. Text that password managers mark as private is never shared.</small></div>
            <label class="switch"><input type="checkbox" data-app="ShareClipboard"></label></div>
        </div>
        <div class="set-sub">Notifications</div>
        <div class="set-group">
          <div class="set-row"><div class="set-label">Connection lost or restored</div><label class="switch"><input type="checkbox" data-app="NotifyConnection"></label></div>
          <div class="set-row"><div class="set-label">Failed login attempts</div><label class="switch"><input type="checkbox" data-app="NotifyFailedLogin"></label></div>
          <div class="set-row"><div class="set-label">Login from a computer that is not in the list</div><label class="switch"><input type="checkbox" data-app="NotifyUnknown"></label></div>
          <div class="set-row"><div class="set-label">Transfer finished<small>Only while the window is in the background.</small></div><label class="switch"><input type="checkbox" data-app="NotifyTransfers"></label></div>
        </div>
        <div class="set-sub">Downloads and connection</div>
        <div class="set-group">
          <div class="set-row"><div class="set-label">Download folder</div>
            <div class="inline"><input id="dl" readonly><button data-act="pick-download-folder">Change…</button></div></div>
          <div class="set-row"><div class="set-label">Ask where to save each download</div><label class="switch"><input type="checkbox" data-app="AskWhereToSave"></label></div>
          <div class="set-row"><div class="set-label">Connection to this PC<small>How the app reaches this computer's own server.</small></div>
            <div class="inline"><input id="conn" readonly><button data-act="open-setup">Change…</button></div></div>
        </div>
        """, """
        const $ = id => document.getElementById(id);
        let theme = "system";
        function showTheme() { document.querySelectorAll("#theme button").forEach(b => b.classList.toggle("on", b.dataset.themeV === theme)); }
        chrome.webview.addEventListener("message", e => {
          const m = e.data;
          if (m?.type !== "app-settings") return;
          const s = m.settings || {};
          document.querySelectorAll("[data-app]").forEach(i => i.checked = !!s[i.dataset.app]);
          $("dl").value = s.DownloadFolder || "";
          $("conn").value = s.Connection || "";
          $("autoupdate-row").hidden = !!s.ServerUpdatesApp;
          $("where").textContent = `The SSH Manager app on ${m.machine || "this computer"} · version ${s.AppVersion || ""}`;
          theme = s.Theme || "system"; showTheme();
        });
        document.addEventListener("change", e => {
          const el = e.target;
          if (el.dataset.app) send("set-app-settings", { settings: { [el.dataset.app]: el.checked } });
        });
        $("theme").onclick = e => {
          const b = e.target.closest("[data-theme-v]"); if (!b) return;
          theme = b.dataset.themeV; showTheme();
          send("set-app-settings", { settings: { Theme: theme } });
        };
        send("get-app-settings");
        """, settingsPage: true);

    /// <summary>All PCs at a glance. Filled and refreshed by "overview" messages from the app.</summary>
    public static string Overview() => Shell("All PCs", """
        .banner { border:1px solid var(--accent-line); border-left:3px solid var(--accent); background:color-mix(in srgb, var(--accent) 7%, var(--surface));
                  border-radius:var(--r-sm); padding:9px 12px; display:flex; align-items:center; gap:12px; font-size:12.5px; }
        .banner span { flex:1; } .banner[hidden] { display:none; }
        .kpis { display:grid; grid-template-columns:repeat(auto-fit, minmax(150px, 1fr)); gap:1px; background:var(--rule); border:1px solid var(--rule); border-radius:var(--r); overflow:hidden; }
        .kpi { background:var(--surface); padding:12px 16px 13px; min-width:0; }
        .kpi-label { font-size:11.5px; color:var(--muted); }
        .kpi-value { font-size:22px; font-weight:600; letter-spacing:-.02em; margin-top:3px; line-height:1.15; white-space:nowrap; }
        .kpi-value.bad { color:var(--critical); }
        .kpi-sub { font-size:11.5px; color:var(--muted); white-space:nowrap; overflow:hidden; text-overflow:ellipsis; margin-top:1px; }
        .table-wrap { overflow-x:auto; }
        .machines { width:100%; border-collapse:collapse; }
        .machines th { text-align:left; font-size:11px; font-weight:500; color:var(--faint); padding:6px 12px; white-space:nowrap; }
        .machines td { padding:10px 12px; border-top:1px solid var(--rule); vertical-align:middle; white-space:nowrap; }
        .machines tbody tr:hover td { background:var(--hover); }
        .machines .who { display:flex; align-items:center; gap:10px; }
        .machines .who b { font-weight:600; }
        .machines .who small { display:block; color:var(--muted); font-size:11.5px; max-width:260px; overflow:hidden; text-overflow:ellipsis; }
        .machines .msg { color:var(--muted); font-size:11.5px; white-space:normal; max-width:420px; }
        .machines .acts { display:flex; gap:6px; justify-content:flex-end; }
        .machines .acts button { height:26px; padding:0 10px; font-size:12px; }
        .mini { display:grid; grid-template-columns:36px minmax(60px, 110px); align-items:center; gap:8px; }
        .meter { height:3px; background:var(--rule); border-radius:2px; overflow:hidden; }
        .meter i { display:block; height:100%; background:var(--teal); border-radius:2px; }
        .meter i.hot { background:var(--warning); } .meter i.crit { background:var(--critical); }
        .add-row td { color:var(--accent); cursor:pointer; }
        .two { display:grid; grid-template-columns:minmax(300px, 1fr) minmax(0, 1.7fr); gap:22px; align-items:start; }
        @media (max-width: 1100px) { .two { grid-template-columns:minmax(0, 1fr); } }
        .col { display:flex; flex-direction:column; gap:22px; min-width:0; }
        .bars { position:relative; display:flex; align-items:flex-end; gap:2px; height:110px; margin:14px 0 22px 26px; border-bottom:1px solid var(--axis); }
        .bars .gl { position:absolute; left:0; right:0; border-top:1px solid var(--grid); pointer-events:none; }
        .bars .gl span { position:absolute; left:-26px; top:-7px; width:20px; text-align:right; font-size:10.5px; color:var(--muted); font-variant-numeric:tabular-nums; }
        .bars .b { flex:1; height:100%; display:flex; flex-direction:column-reverse; align-items:center; gap:2px; position:relative; }
        .bars .b i { display:block; width:100%; max-width:24px; flex:none; }
        .bars .b i:last-of-type { border-radius:4px 4px 0 0; }
        .bars .b:hover i { filter:brightness(1.15); }
        .bars .b span { position:absolute; bottom:-18px; left:50%; transform:translateX(-50%); font-size:10.5px; color:var(--muted); }
        .legend { display:flex; gap:14px; flex-wrap:wrap; font-size:11.5px; color:var(--ink-2); }
        .key { display:inline-block; width:8px; height:8px; border-radius:2px; margin-right:6px; vertical-align:0; flex:none; }
        .sess { padding:8px 2px; border-top:1px solid var(--rule); font-size:12.5px; } .sess:first-child { border-top:0; }
        .sess .line { display:flex; gap:8px; align-items:center; min-width:0; } .sess .arrow { color:var(--faint); }
        .sess .when { margin-left:auto; color:var(--muted); font-size:11.5px; white-space:nowrap; }
        .sess .cmd { margin:5px 0 0 15px; padding:4px 8px; border-radius:var(--r-sm); background:var(--raised); font:11.5px var(--mono);
                     white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
        .filters { display:flex; gap:6px; align-items:center; flex-wrap:wrap; }
        .filters select, .filters input { height:28px; } .filters input { width:150px; }
        .seg { display:inline-flex; border:1px solid var(--rule-2); border-radius:var(--r-sm); overflow:hidden; height:28px; }
        .seg button { border:0; border-radius:0; background:none; color:var(--muted); height:26px; padding:0 9px; font-size:12px; border-left:1px solid var(--rule); }
        .seg button:first-child { border-left:0; }
        .seg button.on { background:var(--accent-wash); color:var(--ink); font-weight:600; }
        #acts { max-height:600px; overflow-y:auto; }
        .act { display:grid; grid-template-columns:48px minmax(90px, 140px) 90px minmax(70px, 120px) minmax(0, 1fr); gap:10px; align-items:center; min-height:32px;
               padding:0 6px; border-top:1px solid var(--rule); cursor:pointer; font-size:12.5px; }
        .act:first-child { border-top:0; } .act:hover { background:var(--hover); }
        .act .t { color:var(--muted); font-size:11.5px; font-variant-numeric:tabular-nums; }
        .act .pc, .act .who { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; } .act .who { color:var(--ink-2); font-size:12px; }
        .act .what { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; font:12px var(--mono); }
        .kind { font-size:10.5px; font-weight:600; text-transform:uppercase; letter-spacing:.04em; color:var(--ink-2); display:inline-flex; align-items:center; gap:6px; white-space:nowrap; }
        .kind::before { content:""; width:6px; height:6px; border-radius:1px; background:var(--k, var(--faint)); flex:none; }
        .kind.command, .kind.powershell, .kind.shell, .kind.cmd, .kind.git, .kind.wrapper { --k:var(--s1); }
        .kind.run, .kind.job, .kind.ui-job { --k:var(--s2); } .kind.file, .kind.sftp { --k:var(--s3); } .kind.setup { --k:var(--s4); }
        @media (max-width: 800px) { .act { grid-template-columns:56px 100px minmax(0, 1fr); } .act .kind, .act .who { display:none; } }
        dialog { background:var(--surface); color:var(--ink); border:1px solid var(--rule-2); border-radius:var(--r); width:min(860px, 94vw); padding:16px; box-shadow:var(--shadow); }
        dialog::backdrop { background:rgba(0, 0, 0, .55); }
        .dlg-head { display:flex; justify-content:space-between; align-items:center; gap:10px; margin-bottom:4px; }
        dialog pre { background:var(--sunken); border:1px solid var(--rule); border-radius:var(--r-sm); padding:10px; max-height:60vh; overflow:auto; white-space:pre-wrap; word-break:break-all; }
        """, """
        <div class="banner" id="banner" hidden><span id="banner-text"></span><button class="primary" id="banner-btn" data-act="app-update-install">Install update</button></div>
        <div class="kpis" id="kpis"></div>
        <div class="panel">
          <div class="panel-head"><h2>Computers</h2><span class="muted small">PCs this app connects to, and the computers that use them</span></div>
          <div class="table-wrap"><table class="machines">
            <thead><tr><th>Computer</th><th>CPU</th><th>Memory</th><th>Disks</th><th>Sessions</th><th></th></tr></thead>
            <tbody id="grid"></tbody>
          </table></div>
        </div>
        <div class="two">
          <div class="col">
            <div class="panel"><div class="panel-head"><h2>SSH logins · 24 h</h2><span class="muted small" id="login-total"></span></div>
              <div class="bars" id="bars"></div><div class="legend" id="legend"></div></div>
            <div class="panel"><div class="panel-head"><h2>Live sessions</h2><span class="muted small" id="sess-count"></span></div>
              <div id="sessions"><div class="none">Loading…</div></div></div>
          </div>
          <div class="panel">
            <div class="panel-head"><h2>Activity on all PCs</h2>
              <div class="filters">
                <select id="f-pc"><option value="">All PCs</option></select>
                <div class="seg" id="f-kind"><button data-k="" class="on">All</button><button data-k="command">Commands</button><button data-k="action">Runs</button><button data-k="file">Files</button><button data-k="setup">Setup</button></div>
                <input id="f-q" placeholder="Search" spellcheck="false">
              </div></div>
            <div id="acts"><div class="none">Loading…</div></div>
          </div>
        </div>
        <dialog id="dlg"><div class="dlg-head"><b id="dlg-title"></b><button id="dlg-close">Close</button></div>
          <div class="muted small" id="dlg-meta"></div><pre id="dlg-body"></pre></dialog>
        """, """
        const $ = id => document.getElementById(id);
        const bytes = n => { if (n == null) return ""; const u = ["B","KB","MB","GB","TB"]; let i = 0; while (n >= 1024 && i < 4) { n /= 1024; i++; } return (i ? n.toFixed(n < 10 ? 1 : 0) : n) + " " + u[i]; };
        const dur = s => s == null ? "" : s < 3600 ? Math.round(s / 60) + " min" : s < 86400 ? Math.floor(s / 3600) + " h " + Math.floor(s % 3600 / 60) + " min" : Math.floor(s / 86400) + " d " + Math.floor(s % 86400 / 3600) + " h";
        const meter = p => `<div class="meter"><i class="${p > 90 ? "crit" : p > 75 ? "hot" : ""}" style="width:${Math.min(100, p || 0)}%"></i></div>`;
        const mini = p => p == null ? `<span class="dim">—</span>` : `<div class="mini"><span class="num">${Math.round(p)}%</span>${meter(p)}</div>`;
        const hm = d => d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
        const when = iso => { const d = new Date(iso); return d.toDateString() === new Date().toDateString() ? hm(d) : d.toLocaleDateString([], { day: "numeric", month: "short" }); };
        const ago = iso => { if (!iso) return ""; const s = (Date.now() - new Date(iso)) / 1000; return s < 60 ? "just now" : dur(s) + " ago"; };
        $("dlg-close").onclick = () => $("dlg").close();

        // one fixed color per PC, in the order the app lists them; past four they share "Other"
        let order = [];
        const SERIES = ["var(--s1)", "var(--s2)", "var(--s3)", "var(--s4)"];
        const slot = name => { const i = order.indexOf(name); return i >= 0 && i < 4 ? i : 4; };
        const color = name => SERIES[slot(name)] || "var(--faint)";
        const key = name => `<span class="key" style="background:${color(name)}"></span>`;

        function row(p, i) {
          const st = p.kind === "this" ? "ok" : p.kind === "client" ? (p.state === "Connected" ? "ok" : p.state === "Online" ? "" : "bad")
                   : p.state === "Connected" ? "ok" : p.state === "Connecting" ? "warn" : "bad";
          const s = p.stats || {};
          const sub = p.kind === "this" ? "this computer · app"
            : p.kind === "client" ? (p.state === "Connected" ? `connected to ${esc(p.via)}` : p.state === "Online" ? "online · not connected" : "offline") + (p.version ? ` · v${esc(p.version)}` : "")
            : (p.state === "Connected" ? "connected" : esc((p.state || "").toLowerCase())) + (p.version ? ` · v${esc(p.version)}` : "");
          const extra = [s.battery != null ? `battery ${s.battery}%${s.charging ? " charging" : ""}` : "", s.uptime_s != null ? `up ${dur(s.uptime_s)}` : ""].filter(Boolean).join(" · ");
          const tag = p.kind === "pc" ? `<span class="tag accent">server</span>` : p.kind === "client" ? `<span class="tag">app</span>` : "";
          const disks = (s.disks || []).slice(0, 2).map((d, j) => `${j ? "<br>" : ""}<span class="${j ? "dim" : ""}">${esc(d.mount)} ${bytes(d.free)} free</span>`).join("") || `<span class="dim">—</span>`;
          const btns = p.kind === "this" ? "" : p.kind === "client"
            ? (p.state === "Offline" && p.mac ? `<button data-act="wake-mac" data-mac="${esc(p.mac)}" data-ip="${esc(p.ip)}" data-name="${esc(p.name)}">Wake up</button>` : "")
            : [p.state === "Connected" ? `<button class="primary" data-act="switch-pc" data-id="${esc(p.id)}">Open</button>` : `<button data-act="retry" data-id="${esc(p.id)}">Retry</button>`,
               p.state !== "Connected" && p.mac ? `<button data-act="wake-pc" data-id="${esc(p.id)}">Wake up</button>` : "",
               p.canUpdate ? `<button data-act="update-pc" data-id="${esc(p.id)}">Update to v${esc(p.latest)}</button>` : "",
               p.local ? "" : `<button data-act="edit-pc" data-id="${esc(p.id)}">Edit</button><button data-act="remove-pc" data-id="${esc(p.id)}">Remove</button>`].join("");
          const msg = p.state !== "Connected" && p.kind === "pc" && p.message ? `<div class="msg">${esc(p.message)}</div>` : "";
          const line = sub + (extra ? " · " + esc(extra) : "");
          return `<tr><td><div class="who"><span class="dot ${st}"></span><div><b>${esc(p.name)}</b><small title="${line}">${line}</small>${msg}</div>${tag}</div></td>
            <td>${mini(s.cpu)}</td><td>${mini(s.ram_total ? s.ram_used / s.ram_total * 100 : null)}</td><td class="num">${disks}</td>
            <td class="num">${p.sessions ?? `<span class="dim">—</span>`}${p.failures ? ` <span class="tag" style="color:var(--critical)">${p.failures} failed</span>` : ""}</td>
            <td><div class="acts">${btns}</div></td></tr>`;
        }
        let acts = [], fKind = "";
        function kpis(sm) {
          const k = (label, value, sub, cls = "") => `<div class="kpi"><div class="kpi-label">${label}</div><div class="kpi-value ${cls}">${value}</div><div class="kpi-sub">${sub}</div></div>`;
          $("kpis").innerHTML = [
            k("PCs online", `${sm.online} / ${sm.total}`, sm.online < sm.total ? `${sm.total - sm.online} not connected` : "all connected", sm.online < sm.total ? "bad" : ""),
            k("Sessions now", sm.sessions, "across all PCs"),
            k("Logins · 24 h", sm.logins, "on all PCs"),
            k("Failed logins · 24 h", sm.failures, sm.failures ? "see Connections on that PC" : "none", sm.failures ? "bad" : ""),
            k("Commands · 24 h", sm.commands + sm.actions, sm.actions ? `${sm.actions} from Run` : "over SSH"),
            k("Transferred · 24 h", sm.bytes ? bytes(sm.bytes) : "0 B", `${sm.files} file${sm.files === 1 ? "" : "s"}`),
          ].join("");
        }
        function niceMax(v) { const p = 10 ** Math.floor(Math.log10(v)); return [2, 4, 10].map(m => m * p).find(m => m >= v); }
        function chart(h) {
          const n = Math.max(0, ...h.series.map(s => s.counts.length));
          // fold PCs past the fourth into "Other" so colors never repeat
          const groups = [0, 1, 2, 3, 4].map(g => ({ name: g < 4 ? null : "Other", counts: Array(n).fill(0), color: SERIES[g] || "var(--faint)" }));
          for (const s of h.series) { const g = groups[slot(s.name)]; if (g.name == null) g.name = s.name; s.counts.forEach((c, i) => g.counts[i] += c || 0); }
          const used = groups.filter(g => g.name && g.counts.some(c => c));
          const totals = Array.from({ length: n }, (_, i) => used.reduce((a, g) => a + g.counts[i], 0));
          const max = niceMax(Math.max(1, ...totals));
          $("login-total").textContent = `${totals.reduce((a, b) => a + b, 0)} logins`;
          $("bars").innerHTML = [0, max / 2, max].map(v => `<div class="gl" style="bottom:${v / max * 100}%"><span>${v}</span></div>`).join("") +
            totals.map((t, i) => {
              const d = h.labels[i] ? new Date(h.labels[i]) : null;
              const tip = (d ? `${d.getHours()}:00–${d.getHours()}:59 · ` : "") + `${t} login${t === 1 ? "" : "s"}` + used.filter(g => g.counts[i]).map(g => `\n${g.name}: ${g.counts[i]}`).join("");
              return `<div class="b" data-tip="${esc(tip)}">${used.map(g => g.counts[i] ? `<i style="height:max(2px, calc(${g.counts[i] / max * 100}% - 2px));background:${g.color}"></i>` : "").join("")}${d && i % 3 === 0 ? `<span>${d.getHours()}</span>` : ""}</div>`;
            }).join("");
          $("legend").innerHTML = used.length > 1 ? used.map(g => `<span><span class="key" style="background:${g.color}"></span>${esc(g.name)}</span>`).join("") : "";
        }
        function sessions(list) {
          $("sess-count").textContent = list.length ? `${list.length} open` : "";
          $("sessions").innerHTML = list.length ? list.map(x => `<div class="sess">
              <div class="line"><span class="dot ok"></span><b>${esc(x.client || x.ip)}</b><span class="arrow">→</span>
                <span>${key(x.pc)}${esc(x.pc)}</span>
                ${x.app ? `<span class="tag">app</span>` : x.tunnel ? `<span class="tag">port forward</span>` : ""}<span class="when">${ago(x.start)}</span></div>
              ${(x.agents || []).map(a => `<div class="cmd"><span class="kind setup">AI agent</span> ${esc(a)} (${esc(x.client || x.ip)})</div>`).join("")}
              ${x.commands.map(c => `<div class="cmd" title="${esc(c.summary)}"><span class="kind ${esc(c.kind)}">${esc(c.kind)}</span> ${esc(c.summary)}</div>`).join("")}
            </div>`).join("") : `<div class="none">No one is connected over SSH to any PC right now.</div>`;
        }
        function label(a) {
          if (a.kind === "command") return { kind: a.type || "command", text: a.summary || "" };
          if (a.kind === "action") return { kind: "run", text: (a.title || "") + (a.status && a.status !== "done" ? ` (${a.status})` : "") };
          if (a.kind === "setup") {
            const t = { settings: `Changed settings (${(a.changed || []).join(", ")})`, "key-added": `Allowed SSH key ${a.label || a.fingerprint}`,
              "key-removed": `Removed SSH key ${a.label || a.fingerprint}`, "pair-requested": `Asked ${a.target} for access`,
              "pair-allowed": `${a.target} allowed access`, "pair-denied": `${a.target} denied access`, "screen-viewed": "Viewed the screen",
              "approval-requested": `Asked for approval to run "${a.title}"`, "approval-allowed": `Allowed ${a.requested_by || "an AI agent"} to run "${a.title}"`,
              "approval-denied": `Denied ${a.requested_by || "an AI agent"} running "${a.title}"`, "approval-unanswered": `"${a.title}" not run: nobody approved it` }[a.action] || a.action;
            return { kind: "setup", text: t };
          }
          const p = a.path || (a.paths || []).join(", ");
          const verbs = { upload: "Received", download: "Sent", "download-zip": "Sent (zip)", mkdir: "Created folder", rename: "Renamed", delete: "Deleted" };
          return { kind: "file", text: `${verbs[a.action] || a.action || ""} ${p}${a.to ? " → " + a.to : ""}${a.size != null ? " (" + bytes(a.size) + ")" : ""}` };
        }
        function renderActs() {
          const pc = $("f-pc").value, q = $("f-q").value.trim().toLowerCase();
          const list = acts.filter(a => (!pc || a.pc === pc) && (!fKind || a.kind === fKind) && (!q || JSON.stringify(a).toLowerCase().includes(q)));
          $("acts").innerHTML = list.length ? list.map(a => {
            const l = label(a);
            return `<div class="act" data-i="${acts.indexOf(a)}"><span class="t">${when(a.ts)}</span>
              <span class="pc">${key(a.pc)}${esc(a.pc)}</span>
              <span class="kind ${esc(l.kind)}">${esc(l.kind)}</span><span class="who">${esc(a.client || "")}</span>
              <span class="what" title="${esc(l.text)}">${esc(l.text)}</span></div>`;
          }).join("") : `<div class="none">${acts.length ? "Nothing matches the filter." : "No activity recorded on the connected PCs yet."}</div>`;
        }
        $("f-pc").onchange = renderActs;
        $("f-q").oninput = renderActs;
        $("f-kind").onclick = e => { const b = e.target.closest("[data-k]"); if (!b) return; fKind = b.dataset.k;
          document.querySelectorAll("#f-kind button").forEach(x => x.classList.toggle("on", x === b)); renderActs(); };
        $("acts").onclick = e => {
          const r = e.target.closest("[data-i]"); if (!r) return;
          const a = acts[+r.dataset.i], l = label(a);
          $("dlg-title").textContent = l.text;
          $("dlg-meta").textContent = `${a.pc} · ${l.kind}${a.client ? " · by " + a.client : ""} · ${new Date(a.ts).toLocaleString()}${a.duration_s != null ? " · " + a.duration_s + " s" : ""}`;
          $("dlg-body").textContent = [a.script, a.output].filter(Boolean).join("\n\n--- output ---\n") || JSON.stringify(a, null, 2);
          $("dlg").showModal();
        };
        function combined(c) {
          kpis(c.summary);
          chart(c.hours);
          sessions(c.sessions);
          const sel = $("f-pc"), keep = sel.value, names = [...new Set(c.hours.series.map(s => s.name).concat(c.activity.map(a => a.pc)))];
          sel.innerHTML = `<option value="">All PCs</option>` + names.map(n => `<option>${esc(n)}</option>`).join("");
          sel.value = names.includes(keep) ? keep : "";
          acts = c.activity;
          if (!$("dlg").open) renderActs();
        }
        chrome.webview.addEventListener("message", e => {
          const m = e.data;
          if (m?.type !== "overview") return;
          order = m.pcs.filter(p => p.kind === "pc").map(p => p.name);
          if (m.combined) { for (const s of m.combined.hours.series) if (!order.includes(s.name)) order.push(s.name); combined(m.combined); }
          $("grid").innerHTML = m.pcs.map(row).join("") + `<tr class="add-row"><td colspan="6" data-act="add-pc">+ Add a PC</td></tr>`;
          const b = $("banner");
          b.hidden = !m.appUpdate;
          if (m.appUpdate) {
            $("banner-text").textContent = m.appUpdate.text;
            $("banner-btn").hidden = !m.appUpdate.canInstall;
          }
        });
        """);
}
