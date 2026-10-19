# Changelog

All notable changes to SSH Manager. Versions follow [semantic versioning](https://semver.org/); every
version is available on the [releases page](https://github.com/Sandiozo/ssh-manager/releases).

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
