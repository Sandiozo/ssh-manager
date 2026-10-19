"use strict";
const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => [...el.querySelectorAll(s)];
const esc = s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const store = {
  get(k, d) { try { return JSON.parse(localStorage.getItem("sshmgr." + k)) ?? d; } catch { return d; } },
  set(k, v) { try { localStorage.setItem("sshmgr." + k, JSON.stringify(v)); } catch { } },
};

// ---------- formatting ----------
function fmtBytes(n) {
  if (n == null) return "";
  const u = ["B", "KB", "MB", "GB", "TB"]; let i = 0;
  while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
  return (i ? n.toFixed(n < 10 ? 1 : 0) : n) + " " + u[i];
}
function fmtDur(s) {
  if (s == null) return "";
  s = Math.round(s);
  if (s < 60) return s + "s";
  if (s < 3600) return Math.floor(s / 60) + "m " + (s % 60) + "s";
  if (s < 86400) return Math.floor(s / 3600) + "h " + Math.floor((s % 3600) / 60) + "m";
  return Math.floor(s / 86400) + "d " + Math.floor((s % 86400) / 3600) + "h";
}
function fmtTime(iso, withDate) {
  if (!iso) return "";
  const d = new Date(iso), now = new Date();
  const t = d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
  if (!withDate && d.toDateString() === now.toDateString()) return t;
  return d.toLocaleDateString([], { day: "numeric", month: "short" }) + " " + t.slice(0, 5);
}
function ago(iso) {
  if (!iso) return "never";
  const s = (Date.now() - new Date(iso)) / 1000;
  return s < 5 ? "just now" : fmtDur(s) + " ago";
}
function toast(msg, bad) {
  const t = $("#toast"); t.textContent = msg; t.className = "on" + (bad ? " bad" : "");
  clearTimeout(toast.h); toast.h = setTimeout(() => t.className = "", 3500);
}
const baseName = p => p.replace(/[\\/]+$/, "").split(/[\\/]/).pop() || p;

async function api(url, opts = {}) {
  const r = await fetch(url, { ...opts, headers: { "X-SSHMgr": "1", ...(opts.json ? { "Content-Type": "application/json" } : {}), ...opts.headers },
    body: opts.json ? JSON.stringify(opts.json) : opts.body });
  if (!r.ok) {
    let msg = r.statusText;
    try {
      const d = (await r.json()).detail;
      msg = Array.isArray(d) ? d.map(x => `${(x.loc || []).slice(1).join(".")}: ${x.msg}`).join("; ") : d || msg;
    } catch { }
    if (r.status === 401) toHost("auth-failed");   // the app fetches a fresh token and reconnects
    const e = new Error(msg); e.status = r.status; throw e;
  }
  return r.json();
}

// ---------- the desktop app (WebView2) ----------
// When this page runs inside the SSH Manager app, it can save downloads natively, show tray
// notifications and edit the app's own settings. In a plain browser these are simply absent.
const host = window.chrome?.webview || null;
const hostHandlers = {};
function toHost(type, data = {}) { host?.postMessage({ type, ...data }); }
host?.addEventListener("message", e => { const m = e.data; if (m && hostHandlers[m.type]) hostHandlers[m.type](m); });
function notify(kind, title, text) { toHost("notify", { kind, title, text }); }

// ---------- tabs ----------
function showTab(name) {
  $$("#tabs button").forEach(b => b.classList.toggle("active", b.dataset.tab === name));
  $("#open-settings").classList.toggle("active", name === "settings");
  $$("main > section").forEach(s => s.hidden = s.id !== "tab-" + name);
  store.set("tab", name);
  if (name === "files" && !files.loaded) files.open(store.get("path", null));
  if (name === "settings") { settingsUi.open(); showSettingsSection(store.get("setSec", "general")); }
  if (name === "run") runUi.load();
  if (name === "logs") logUi.load();
  if (name === "screen") screenUi.open(); else screenUi.stop();
  if (name === "sync") toHost("sync-get");
}
$("#tabs").onclick = e => { const b = e.target.closest("[data-tab]"); if (b) showTab(b.dataset.tab); };
$("#open-settings").onclick = () => showTab("settings");

// settings: one section at a time
function showSettingsSection(name) {
  if (!$(`#set-nav [data-sec="${name}"]`) || $(`#set-nav [data-sec="${name}"]`).hidden) name = "general";
  $$("#set-nav button").forEach(b => b.classList.toggle("active", b.dataset.sec === name));
  $$(".set-sec").forEach(s => s.classList.toggle("on", s.dataset.sec === name));
  store.set("setSec", name);
}
$("#set-nav").onclick = e => { const b = e.target.closest("[data-sec]"); if (b) showSettingsSection(b.dataset.sec); };
document.addEventListener("click", e => { const g = e.target.closest("[data-goto]"); if (g) showTab(g.dataset.goto); });

// ---------- status ----------
let last = null, viewer = null, hostName = "";
function dot(state) { return `<span class="dot ${state}"></span>`; }
function meter(pct) { return `<div class="meter"><i class="${pct > 85 ? "hot" : ""}" style="width:${Math.min(100, pct)}%"></i></div>`; }

// CPU / RAM / disks / battery that the SSH Manager app on another computer reports about itself
function clientStats(c) {
  if (!c) return "";
  const rows = [];
  if (c.cpu != null) rows.push(`<div class="row"><span>CPU</span><span>${Math.round(c.cpu)}%</span></div>${meter(c.cpu)}`);
  if (c.ram_total) rows.push(`<div class="row"><span>RAM</span><span>${fmtBytes(c.ram_used)} / ${fmtBytes(c.ram_total)}</span></div>${meter(c.ram_used / c.ram_total * 100)}`);
  for (const d of (c.disks || []).slice(0, 2)) rows.push(`<div class="row"><span>${esc(d.mount)}</span><span>${fmtBytes(d.free)} free of ${fmtBytes(d.total)}</span></div>${meter((1 - d.free / d.total) * 100)}`);
  if (c.battery != null) rows.push(`<div class="row"><span>Battery</span><span>${c.battery}%${c.charging ? " · charging" : ""}</span></div>`);
  if (c.uptime_s != null) rows.push(`<div class="row"><span>Up</span><span>${fmtDur(c.uptime_s)}</span></div>`);
  return rows.join("");
}

function renderStatus(s) {
  last = s;
  hostName = s.host.name;
  const sshOk = s.sshd.status === "running" && s.sshd.listening;
  const dc = s.desktop_commander;
  document.title = `SSH Manager · ${s.host.name}`;
  $("#title").textContent = s.host.name;
  $("#hostline").textContent = `${s.host.ips.join(", ")} · up ${fmtDur(s.host.uptime_s)}`;
  $("#viewer-chip").innerHTML = viewer && viewer !== s.host.name ? `<span class="chip">${dot("ok")}you're on ${esc(viewer)}</span>` : "";
  $("#lane-right").textContent = s.host.name;
  $("#drop-host").textContent = s.host.name;

  // status in the sidebar footer
  const pill = (state, text, title) => `<div class="pill" title="${esc(title || text)}">${dot(state)}<span class="t">${esc(text)}</span></div>`;
  const pills = [pill(sshOk ? "ok" : "bad", sshOk ? "SSH server running" : `SSH server ${s.sshd.status}`)];
  if (dc.installed) pills.push(pill(dc.online ? "ok" : "", `Desktop Commander ${dc.state}`));
  pills.push(`<div class="pill muted small"><span class="t">v${esc(s.version || "")}</span></div>`);
  $("#pills").innerHTML = pills.join("");

  const u = s.update || {};
  const upd = u.status && u.status !== "idle" ? `<div class="alert update"><span>${u.status === "downloading" ? "Downloading" : "Installing"} SSH Manager ${esc(u.latest)}… the page reconnects by itself.</span></div>`
    : u.available ? `<div class="alert update"><span><b>SSH Manager ${esc(u.latest)} is available</b> on ${esc(s.host.name)} (it has ${esc(u.current)}).
        ${u.installable ? "" : "This copy runs from source: update it with <code>git pull</code>."}</span>
        ${u.installable ? `<button class="primary" data-update-now>Install now</button>` : ""}</div>` : "";
  $("#notice").innerHTML = upd + (s.elevated ? "" : `<div class="alert warnish"><b>Running without admin rights.</b>
    SSH commands from an admin account run elevated, so their contents show as "hidden", and SSH keys can't be managed,
    until SSH Manager runs as admin. Reinstall SSH Manager on this PC as "Client and server" to fix it.</div>`);

  // KPI tiles
  const ram = s.host.ram;
  const kpi = (label, value, sub, extra = "", cls = "") =>
    `<div class="kpi"><div class="kpi-label">${label}</div><div class="kpi-value ${cls}">${value}</div>${sub ? `<div class="kpi-sub">${sub}</div>` : ""}${extra}</div>`;
  $("#kpis").innerHTML = [
    kpi(`${dot(sshOk ? "ok" : "bad")}SSH server`, sshOk ? "Running" : esc(s.sshd.status), `port 22 · ${esc(s.sshd.start_type || "")}`),
    kpi("Sessions now", s.sessions.length, s.sessions.length ? esc([...new Set(s.sessions.map(x => x.client))].join(", ")) : "nobody connected"),
    kpi("Logins · 24 h", s.logins_24h, `${s.unknown_clients.length ? s.unknown_clients.length + " unknown computer(s)" : "all from known computers"}`),
    kpi("Failed logins · 24 h", s.failures_24h.length, s.failures_24h.length ? `last from ${esc(s.failures_24h.at(-1).ip)}` : "none", "", s.failures_24h.length ? "bad" : ""),
    kpi("CPU", `${s.host.cpu.toFixed(0)}%`, "", meter(s.host.cpu)),
    kpi("Memory", `${ram.percent.toFixed(0)}%`, `${fmtBytes(ram.used)} of ${fmtBytes(ram.total)}`, meter(ram.percent)),
  ].join("");

  // computers: this PC, the computers that connect to it, and Desktop Commander if installed
  const disks = s.host.disks.map(d => `<div class="row"><span>${esc(d.mount)}</span><span>${fmtBytes(d.free)} free of ${fmtBytes(d.total)}</span></div>${meter(d.percent)}`).join("");
  const cards = [
    `<div class="card"><div class="head">${dot("ok")}${esc(s.host.name)}<span class="tag blue">this PC · server</span></div>
      <div class="row"><span>Uptime</span><span>${fmtDur(s.host.uptime_s)}</span></div>${disks}</div>`,
    ...s.peers.map(p => `<div class="card"><div class="head">${dot(p.connected ? "ok" : p.online ? "warn" : "bad")}${esc(p.name)}${p.stats ? `<span class="tag green">app</span>` : ""}</div>
      <div class="big">${p.connected ? "Connected" : p.online ? "Online · not connected" : "Offline"}</div>
      ${clientStats(p.stats)}
      <div class="row"><span>Address</span><span>${esc(p.resolved_ip || p.ip || "—")}</span></div>
      <div class="row"><span>Ping · last login</span><span>${p.online ? (p.latency_ms ?? "<1") + " ms" : p.online === false ? "no reply" : "…"} · ${ago(p.last_login)}</span></div>
      ${!p.online && (p.mac || p.stats?.macs?.[0]) ? `<div><button data-wake="${esc(p.mac || p.stats.macs[0])}" data-ip="${esc(p.ip || "")}">Wake up</button></div>` : ""}</div>`),
    ...(s.other_apps || []).map(c => `<div class="card"><div class="head">${dot("ok")}${esc(c.machine)}<span class="tag green">app</span></div>
      ${clientStats(c)}<div class="muted small">Not in this PC's computer list (Settings → Computers).</div></div>`),
    !dc.installed ? "" : `<div class="card"><div class="head">${dot(dc.online ? "ok" : "")}Desktop Commander<span class="tag">${dc.online ? "online" : "offline"}</span></div>
      <div class="row"><span>Last heartbeat</span><span>${dc.heartbeat ? ago(dc.heartbeat) : "—"}</span></div>
      <div class="muted small">A second way in that doesn't go through SSH. Its commands don't show up here.</div></div>`,
  ];
  $("#cards").innerHTML = cards.filter(Boolean).join("");

  // sessions
  $("#session-count").textContent = s.sessions.length ? `${s.sessions.length} open` : "";
  // one compact row per session; its running commands underneath
  $("#sessions").innerHTML = s.sessions.length ? s.sessions.map(x => {
    const cmds = x.commands.filter(c => c.kind !== "dashboard");
    const isApp = x.commands.some(c => c.kind === "dashboard");
    return `<div class="session">
      <div class="line" title="${esc(x.user || "?")}@${esc(x.ip)}:${x.port}${x.key ? " · " + esc(x.key) : ""}">
        ${dot("ok")}<span class="who">${esc(x.client)}</span>
        ${isApp ? `<span class="tag blue">app</span>` : x.tunnel ? `<span class="tag">port forward</span>` : ""}
        <span class="what">${cmds.length ? "" : isApp ? "SSH Manager app" : x.tunnel ? "Port forward (script or tool)" : "idle"}</span>
        <span class="muted small">${x.start ? ago(x.start) : ""}</span>
      </div>
      ${cmds.map(c => `<div class="cmd" data-running="${c.pid}">
          <span class="kind ${c.kind}">${esc(c.kind)}</span><code>${esc(c.summary)}</code>
          <span class="muted small">${fmtDur((Date.now() - new Date(c.start)) / 1000)}</span>
        </div>`).join("")}
    </div>`;
  }).join("") : `<div class="none">No one is connected over SSH right now.</div>`;

  // hourly bars
  const max = Math.max(1, ...s.hours.map(h => h.count));
  $("#login-total").textContent = `${s.logins_24h} logins`;
  $("#hours").innerHTML = s.hours.map((h, i) => {
    const d = new Date(h.hour);
    return `<div class="b" title="${d.getHours()}:00 — ${h.count} logins"><i style="height:${h.count / max * 100}%"></i>${i % 3 === 0 ? `<span>${d.getHours()}</span>` : ""}</div>`;
  }).join("");

  // connection history
  $("#history").innerHTML = s.history.slice().reverse().map(c => {
    const dur = c.end ? (new Date(c.end) - new Date(c.start)) / 1000 : (Date.now() - new Date(c.start)) / 1000;
    return `<tr><td>${esc(c.client)} <span class="muted">${esc(c.ip)}</span></td><td>${esc(c.user)}</td>
      <td class="muted">${esc(c.key || c.method)}</td><td>${fmtTime(c.start, true)}</td>
      <td>${fmtDur(dur)}</td><td class="muted">${c.end ? fmtTime(c.end) : '<span style="color:var(--ok)">open</span>'}</td></tr>`;
  }).join("") || `<tr><td colspan="6" class="muted">No connections in the event log.</td></tr>`;
  $("#failures").innerHTML = s.failures_24h.length
    ? `<div class="alert"><b>${s.failures_24h.length} failed login attempt(s) in the last 24 hours</b>
        ${s.failures_24h.slice(-10).reverse().map(f => `<div class="small">${fmtTime(f.ts, true)} · ${esc(f.ip)} · ${esc(f.user)} · ${esc(f.method)}</div>`).join("")}</div>`
    : `<div class="alert okish">${dot("ok")} No failed login attempts in the last 24 hours.</div>`;

  watchForAlerts(s);
  if (!$("#tab-settings").hidden) settingsUi.renderPeers();
}

// tray notifications for things worth interrupting for (the app decides whether to show them)
let seenFailures = null, seenUnknown = null;
function watchForAlerts(s) {
  const fails = s.failures_24h.map(f => f.ts + f.ip);
  const unknown = s.unknown_clients.map(u => u.ip + u.last);
  if (seenFailures) {
    const fresh = s.failures_24h.filter(f => !seenFailures.has(f.ts + f.ip));
    if (fresh.length) notify("failed-login", "Failed SSH login", `${fresh.length} failed attempt(s) on ${s.host.name}, last from ${fresh.at(-1).ip} as "${fresh.at(-1).user}".`);
    const newUnknown = s.unknown_clients.filter(u => !seenUnknown.has(u.ip + u.last));
    if (newUnknown.length) notify("unknown", "Unknown computer logged in", `${newUnknown[0].ip} logged in to ${s.host.name} as ${newUnknown[0].user}. Add it under Settings → Computers if you know it.`);
  }
  seenFailures = new Set(fails);
  seenUnknown = new Set(unknown);
}

$("#sessions").onclick = e => {
  const c = e.target.closest("[data-running]");
  if (!c || !last) return;
  // running commands only carry the summary; the full script lands in Activity when it ends
  const pid = +c.dataset.running;
  for (const s of last.sessions) for (const x of s.commands)
    if (x.pid === pid) showDetail(`Running · PID ${pid}`, `${s.client} · started ${fmtTime(x.start, true)} · ${x.children.join(", ")}`, x.summary);
};

let pageReady = false;
async function pollStatus() {
  try {
    renderStatus(await api("/api/status"));
    if (!pageReady) { pageReady = true; toHost("page-ready"); }   // the app shows this page once it's drawn
  }
  catch (e) { $("#pills").innerHTML = `<span class="pill">${dot("bad")}Dashboard unreachable</span>`; }
  setTimeout(pollStatus, 3000);
}

// ---------- activity ----------
let actItems = [], actAfter = 0, actKind = "", actQuery = "";
function actLabel(a) {
  if (a.kind === "command") return { kind: a.type, text: a.summary };
  if (a.kind === "action") return { kind: "run", text: `${a.title}${a.status && a.status !== "done" ? ` (${a.status})` : ""}` };
  if (a.kind === "setup") {
    const t = { settings: `Changed settings (${(a.changed || []).join(", ")})`, "key-added": `Allowed SSH key ${a.label || a.fingerprint}`,
      "key-removed": `Removed SSH key ${a.label || a.fingerprint}` }[a.action] || a.action;
    return { kind: "setup", text: t };
  }
  const p = a.path || (a.paths || []).join(", ");
  const verbs = { upload: "Received", download: "Sent", "download-zip": "Sent (zip)", mkdir: "Created folder", rename: "Renamed", delete: "Deleted" };
  return { kind: "file", text: `${verbs[a.action] || a.action} ${p}${a.to ? " → " + a.to : ""}${a.size != null ? " (" + fmtBytes(a.size) + ")" : ""}` };
}
function actRow(a) {
  const l = actLabel(a);
  return `<div class="act" data-id="${a.id}"><span class="time">${fmtTime(a.ts)}</span>
    <span class="kind ${l.kind}">${esc(l.kind)}</span><span class="client muted small">${esc(a.client || "")}</span>
    <span class="what" title="${esc(l.text)}">${esc(l.text)}</span><span class="dur muted small">${a.duration_s != null ? fmtDur(a.duration_s) : ""}</span></div>`;
}
function renderActivity() {
  const q = actQuery.toLowerCase();
  const items = actItems.filter(a => (!actKind || a.kind === actKind) &&
    (!q || JSON.stringify(a).toLowerCase().includes(q))).slice(-300).reverse();
  $("#activity").innerHTML = items.map(actRow).join("") || `<div class="none">Nothing yet. Commands sent over SSH and file transfers will show up here.</div>`;
  $("#recent").innerHTML = actItems.filter(a => a.type !== "dashboard").slice(-8).reverse().map(actRow).join("") || `<div class="none">No activity recorded yet.</div>`;
}
async function pollActivity() {
  try {
    const { items } = await api(`/api/activity?after=${actAfter}&limit=2000`);
    if (items.length) { actItems.push(...items); actAfter = items.at(-1).id; actItems = actItems.slice(-3000); renderActivity(); }
  } catch { }
  setTimeout(pollActivity, 3000);
}
$("#act-filter").onclick = e => {
  if (e.target.dataset.kind === undefined) return;
  actKind = e.target.dataset.kind;
  $$("#act-filter button").forEach(b => b.classList.toggle("active", b === e.target));
  renderActivity();
};
$("#act-search").oninput = e => { actQuery = e.target.value; renderActivity(); };
function onActClick(e) {
  const row = e.target.closest(".act"); if (!row) return;
  const a = actItems.find(x => x.id === +row.dataset.id); if (!a) return;
  if (a.kind === "command")
    showDetail(`${a.type} · ${a.process} · PID ${a.pid}`, `${a.client} (${a.ip || "?"}) · ${fmtTime(a.ts, true)} · ran ${fmtDur(a.duration_s)}`,
      a.script + (a.output != null ? "\n\n──────── output ────────\n" + (a.output || "(no output)") : ""));
  else showDetail(`${a.kind === "setup" ? "Setup" : "File"} · ${a.action}`, `${a.client} · ${fmtTime(a.ts, true)}`, JSON.stringify(a, null, 2));
}
$("#activity").onclick = onActClick;
$("#recent").onclick = onActClick;

function showDetail(title, meta, body) {
  $("#detail-title").textContent = title; $("#detail-meta").textContent = meta; $("#detail-body").textContent = body;
  $("#detail").showModal();
}
$("#detail-close").onclick = () => $("#detail").close();
$("#detail").onclick = e => { if (e.target === $("#detail")) $("#detail").close(); };

// ---------- files ----------
const files = {
  path: null, items: [], sel: new Set(), loaded: false, anchor: null,
  async loadRoots() {
    const r = await api("/api/fs/roots");
    $("#bookmarks").innerHTML = r.bookmarks.map(b => `<a data-path="${esc(b.path)}" class="${b.exists ? "" : "missing"}" title="${esc(b.path)}">${esc(b.name)}</a>`).join("")
      || `<div class="muted small" style="padding:4px 8px">None yet. Use ☆ to add one.</div>`;
    $("#drives").innerHTML = r.drives.map(d => `<a data-path="${esc(d.path)}">${esc(d.name)} <small>${fmtBytes(d.free)} free</small></a>`).join("");
    if (!this.path) this.open(r.bookmarks.find(b => b.exists)?.path || r.drives[0]?.path);
    else this.markActive();
  },
  async open(path) {
    this.loaded = true;
    if (!path) return this.loadRoots();
    try {
      const r = await api("/api/fs/list?path=" + encodeURIComponent(path));
      this.path = r.path; this.parent = r.parent; this.items = r.items; this.sel.clear();
      store.set("path", r.path);
      this.render();
    } catch (e) { toast(e.message, true); if (!this.path) this.loadRoots(); }
    if (!$("#drives").innerHTML) this.loadRoots();
  },
  visible() { return this.items.filter(i => $("#fs-hidden").checked || !i.hidden); },
  markActive() {
    $$(".fs-side a").forEach(a => a.classList.toggle("active", !!this.path && a.dataset.path.toLowerCase() === this.path.toLowerCase()));
    const starred = settingsUi.saved?.bookmarks.some(b => b.path.toLowerCase() === (this.path || "").toLowerCase());
    $("#fs-star").textContent = starred ? "★" : "☆";
  },
  render() {
    $("#fs-path").value = this.path;
    this.markActive();
    const vis = this.visible();
    $("#fs-body").innerHTML = vis.map(i => `<tr draggable="true" data-path="${esc(i.path)}" class="${this.sel.has(i.path) ? "sel" : ""} ${i.hidden ? "hidden-file" : ""}">
      <td class="cb"><input type="checkbox" ${this.sel.has(i.path) ? "checked" : ""}></td>
      <td class="name"><span class="ic">${i.dir ? "📁" : "📄"}</span>${esc(i.name)}</td>
      <td class="num">${i.dir ? "" : fmtBytes(i.size)}</td><td class="muted">${fmtTime(i.mtime, true)}</td></tr>`).join("");
    $("#fs-empty").hidden = vis.length > 0;
    this.updateButtons();
  },
  updateButtons() {
    const n = this.sel.size;
    $("#fs-download").disabled = !n; $("#fs-delete").disabled = !n; $("#fs-rename").disabled = n !== 1;
    $("#fs-log").disabled = !(n === 1 && !this.items.find(i => this.sel.has(i.path))?.dir);
    $("#fs-all").checked = n > 0 && n === this.visible().length;
    const size = this.items.filter(i => this.sel.has(i.path) && !i.dir).reduce((a, i) => a + i.size, 0);
    $("#fs-selinfo").textContent = n ? `${n} selected${size ? " · " + fmtBytes(size) : ""}` : "";
    $$("#fs-body tr").forEach(tr => { const on = this.sel.has(tr.dataset.path); tr.classList.toggle("sel", on); tr.querySelector("input").checked = on; });
  },
  downloadUrl(paths) { return "/api/fs/download?" + paths.map(p => "path=" + encodeURIComponent(p)).join("&"); },
  download(paths) {
    const a = document.createElement("a");
    a.href = this.downloadUrl(paths);
    a.download = ""; document.body.appendChild(a); a.click(); a.remove();
    if (!host) toast("Download started");
  },
};

$(".fs-side").onclick = e => { const a = e.target.closest("a[data-path]"); if (a) files.open(a.dataset.path); };
$("#fs-up").onclick = () => files.parent && files.open(files.parent);
$("#fs-refresh").onclick = () => files.open(files.path);
$("#fs-path").onkeydown = e => { if (e.key === "Enter") files.open(e.target.value.trim()); };
$("#fs-hidden").onchange = () => files.render();
$("#fs-star").onclick = async () => {
  if (!files.path || !settingsUi.saved) return;
  const s = structuredClone(settingsUi.saved);
  const i = s.bookmarks.findIndex(b => b.path.toLowerCase() === files.path.toLowerCase());
  if (i >= 0) s.bookmarks.splice(i, 1);
  else s.bookmarks.push({ name: baseName(files.path), path: files.path });
  try { await settingsUi.save(s); toast(i >= 0 ? "Removed from shortcuts" : "Added to shortcuts"); }
  catch (e) { toast(e.message, true); }
};
$("#fs-all").onchange = e => { files.sel = new Set(e.target.checked ? files.visible().map(i => i.path) : []); files.updateButtons(); };
$("#fs-body").onclick = e => {
  const tr = e.target.closest("tr"); if (!tr) return;
  const p = tr.dataset.path, vis = files.visible().map(i => i.path);
  if (e.target.type === "checkbox" || e.ctrlKey || e.metaKey) files.sel.has(p) ? files.sel.delete(p) : files.sel.add(p);
  else if (e.shiftKey && files.anchor) {
    const [a, b] = [vis.indexOf(files.anchor), vis.indexOf(p)].sort((x, y) => x - y);
    vis.slice(a, b + 1).forEach(x => files.sel.add(x));
  } else files.sel = new Set([p]);
  files.anchor = p; files.updateButtons();
};
$("#fs-body").ondblclick = e => {
  const tr = e.target.closest("tr"); if (!tr) return;
  const item = files.items.find(i => i.path === tr.dataset.path);
  item.dir ? files.open(item.path) : files.download([item.path]);
};
// drag rows out of the window (to Explorer / the desktop) to download them
$("#fs-body").addEventListener("dragstart", e => {
  const tr = e.target.closest("tr"); if (!tr) return;
  if (!files.sel.has(tr.dataset.path)) { files.sel = new Set([tr.dataset.path]); files.updateButtons(); }
  const paths = [...files.sel];
  const single = paths.length === 1 && !files.items.find(i => i.path === paths[0])?.dir;
  const name = single ? baseName(paths[0]) : (paths.length === 1 ? baseName(paths[0]) : `${baseName(files.path)}-${paths.length}-items`) + ".zip";
  e.dataTransfer.effectAllowed = "copy";
  e.dataTransfer.setData("DownloadURL", `${single ? "application/octet-stream" : "application/zip"}:${name}:${location.origin}${files.downloadUrl(paths)}`);
  e.dataTransfer.setData("text/plain", paths.join("\n"));
});
$("#fs-download").onclick = () => files.download([...files.sel]);
$("#fs-mkdir").onclick = async () => {
  const name = prompt("New folder name:"); if (!name) return;
  try { await api("/api/fs/mkdir", { method: "POST", json: { path: files.path + "\\" + name } }); files.open(files.path); }
  catch (e) { toast(e.message, true); }
};
$("#fs-rename").onclick = async () => {
  const p = [...files.sel][0], item = files.items.find(i => i.path === p);
  const name = prompt("Rename to:", item.name); if (!name || name === item.name) return;
  try { await api("/api/fs/rename", { method: "POST", json: { path: p, new_name: name } }); files.open(files.path); }
  catch (e) { toast(e.message, true); }
};
$("#fs-delete").onclick = async () => {
  const paths = [...files.sel];
  if (!confirm(`Move ${paths.length} item(s) to the Recycle Bin on ${hostName || "the PC"}?\n\n` + paths.map(baseName).join("\n"))) return;
  try { await api("/api/fs/delete", { method: "POST", json: { paths } }); toast("Moved to Recycle Bin"); files.open(files.path); }
  catch (e) { toast(e.message, true); }
};
document.addEventListener("keydown", e => {
  if ($("#tab-files").hidden || e.target.matches("input, textarea") || $("#detail").open) return;
  if (e.key === "Delete" && files.sel.size) $("#fs-delete").click();
  else if (e.key === "Backspace") $("#fs-up").click();
  else if (e.key === "F2" && files.sel.size === 1) $("#fs-rename").click();
  else if (e.key === "a" && (e.ctrlKey || e.metaKey)) { e.preventDefault(); $("#fs-all").checked = true; $("#fs-all").onchange({ target: $("#fs-all") }); }
});

// ---------- transfers (dock with animation) ----------
// One "batch" is everything started since the dock was last empty; uploads go one file at a
// time over PUT, downloads are reported by the app (WebView2 download events).
const xfer = {
  items: [], seq: 0, uploading: false, hideTimer: null, speed: 0, lastBytes: 0, lastTime: 0, showList: false,
  batch() { return this.items.filter(t => !t.cleared); },
  add(t) {
    if (this.batch().length && this.batch().every(x => ["done", "error", "skipped"].includes(x.status)))
      this.items.forEach(x => x.cleared = true);   // previous batch finished: start a fresh one
    t.id = ++this.seq; this.items.push(t);
    clearTimeout(this.hideTimer);
    $("#dock").hidden = false;
    this.draw();
    return t;
  },
  draw() {
    if (this.drawPending) return;
    this.drawPending = requestAnimationFrame(() => { this.drawPending = null; this.paint(); });
  },
  paint() {
    const b = this.batch();
    if (!b.length) return;
    const total = b.reduce((a, t) => a + (t.size || 0), 0);
    const done = b.reduce((a, t) => a + (["done"].includes(t.status) ? t.size || 0 : t.loaded || 0), 0);
    const active = b.filter(t => t.status === "active");
    const pending = b.filter(t => ["queued", "active"].includes(t.status));
    const ups = b.filter(t => t.dir === "up"), downs = b.filter(t => t.dir === "down");
    const now = performance.now();
    if (now - this.lastTime > 400) {
      const inst = (done - this.lastBytes) / ((now - this.lastTime) / 1000);
      if (this.lastTime && inst >= 0) this.speed = this.speed ? this.speed * 0.6 + inst * 0.4 : inst;
      this.lastBytes = done; this.lastTime = now;
    }
    const finished = !pending.length;
    const failed = b.filter(t => t.status === "error").length;
    $("#flow-up").classList.toggle("on", active.some(t => t.dir === "up"));
    $("#flow-down").classList.toggle("on", active.some(t => t.dir === "down"));
    $("#done-mark").className = "done-mark" + (finished ? " on" + (failed ? " bad" : "") : "");
    $("#done-mark").textContent = failed ? "!" : "✓";
    $("#lane-left").textContent = viewer || "This computer";
    const verb = ups.length && downs.length ? "Transferring" : downs.length ? (finished ? "Received" : "Receiving") : (finished ? "Sent" : "Sending");
    const where = downs.length && !ups.length ? `from ${hostName}` : `to ${hostName}`;
    const doneCount = b.filter(t => t.status === "done").length;
    $("#dock-title").textContent = `${verb} ${b.length} file${b.length === 1 ? "" : "s"} ${where}`;
    $("#dock-file").textContent = finished ? (failed ? `${failed} failed — open the list for details` : "All done")
      : (active[0] ? (active[0].dir === "up" ? "↑ " : "↓ ") + (active[0].rel || active[0].name) : "Waiting…");
    $("#dock-bar").style.width = (total ? done / total * 100 : finished ? 100 : 0) + "%";
    const left = this.speed > 0 && !finished ? (total - done) / this.speed : null;
    $("#dock-stats").innerHTML = `<span>${doneCount}/${b.length} files · ${fmtBytes(done)} of ${fmtBytes(total)}</span>
      <span>${finished ? "" : `${fmtBytes(this.speed)}/s${left != null && left < 86400 ? " · " + fmtDur(left) + " left" : ""}`}</span>`;
    if (this.showList) {
      $("#dock-list").innerHTML = b.slice().reverse().map(t => {
        const st = { queued: "waiting", active: (t.size ? Math.round((t.loaded || 0) / t.size * 100) : 0) + "%", done: "done",
          error: "failed", skipped: "skipped", conflict: "exists" }[t.status];
        const show = t.dir === "down" && t.status === "done" && t.savedPath && host ? ` · <a data-show="${esc(t.savedPath)}">show</a>` : "";
        return `<div class="xf"><span>${t.dir === "up" ? "↑" : "↓"}</span><span class="n" title="${esc(t.dest || t.savedPath || "")}">${esc(t.rel || t.name)}</span>
          <span class="st ${t.status === "done" ? "ok" : t.status === "error" ? "bad" : ""}" title="${esc(t.error || "")}">${st}${show}</span></div>`;
      }).join("");
    }
    if (finished && !this.finishedNotified) {
      this.finishedNotified = true;
      notify("transfers", failed ? "Transfer finished with errors" : "Transfer finished",
        `${$("#dock-title").textContent}${failed ? ` (${failed} failed)` : ""}.`);
      clearTimeout(this.hideTimer);
      if (!failed) this.hideTimer = setTimeout(() => { if (!this.showList && !$("#dock").matches(":hover")) $("#dock").hidden = true; }, 6000);
      if (files.path && ups.length) files.open(files.path);
    } else if (!finished) this.finishedNotified = false;
  },
};
$("#dock-close").onclick = () => { $("#dock").hidden = true; };
$("#dock-more").onclick = () => { xfer.showList = !xfer.showList; $("#dock-list").hidden = !xfer.showList; xfer.paint(); };
$("#dock-list").onclick = e => { const a = e.target.closest("[data-show]"); if (a) toHost("show-file", { path: a.dataset.show }); };

function joinPath(dir, rel) { return dir.replace(/[\\/]+$/, "") + "\\" + rel.replace(/\//g, "\\"); }
function sendFiles(entries, dir) {
  if (!entries.length) return;
  for (const { file, rel } of entries)
    xfer.add({ dir: "up", file, rel, name: file.name, size: file.size, loaded: 0, dest: joinPath(dir, rel), status: "queued", overwrite: false });
  runUploads();
}
function put(t) {
  return new Promise(resolve => {
    const x = new XMLHttpRequest();
    x.open("PUT", `/api/fs/upload?path=${encodeURIComponent(t.dest)}&overwrite=${t.overwrite ? 1 : 0}&mtime=${t.file.lastModified / 1000}`);
    x.setRequestHeader("X-SSHMgr", "1");
    x.upload.onprogress = e => { t.loaded = e.loaded; xfer.draw(); };
    x.onload = () => {
      if (x.status === 409 && !t.overwrite) t.status = "conflict";
      else if (x.status >= 200 && x.status < 300) { t.status = "done"; t.loaded = t.size; }
      else { t.status = "error"; try { t.error = JSON.parse(x.responseText).detail; } catch { t.error = x.statusText; } }
      resolve();
    };
    x.onerror = () => { t.status = "error"; t.error = "network error"; resolve(); };
    t.status = "active"; xfer.draw(); x.send(t.file);
  });
}
async function runUploads() {
  if (xfer.uploading) return;
  xfer.uploading = true;
  let t;
  while ((t = xfer.items.find(q => q.dir === "up" && q.status === "queued"))) { await put(t); xfer.draw(); }
  const conflicts = xfer.items.filter(q => q.status === "conflict");
  xfer.uploading = false;
  if (conflicts.length) {
    const yes = confirm(`${conflicts.length} file(s) already exist on ${hostName}:\n\n${conflicts.slice(0, 15).map(c => c.rel).join("\n")}${conflicts.length > 15 ? "\n…" : ""}\n\nReplace them?`);
    conflicts.forEach(c => { c.overwrite = yes; c.status = yes ? "queued" : "skipped"; });
    xfer.draw();
    if (yes) return runUploads();
  }
}
$("#fs-upload-files").onclick = () => $("#pick-files").click();
$("#fs-upload-folder").onclick = () => $("#pick-folder").click();
$("#pick-files").onchange = e => { sendFiles([...e.target.files].map(f => ({ file: f, rel: f.name })), files.path); e.target.value = ""; };
$("#pick-folder").onchange = e => { sendFiles([...e.target.files].map(f => ({ file: f, rel: f.webkitRelativePath || f.name })), files.path); e.target.value = ""; };

// downloads saved by the app report progress here
hostHandlers.download = m => {
  let t = xfer.items.find(x => x.hostId === m.id);
  if (!t) t = xfer.add({ dir: "down", hostId: m.id, name: m.name, rel: m.name, size: m.total || 0, loaded: 0, status: "active" });
  t.size = m.total || t.size; t.loaded = m.received;
  t.savedPath = m.path;
  t.status = m.state === "completed" ? "done" : m.state === "interrupted" ? "error" : "active";
  if (m.state === "interrupted") t.error = m.reason || "interrupted";
  if (t.status === "done") t.loaded = t.size = Math.max(t.size, m.received);
  xfer.draw();
};

// ---------- drop anywhere ----------
let dragDepth = 0, dropFolder = null;
const hasFiles = e => [...(e.dataTransfer?.types || [])].includes("Files");
function dropDest() {
  if (dropFolder) return dropFolder;
  if (files.loaded && files.path) return files.path;
  return settingsUi.saved?.default_upload || "%USERPROFILE%\\Desktop";
}
function markDropRow(tr) {
  $$("#fs-body tr.drop-target").forEach(r => r !== tr && r.classList.remove("drop-target"));
  tr?.classList.add("drop-target");
}
document.addEventListener("dragenter", e => {
  if (!hasFiles(e)) return;
  e.preventDefault();
  if (dragDepth++ === 0) $("#dropzone").classList.add("on");
});
document.addEventListener("dragover", e => {
  if (!hasFiles(e)) return;
  e.preventDefault();
  e.dataTransfer.dropEffect = "copy";
  const tr = !$("#tab-files").hidden && e.target.closest?.("#fs-body tr");
  const item = tr && files.items.find(i => i.path === tr.dataset.path && i.dir);
  dropFolder = item ? item.path : null;
  markDropRow(item ? tr : null);
  $("#drop-path").textContent = dropDest();
});
document.addEventListener("dragleave", e => {
  if (!hasFiles(e)) return;
  if (--dragDepth <= 0) { dragDepth = 0; $("#dropzone").classList.remove("on"); markDropRow(null); dropFolder = null; }
});
document.addEventListener("drop", async e => {
  if (!hasFiles(e)) return;
  e.preventDefault();
  const dest = dropDest();
  dragDepth = 0; dropFolder = null; $("#dropzone").classList.remove("on"); markDropRow(null);
  const roots = [...e.dataTransfer.items].map(i => i.webkitGetAsEntry?.()).filter(Boolean);
  const out = [];
  async function walk(entry, prefix) {
    if (entry.isFile) {
      const file = await new Promise((res, rej) => entry.file(res, rej));
      out.push({ file, rel: prefix + file.name });
    } else if (entry.isDirectory) {
      const reader = entry.createReader();
      let batch;
      do {
        batch = await new Promise((res, rej) => reader.readEntries(res, rej));
        for (const child of batch) await walk(child, prefix + entry.name + "/");
      } while (batch.length);
    }
  }
  for (const r of roots) await walk(r, "");
  if (!out.length) return toast("Nothing to send (empty folder?)", true);
  sendFiles(out, dest);
});

// ---------- settings ----------
const settingsUi = {
  saved: null, draft: null, keys: null,
  async load() {
    this.saved = await api("/api/settings");
    this.draft = structuredClone(this.saved);
    return this.saved;
  },
  async open() {
    try { if (!this.saved || !this.dirty()) await this.load(); } catch (e) { toast(e.message, true); }
    this.render();
    this.loadKeys();
    if (host) toHost("get-app-settings");
  },
  dirty() { return JSON.stringify(this.saved) !== JSON.stringify(this.draft); },
  touch() { $("#savebar").hidden = !this.dirty(); },
  async save(next) {
    const body = next || this.draft;
    this.saved = await api("/api/settings", { method: "PUT", json: {
      name: body.name, peers: body.peers, keys: body.keys, bookmarks: body.bookmarks, default_upload: body.default_upload,
      auto_update: body.auto_update !== false, actions: body.actions || [], logs: body.logs || [],
      screen_preview: body.screen_preview !== false, screen_monitors: body.screen_monitors !== false,
      screen_apps: body.screen_apps || [] } });
    if (!$("#tab-run").hidden) runUi.load();
    this.draft = structuredClone(this.saved);
    this.touch();
    if (!$("#tab-settings").hidden) this.render();
    files.loadRoots().catch(() => { });
  },
  render() {
    const d = this.draft; if (!d) return;
    $("#set-name").value = d.name; $("#set-name").placeholder = d.computer_name;
    $("#set-upload").value = d.default_upload;
    $("#set-autoupdate").checked = d.auto_update !== false;
    $("#set-screen").checked = d.screen_preview !== false;
    $("#set-screen-monitors").checked = d.screen_monitors !== false;
    $("#set-screen-apps").value = (d.screen_apps || []).join(", ");
    $("#set-screen-monitors").disabled = $("#set-screen-apps").disabled = d.screen_preview === false;
    renderUpdate();
    renderActionEditor();
    renderLogEditor();
    this.renderPeers(true);
    this.renderKeys();
    $("#set-shortcuts").innerHTML = d.bookmarks.map((b, i) => `<tr data-i="${i}">
      <td><input data-f="name" value="${esc(b.name)}"></td><td style="width:60%"><input data-f="path" value="${esc(b.path)}" spellcheck="false"></td>
      <td style="white-space:nowrap"><button class="icon" data-act="up" ${i ? "" : "disabled"} title="Move up">↑</button><button class="icon" data-act="down" ${i < d.bookmarks.length - 1 ? "" : "disabled"} title="Move down">↓</button><button class="icon" data-act="del" title="Remove">✕</button></td></tr>`).join("")
      || `<tr><td colspan="3" class="muted">No shortcuts.</td></tr>`;
    this.touch();
  },
  renderPeers(force) {
    const d = this.draft; if (!d) return;
    if (!force && document.activeElement?.closest?.("#set-peers")) return;   // don't redraw under the user's cursor
    const live = Object.fromEntries((last?.peers || []).map(p => [p.name, p]));
    $("#set-peers").innerHTML = d.peers.map((p, i) => {
      const l = live[p.name];
      return `<tr data-i="${i}"><td>${dot(l ? (l.connected ? "ok" : l.online ? "warn" : "bad") : "")}</td>
        <td><input data-f="name" value="${esc(p.name)}"></td><td><input data-f="host" value="${esc(p.host)}" spellcheck="false" placeholder="e.g. LAPTOP-1234"></td>
        <td><input data-f="ip" value="${esc(p.ip)}" spellcheck="false" placeholder="192.168.x.x"></td>
        <td><input data-f="mac" value="${esc(p.mac || "")}" spellcheck="false" placeholder="${esc(l?.stats?.macs?.[0] || "AA:BB:CC:DD:EE:FF")}"></td>
        <td class="muted small">${l ? ago(l.last_login) : "—"}</td><td><button class="icon" data-act="del" title="Remove">✕</button></td></tr>`;
    }).join("") || `<tr><td colspan="7" class="muted">No computers yet.</td></tr>`;
    const unknown = (last?.unknown_clients || []).filter(u => !d.peers.some(p => p.ip === u.ip));
    $("#set-unknown").innerHTML = unknown.length ? `<div class="unknown"><b>Logged in, but not in your list:</b>
      ${unknown.slice(0, 8).map(u => `<div><span class="mono">${esc(u.ip)}</span><span class="muted">${u.count} login(s) as ${esc(u.user)}, last ${ago(u.last)}</span>
      <button data-add-ip="${esc(u.ip)}">Add</button></div>`).join("")}</div>` : "";
  },
  async loadKeys() {
    try { this.keys = await api("/api/keys"); } catch (e) { this.keys = { error: e.message, keys: [], files: [] }; }
    this.renderKeys();
  },
  renderKeys() {
    const k = this.keys; if (!k || !this.draft) return;
    const unreadable = (k.files || []).find(f => f.scope === "admin" && f.exists && !f.readable);
    $("#plugins").innerHTML = (last?.plugins || []).map(p => `<div class="row-line">${dot(p.ok ? "ok" : "bad")}<b>${esc(p.name)}</b> <span class="muted small">${esc(p.description)}</span></div>`).join("")
      || `<div class="muted small">No plugins. Put <code>.py</code> files in the <code>plugins</code> folder of your SSH Manager home folder (see the README).</div>`;
    $("#keys-note").innerHTML = k.error ? `<div class="alert">${esc(k.error)}</div>`
      : unreadable ? `<div class="alert warnish">${esc(unreadable.error)}</div>`
      : !k.can_edit ? `<div class="alert warnish">SSH Manager isn't running with admin rights, so keys can be viewed but not changed.</div>` : "";
    $("#set-keys").innerHTML = k.keys.map(x => `<tr data-fp="${esc(x.fingerprint)}">
      <td><input data-f="label" value="${esc(this.draft.keys[x.fingerprint] || "")}" placeholder="${esc(x.comment || "label")}"></td>
      <td class="muted small">${esc(x.type.replace("ssh-", ""))}</td><td class="fp" title="${esc(x.fingerprint)}">${esc(x.fingerprint.slice(0, 22))}…</td>
      <td class="muted small">${esc(x.comment)}${x.file === "user" ? " (user file)" : ""}</td>
      <td class="muted small">${x.in_use ? '<span style="color:var(--ok)">in use now</span>' : x.last_used ? ago(x.last_used) + (x.last_client ? " · " + esc(x.last_client) : "") : "not in the log"}</td>
      <td><button class="icon" data-act="del" title="${x.in_use ? "In use by an open session" : "Stop allowing this key"}" ${!k.can_edit || x.in_use || x.file !== "admin" ? "disabled" : ""}>✕</button></td></tr>`).join("")
      || `<tr><td colspan="6" class="muted">${unreadable ? "—" : "No keys."}</td></tr>`;
  },
};

$("#set-name").oninput = e => { settingsUi.draft.name = e.target.value; settingsUi.touch(); };
$("#set-upload").oninput = e => { settingsUi.draft.default_upload = e.target.value; settingsUi.touch(); };
$("#set-peers").oninput = e => {
  const tr = e.target.closest("tr[data-i]"); if (!tr || !e.target.dataset.f) return;
  settingsUi.draft.peers[+tr.dataset.i][e.target.dataset.f] = e.target.value; settingsUi.touch();
};
$("#set-peers").onclick = e => {
  const b = e.target.closest("[data-act=del]"); if (!b) return;
  const i = +b.closest("tr").dataset.i, p = settingsUi.draft.peers[i];
  if (!confirm(`Remove ${p.name} from the list? It can still connect if its SSH key is allowed (see SSH keys below).`)) return;
  settingsUi.draft.peers.splice(i, 1); settingsUi.render();
};
$("#set-add-peer").onclick = () => {
  settingsUi.draft.peers.push({ name: `Computer ${settingsUi.draft.peers.length + 1}`, host: "", ip: "" });
  settingsUi.render();
  $("#set-peers tr:last-child input")?.select();
};
$("#set-unknown").onclick = e => {
  const ip = e.target.dataset.addIp; if (!ip) return;
  settingsUi.draft.peers.push({ name: `Computer ${settingsUi.draft.peers.length + 1}`, host: "", ip });
  settingsUi.render();
  $("#set-peers tr:last-child input")?.select();
};
$("#set-keys").oninput = e => {
  const tr = e.target.closest("tr[data-fp]"); if (!tr) return;
  settingsUi.draft.keys = { ...settingsUi.draft.keys, [tr.dataset.fp]: e.target.value }; settingsUi.touch();
};
$("#set-keys").onclick = async e => {
  const b = e.target.closest("[data-act=del]"); if (!b) return;
  const fp = b.closest("tr").dataset.fp, label = settingsUi.draft.keys[fp] || fp;
  if (!confirm(`Stop allowing "${label}"?\n\nThe computer using this key will no longer be able to connect to ${hostName}. A backup of the key file is kept in D:\\SSH-Manager\\data.`)) return;
  try { await api("/api/keys/remove", { method: "POST", json: { fingerprint: fp } }); toast("Key removed"); await settingsUi.load(); settingsUi.render(); settingsUi.loadKeys(); }
  catch (err) { toast(err.message, true); }
};
$("#key-add").onclick = async () => {
  try {
    await api("/api/keys/add", { method: "POST", json: { public_key: $("#key-text").value, label: $("#key-label").value } });
    $("#key-text").value = ""; $("#key-label").value = ""; $("#addkey").open = false;
    toast("Key allowed. That computer can now connect.");
    await settingsUi.load(); settingsUi.render(); settingsUi.loadKeys();
  } catch (err) { toast(err.message, true); }
};
$("#set-shortcuts").oninput = e => {
  const tr = e.target.closest("tr[data-i]"); if (!tr || !e.target.dataset.f) return;
  settingsUi.draft.bookmarks[+tr.dataset.i][e.target.dataset.f] = e.target.value; settingsUi.touch();
};
$("#set-shortcuts").onclick = e => {
  const b = e.target.closest("[data-act]"); if (!b) return;
  const list = settingsUi.draft.bookmarks, i = +b.closest("tr").dataset.i;
  if (b.dataset.act === "del") list.splice(i, 1);
  else { const j = b.dataset.act === "up" ? i - 1 : i + 1; [list[i], list[j]] = [list[j], list[i]]; }
  settingsUi.render();
};
$("#set-add-shortcut").onclick = () => {
  settingsUi.draft.bookmarks.push({ name: files.path ? baseName(files.path) : "New shortcut", path: files.path || "C:\\" });
  settingsUi.render();
};
$("#set-save").onclick = async () => {
  try { await settingsUi.save(); toast("Settings saved"); } catch (e) { toast(e.message, true); }
};
$("#set-discard").onclick = () => { settingsUi.draft = structuredClone(settingsUi.saved); settingsUi.render(); };

// app settings (only inside the desktop app)
let appSettings = null;
hostHandlers["app-settings"] = m => {
  appSettings = m.settings;
  $("#app-panel").hidden = false;
  $("#app-where").textContent = `on ${m.machine}`;
  $$("[data-app]").forEach(i => i.checked = !!appSettings[i.dataset.app]);
  $("#app-conn").value = appSettings.Connection || "";
  $("#app-dl").value = appSettings.DownloadFolder || "";
  $("#app-where").textContent = `on ${m.machine} · app version ${appSettings.AppVersion}`;
  $("#app-autoupdate-row").hidden = !!appSettings.ServerUpdatesApp;
};
$("#app-panel").addEventListener("change", e => {
  const el = e.target;
  if (el.dataset.app) toHost("set-app-settings", { settings: { [el.dataset.app]: el.checked } });
});
$("#app-dl-pick").onclick = () => toHost("pick-download-folder");
$("#app-conn-change").onclick = () => toHost("open-setup");

// ---------- updates ----------
async function installUpdate() {
  if (!confirm(`Install the SSH Manager update on ${hostName}? Its server restarts; this window reconnects by itself.`)) return;
  try { await api("/api/update/install", { method: "POST" }); toast("Update started"); } catch (e) { toast(e.message, true); }
}
document.addEventListener("click", e => { if (e.target.closest("[data-update-now]")) installUpdate(); });
function renderUpdate() {
  const u = last?.update; if (!u) return;
  $("#upd-version").textContent = `${hostName}: version ${u.current}`;
  $("#upd-state").innerHTML = u.status !== "idle" ? `${u.status === "downloading" ? "Downloading" : "Installing"} ${esc(u.latest)}…`
    : u.available ? `<b>Version ${esc(u.latest)} is available.</b>` + (u.installable ? "" : " This copy runs from source; update it with <code>git pull</code>.")
    : u.error ? esc(u.error) : u.checked ? `Up to date (checked ${ago(u.checked)}).` : "Not checked yet.";
  $("#upd-install").hidden = !(u.available && u.installable && u.status === "idle");
}
$("#upd-check").onclick = async () => { try { last.update = await api("/api/update/check", { method: "POST" }); renderUpdate(); } catch (e) { toast(e.message, true); } };
$("#upd-install").onclick = installUpdate;
$("#srv-restart").onclick = async () => {
  if (!confirm(`Restart SSH Manager's server on ${hostName}? Pages and connections pause for 10 to 20 seconds.`)) return;
  const b = $("#srv-restart");
  b.disabled = true; b.textContent = "Restarting…";
  try {
    await api("/api/server/restart", { method: "POST" });
    const t0 = Date.now();
    await new Promise(r => setTimeout(r, 1500));
    while (Date.now() - t0 < 30000) {   // wait until it answers again
      try { if ((await fetch("/api/ping", { cache: "no-store" })).ok) break; } catch { }
      await new Promise(r => setTimeout(r, 700));
    }
    toast("Server restarted.");
    settingsUi.open();
  } catch (e) { toast(e.message, true); }
  finally { b.disabled = false; b.textContent = "Restart"; }
};
$("#set-autoupdate").onchange = e => { settingsUi.draft.auto_update = e.target.checked; settingsUi.touch(); };

// ---------- PC switcher (inside the app) ----------
hostHandlers.pcs = m => {
  $("#pc-section").hidden = false;
  appPcs = m.pcs; fillSyncPcs();
  const state = p => p.state === "Connected" ? "ok" : p.state === "Connecting" ? "warn" : "bad";
  // the Screen page would show the screen this app runs on: hide it for this PC
  const own = m.current === "local";
  $("#nav-screen").hidden = own;
  if (own && !$("#tab-screen").hidden) showTab("overview");
  $("#pcbar").innerHTML = (m.pcs.length > 1 ? `<button data-pc="all"><span class="dot" style="background:var(--accent)"></span><span class="name">All PCs</span></button>` : "") +
    m.pcs.map(p => `<button data-pc="${esc(p.id)}" class="${p.id === m.current ? "on" : ""}" title="${esc(p.name)}">${dot(state(p))}<span class="name">${esc(p.name)}</span></button>`).join("") +
    `<button class="add" data-pc="add" title="Add a PC"><span class="dot" style="background:none;color:var(--accent)">+</span><span class="name">Add PC</span></button>`;
};
$("#pcbar").onclick = e => {
  const b = e.target.closest("[data-pc]"); if (!b) return;
  toHost(b.dataset.pc === "add" ? "add-pc" : "switch-pc", { id: b.dataset.pc });
};

// ---------- Run: quick actions and the command panel ----------
const runUi = {
  actions: [], current: null, next: 0, timer: null,
  async load() {
    try {
      const r = await api("/api/actions");
      this.actions = r.actions;
      this.renderActions();
      this.renderRuns(r.runs);
    } catch (e) { toast(e.message, true); }
    this.renderHistory();
  },
  renderActions() {
    const groups = {};
    for (const a of this.actions) (groups[a.group || ""] ||= []).push(a);
    const play = '<svg viewBox="0 0 16 16"><path d="M4 2.5v11l9-5.5z"/></svg>';
    $("#actions").innerHTML = this.actions.length ? `<div class="action-groups">` + Object.entries(groups).map(([g, list]) =>
      `<div class="action-group">${g ? `<h4>${esc(g)}</h4>` : ""}<div class="action-grid">` +
      list.map(a => `<button data-action="${esc(a.id)}" title="${esc(a.script.slice(0, 400))}">${play}${esc(a.name)}</button>`).join("") + `</div></div>`).join("") + `</div>`
      : `<div class="none">No quick actions yet. Add them under Settings → Quick actions, or type a command below and save it as one.</div>`;
  },
  renderRuns(runs) {
    $("#runs").innerHTML = (runs || []).slice(0, 12).map(r => `<div class="run-row" data-run="${r.id}">${dot(r.status === "running" ? "warn" : r.status === "done" ? "ok" : "bad")}
      <span class="t">${esc(r.title)}</span><span class="muted small">${esc(r.client)}</span><span class="muted small">${fmtTime(r.started)}</span></div>`).join("")
      || `<div class="none">Nothing run yet.</div>`;
  },
  renderHistory() {
    const h = store.get("cmdHistory", []);
    $("#cmd-history").innerHTML = `<option value="">History…</option>` + h.map((c, i) => `<option value="${i}">${esc(c.split("\n")[0].slice(0, 60))}</option>`).join("");
  },
  async start(request) {
    try { const run = await request; this.show(run.id); } catch (e) { toast(e.message, true); }
  },
  show(id) {
    clearTimeout(this.timer);
    this.current = id; this.next = 0;
    $("#out-text").textContent = "";
    this.poll();
  },
  async poll() {
    const id = this.current; if (!id) return;
    try {
      const r = await api(`/api/runs/${id}?after=${this.next}`);
      if (id !== this.current) return;
      const out = $("#out-text");
      const atBottom = out.scrollTop + out.clientHeight >= out.scrollHeight - 24;
      if (r.lines.length) {
        out.textContent += (out.textContent ? "\n" : "") + r.lines.join("\n");
        this.next = r.next;
        if (atBottom) out.scrollTop = out.scrollHeight;
      }
      $("#out-title").textContent = r.title;
      $("#out-state").innerHTML = `<span class="state-chip ${r.status}">${r.status}${r.exit_code != null && r.status !== "done" ? " · exit " + r.exit_code : ""} · ${fmtDur(r.duration_s)}</span>`;
      $("#out-stop").hidden = r.status !== "running";
      if (r.status === "running") this.timer = setTimeout(() => this.poll(), 700);
      else {
        if (!out.textContent) out.textContent = "(no output)";
        api("/api/runs").then(x => this.renderRuns(x.runs)).catch(() => { });
      }
    } catch (e) { $("#out-state").textContent = e.message; }
  },
};
$("#actions").onclick = e => {
  const b = e.target.closest("[data-action]"); if (!b) return;
  const a = runUi.actions.find(x => x.id === b.dataset.action); if (!a) return;
  if (a.confirm && !confirm(`Run "${a.name}" on ${hostName}?`)) return;
  runUi.start(api(`/api/actions/${encodeURIComponent(a.id)}/run`, { method: "POST" }));
};
$("#runs").onclick = e => { const r = e.target.closest("[data-run]"); if (r) runUi.show(r.dataset.run); };
$("#out-stop").onclick = () => runUi.current && api(`/api/runs/${runUi.current}/stop`, { method: "POST" }).catch(e => toast(e.message, true));
function runCommand() {
  const script = $("#cmd-text").value.trim(); if (!script) return;
  const h = store.get("cmdHistory", []).filter(c => c !== script);
  h.unshift(script); store.set("cmdHistory", h.slice(0, 20));
  runUi.renderHistory();
  runUi.start(api("/api/run", { method: "POST", json: { script } }));
}
$("#cmd-run").onclick = runCommand;
$("#cmd-text").onkeydown = e => { if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) { e.preventDefault(); runCommand(); } };
$("#cmd-history").onchange = e => { const h = store.get("cmdHistory", []); if (e.target.value !== "") $("#cmd-text").value = h[+e.target.value]; e.target.value = ""; };
$("#cmd-save").onclick = async () => {
  const script = $("#cmd-text").value.trim(); if (!script) return toast("Type a command first", true);
  const name = prompt("Name for this quick action:"); if (!name) return;
  if (!settingsUi.saved) await settingsUi.load();
  const next = structuredClone(settingsUi.saved);
  next.actions = [...(next.actions || []), { name, script, group: "", confirm: false }];
  try { await settingsUi.save(next); toast("Saved as a quick action"); runUi.load(); } catch (e) { toast(e.message, true); }
};
$("#actions-edit").onclick = () => { showTab("settings"); showSettingsSection("actions"); };

// ---------- Logs ----------
const logUi = {
  list: [], sel: null, file: "", offset: -1, lines: [], tail: "", timer: null,
  async load() {
    try {
      const r = await api("/api/logs");
      this.list = r.logs;
      this.renderList();
      if (!this.sel && this.list.length) this.open({ index: 0 });
      else this.poll();
    } catch (e) { toast(e.message, true); }
  },
  renderList() {
    $("#log-list").innerHTML = this.list.map(l => `<a data-log="${l.index}" class="${this.sel?.index === l.index ? "active" : ""} ${l.file ? "" : "missing"}" title="${esc(l.file || l.pattern)}">${esc(l.name)}${l.size != null ? `<span class="sz">${fmtBytes(l.size)}</span>` : ""}</a>`).join("")
      || `<div class="muted small" style="padding:4px 8px">None yet. Add logs under Settings → Logs.</div>`;
  },
  open(sel) {
    this.sel = sel; this.file = ""; this.offset = -1; this.lines = []; this.tail = "";
    $("#log-text").textContent = "";
    this.renderList();
    this.poll();
  },
  async poll() {
    clearTimeout(this.timer);
    if (!this.sel || $("#tab-logs").hidden) return;
    const q = this.sel.index != null ? `index=${this.sel.index}` : `path=${encodeURIComponent(this.sel.path)}`;
    try {
      const r = await api(`/api/logs/read?${q}&offset=${this.offset}&file=${encodeURIComponent(this.file)}`);
      if (r.error) $("#log-file").textContent = r.error;
      else {
        $("#log-file").textContent = r.file; $("#log-file").title = r.file;
        if (r.reset) { this.lines = []; this.tail = ""; }
        if (r.text) {
          const parts = (this.tail + r.text).replace(/\r/g, "").split("\n");
          this.tail = parts.pop();
          this.lines.push(...parts);
          if (this.lines.length > 5000) this.lines.splice(0, this.lines.length - 5000);
          this.render();
        }
        this.file = r.file; this.offset = r.offset;
      }
    } catch (e) { $("#log-file").textContent = e.message; }
    this.timer = setTimeout(() => this.poll(), 1500);
  },
  render() {
    const el = $("#log-text");
    const f = $("#log-filter").value.trim().toLowerCase();
    let lines = this.tail ? this.lines.concat([this.tail]) : this.lines;
    if (f) {
      const re = new RegExp(f.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), "gi");
      el.innerHTML = lines.filter(l => l.toLowerCase().includes(f)).map(l => esc(l).replace(re, m => `<mark>${m}</mark>`)).join("\n");
    } else el.textContent = lines.join("\n");
    if ($("#log-follow").checked) el.scrollTop = el.scrollHeight;
  },
};
$("#log-list").onclick = e => { const a = e.target.closest("[data-log]"); if (a) logUi.open({ index: +a.dataset.log }); };
$("#log-open").onclick = () => { const p = $("#log-path").value.trim(); if (p) logUi.open({ path: p }); };
$("#log-path").onkeydown = e => { if (e.key === "Enter") $("#log-open").click(); };
$("#log-filter").oninput = () => logUi.render();
$("#log-wrap").onchange = e => $("#log-text").classList.toggle("wrap", e.target.checked);
$("#log-clear").onclick = () => { logUi.lines = []; logUi.tail = ""; logUi.render(); };
$("#fs-log").onclick = () => { const p = [...files.sel][0]; if (!p) return; $("#log-path").value = p; showTab("logs"); logUi.open({ path: p }); };

// ---------- Screen preview ----------
const screenUi = {
  timer: null, url: null, busy: false,
  // what to show: "m:<monitor>" (a whole screen) or "w:<window id>" (one app); apps are re-found by name after a restart
  async open() {
    $("#scr-wrap").classList.toggle("fit", $("#scr-fit").checked);
    await this.loadSources();
    this.tick(true);
  },
  async loadSources() {
    try {
      const [m, w] = await Promise.all([api("/api/screen/monitors"), api("/api/screen/windows").catch(() => ({ windows: [] }))]);
      const short = t => t.length > 60 ? t.slice(0, 58) + "…" : t;
      $("#scr-monitor").innerHTML =
        (m.monitors.length ? `<optgroup label="Screens">${m.monitors.map(x => `<option value="m:${x.index}">Screen ${x.index} · ${x.width}×${x.height}</option>`).join("")}</optgroup>` : "") +
        (w.windows.length ? `<optgroup label="Apps">${w.windows.map(x =>
          `<option value="w:${x.id}" data-app="${esc(x.app)}">${esc(x.app || "App")} · ${esc(short(x.title))}${x.minimized ? " (minimized)" : ""}</option>`).join("")}</optgroup>` : "");
      const want = String(store.get("scrSource", "m:1"));
      const sel = $("#scr-monitor");
      sel.value = want;
      if (!sel.value && want.startsWith("w:")) {
        const app = store.get("scrApp", "");
        const same = [...sel.options].find(o => o.dataset.app && o.dataset.app === app);
        sel.value = same ? same.value : "m:1";
      }
      if (!sel.value) sel.value = sel.options[0]?.value || "";
      if (!sel.options.length) this.message(m.whole_screen === false
        ? "Whole-screen preview is off on this PC, and none of the apps that may be viewed are open."
        : "Nothing to show.");
    } catch (e) { this.message(e.message); }
  },
  remember() {
    const o = $("#scr-monitor").selectedOptions[0];
    store.set("scrSource", $("#scr-monitor").value);
    store.set("scrApp", o?.dataset.app || "");
  },
  stop() { clearTimeout(this.timer); },
  message(text) { $("#scr-msg").hidden = !text; $("#scr-msg").textContent = text || ""; if (text) $("#scr-img").removeAttribute("src"); },
  async tick(force) {
    clearTimeout(this.timer);
    if ($("#tab-screen").hidden) return;
    const rate = +$("#scr-rate").value;
    if ((force || rate) && !this.busy) {
      this.busy = true;
      try {
        if (!$("#scr-monitor").value) { this.busy = false; return; }
        const [kind, id] = $("#scr-monitor").value.split(":");
        const what = kind === "w" ? `window=${id}` : `monitor=${id}`;
        const r = await fetch(`/api/screen?${what}&width=${$("#scr-size").value}&t=${Date.now()}`);
        if (!r.ok) {
          let m = r.statusText; try { m = (await r.json()).detail; } catch { }
          this.message(m);
        } else {
          const b = await r.blob();
          const u = URL.createObjectURL(b);
          $("#scr-img").src = u;
          if (this.url) URL.revokeObjectURL(this.url);
          this.url = u;
          this.message("");
          $("#scr-info").textContent = `${new Date().toLocaleTimeString()} · ${fmtBytes(b.size)}`;
        }
      } catch (e) { this.message(e.message); }
      finally { this.busy = false; }
    }
    if (rate && !$("#tab-screen").hidden) this.timer = setTimeout(() => this.tick(), rate);
  },
};
$("#scr-monitor").onchange = () => { screenUi.remember(); screenUi.tick(true); };
$("#scr-size").onchange = () => screenUi.tick(true);
$("#scr-rate").onchange = () => screenUi.tick(true);
$("#scr-now").onclick = async () => { await screenUi.loadSources(); screenUi.tick(true); };
$("#scr-fit").onchange = e => $("#scr-wrap").classList.toggle("fit", e.target.checked);

// ---------- Folder sync (runs in the app; this page shows and edits it) ----------
let appPcs = [];
const modeArrow = { "to-pc": "→", "from-pc": "←", "two-way": "⇄" };
hostHandlers["sync-state"] = m => {
  $("#sync-list").innerHTML = m.pairs.length ? m.pairs.map(p => `<div class="sync-row">
      ${dot(!p.enabled ? "" : p.state === "error" ? "bad" : p.state === "syncing" ? "warn" : "ok")}
      <span class="p" title="${esc(p.local)}">${esc(p.local)}</span><span class="arrow">${modeArrow[p.mode] || "→"}</span>
      <span class="p" title="${esc(p.pcName)}: ${esc(p.remote)}">${esc(p.pcName)}: ${esc(p.remote)}</span>
      <span class="st" title="${esc(p.message || "")}">${p.enabled ? esc(p.message || "") : "paused"}</span>
      <span class="btns"><button class="icon" data-sync="now" data-id="${p.id}" title="Sync now">⟳</button>
        <button class="icon" data-sync="toggle" data-id="${p.id}" title="${p.enabled ? "Pause" : "Resume"}">${p.enabled ? "❚❚" : "▶"}</button>
        <button class="icon" data-sync="remove" data-id="${p.id}" title="Remove">✕</button></span></div>`).join("")
    : `<div class="none">No folder pairs yet. Add one below.</div>`;
};
hostHandlers["sync-local-picked"] = m => { $("#sync-local").value = m.path || ""; };
$("#sync-list").onclick = e => {
  const b = e.target.closest("[data-sync]"); if (!b) return;
  if (b.dataset.sync === "remove" && !confirm("Stop syncing this folder pair? No files are deleted.")) return;
  toHost("sync-" + b.dataset.sync, { id: b.dataset.id });
};
$("#sync-pick").onclick = () => toHost("sync-pick-local");
$("#sync-add").onclick = () => {
  const pair = { local: $("#sync-local").value, pcId: $("#sync-pc").value, remote: $("#sync-remote").value.trim(),
    mode: $("#sync-mode").value, ignore: $("#sync-ignore").value };
  if (!pair.local || !pair.remote || !pair.pcId) return toast("Choose both folders and the PC", true);
  toHost("sync-add", { pair });
  $("#sync-local").value = ""; $("#sync-remote").value = "";
};
function fillSyncPcs() {
  const sel = $("#sync-pc"), keep = sel.value;
  sel.innerHTML = appPcs.filter(p => p.id !== "local").map(p => `<option value="${esc(p.id)}">${esc(p.name)}</option>`).join("");
  if (keep) sel.value = keep;
  if (!$("#sync-remote").value && files.path) $("#sync-remote").placeholder = files.path;
}

// ---------- settings: quick actions and logs editors ----------
function renderActionEditor() {
  const list = settingsUi.draft?.actions || [];
  $("#set-actions").innerHTML = list.map((a, i) => `<div class="action-edit" data-i="${i}">
      <div class="top"><input data-f="name" value="${esc(a.name)}" placeholder="Name">
        <input data-f="group" value="${esc(a.group || "")}" placeholder="Group (optional)">
        <label class="check"><input type="checkbox" data-f="confirm" ${a.confirm ? "checked" : ""}> ask first</label>
        <button class="icon" data-act="up" ${i ? "" : "disabled"}>↑</button><button class="icon" data-act="down" ${i < list.length - 1 ? "" : "disabled"}>↓</button>
        <button class="icon" data-act="del" title="Remove">✕</button></div>
      <textarea data-f="script" rows="3" spellcheck="false" placeholder="PowerShell script">${esc(a.script)}</textarea></div>`).join("")
    || `<div class="none">No quick actions.</div>`;
}
function renderLogEditor() {
  const list = settingsUi.draft?.logs || [];
  $("#set-logs").innerHTML = list.map((l, i) => `<tr data-i="${i}"><td><input data-f="name" value="${esc(l.name)}"></td>
      <td style="width:65%"><input data-f="path" value="${esc(l.path)}" spellcheck="false"></td>
      <td><button class="icon" data-act="del" title="Remove">✕</button></td></tr>`).join("")
    || `<tr><td colspan="3" class="muted">No logs.</td></tr>`;
}
function editList(listName, rerender) {
  return {
    input: e => {
      const row = e.target.closest("[data-i]"); if (!row || !e.target.dataset.f) return;
      const item = settingsUi.draft[listName][+row.dataset.i];
      item[e.target.dataset.f] = e.target.type === "checkbox" ? e.target.checked : e.target.value;
      settingsUi.touch();
    },
    click: e => {
      const b = e.target.closest("[data-act]"); if (!b) return;
      const list = settingsUi.draft[listName], i = +b.closest("[data-i]").dataset.i;
      if (b.dataset.act === "del") list.splice(i, 1);
      else { const j = b.dataset.act === "up" ? i - 1 : i + 1; [list[i], list[j]] = [list[j], list[i]]; }
      rerender(); settingsUi.touch();
    },
  };
}
{
  const ae = editList("actions", renderActionEditor);
  $("#set-actions").addEventListener("input", ae.input);
  $("#set-actions").addEventListener("change", ae.input);
  $("#set-actions").onclick = ae.click;
  const le = editList("logs", renderLogEditor);
  $("#set-logs").addEventListener("input", le.input);
  $("#set-logs").onclick = le.click;
}
$("#set-add-action").onclick = () => {
  (settingsUi.draft.actions ||= []).push({ name: "New action", script: "", group: "", confirm: false });
  renderActionEditor(); settingsUi.touch();
  $("#set-actions .action-edit:last-child input")?.select();
};
$("#set-add-log").onclick = () => {
  (settingsUi.draft.logs ||= []).push({ name: "New log", path: files.path ? files.path + "\\*.log" : "C:\\path\\to\\*.log" });
  renderLogEditor(); settingsUi.touch();
};
$("#set-screen").onchange = e => { settingsUi.draft.screen_preview = e.target.checked; settingsUi.touch(); settingsUi.render(); };
$("#set-screen-monitors").onchange = e => { settingsUi.draft.screen_monitors = e.target.checked; settingsUi.touch(); };
$("#set-screen-apps").oninput = e => {
  settingsUi.draft.screen_apps = e.target.value.split(/[,;]/).map(s => s.trim().replace(/\.exe$/i, "")).filter(Boolean);
  settingsUi.touch();
};

// ---------- Wake-on-LAN for computers in the list ----------
document.addEventListener("click", async e => {
  const b = e.target.closest("[data-wake]"); if (!b) return;
  try { await api("/api/wol", { method: "POST", json: { mac: b.dataset.wake, ips: b.dataset.ip ? [b.dataset.ip] : [] } }); toast("Wake-up signal sent"); }
  catch (err) { toast(err.message, true); }
});

// ---------- start ----------
(async function whoami() {
  try {
    const w = await api("/api/whoami");
    viewer = w.viewer; hostName = w.host; $("#lane-right").textContent = w.host;
  } catch { }
  if (!viewer || viewer === "SSH tunnel" || viewer === "unknown") setTimeout(whoami, 3000);   // login not logged yet
})();
settingsUi.load().then(() => files.markActive()).catch(() => { });
if (host) { $("#app-panel").hidden = false; $("#set-nav-app").hidden = false; $("#nav-sync").hidden = false; }
pollStatus();
pollActivity();
showTab(store.get("tab", "overview"));
