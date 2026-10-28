# Changelog

All notable changes to SSH Manager. Versions follow [semantic versioning](https://semver.org/); every
version is available on the [releases page](https://github.com/Sandiozo/ssh-manager/releases).

## [1.7.2] - 2026-10-28

### Security
- The offline backup release key was replaced with a new one.

### Fixed
- An update could leave files of the previous version's server behind; when a package was among them, the
  server failed to start or dropped every connection, and no PC could connect to it. Updates now replace
  the server's files completely, and the server no longer picks optional packages up by itself.
- The "program folder can be changed by NT SERVICE\TrustedInstaller" warning no longer shows: Windows' own
  installer service is trusted, as intended.
- All PCs: the Computers table no longer scrolls sideways in a window about 1400 pixels wide; a computer's
  details line shows in full on hover.
- Activity lists: the type ("POWERSHELL") no longer runs into the computer name.

## [1.7.1] - 2026-10-28

The 1.7.0 installer published on GitHub was wrongly flagged by Microsoft Defender as a trojan (a
machine-learning guess about an installer that sets up remote administration). 1.7.1 contains the same
program as 1.7.0, built again, plus:

### Security
- Updates are also accepted when signed with a second release key that is kept offline, so a lost or
  stolen release key can be replaced without breaking updates.
- Releases are published only after the maintainer approves the build and has checked the installer.

## [1.7.0] - 2026-10-28

A security and reliability release, with a new look.

### Security
- Updates are signed. SSH Manager installs an update only when its release signature and checksum both
  match; the server keeps downloaded installers in a folder only administrators can change.
- A PC's dashboard can no longer change the app: adding PCs, setup steps, the app's settings and updates
  only work from the app's own pages. Folder syncs a dashboard asks for are confirmed in a Windows dialog,
  and system and private folders (Windows, Program Files, AppData, `.ssh`, Startup) can't be synced.
- Links in pages open in your browser only after you confirm, and only web links.
- Folder sync refuses file paths from a PC that would land outside the synced folder.
- Each PC's dashboard only accepts its own login cookie, which pages can't read, so one PC no longer
  receives another PC's access token.
- The server's home folder (access token, settings, plugins) is made private to you, administrators and
  the system, and plugins are not loaded from a folder other accounts can change.
- Before handing out its access token, SSH Manager checks that the program on the server's port really is
  its server.
- AI agents can no longer approve their own requests, answer pairing requests, use the clipboard, restart
  the server, install updates, or read SSH Manager's token, settings and plugins or the PC's SSH keys.
  `sshmgr mcp` always identifies itself as an agent, and running the script of an "ask first" action
  directly asks too.
- Pairing prompts show the computer that really asked, with its address and key fingerprint; after Deny,
  the same computer has to wait before asking again.
- Before you sign in to a new PC with a password, the app shows the PC's host key to compare.
- Shared clipboard skips text that password managers mark as private.
- Uninstalling removes SSH Manager's firewall rule and offers to remove the SSH keys it added and to turn
  off OpenSSH Server (`/REMOVEACCESS` for silent uninstalls).
- Names shown on the app's own pages can no longer inject script.

### Changed
- New look for the dashboard and the app's pages: a calmer dark theme and a light theme, a table of
  computers instead of cards, and a logins chart with hover details. **App settings** (bottom of the left
  bar) has the app's own options, including Light, Dark or follow Windows.
- Two-way folder sync remembers what it synced last: if a file changed on both sides, both copies are kept;
  a file deleted on the PC goes to the Recycle Bin here; a file you delete here isn't fetched again; one
  failing file no longer stops the sync.
- Automatic updates wait until no commands or file transfers are running; a failed update is retried
  later instead of over and over. Updating stops only SSH Manager's own programs, reopens the app for
  everyone who had it open, keeps `sshmgr` sessions of AI agents running, and starts the server again if
  setup fails. Silent updates no longer reconfigure OpenSSH Server or the firewall.
- AI agents: runs no longer time out in the agent's tool. After about 40 seconds the tool returns the run's
  id and the run keeps going (`run_status`, new `list_runs`). New tools `read_file`, `make_folder`,
  `rename` and `delete` (to the Recycle Bin). Files an agent saves or uploads on this computer must be in
  its working folder, the temp folder or `SSHMGR_MCP_ROOT`.
- `sshmgr run`: options go before the PC and everything after it is the script; new `sshmgr runs <pc>`;
  exit code 4 when the connection was lost while a run may still be going; Ctrl+C twice quits at once.
- Runs accept a time limit (`timeout`) that the PC enforces.
- SSH Manager now uses .NET 10 (the installer downloads it if needed).

### Fixed
- The server stopped accepting connections after a dropped SSH tunnel.
- The dashboard stalled for up to 30 seconds when the Windows event log was slow.
- A damaged `config.json` stopped the server; it now starts with the last good settings and says so.
- A damaged `settings.json` could wipe the app's PCs and folder syncs; settings are now saved with a backup.
- SSH keys and settings files are saved safely, so a crash mid-save can't lock anyone out.
- Logs, the activity list and run output grew without limit.
- `sshmgr` crashed on exit codes like -1 or 0xC0000005 and showed stack traces.
- Text files in ANSI or UTF-16 encoding are read correctly by agents and the CLI.
- Shared clipboard bounced the same text back and forth between two PCs.
- The app reconnects right after sleep or a network change.
- Uploading to a folder's name gave a misleading error.
- Folder downloads start right away, skip folder loops and list the files they couldn't include.
- Deleting tells you when a file was deleted permanently because its drive has no Recycle Bin.
- Session tracking with user names that contain spaces or a non-standard SSH port; reachability checks on
  every Windows language.
- A server started over SSH moves to your desktop session when you sign in, so screen preview and
  clipboard work.
- The installer hung for minutes when PowerShell scripts were restricted by Group Policy, and explains
  Windows Update / WSUS problems with OpenSSH.
- The firewall check notices when SSH is still blocked, for example on a network set to Public.
- The same PC added twice (by name and by address) can be merged; the window no longer opens off-screen
  after a monitor was removed; only one copy of the app runs per user.

## [1.6.0] - 2026-10-19

### Added
- **AI agents:** `sshmgr mcp` is an MCP server, so Claude Code, Codex and other MCP clients can use the PCs
  the app is connected to as tools: list PCs and their status, run PowerShell (with streamed progress,
  timeouts, background runs and long output shortened to its start and end), run quick actions, take
  screenshots (returned as images), list windows and folders, read logs, download and upload files and wait
  for a file or program. Setup for Claude Code and Codex is in the README under **Use with AI agents**.
- Agents are named: the MCP server (and `sshmgr --as <name>`, or `SSHMGR_AGENT`) tells each PC which agent
  is calling, so Activity, Live sessions and All PCs show for example "Claude (LAPTOP)". Inside Claude
  Code, `sshmgr` names itself Claude.
- Settings → **AI agents** on each PC: full access (default), read-only (status, logs, files, downloads,
  screenshots) or off, checked by that PC's server. Agents can never change settings, SSH keys or pairing.
- Quick actions marked "ask first" that an agent starts wait until someone clicks **Allow** in an SSH
  Manager app (on that PC or on the agent's computer); they don't run on **Deny** or after two minutes
  without an answer. Requests and answers are in the activity log.
- A run's status tells where its complete output is kept on the PC (`output_file`).

### Fixed
- PowerShell errors in runs (command panel, quick actions, `sshmgr run`) showed as unreadable `#< CLIXML`
  blocks. They are now shown as plain text.

## [1.5.1] - 2026-10-19

### Fixed
- `sshmgr` refused to work from an administrator (elevated) window with "belongs to another account".
  It now checks that the app's connection point belongs to the same Windows account, elevated or not.

## [1.5.0] - 2026-10-19

### Added
- `sshmgr`, a command-line tool for scripts and AI agents, installed with the app and added to `PATH`.
  It uses the PCs the app is connected to through the app's own connections, so it needs no SSH
  setup or access token of its own: list PCs, run PowerShell with streamed output and the script's exit
  code, run quick actions, take screenshots, download and upload files, and wait for a file or program.
  The app answers it over a local pipe that only the same Windows account can open. See **Command
  line** in the README.
- **Restart server** (Settings → Updates): restarts that PC's server so changes made to `config.json` by
  hand and new or changed plugins take effect. It refuses when `config.json` isn't valid JSON.

## [1.4.2] - 2026-10-19

### Fixed
- Live sessions labelled every port forward as "SSH Manager connection", so a computer could appear
  twice with the same label. The app's own connection is now marked **app**; a port forward used by a
  script or tool is marked **port forward**.

## [1.4.1] - 2026-10-19

### Changed
- Switching between PCs is instant and no longer flashes: each connected PC's dashboard stays loaded
  (keeping its page and scroll position), and a PC opened for the first time appears once it is drawn.

### Fixed
- "Unknown computer logged in" alerts for computers that aren't unknown: logins with a key allowed on
  that PC (for example through Approve on the other PC) or from a PC whose SSH Manager app reports in
  are no longer counted as unknown.

## [1.4.0] - 2026-10-19

### Added
- Lossless screenshots: `format=png` on `/api/screen`, full size with `width=0`, and JPEG quality up
  to 100 (sharper edges at high quality).
- `/api/wait`: wait until a file appears or changes, or until a program starts or exits, instead of
  polling in a loop.
- Runs: the complete output can be downloaded (`/api/runs/{id}/output`); a run can get a working
  directory and extra environment variables.
- Screen privacy settings: allow single app windows only, and/or limit viewing to a list of apps. The
  server enforces both, for the dashboard and the API.
- An Automation API section in the README.

### Changed
- Run titles over 200 characters are shortened instead of rejected.

### Fixed
- When a long run dropped its oldest output lines, reading "the lines after N" could skip or repeat
  lines. Line numbers now always count from the start of the run.

## [1.3.13] - 2026-10-19

### Added
- **All PCs** now combines every connected PC: totals for the last 24 hours (logins, failed logins,
  commands, data transferred), SSH logins per hour with a color per PC, everyone connected anywhere, and
  one activity timeline across all PCs with filters for PC, type and text.
- A **Runs** filter on the Activity page.

### Fixed
- Runs started from the Run page showed as "undefined" on the Activity page.
- SSH sessions from a computer that isn't in a PC's computer list are now named after its SSH Manager app
  instead of showing only its IP address.

## [1.3.12] - 2026-10-19

### Fixed
- Shortcuts and taskbar pins kept showing the previous icon after an update: the installer now refreshes
  Windows' icon cache after installs and silent updates.

## [1.3.11] - 2026-10-19

### Changed
- New icon and logo for the app, the tray (with a dot showing the connection state), the installer and
  the dashboard.

## [1.3.10] - 2026-10-19

### Changed
- A PC added by its IP address also remembers its computer name. Reconnecting alternates between the name
  and the last known address, and the saved address is kept up to date, so a new address from the router
  no longer loses the PC.

## [1.3.9] - 2026-10-19

### Added
- **Approve on the other PC:** add a PC without a password by clicking **Allow** on that PC, when its app
  is connected to this one. Requests expire after two minutes and are logged on both PCs.
- **Copy the key:** for PCs that only allow PIN / Windows Hello sign-in.

### Changed
- A key that already works (in `~/.ssh` or `~/.ssh/config`) is used as it is.
- Adding a PC that is already in the list updates it instead of adding it twice.

## [1.3.8] - 2026-10-19

### Changed
- The password for adding a PC is typed on the setup page instead of in a console window. It is used once
  and never stored.
- The setup page suggests the PC's Windows user name.

## [1.3.7] - 2026-10-19

### Changed
- Settings moved behind the ⚙ icon in the top bar, with a compact layout showing one section at a time.
- **All PCs** only appears when there are at least two PCs.

### Fixed
- Menu items sometimes needed several clicks: clicks on their icon or label now count too.

## [1.3.6] - 2026-10-19

### Changed
- Computers installed as Client appear as cards in **All PCs** instead of in the PC list.
- No Screen page for the PC the app runs on.

## [1.3.5] - 2026-10-19

### Fixed
- The server used far more CPU than needed while dashboards were open (about four times less now).

## [1.3.4] - 2026-10-19

### Added
- Computers installed as Client are shown with their CPU, memory, disks and battery.

### Fixed
- A garbled character in the window title.

## [1.3.3] - 2026-10-19

### Added
- The screen preview can show a single app window, even when other windows cover it.

## [1.3.2] - 2026-10-19

### Fixed
- After an update the Screen page could stay on "Loading": the dashboard now always loads the files of the
  installed version.

## [1.3.1] - 2026-10-19

### Fixed
- The server now starts in the signed-in user's session, so the screen preview and shared clipboard work
  right after an update.

## [1.3.0] - 2026-10-19

### Added
- **Run:** quick actions and a command panel with live output.
- **Logs:** follow log files live, with filtering.
- **Screen:** live view-only screen preview.
- **Folder sync** between this computer and a PC, one way or both.
- **Shared clipboard** (text).
- **Wake-on-LAN** for PCs that are asleep or off.

## [1.2.0] - 2026-10-19

### Changed
- New compact design with a side bar.
- The installer asks for one of two roles: **Client** or **Client and server**.

## [1.1.5] - 2026-10-19

### Fixed
- After an update the app only reopens once setup has finished.

## [1.1.4] - 2026-10-19

### Fixed
- The installer does exactly what the role page says.

## [1.1.3] - 2026-10-19

### Added
- A "What should this PC do?" page in the installer.

## [1.1.2] - 2026-10-19

### Added
- The installer shows what it is doing (and the download progress) while it sets up a PC.

## [1.1.1] - 2026-10-19

### Added
- Change a PC's roles by running the installer again.

## [1.1.0] - 2026-10-19

### Added
- One installer for every PC, with its dependencies.
- Several PCs in one app.
- Automatic updates.

## [1.0.0] - 2026-10-19

### Added
- First release: dashboard with live sessions, activity log and connection history, file transfer and a
  tray app for Windows' OpenSSH Server.

[1.7.2]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.7.2
[1.7.1]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.7.1
[1.7.0]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.7.0
[1.6.0]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.6.0
[1.5.1]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.5.1
[1.5.0]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.5.0
[1.4.2]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.4.2
[1.4.1]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.4.1
[1.4.0]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.4.0
[1.3.13]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.3.13
[1.3.12]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.3.12
[1.3.11]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.3.11
[1.3.10]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.3.10
[1.3.9]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.3.9
[1.3.8]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.3.8
[1.3.7]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.3.7
[1.3.6]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.3.6
[1.3.5]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.3.5
[1.3.4]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.3.4
[1.3.3]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.3.3
[1.3.2]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.3.2
[1.3.1]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.3.1
[1.3.0]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.3.0
[1.2.0]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.2.0
[1.1.5]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.1.5
[1.1.4]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.1.4
[1.1.3]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.1.3
[1.1.2]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.1.2
[1.1.1]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.1.1
[1.1.0]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.1.0
[1.0.0]: https://github.com/Sandiozo/ssh-manager/releases/tag/v1.0.0
