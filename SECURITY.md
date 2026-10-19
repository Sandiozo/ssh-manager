# Security policy

SSH Manager can read files, run commands and change which keys may log in on the PCs it manages, so
security reports are taken seriously.

## Supported versions

Only the [latest release](https://github.com/Sandiozo/ssh-manager/releases/latest) receives fixes. PCs
update themselves automatically unless automatic updates were turned off.

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Report them privately through GitHub instead:
open the repository's **Security** tab and click **Report a vulnerability**
([direct link](https://github.com/Sandiozo/ssh-manager/security/advisories/new)).

Include what you found, the version, and the steps to reproduce it. You will get an answer as soon as
possible, and a fix will be released before details are made public.

## Scope

In scope: the app, the server, the installer and the update mechanism. The security model is described in
the README's [Security](README.md#security) section; in short, anyone who can log in to a PC over SSH has
full control of that PC through SSH Manager, by design. Problems with Windows' OpenSSH itself should be
reported to Microsoft.
