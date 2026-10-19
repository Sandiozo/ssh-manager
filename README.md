<p align="center">
  <img src="docs/banner.jpg" alt="SSH Manager" width="100%">
</p>

<h3 align="center">See and manage all your Windows PCs from one app</h3>

<p align="center">
  Live status, sessions and activity, file transfer, remote commands, logs and screen preview,
  all over SSH. No cloud service and no accounts: just Windows' built-in OpenSSH.
</p>

<p align="center">
  <a href="https://github.com/Sandiozo/ssh-manager/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/Sandiozo/ssh-manager?style=flat-square&label=release&color=2f7d6d"></a>
  <a href="https://github.com/Sandiozo/ssh-manager/releases"><img alt="Downloads" src="https://img.shields.io/github/downloads/Sandiozo/ssh-manager/total?style=flat-square&color=c8783c"></a>
  <img alt="Windows 10 and 11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-3b4a45?style=flat-square">
  <a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/badge/license-MIT-3b4a45?style=flat-square"></a>
</p>

<p align="center">
  <a href="https://github.com/Sandiozo/ssh-manager/releases/latest/download/SSHManager-Setup.exe"><b>⬇ Download for Windows</b></a>
  &nbsp;·&nbsp; <a href="#install">Install</a>
  &nbsp;·&nbsp; <a href="#features">Features</a>
  &nbsp;·&nbsp; <a href="#security">Security</a>
  &nbsp;·&nbsp; <a href="#troubleshooting">Troubleshooting</a>
</p>

<p align="center">
  <img src="docs/screenshot.png" alt="SSH Manager: overview of a PC with its computers, live sessions and recent activity" width="100%">
</p>

## Why

You work on one computer but run things on others: a home server, a gaming or build PC, a machine
your scripts or AI tools reach over SSH. SSH Manager shows what's going on there (who is connected,
what they ran, how the machine is doing) and lets you move files, run commands and check logs
without opening a remote desktop.

Everything goes through Windows' built-in OpenSSH. Each PC runs a small server that only listens on
`127.0.0.1`; the app reaches it through an SSH tunnel, so the only thing reachable on your network is
SSH itself, and only on PCs you set up to be managed.

## Install

**Requirements:** Windows 10 (1809 or later) or Windows 11, 64-bit. Everything else is installed for you.

Download **[SSHManager-Setup.exe](https://github.com/Sandiozo/ssh-manager/releases/latest/download/SSHManager-Setup.exe)**
and run it on **every PC** you want to use. The installer asks what the PC should do:

- **Client:** installs the app, to see and manage your other PCs from this one.
- **Client and server:** the same, and your other PCs can manage this one too: it turns on
  Windows' OpenSSH Server, allows it through the firewall on private networks, and runs SSH Manager
  in the background (starts at logon).

Pick **Client and server** on the PCs you want to reach (a desktop, a home server) and **Client** on
the ones you only work from (a laptop). The page shows what's installed now and what will be
downloaded: the .NET Desktop Runtime and WebView2 if they're missing, and OpenSSH Server for the
server role. No Python needed. Run the installer again any time to switch.

Then, in the app, click **+ Add PC** and enter the other PC's name (or use **Search my network**).
The app creates a key for this computer and signs in with it. If a key already works (one of yours
in `~/.ssh`, or a host from `~/.ssh/config`), the PC is added right away. Otherwise you allow this
computer on that PC once, in one of three ways:

- **Approve on the other PC** (when its SSH Manager app is connected to this PC): send a request and
  click **Allow** on the other PC. It shows who is asking and the key's fingerprint, and expires
  after two minutes. No password needed.
- **Sign in with a password:** the Windows password of your account on that PC, typed on the page,
  used once and never stored. For a Microsoft account, use the account password, not the PIN.
- **Copy the key:** for PCs that only allow PIN / Windows Hello sign-in. Copy this computer's public
  key and paste it on the other PC under Settings → SSH keys → Allow a new key.

Each PC decides for itself who may manage it: adding PC B on PC A never lets B manage A.

> Windows may show "Windows protected your PC" because the installer isn't code-signed. Click
> **More info → Run anyway**.

## Features

- **All PCs:** everything combined: totals over the last 24 hours (logins, failed logins, commands,
  data transferred), SSH logins per hour for each PC, everyone connected anywhere, and one activity
  timeline across all PCs that you can filter by PC, type or text. Below that, a card per PC with its
  CPU, RAM, disks and failed logins; computers installed as Client appear as cards too.
- **Live sessions:** who is connected over SSH and the command each session is running right now.
- **Activity log:** every command run over SSH with the full script (`powershell -EncodedCommand`
  scripts are decoded), every file transfer and settings change.
- **Connections:** login/logout history and failed login attempts from the OpenSSH event log.
- **Files:** browse a PC, drop files or folders anywhere in the window to send them, drag files out
  to download, folders come as zip; rename, delete (to the Recycle Bin), shortcuts. An animated
  transfer panel shows progress, speed and time left.
- **Run:** one-click quick actions (PowerShell scripts you save, grouped, optionally asking before
  they run) and a command panel for anything else. Output streams live, runs can be stopped, and the
  last 50 runs stay on the page. Everything run here is also in the activity log.
- **Logs:** follow any log file live, like `tail -f`: a file, the newest file in a folder, or the
  newest match of a pattern such as `C:\Logs\app-*.log`. Filter lines as you type.
- **Screen:** a live preview of a PC's screen (view only): a whole monitor or a single app window
  (shown even when other windows cover it, not when it's minimized), refreshing every 1–10 seconds.
  Can be switched off per PC; every viewing is logged.
- **Folder sync:** keep a folder on this computer and a folder on a PC the same: this computer → PC,
  PC → this computer, or both ways. Changes are sent a few seconds after they happen. Sync never
  deletes files; `.git`, `node_modules` and the like are skipped by default.
- **Shared clipboard:** text copied on one computer can be pasted on the others (off by default,
  switch it on in the app settings).
- **Wake-on-LAN:** wake a PC that's asleep or off from its card. The app learns each PC's network
  card while connected; the PC needs Wake-on-LAN enabled in its BIOS and network adapter settings.
- **Settings** (⚙ in the top bar): the computers that connect, the SSH keys allowed to log in (add
  one to set up a computer by hand, remove one to lock it out), quick actions, logs, file shortcuts,
  screen preview, updates and the app's own options.
- **Tray app:** keeps every connection open, reconnects by itself, notifies about lost connections,
  failed logins, unknown computers and finished transfers.
- **Automatic updates:** each PC updates itself when a new release is out; app-only PCs ask once.
- **Plugins:** small Python files that add your own command decoders, activity details or API routes.

## How it works

```
 the PC you work on                               a PC you manage
┌──────────────────────────┐   SSH (port 22)   ┌──────────────────────────────┐
│ SSH Manager app          │ ═════════════════►│ OpenSSH Server               │
│  one port forward per PC │                   │   └ SSH Manager server        │
│  localhost:87xx ─────────┼───────────────────┼──► 127.0.0.1:8765            │
└──────────────────────────┘                   └──────────────────────────────┘
```

The server on each PC only listens on `127.0.0.1`; the app reaches it through an SSH port forward,
so no extra port is opened and using a PC's dashboard requires being able to log in to it over SSH.
The app also sends its own computer's CPU / RAM / battery to the PCs it's connected to, so they can
show it too.

## Security

- The server listens on `127.0.0.1` only; reaching it from another computer requires SSH access.
- Every request needs an access token. The app reads it over SSH when it connects. It's stored in the
  server's data folder.
- Requests from web sites are blocked (Host header check, custom header required for changes).
- The server runs with admin rights (Windows runs an administrator's SSH sessions elevated, and it
  needs to read them). Anyone who can use a PC's dashboard can read and write any file there and
  change which keys may log in: the same power as an SSH login to that account. Only give SSH access
  to people you'd give that to.
- The activity log stores full command lines, which can include secrets passed on the command line.
  It stays on that PC.
- Quick actions and the command panel run with the server's admin rights, the same as an SSH login.
- The screen preview and shared clipboard only work while someone is signed in to that PC. The
  preview can be turned off in Settings; the clipboard is off until you switch it on in the app.
- New host keys are accepted the first time the app connects (trust on first use) and checked after.
- Pairing requests (Approve on the other PC) only carry a public key, can only be filed by someone who
  can already use this PC's dashboard, reach only apps connected to this PC, expire after two minutes,
  and change nothing until someone clicks Allow on the other PC. Every request and answer is logged.
- Passwords typed when adding a PC are handed to ssh once and never stored.
- Updates are downloaded from this repository's GitHub releases and checked against their SHA-256.

## Where things are

| | |
|---|---|
| Program | `C:\Program Files\SSH Manager` (`App`, `Server`, `Tools`) |
| App settings | `%LOCALAPPDATA%\SSHManager\settings.json` |
| Server settings and data ("home") | `%LOCALAPPDATA%\SSHManager\server` of the user who installed it |
| Server logs | `<home>\data\server.log`, setup log: `C:\Program Files\SSH Manager\Server\setup.log` |

To keep the server's settings somewhere else (for example in your own private repository), put the
folder path in `sshmgr-home.txt` in the `Server` folder, or set the `SSHMGR_HOME` environment variable.

The home folder:

```
config.json     computers, key labels, file shortcuts, quick actions, logs, display name, updates, port
plugins\        your plugins (*.py)
data\           activity log, logs, access token, key-file backups, downloaded updates
```

Most of `config.json` is editable under **Settings** (⚙):

| Key | Meaning |
|---|---|
| `port` | port the server listens on (127.0.0.1); default 8765 |
| `name` | display name of this PC (default: the computer name) |
| `peers` | computers that connect: `{ "name", "host", "ip" }`; used for cards and labels |
| `keys` | labels for SSH key fingerprints: `{ "SHA256:…": "Work laptop" }` |
| `bookmarks` | file shortcuts: `{ "name", "path" }` (`%USERPROFILE%` etc. work) |
| `default_upload` | where files dropped outside the Files tab go (default: Desktop) |
| `auto_update` | install new releases automatically (default `true`) |
| `actions` | quick actions on the Run page: `{ "name", "group", "script", "confirm", "timeout" }` |
| `logs` | logs on the Logs page: `{ "name", "path" }` (a file, a folder or a pattern like `logs\app-*.log`) |
| `screen_preview` | allow other PCs to view this PC's screen (default `true`) |

## Plugins

A plugin is a `.py` file in `<home>\plugins\` with a `setup(ctx)` function, loaded when the server
starts. See [`plugins/example.py`](plugins/example.py).

```python
def setup(ctx):
    def decode(rec):                 # a command just started in an SSH session
        if "my-wrapper.ps1" in rec["script"]:
            rec["kind"] = "wrapper"  # change kind / script; the summary is recomputed
    def finished(rec):               # it ended: return extra fields for the activity entry
        return {"output": "..."}
    ctx.add_decoder(decode)
    ctx.on_command_finished(finished)
    # ctx.app is the FastAPI app (add routes), ctx.config, ctx.home, ctx.monitor, ctx.activity
```

Plugins run inside the server with its (admin) rights, so only use plugins you trust.

## Extras

**HiddenRun** (`C:\Program Files\SSH Manager\Tools\HiddenRun.exe` on PCs that can be managed) runs a
program with no window. Scheduled tasks that start `powershell -WindowStyle Hidden` still flash a
console; use `HiddenRun.exe powershell.exe -File script.ps1` as the task's action instead.

## Uninstall

Settings → Apps → **SSH Manager** → Uninstall. It stops the server and removes its logon task.
OpenSSH Server stays installed (remove it in Settings → System → Optional features if you don't need
it), and so do your settings folders listed above.

## Troubleshooting

| Problem | Fix |
|---|---|
| "didn't answer" / timed out | The PC is off, on another network, or its firewall blocks port 22. Make sure the network is set to **Private** on that PC. |
| "refused the connection" | OpenSSH Server isn't running there: reinstall SSH Manager on that PC with "Client and server". |
| "didn't accept this computer's key" | Add the PC again and allow this computer (approve, password or copied key). |
| Commands show as "hidden" | The server isn't running with admin rights; reinstalling SSH Manager on that PC fixes the logon task. |
| "SSH Manager's server isn't installed there" | Install SSH Manager on that PC and choose "Client and server". |

## Building from source

- Server: Python 3.11+, `pip install -r requirements.txt`, `python server.py`.
- Everything, including the installer: Python with `pyinstaller`, the .NET 8 SDK and Inno Setup 6,
  then `.\build.ps1`. Output: `build\SSHManager-Setup.exe`.
- Pushing a tag `vX.Y.Z` (matching `sshmgr/__init__.py`) builds and publishes a release with GitHub Actions.

## License

[MIT](LICENSE)
