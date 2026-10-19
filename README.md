# SSH Manager

A dashboard for the OpenSSH server on a Windows PC, and a Windows app to use it from another
computer. See who is connected and what they ran, move files between the two computers with drag
and drop, and manage which computers and keys may log in.

Useful when you work on one computer but run things on another (a home server, a gaming or build
PC, a machine your scripts or AI tools reach over SSH) and want to see what's going on there.

## Features

- **Status:** OpenSSH server state, CPU / RAM / disks, which of your computers are online, live SSH
  sessions and the command each one is running right now.
- **Activity log:** every command run over SSH, with the full script (`powershell -EncodedCommand`
  scripts are decoded), plus every file transfer and settings change.
- **Connections:** login/logout history and failed login attempts from the OpenSSH event log.
- **Files:** browse the PC, drop files or whole folders anywhere in the window to send them, download
  files and folders (as zip), rename, delete (to the Recycle Bin), shortcuts to your folders. An
  animated transfer panel shows progress, speed and time left.
- **Settings:** your computers, the SSH keys allowed to log in (add a key to set up a new computer,
  remove one to lock it out), file shortcuts.
- **App:** a tray app that keeps the connection open and reconnects by itself, with notifications
  for lost connections, failed logins, unknown computers and finished transfers.
- **Plugins:** small Python files that add your own command decoders, activity details or API routes.

## How it works

```
 your computer                                  the PC you manage
┌───────────────────────┐   SSH (port 22)    ┌──────────────────────────────┐
│ SSH Manager app       │ ═════════════════► │ OpenSSH Server               │
│ (window + tray icon)  │   port forward     │   └ SSH Manager server        │
│  localhost:8765 ──────┼────────────────────┼──► 127.0.0.1:8765 (Python)   │
└───────────────────────┘                    └──────────────────────────────┘
```

The server only listens on `127.0.0.1` of the managed PC; the app reaches it through an SSH port
forward. No extra port is opened, and logging in to the dashboard requires being able to log in
over SSH.

## Setup

### 1. On the PC you want to manage

1. Download this repository (green **Code** button → **Download ZIP**) and unzip it somewhere
   permanent, e.g. `C:\SSH-Manager`. Or `git clone https://github.com/Sandiozo/ssh-manager C:\SSH-Manager`.
2. Double-click **`Setup.cmd`** and allow it to run as administrator. It:
   - installs and starts Windows' **OpenSSH Server** if needed, and allows it through the firewall
     on private networks;
   - finds **Python 3.11+** (or offers to install it with winget);
   - installs SSH Manager's packages into `.venv`;
   - starts SSH Manager now and at every logon.
3. Note the **PC name** and **user name** it prints at the end.

### 2. On the computer you work on

1. Download **`SSHManager.zip`** from the [latest release](https://github.com/Sandiozo/ssh-manager/releases/latest),
   unzip it and double-click **`Install.cmd`** (no admin rights needed; it installs the .NET 8
   Desktop Runtime if it's missing).
2. SSH Manager opens and asks for the **PC name or IP address** and your **Windows user name** on it.
3. The first time, a window asks for that account's Windows password once (with a Microsoft account,
   the Microsoft account password). The app creates a key for this computer and adds it to the PC;
   from then on it logs in with the key.

Already have SSH keys and a host in `~/.ssh/config`? Pick it under "Or use a host from your SSH
config" instead.

To use SSH Manager on the managed PC itself, run `start.bat` (or install the app there too: it
detects the local server).

## Admin rights

On Windows, SSH sessions of an administrator account run elevated. The server therefore runs with
admin rights (the logon task Setup.cmd creates) so it can read those sessions' command lines and
manage `administrators_authorized_keys`. Without them, commands show as "hidden" and keys can't be
changed.

## Security

- Listens on `127.0.0.1` only; reaching it from another computer requires SSH access.
- Every request needs an access token (cookie or `X-SSHMgr-Token` header). The app reads it over SSH
  when it connects; `start.bat` uses a one-time login link. The token is in `<home>\data\token`.
- Requests from other web sites are blocked (Host header check, custom header required for changes).
- Anyone who can use the dashboard can read and write any file the server can (it runs as admin)
  and change which keys may log in, i.e. the same power as an SSH login to that account. Only give
  SSH access to people you'd give that to.
- The activity log stores full command lines, which can include secrets you pass on the command line.
  It stays on the managed PC in `<home>\data\activity.jsonl`.
- New host keys are accepted the first time the app connects (trust on first use) and checked after.

## Configuration

Settings live in the **home folder**: `<install folder>\home` by default. To keep them somewhere else
(for example in your own private repository), put the folder path in `sshmgr-home.txt` next to
`server.py`, or set the `SSHMGR_HOME` environment variable.

```
home\
  config.json     computers, key labels, file shortcuts, drop folder, display name, port
  plugins\        your plugins (*.py)
  data\           activity log, server logs, access token, backups of the key file
```

Most of it is editable in the **Settings** tab. `config.json`:

| Key | Meaning |
|---|---|
| `port` | port the server listens on (127.0.0.1); default 8765 |
| `name` | display name of this PC (default: the computer name) |
| `peers` | your computers: `{ "name", "host", "ip" }`; used for status cards and labels |
| `keys` | labels for SSH key fingerprints: `{ "SHA256:…": "Work laptop" }` |
| `bookmarks` | file shortcuts: `{ "name", "path" }` (environment variables like `%USERPROFILE%` work) |
| `default_upload` | where files dropped outside the Files tab go (default: Desktop) |

## Plugins

A plugin is a `.py` file in `<home>\plugins\` with a `setup(ctx)` function; it's loaded when the
server starts. See [`plugins/example.py`](plugins/example.py).

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

- **HiddenRun** (`app\HiddenRun`, built to `bin\HiddenRun.exe`): runs a program with no window.
  Scheduled tasks that start `powershell -WindowStyle Hidden` still flash a console; use
  `HiddenRun.exe powershell.exe -File script.ps1` as the task's action instead.

## Updating

On the managed PC: `git pull` (or download the ZIP again over the same folder), run `Setup.cmd`
again, done. Your home folder is not touched. For the app, install the new release with `Install.cmd`.

## Uninstall

On the managed PC run `Uninstall.cmd`, then delete the folder. OpenSSH Server stays installed (remove
it in Settings → System → Optional features if you don't need it). On the other computer delete
`%LOCALAPPDATA%\Programs\SSH Manager` and the shortcuts; the app's settings are in
`%LOCALAPPDATA%\SSHManager`.

## Building from source

- Server: Python 3.11+, `pip install -r requirements.txt`, `python server.py`.
- App: .NET 8 SDK, then `app\build.ps1` (output in `app\dist`).

## Troubleshooting

| Problem | Fix |
|---|---|
| "didn't answer" / timed out | PC off, different network, or the firewall blocks port 22. Run Setup.cmd on the PC; check the network is set to Private. |
| "refused the connection" | OpenSSH Server isn't running on the PC: run Setup.cmd there. |
| "didn't accept this computer's key" | The key wasn't added. Run the app's connection setup again (Settings → This app → Change connection…). |
| Commands show as "hidden" | The server isn't running with admin rights. Run Setup.cmd once (creates the logon task) or reconnect from the app. |
| "SSH Manager isn't installed there yet" | Run Setup.cmd on the PC. |

## License

[MIT](LICENSE)
