# CUS — Clipboard Ultra Shared

Share your clipboard in real time with other PCs on the same local network: with colleagues (after they consent) or with yourself on another machine.

![CUS main window](docs/screenshots/main.png)

## Features

- **AirDrop-style sessions:** one PC offers itself as host, others ask to join, and the host accepts each one as two-way or read-only.
- **Everything you copy:** text, images, files and folders (files are actually transferred, not just their paths).
- **Always-visible indicator:** while the clipboard is shared, an always-on-top badge shows who is connected; one click leaves the session.
- **Friends:** trusted peers can join without confirmation, or connect at any time.
- **Sensitive content:** copied passwords go only to friends marked as trusted and never enter the history.
- **End-to-end encrypted:** signed identities, encrypted channel (libsodium), local database protected with DPAPI.

![Shared session](docs/screenshots/session.png)

## Requirements

- Windows 10 1809 or later
- [.NET SDK](https://dotnet.microsoft.com/download) as pinned in `global.json` (to build)

## Build and run

```powershell
dotnet build Cus.slnx
dotnet run --project src/Cus.App.Windows
```

Useful options: `--profile <name>`, `--name <display name>`, `--port <port>` (default 23700), `--peer <host:port>`, `--bind <address>`, `--clipboard real|simulated`, `--no-discovery`.

Try two instances on one PC (separate profiles, simulated clipboard):

```powershell
./scripts/run-two-instances.ps1
```

## Tests

```powershell
dotnet test Cus.slnx
```

## Installer

Builds a self-contained `win-x64` package and an [Inno Setup](https://jrsoftware.org/isinfo.php) installer in `artifacts/`:

```powershell
./scripts/publish.ps1 -Version 0.1.0
```
