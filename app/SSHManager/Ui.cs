using System.Net;

namespace SSHManager;

/// <summary>Pages the app draws itself (the overview, connection status, setup), styled like the
/// dashboard: a left rail with the PC list (filled by the app's "pcs" message) and a main area.</summary>
internal static class Ui
{
    public const string BaseCss = """
        :root { color-scheme: dark; --bg:#0b0e13; --panel:#12161d; --panel-2:#181d26; --hover:#1d2430; --line:#222a35; --line-2:#2c3542;
                --text:#e5e9f0; --muted:#8892a3; --faint:#5f6878; --accent:#4f8cff; --accent-soft:color-mix(in srgb, #4f8cff 16%, transparent);
                --ok:#34c77b; --warn:#e8a317; --bad:#ef5350;
                --font:"Segoe UI Variable Text", "Segoe UI", Inter, system-ui, sans-serif; --mono:"Cascadia Mono", Consolas, monospace; }
        @media (prefers-color-scheme: light) { :root { color-scheme: light; --bg:#f4f6f9; --panel:#fff; --panel-2:#f2f4f7; --hover:#eceff4;
                --line:#e3e7ed; --line-2:#d5dbe3; --text:#18202b; --muted:#5d6878; --faint:#8a94a3; --accent:#2f6fe8; } }
        * { box-sizing: border-box; }
        html, body { margin:0; height:100%; background:var(--bg); color:var(--text); font:13px/1.45 var(--font); }
        button { font:inherit; color:inherit; background:var(--panel-2); border:1px solid var(--line-2); border-radius:6px; padding:4px 12px; height:28px; cursor:pointer; }
        button:hover { background:var(--hover); border-color:var(--faint); }
        button.primary { background:var(--accent); border-color:var(--accent); color:#fff; }
        input, select { font:inherit; color:var(--text); background:var(--bg); border:1px solid var(--line-2); border-radius:6px; padding:4px 9px; height:30px; }
        input:focus, select:focus { outline:none; border-color:var(--accent); box-shadow:0 0 0 3px var(--accent-soft); }
        code { font-family:var(--mono); font-size:12px; }
        .muted { color:var(--muted); } .small { font-size:11.5px; }
        .dot { width:7px; height:7px; border-radius:50%; background:var(--faint); display:inline-block; flex:none; }
        .dot.ok { background:var(--ok); box-shadow:0 0 0 3px color-mix(in srgb, var(--ok) 22%, transparent); } .dot.warn { background:var(--warn); } .dot.bad { background:var(--bad); }
        .spin { width:16px; height:16px; border:2px solid var(--line-2); border-top-color:var(--ok); border-radius:50%; display:inline-block; animation:s .9s linear infinite; }
        @keyframes s { to { transform:rotate(360deg); } }
        .app { display:grid; grid-template-columns:208px minmax(0, 1fr); min-height:100vh; }
        .rail { position:sticky; top:0; height:100vh; display:flex; flex-direction:column; gap:2px; padding:12px 10px; background:var(--panel); border-right:1px solid var(--line); }
        .brand { display:flex; align-items:center; gap:9px; padding:2px 6px 12px; font-weight:600; font-size:14px; }
        .logo { width:24px; height:24px; flex:none; }
        .rail-label { font-size:10.5px; font-weight:600; letter-spacing:.06em; text-transform:uppercase; color:var(--faint); padding:10px 8px 4px; }
        .pc-list { display:flex; flex-direction:column; gap:1px; }
        .pc-list button { display:flex; align-items:center; gap:9px; width:100%; height:30px; padding:0 8px; background:none; border:none; border-radius:6px; color:var(--muted); text-align:left; }
        .pc-list button:hover { background:var(--hover); color:var(--text); }
        .pc-list button.on { background:var(--accent-soft); color:var(--text); }
        .pc-list .add { color:var(--accent); }
        .pc-list .name { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
        .main { min-width:0; display:flex; flex-direction:column; }
        .topbar { height:48px; display:flex; align-items:center; gap:10px; padding:0 20px; border-bottom:1px solid var(--line); }
        .topbar h1 { margin:0; font-size:15px; font-weight:600; }
        .content { padding:16px 20px 40px; flex:1; }
        .content.center { display:flex; align-items:center; justify-content:center; }
        @media (max-width: 860px) { .app { grid-template-columns:52px minmax(0, 1fr); } .brand span:last-child, .rail-label, .pc-list .name { display:none; }
          .pc-list button { justify-content:center; padding:0; } .brand { justify-content:center; } }
        """;

    /// <summary>The app icon for pages shown with NavigateToString (no URL to load a file from).</summary>
    static readonly string LogoUri = "data:image/png;base64," + Convert.ToBase64String(AppIcon.LogoPng());

    /// <summary>Fills the PC list from the app's "pcs" message; clicks go back to the app.</summary>
    public const string PcBarScript = """
        const esc = s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
        chrome.webview.addEventListener("message", e => {
          const m = e.data;
          if (!m || m.type !== "pcs") return;
          const st = p => p.state === "Connected" ? "ok" : p.state === "Connecting" ? "warn" : "bad";
          document.getElementById("pcbar").innerHTML =
            (m.pcs.length > 1 ? `<button data-pc="all" class="${m.current === "all" ? "on" : ""}"><span class="dot" style="background:var(--accent)"></span><span class="name">All PCs</span></button>` : "") +
            m.pcs.map(p => `<button data-pc="${esc(p.id)}" class="${p.id === m.current ? "on" : ""}" title="${esc(p.name)}"><span class="dot ${st(p)}"></span><span class="name">${esc(p.name)}</span></button>`).join("") +
            `<button class="add" data-pc="add"><span class="dot" style="background:var(--accent)"></span><span class="name">Add PC</span></button>`;
          if (window.onPcs) window.onPcs(m);
        });
        document.addEventListener("click", e => {
          const b = e.target.closest("#pcbar [data-pc]");
          if (b) chrome.webview.postMessage(b.dataset.pc === "add" ? { type: "add-pc" } : { type: "switch-pc", id: b.dataset.pc });
        });
        """;

    /// <summary>A whole page: rail with the PC list, a top bar with the title, and the content.</summary>
    public static string Shell(string title, string css, string content, string script = "", bool center = false) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>SSH Manager · {{WebUtility.HtmlEncode(title)}}</title><style>{{BaseCss}}{{css}}</style></head>
        <body><div class="app">
          <aside class="rail">
            <div class="brand"><img class="logo" src="{{LogoUri}}" alt=""><span>SSH Manager</span></div>
            <div class="rail-label">PCs</div>
            <div class="pc-list" id="pcbar"></div>
          </aside>
          <div class="main">
            <header class="topbar"><h1>{{WebUtility.HtmlEncode(title)}}</h1></header>
            <div class="content{{(center ? " center" : "")}}">{{content}}</div>
          </div>
        </div><script>{{PcBarScript}}{{script}}</script></body></html>
        """;

    static string Esc(string s) => WebUtility.HtmlEncode(s);

    public static string StatusPage(PcEntry pc, LinkState state, string message)
    {
        var connecting = state == LinkState.Connecting;
        var lines = message.Split('\n', 2);
        var detail = lines.Length > 1 ? $"<pre>{Esc(lines[1].Trim())}</pre>" : "";
        var id = Esc(pc.Id);
        var buttons = connecting ? "" :
            "<button class=\"primary\" onclick=\"chrome.webview.postMessage({type:'retry', id:'" + id + "'})\">Retry now</button>" +
            (pc.Mac != "" && !pc.IsLocal ? "<button onclick=\"chrome.webview.postMessage({type:'wake-pc', id:'" + id + "'})\">Wake up PC</button>" : "") +
            (pc.IsLocal ? "" : "<button onclick=\"chrome.webview.postMessage({type:'edit-pc', id:'" + id + "'})\">Change connection…</button>");
        const string css = """
            .box { width:min(560px, 100%); padding:22px 24px; background:var(--panel); border:1px solid var(--line); border-radius:10px; }
            .state { display:flex; align-items:center; gap:10px; font-size:14px; font-weight:600; margin-bottom:4px; }
            p { color:var(--muted); margin:0 0 14px; }
            pre { white-space:pre-wrap; font:11.5px var(--mono); color:var(--muted); background:var(--bg); border:1px solid var(--line);
                  border-radius:6px; padding:9px; max-height:180px; overflow:auto; margin:0 0 14px; }
            .buttons { display:flex; gap:8px; }
            """;
        var icon = connecting ? "<span class=\"spin\"></span>" : "<span class=\"dot bad\"></span>";
        return Shell(pc.DisplayName, css, $"""
            <div class="box">
              <div class="state">{icon}{Esc(lines[0])}</div>
              <p>{(connecting ? "Opening an SSH connection." : "SSH Manager keeps retrying in the background.")}</p>
              {detail}
              <div class="buttons">{buttons}</div>
            </div>
            """, center: true);
    }

    /// <summary>All PCs at a glance. Filled and refreshed by "overview" messages from the app.</summary>
    public static string Overview() => Shell("All PCs", """
        .banner { background:color-mix(in srgb, var(--accent) 9%, var(--panel)); border:1px solid color-mix(in srgb, var(--accent) 40%, transparent);
                  border-radius:8px; padding:8px 12px; margin-bottom:12px; display:flex; align-items:center; gap:12px; font-size:12.5px; }
        .banner span { flex:1; } .banner[hidden] { display:none; }
        .grid { display:grid; grid-template-columns:repeat(auto-fill, minmax(260px, 1fr)); gap:10px; }
        .card { background:var(--panel); border:1px solid var(--line); border-radius:8px; padding:11px 13px; display:flex; flex-direction:column; gap:4px; }
        .card.add { align-items:center; justify-content:center; border-style:dashed; color:var(--accent); cursor:pointer; min-height:130px; }
        .card.add:hover { background:var(--hover); }
        .head { display:flex; align-items:center; gap:7px; font-weight:600; }
        .head .tag { margin-left:auto; }
        .tag { font-size:10.5px; padding:0 6px; line-height:17px; border-radius:4px; border:1px solid var(--line-2); color:var(--muted); font-weight:400; background:var(--panel-2); }
        .row { display:flex; justify-content:space-between; gap:10px; font-size:12px; } .row span:first-child { color:var(--muted); }
        .meter { height:4px; background:var(--panel-2); border-radius:2px; overflow:hidden; margin-bottom:3px; }
        .meter i { display:block; height:100%; background:var(--accent); } .meter i.hot { background:var(--warn); }
        .actions { display:flex; gap:6px; margin-top:6px; flex-wrap:wrap; } .actions button { height:26px; padding:2px 10px; font-size:12px; }
        .msg { font-size:12px; color:var(--muted); white-space:pre-wrap; max-height:54px; overflow:hidden; }
        .warn-text { color:var(--bad); }
        .content { display:flex; flex-direction:column; gap:12px; }
        .section { font-size:10.5px; font-weight:600; letter-spacing:.06em; text-transform:uppercase; color:var(--faint); margin:4px 2px -4px; }
        .kpis { display:grid; grid-template-columns:repeat(auto-fit, minmax(140px, 1fr)); gap:10px; }
        .kpi { background:var(--panel); border:1px solid var(--line); border-radius:8px; padding:10px 12px; min-width:0; }
        .kpi-label { font-size:11px; color:var(--muted); }
        .kpi-value { font-size:19px; font-weight:600; margin-top:2px; white-space:nowrap; } .kpi-value.bad { color:var(--bad); }
        .kpi-sub { font-size:11px; color:var(--faint); white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
        .two { display:grid; grid-template-columns:minmax(300px, 1fr) minmax(0, 1.9fr); gap:12px; align-items:start; }
        @media (max-width: 1100px) { .two { grid-template-columns:minmax(0, 1fr); } }
        .col { display:flex; flex-direction:column; gap:12px; min-width:0; }
        .panel { background:var(--panel); border:1px solid var(--line); border-radius:8px; padding:12px 14px; min-width:0; }
        .panel-head { display:flex; align-items:center; justify-content:space-between; gap:10px; margin-bottom:8px; flex-wrap:wrap; }
        .panel h2 { font-size:12.5px; margin:0; font-weight:600; }
        .bars { display:flex; align-items:flex-end; gap:2px; height:80px; margin-bottom:16px; }
        .bars .b { flex:1; height:100%; display:flex; flex-direction:column-reverse; position:relative; }
        .bars .b i { display:block; min-height:0; opacity:.85; } .bars .b:hover i { opacity:1; }
        .bars .b i:last-child { border-radius:2px 2px 0 0; }
        .bars .b span { position:absolute; bottom:-15px; left:50%; transform:translateX(-50%); font-size:9.5px; color:var(--faint); }
        .legend { display:flex; gap:12px; flex-wrap:wrap; font-size:11.5px; color:var(--muted); margin-top:6px; }
        .legend i, .pcdot { display:inline-block; width:8px; height:8px; border-radius:2px; margin-right:5px; vertical-align:0; }
        .sess { padding:5px 2px; border-bottom:1px solid var(--line); font-size:12.5px; } .sess:last-child { border-bottom:none; }
        .sess .line { display:flex; gap:7px; align-items:center; } .sess .arrow { color:var(--faint); }
        .sess .when { margin-left:auto; color:var(--muted); font-size:11.5px; }
        .sess .cmd { margin:3px 0 0 14px; padding:3px 8px; border-radius:5px; background:var(--panel-2); font:11.5px var(--mono);
                     white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
        .none { color:var(--muted); font-size:12.5px; padding:4px 0; }
        .filters { display:flex; gap:6px; align-items:center; flex-wrap:wrap; }
        .filters select, .filters input { height:28px; } .filters input { width:140px; }
        .seg { display:inline-flex; border:1px solid var(--line-2); border-radius:6px; overflow:hidden; height:28px; }
        .seg button { border:none; border-radius:0; background:none; color:var(--muted); height:26px; padding:2px 9px; font-size:12px; }
        .seg button.on { background:var(--accent-soft); color:var(--text); }
        #acts { max-height:560px; overflow-y:auto; }
        .act { display:grid; grid-template-columns:44px minmax(90px, 150px) 78px minmax(70px, 110px) minmax(0, 1fr); gap:8px; align-items:center; height:30px; padding:0 6px;
               border-bottom:1px solid var(--line); border-radius:4px; cursor:pointer; font-size:12.5px; }
        .act:hover { background:var(--hover); } .act:last-child { border-bottom:none; }
        .act .t { color:var(--muted); font-size:11.5px; font-variant-numeric:tabular-nums; }
        .act .pc, .act .who { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; } .act .who { color:var(--muted); font-size:11.5px; }
        .act .what { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; font:12px var(--mono); }
        .kind { font-size:10px; font-weight:700; text-transform:uppercase; letter-spacing:.04em; }
        .kind.command, .kind.powershell, .kind.shell { color:#a78bfa; } .kind.job, .kind.ui-job { color:#f472b6; }
        .kind.file, .kind.sftp { color:var(--ok); } .kind.setup { color:var(--warn); } .kind.run { color:#22d3ee; }
        @media (max-width: 800px) { .act { grid-template-columns:62px 100px minmax(0, 1fr); } .act .kind, .act .who { display:none; } }
        dialog { background:var(--panel); color:var(--text); border:1px solid var(--line-2); border-radius:10px; width:min(860px, 94vw); padding:14px; }
        dialog::backdrop { background:rgba(0, 0, 0, .55); }
        .dlg-head { display:flex; justify-content:space-between; align-items:center; gap:10px; margin-bottom:4px; }
        dialog pre { background:var(--bg); border:1px solid var(--line); border-radius:6px; padding:10px; max-height:60vh; overflow:auto;
                     white-space:pre-wrap; word-break:break-all; font:12px var(--mono); }
        """, """
        <div class="banner" id="banner" hidden><span id="banner-text"></span><button class="primary" id="banner-btn">Install update</button></div>
        <div class="kpis" id="kpis"></div>
        <div class="section">Computers</div>
        <div class="grid" id="grid"></div>
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
        <dialog id="dlg"><div class="dlg-head"><b id="dlg-title"></b><button onclick="document.getElementById('dlg').close()">Close</button></div>
          <div class="muted small" id="dlg-meta"></div><pre id="dlg-body"></pre></dialog>
        """, """
        const bytes = n => { if (n == null) return ""; const u = ["B","KB","MB","GB","TB"]; let i = 0; while (n >= 1024 && i < 4) { n /= 1024; i++; } return (i ? n.toFixed(n < 10 ? 1 : 0) : n) + " " + u[i]; };
        const dur = s => s == null ? "" : s < 3600 ? Math.round(s / 60) + " min" : s < 86400 ? Math.floor(s / 3600) + " h " + Math.floor(s % 3600 / 60) + " min" : Math.floor(s / 86400) + " d " + Math.floor(s % 86400 / 3600) + " h";
        const meter = p => `<div class="meter"><i class="${p > 85 ? "hot" : ""}" style="width:${Math.min(100, p || 0)}%"></i></div>`;
        const send = (type, data) => chrome.webview.postMessage({ type, ...data });
        function card(p) {
          const st = p.state === "Connected" ? "ok" : p.state === "Connecting" ? "warn" : "bad";
          const s = p.stats || {};
          const rows = [];
          if (s.cpu != null) rows.push(`<div class="row"><span>CPU</span><span>${Math.round(s.cpu)}%</span></div>${meter(s.cpu)}`);
          if (s.ram_total) rows.push(`<div class="row"><span>Memory</span><span>${bytes(s.ram_used)} / ${bytes(s.ram_total)}</span></div>${meter(s.ram_used / s.ram_total * 100)}`);
          for (const d of (s.disks || []).slice(0, 2)) rows.push(`<div class="row"><span>${esc(d.mount)}</span><span>${bytes(d.free)} free</span></div>${meter((1 - d.free / d.total) * 100)}`);
          if (s.battery != null) rows.push(`<div class="row"><span>Battery</span><span>${s.battery}%${s.charging ? " · charging" : ""}</span></div>`);
          if (s.uptime_s != null) rows.push(`<div class="row"><span>Up</span><span>${dur(s.uptime_s)}</span></div>`);
          if (p.sessions != null) rows.push(`<div class="row"><span>SSH sessions now</span><span>${p.sessions}</span></div>`);
          if (p.failures) rows.push(`<div class="row"><span>Failed logins · 24 h</span><span class="warn-text">${p.failures}</span></div>`);
          if (p.kind === "client") rows.unshift(`<div class="row"><span>${p.state === "Connected" ? "Connected to" : "Status"}</span><span>${p.state === "Connected" ? esc(p.via) : p.state === "Online" ? "Online · not connected" : "Offline"}</span></div>`);
          const tag = p.kind === "this" ? "this computer · client" : p.kind === "client" ? "client" + (p.version ? " · v" + esc(p.version) : "") : p.version ? "v" + esc(p.version) : "";
          const acts = p.kind === "this" ? "" : p.kind === "client"
            ? (p.state === "Offline" && p.mac ? `<div class="actions"><button onclick="send('wake-mac',{mac:'${esc(p.mac)}',ip:'${esc(p.ip)}',name:'${esc(p.name)}'})">Wake up</button></div>` : "")
            : `<div class="actions">
              ${p.state === "Connected" ? `<button class="primary" onclick="send('switch-pc',{id:'${esc(p.id)}'})">Open</button>` : `<button onclick="send('retry',{id:'${esc(p.id)}'})">Retry</button>`}
              ${p.state !== "Connected" && p.mac ? `<button onclick="send('wake-pc',{id:'${esc(p.id)}'})">Wake up</button>` : ""}
              ${p.canUpdate ? `<button onclick="send('update-pc',{id:'${esc(p.id)}'})">Update to v${esc(p.latest)}</button>` : ""}
              ${p.local ? "" : `<button onclick="send('edit-pc',{id:'${esc(p.id)}'})">Edit</button><button onclick="send('remove-pc',{id:'${esc(p.id)}'})">Remove</button>`}
            </div>`;
          return `<div class="card"><div class="head"><span class="dot ${p.kind === "this" ? "ok" : p.kind === "client" && p.state === "Online" ? "" : st}"></span>${esc(p.name)}${tag ? `<span class="tag">${tag}</span>` : ""}</div>
            ${p.state !== "Connected" && p.kind === "pc" ? `<div class="msg">${esc(p.message)}</div>` : ""}${rows.join("")}${acts}</div>`;
        }
        // ---- combined: totals, logins per hour, live sessions, activity on all PCs ----
        const $ = id => document.getElementById(id);
        const palette = ["#4f8cff", "#34c77b", "#c8783c", "#a78bfa", "#f472b6", "#22d3ee", "#e8a317"];
        const colorOf = {};
        const color = name => colorOf[name] ??= palette[Object.keys(colorOf).length % palette.length];
        const hm = d => d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
        const when = iso => { const d = new Date(iso); return d.toDateString() === new Date().toDateString() ? hm(d) : d.toLocaleDateString([], { day: "numeric", month: "short" }); };
        const ago = iso => { if (!iso) return ""; const s = (Date.now() - new Date(iso)) / 1000; return s < 60 ? "just now" : dur(s) + " ago"; };
        let acts = [], fKind = "";
        function kpis(sm) {
          const k = (label, value, sub, cls = "") => `<div class="kpi"><div class="kpi-label">${label}</div><div class="kpi-value ${cls}">${value}</div><div class="kpi-sub">${sub}</div></div>`;
          $("kpis").innerHTML = [
            k("PCs online", `${sm.online} / ${sm.total}`, sm.online < sm.total ? `${sm.total - sm.online} not connected` : "all connected", sm.online < sm.total ? "bad" : ""),
            k("Sessions now", sm.sessions, "across all PCs"),
            k("Logins · 24 h", sm.logins, "on all PCs"),
            k("Failed logins · 24 h", sm.failures, sm.failures ? "see Connections" : "none", sm.failures ? "bad" : ""),
            k("Commands · 24 h", sm.commands + sm.actions, sm.actions ? `${sm.actions} from Run` : "over SSH"),
            k("Transferred · 24 h", sm.bytes ? bytes(sm.bytes) : "0 B", `${sm.files} file${sm.files === 1 ? "" : "s"}`),
          ].join("");
        }
        function chart(h) {
          const n = Math.max(0, ...h.series.map(s => s.counts.length));
          const totals = Array.from({ length: n }, (_, i) => h.series.reduce((a, s) => a + (s.counts[i] || 0), 0));
          const max = Math.max(1, ...totals);
          $("login-total").textContent = `${totals.reduce((a, b) => a + b, 0)} logins`;
          $("bars").innerHTML = totals.map((t, i) => {
            const d = h.labels[i] ? new Date(h.labels[i]) : null;
            const tip = (d ? `${d.getHours()}:00` : "") + ` · ${t} logins` + h.series.filter(s => s.counts[i]).map(s => `\n${s.name}: ${s.counts[i]}`).join("");
            return `<div class="b" title="${esc(tip)}">${h.series.map(s => s.counts[i] ? `<i style="height:${s.counts[i] / max * 100}%;background:${color(s.name)}"></i>` : "").join("")}${d && i % 3 === 0 ? `<span>${d.getHours()}</span>` : ""}</div>`;
          }).join("");
          $("legend").innerHTML = h.series.map(s => `<span><i style="background:${color(s.name)}"></i>${esc(s.name)}</span>`).join("");
        }
        function sessions(list) {
          $("sess-count").textContent = list.length ? `${list.length} open` : "";
          $("sessions").innerHTML = list.length ? list.map(x => `<div class="sess">
              <div class="line"><span class="dot ok"></span><b>${esc(x.client || x.ip)}</b><span class="arrow">→</span>
                <span><span class="pcdot" style="background:${color(x.pc)}"></span>${esc(x.pc)}</span>
                ${x.tunnel ? `<span class="tag">app</span>` : ""}<span class="when">${ago(x.start)}</span></div>
              ${x.commands.map(c => `<div class="cmd" title="${esc(c.summary)}"><span class="kind ${esc(c.kind)}">${esc(c.kind)}</span> ${esc(c.summary)}</div>`).join("")}
            </div>`).join("") : `<div class="none">No one is connected over SSH to any PC right now.</div>`;
        }
        function label(a) {
          if (a.kind === "command") return { kind: a.type || "command", text: a.summary || "" };
          if (a.kind === "action") return { kind: "run", text: (a.title || "") + (a.status && a.status !== "done" ? ` (${a.status})` : "") };
          if (a.kind === "setup") {
            const t = { settings: `Changed settings (${(a.changed || []).join(", ")})`, "key-added": `Allowed SSH key ${a.label || a.fingerprint}`,
              "key-removed": `Removed SSH key ${a.label || a.fingerprint}`, "pair-requested": `Asked ${a.target} for access`,
              "pair-allowed": `${a.target} allowed access`, "pair-denied": `${a.target} denied access`, "screen-viewed": "Viewed the screen" }[a.action] || a.action;
            return { kind: "setup", text: t };
          }
          const p = a.path || (a.paths || []).join(", ");
          const verbs = { upload: "Received", download: "Sent", "download-zip": "Sent (zip)", mkdir: "Created folder", rename: "Renamed", delete: "Deleted" };
          return { kind: "file", text: `${verbs[a.action] || a.action || ""} ${p}${a.to ? " → " + a.to : ""}${a.size != null ? " (" + bytes(a.size) + ")" : ""}` };
        }
        function renderActs() {
          const pc = $("f-pc").value, q = $("f-q").value.trim().toLowerCase();
          const list = acts.filter(a => (!pc || a.pc === pc) && (!fKind || a.kind === fKind) && (!q || JSON.stringify(a).toLowerCase().includes(q)));
          $("acts").innerHTML = list.length ? list.map((a, i) => {
            const l = label(a);
            return `<div class="act" data-i="${acts.indexOf(a)}"><span class="t">${when(a.ts)}</span>
              <span class="pc"><span class="pcdot" style="background:${color(a.pc)}"></span>${esc(a.pc)}</span>
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
          if (!document.getElementById("dlg").open) renderActs();
        }
        chrome.webview.addEventListener("message", e => {
          const m = e.data;
          if (!m || m.type !== "overview") return;
          if (m.combined) combined(m.combined);
          document.getElementById("grid").innerHTML = m.pcs.map(card).join("") + `<div class="card add" onclick="send('add-pc',{})">+ Add a PC</div>`;
          const b = document.getElementById("banner");
          b.hidden = !m.appUpdate;
          if (m.appUpdate) {
            document.getElementById("banner-text").textContent = m.appUpdate.text;
            document.getElementById("banner-btn").hidden = !m.appUpdate.canInstall;
            document.getElementById("banner-btn").onclick = () => send("app-update-install", {});
          }
        });
        """);
}
