# Contributing to SSH Manager

Thanks for helping! Bug reports, ideas and pull requests are all welcome.

## Reporting bugs and requesting features

- Search the [existing issues](https://github.com/Sandiozo/ssh-manager/issues) first.
- Use the issue forms: they ask for the version, Windows version and logs that are needed to look into a
  problem.
- Security problems: please follow [SECURITY.md](SECURITY.md) instead of opening a public issue.

## Project layout

| Path | What it is |
|---|---|
| `server.py`, `sshmgr/` | The server (Python, FastAPI): status, activity log, files, run, logs, screen, pairing, updates. |
| `static/` | The dashboard (plain HTML, CSS and JavaScript, no build step). |
| `app/SSHManager/` | The Windows app (C#, .NET 10, WinForms + WebView2): tunnels, tray, setup pages, sync, clipboard. |
| `app/HiddenRun/` | Small helper that starts a program without a window. |
| `app/SshMgrCli/` | `sshmgr.exe`, the command-line tool: asks the app for a PC's connection over a named pipe (`CliPipe.cs` in the app) and calls that PC's API. |
| `installer/` | Inno Setup script and the PowerShell scripts it runs (server setup, stop, app restart). |
| `plugins/` | Example server plugin. |
| `build.ps1`, `server.spec` | Builds the server (PyInstaller), the app and the installer. |

## Development setup

1. Python 3.11+, the .NET 10 SDK and (for the installer) Inno Setup 6.5 or newer.
2. Server: `python -m venv .venv`, `.venv\Scripts\pip install -r requirements.txt`, then
   `.venv\Scripts\python server.py`. It prints a login URL for a browser. Use `SSHMGR_HOME` to keep a
   test configuration away from your real one.
3. App: `dotnet build app\SSHManager -c Release`.
4. Everything, including the installer: `.\build.ps1`. The installer ends up in `build\`.

## Pull requests

- Keep each pull request focused on one change and describe what it does and how you tested it.
- Match the style of the surrounding code: small functions, comments that explain why, and plain,
  neutral wording in anything users see.
- Test on Windows 10 or 11, ideally between two PCs (one with **Client and server**).
- Don't bump the version; releases are cut by the maintainer.

## Releases

The version lives in `sshmgr/__init__.py`. Pushing a tag `vX.Y.Z` that matches it builds, tests and
signs the installer with GitHub Actions (after the maintainer approves the run) and creates a draft release,
using that version's section of [CHANGELOG.md](CHANGELOG.md) as the release notes. The maintainer checks the
installer (for example with Microsoft Defender) and then publishes the draft.

By contributing you agree that your contributions are licensed under the [MIT license](LICENSE).
