# SSH Manager

See and manage all your Windows PCs from one app: who is connected, what they ran, CPU / RAM /
disks, and drag-and-drop file transfer between them, all over SSH.

Useful when you work on one computer but run things on others (a home server, a gaming or build PC,
a machine your scripts or AI tools reach over SSH) and want to see what's going on there.

## Install

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

Then, in the app, click **+ Add PC** and enter the other PC's name (or use **Search my network**)
and your Windows user name there. The first time, a window asks for that account's Windows password
once; the app creates a key for this computer and adds it to that PC, and logs in with the key from
then on. With a Microsoft account, use its password; if that PC only allows PIN / Windows Hello
sign-in, turn off "Only allow Windows Hello sign-in" there first (Settings → Accounts → Sign-in options).

> Windows may show "Windows protected your PC" because the installer isn't code-signed. Click
> **More info → Run anyway**.

## Features

- **All PCs:** one overview with every PC's status, CPU, RAM, disks and failed logins; switch between
  PCs with the bar at the top.
- **Live sessions:** who is connected over SSH and the command each session is running right now.
- **Activity log:** every command run over SSH with the full script (`powershell -EncodedCommand`
  scripts are decoded), every file transfer and settings change.
- **Connections:** login/logout history and failed login attempts from the OpenSSH event log.
- **Files:** browse a PC, drop files or folders anywhere in the window to send them, drag files out
  to download, folders come as zip; rename, delete (to the Recycle Bin), shortcuts. An animated
  transfer panel shows progress, speed and time left.
- **Settings:** the computers that connect, the SSH keys allowed to log in (add one to set up a
  computer by hand, remove one to lock it out), file shortcuts, updates.
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
- New host keys are accepted the first time the app connects (trust on first use) and checked after.
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
config.json     computers, key labels, file shortcuts, drop folder, display name, auto_update, port
plugins\        your plugins (*.py)
data\           activity log, logs, access token, key-file backups, downloaded updates
```

Most of `config.json` is editable in the **Settings** tab:

| Key | Meaning |
|---|---|
| `port` | port the server listens on (127.0.0.1); default 8765 |
| `name` | display name of this PC (default: the computer name) |
| `peers` | computers that connect: `{ "name", "host", "ip" }`; used for cards and labels |
| `keys` | labels for SSH key fingerprints: `{ "SHA256:…": "Work laptop" }` |
| `bookmarks` | file shortcuts: `{ "name", "path" }` (`%USERPROFILE%` etc. work) |
| `default_upload` | where files dropped outside the Files tab go (default: Desktop) |
| `auto_update` | install new releases automatically (default `true`) |

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
| "refused the connection" | OpenSSH Server isn't running there: reinstall SSH Manager on that PC with "Let other PCs manage this PC". |
| "didn't accept this computer's key" | Add the PC again (the password step adds the key). |
| Commands show as "hidden" | The server isn't running with admin rights; reinstalling SSH Manager on that PC fixes the logon task. |
| "SSH Manager isn't set up there yet" | Install SSH Manager on that PC and choose "Let other PCs manage this PC". |

## Building from source

- Server: Python 3.11+, `pip install -r requirements.txt`, `python server.py`.
- Everything, including the installer: Python with `pyinstaller`, the .NET 8 SDK and Inno Setup 6,
  then `.\build.ps1`. Output: `build\SSHManager-Setup.exe`.
- Pushing a tag `vX.Y.Z` (matching `sshmgr/__init__.py`) builds and publishes a release with GitHub Actions.

## License

[MIT](LICENSE)
