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
        .logo { font:700 12px var(--mono); background:var(--ok); color:#062814; border-radius:6px; padding:3px 6px; }
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
            <div class="brand"><span class="logo">&gt;_</span><span>SSH Manager</span></div>
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
        """, """
        <div class="banner" id="banner" hidden><span id="banner-text"></span><button class="primary" id="banner-btn">Install update</button></div>
        <div class="grid" id="grid"></div>
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
          const tag = p.kind === "this" ? "this computer · client" : p.version ? "v" + esc(p.version) : "";
          const acts = p.kind === "this" ? "" : `<div class="actions">
              ${p.state === "Connected" ? `<button class="primary" onclick="send('switch-pc',{id:'${esc(p.id)}'})">Open</button>` : `<button onclick="send('retry',{id:'${esc(p.id)}'})">Retry</button>`}
              ${p.canUpdate ? `<button onclick="send('update-pc',{id:'${esc(p.id)}'})">Update to v${esc(p.latest)}</button>` : ""}
              ${p.local ? "" : `<button onclick="send('edit-pc',{id:'${esc(p.id)}'})">Edit</button><button onclick="send('remove-pc',{id:'${esc(p.id)}'})">Remove</button>`}
            </div>`;
          return `<div class="card"><div class="head"><span class="dot ${p.kind === "this" ? "ok" : st}"></span>${esc(p.name)}${tag ? `<span class="tag">${tag}</span>` : ""}</div>
            ${p.state !== "Connected" && p.kind !== "this" ? `<div class="msg">${esc(p.message)}</div>` : ""}${rows.join("")}${acts}</div>`;
        }
        chrome.webview.addEventListener("message", e => {
          const m = e.data;
          if (!m || m.type !== "overview") return;
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
