using System.Net;

namespace SSHManager;

/// <summary>Pages the app draws itself (the overview, connection status, setup), styled like the
/// dashboard. All of them carry the PC bar, which the app fills with a "pcs" message.</summary>
internal static class Ui
{
    public const string BaseCss = """
        :root { color-scheme: dark; --bg:#0d1117; --panel:#151b23; --panel-2:#1b2430; --line:#273140; --text:#e6edf3; --muted:#8b98a8;
                --accent:#3b82f6; --ok:#22c55e; --warn:#f59e0b; --bad:#ef4444; }
        @media (prefers-color-scheme: light) { :root { color-scheme: light; --bg:#f5f7fa; --panel:#fff; --panel-2:#f0f3f7; --line:#dde3ea; --text:#17202b; --muted:#5f6b7a; } }
        * { box-sizing: border-box; }
        body { margin:0; background:var(--bg); color:var(--text); font:14px/1.5 "Segoe UI", system-ui, sans-serif; }
        button { font:inherit; color:inherit; background:var(--panel-2); border:1px solid var(--line); border-radius:7px; padding:6px 14px; cursor:pointer; }
        button:hover { border-color:var(--muted); }
        button.primary { background:var(--accent); border-color:var(--accent); color:#fff; }
        input, select { font:inherit; color:var(--text); background:var(--bg); border:1px solid var(--line); border-radius:7px; padding:7px 10px; }
        code { font-family:Consolas, monospace; }
        .logo { display:inline-block; font:700 16px Consolas, monospace; background:var(--ok); color:#052e16; border-radius:7px; padding:4px 8px; }
        .muted { color:var(--muted); } .small { font-size:12px; }
        .dot { width:8px; height:8px; border-radius:50%; background:var(--muted); display:inline-block; flex:none; }
        .dot.ok { background:var(--ok); } .dot.warn { background:var(--warn); } .dot.bad { background:var(--bad); }
        .spin { width:18px; height:18px; border:2px solid var(--line); border-top-color:var(--ok); border-radius:50%; display:inline-block; animation:s .9s linear infinite; }
        @keyframes s { to { transform:rotate(360deg); } }
        .pcbar { position:fixed; top:0; left:0; right:0; display:flex; gap:4px; align-items:center; padding:6px 12px; background:var(--panel);
                 border-bottom:1px solid var(--line); z-index:50; overflow-x:auto; }
        .pcbar:empty { display:none; }
        .pcbar button { display:inline-flex; align-items:center; gap:7px; border:none; background:none; padding:5px 11px; border-radius:6px; white-space:nowrap; color:var(--muted); }
        .pcbar button.on { background:var(--panel-2); color:var(--text); }
        .pcbar button:hover { color:var(--text); }
        .pcbar .add { margin-left:4px; color:var(--accent); }
        body.has-bar { padding-top:44px; }
        """;

    public const string PcBar = """<nav class="pcbar" id="pcbar"></nav>""";

    /// <summary>Fills the PC bar from the app's "pcs" message; clicks go back to the app.</summary>
    public const string PcBarScript = """
        const esc = s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
        chrome.webview.addEventListener("message", e => {
          const m = e.data;
          if (!m || m.type !== "pcs") return;
          const bar = document.getElementById("pcbar");
          bar.innerHTML = (m.pcs.length > 1 ? `<button data-pc="all" class="${m.current === "all" ? "on" : ""}">▦ All PCs</button>` : "") +
            m.pcs.map(p => `<button data-pc="${esc(p.id)}" class="${p.id === m.current ? "on" : ""}"><span class="dot ${p.state === "Connected" ? "ok" : p.state === "Connecting" ? "warn" : "bad"}"></span>${esc(p.name)}</button>`).join("") +
            `<button class="add" data-pc="add">+ Add PC</button>`;
          document.body.classList.add("has-bar");
          if (window.onPcs) window.onPcs(m);
        });
        document.addEventListener("click", e => {
          const b = e.target.closest("#pcbar [data-pc]");
          if (b) chrome.webview.postMessage(b.dataset.pc === "add" ? { type: "add-pc" } : { type: "switch-pc", id: b.dataset.pc });
        });
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
        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><title>SSH Manager</title><style>{{BaseCss}}
              body { min-height:100vh; display:flex; align-items:center; justify-content:center; }
              .box { max-width:580px; padding:28px 32px; background:var(--panel); border:1px solid var(--line); border-radius:12px; text-align:center; }
              .logo { background:{{(connecting ? "var(--warn)" : "var(--bad)")}}; color:#111; margin-bottom:14px; }
              h1 { font-size:17px; margin:0 0 6px; } p { color:var(--muted); margin:0 0 16px; }
              pre { text-align:left; white-space:pre-wrap; font:12px Consolas, monospace; color:var(--muted); background:var(--bg);
                    border:1px solid var(--line); border-radius:8px; padding:10px; max-height:180px; overflow:auto; }
              .buttons { display:flex; gap:8px; justify-content:center; }
              h1 .spin { vertical-align:middle; margin-right:8px; }
            </style></head><body>{{PcBar}}<div class="box">
              <div class="logo">&gt;_</div>
              <h1>{{(connecting ? "<span class=\"spin\"></span>" : "")}}{{Esc(lines[0])}}</h1>
              <p>{{(connecting ? "Opening an SSH connection." : "SSH Manager keeps retrying in the background.")}}</p>
              {{detail}}
              <div class="buttons">{{buttons}}</div>
            </div><script>{{PcBarScript}}</script></body></html>
            """;
    }

    /// <summary>All PCs at a glance. Filled and refreshed by "overview" messages from the app.</summary>
    public const string Overview = $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>SSH Manager · All PCs</title><style>{{BaseCss}}
          main { padding:20px 24px 40px; max-width:1400px; margin:0 auto; }
          h1 { font-size:18px; margin:4px 0 16px; display:flex; align-items:center; gap:10px; }
          .banner { background:color-mix(in srgb, var(--accent) 12%, var(--panel)); border:1px solid color-mix(in srgb, var(--accent) 45%, transparent);
                    border-radius:10px; padding:10px 14px; margin-bottom:16px; display:flex; align-items:center; gap:12px; }
          .banner span { flex:1; } .banner[hidden] { display:none; }
          .grid { display:grid; grid-template-columns:repeat(auto-fill, minmax(290px, 1fr)); gap:14px; }
          .card { background:var(--panel); border:1px solid var(--line); border-radius:12px; padding:14px 16px; display:flex; flex-direction:column; gap:6px; }
          .card.add { align-items:center; justify-content:center; border-style:dashed; color:var(--accent); cursor:pointer; min-height:160px; font-size:15px; }
          .head { display:flex; align-items:center; gap:8px; font-weight:600; font-size:15px; }
          .head .tag { margin-left:auto; }
          .tag { font-size:11px; padding:1px 7px; border-radius:4px; border:1px solid var(--line); color:var(--muted); font-weight:400; }
          .row { display:flex; justify-content:space-between; gap:10px; font-size:13px; } .row span:first-child { color:var(--muted); }
          .meter { height:5px; background:var(--panel-2); border-radius:3px; overflow:hidden; } .meter i { display:block; height:100%; background:var(--accent); }
          .meter i.hot { background:var(--warn); }
          .actions { display:flex; gap:6px; margin-top:6px; flex-wrap:wrap; } .actions button { padding:4px 11px; font-size:13px; }
          .msg { font-size:12.5px; color:var(--muted); white-space:pre-wrap; max-height:60px; overflow:hidden; }
          .warn-text { color:var(--bad); }
        </style></head><body>{{PcBar}}<main>
          <h1><span class="logo">&gt;_</span>All PCs</h1>
          <div class="banner" id="banner" hidden><span id="banner-text"></span><button class="primary" id="banner-btn">Install update</button></div>
          <div class="grid" id="grid"></div>
        </main><script>
          {{PcBarScript}}
          const bytes = n => { if (n == null) return ""; const u = ["B","KB","MB","GB","TB"]; let i = 0; while (n >= 1024 && i < 4) { n /= 1024; i++; } return (i ? n.toFixed(n < 10 ? 1 : 0) : n) + " " + u[i]; };
          const dur = s => s == null ? "" : s < 3600 ? Math.round(s / 60) + " min" : s < 86400 ? Math.floor(s / 3600) + " h " + Math.floor(s % 3600 / 60) + " min" : Math.floor(s / 86400) + " d " + Math.floor(s % 86400 / 3600) + " h";
          const meter = p => `<div class="meter"><i class="${p > 85 ? "hot" : ""}" style="width:${Math.min(100, p || 0)}%"></i></div>`;
          const send = (type, data) => chrome.webview.postMessage({ type, ...data });
          function card(p) {
            const st = p.state === "Connected" ? "ok" : p.state === "Connecting" ? "warn" : "bad";
            const s = p.stats || {};
            const rows = [];
            if (s.cpu != null) rows.push(`<div class="row"><span>CPU</span><span>${Math.round(s.cpu)}%</span></div>${meter(s.cpu)}`);
            if (s.ram_total) rows.push(`<div class="row"><span>RAM</span><span>${bytes(s.ram_used)} / ${bytes(s.ram_total)}</span></div>${meter(s.ram_used / s.ram_total * 100)}`);
            for (const d of (s.disks || []).slice(0, 3)) rows.push(`<div class="row"><span>${esc(d.mount)}</span><span>${bytes(d.free)} free of ${bytes(d.total)}</span></div>${meter((1 - d.free / d.total) * 100)}`);
            if (s.battery != null) rows.push(`<div class="row"><span>Battery</span><span>${s.battery}%${s.charging ? " · charging" : ""}</span></div>`);
            if (s.uptime_s != null) rows.push(`<div class="row"><span>Up</span><span>${dur(s.uptime_s)}</span></div>`);
            if (p.sessions != null) rows.push(`<div class="row"><span>SSH sessions now</span><span>${p.sessions}</span></div>`);
            if (p.failures) rows.push(`<div class="row"><span>Failed logins (24h)</span><span class="warn-text">${p.failures}</span></div>`);
            const tag = p.kind === "this" ? "this computer" : p.version ? "v" + esc(p.version) : "";
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
        </script></body></html>
        """;
}
