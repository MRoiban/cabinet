<p align="center"><img src="icon/cabinet-1024.png" width="128" alt="Cabinet icon"></p>

<h1 align="center">Cabinet</h1>

<p align="center">Keeps your Steam achievements, stats and playtime filed under the right game, even when an OnlineFix patch makes the game run as Spacewar.</p>

## Install

Open PowerShell and run:

```powershell
irm https://raw.githubusercontent.com/MRoiban/cabinet/main/install.ps1 | iex
```

Or download `Cabinet.exe` from the [latest release](https://github.com/MRoiban/cabinet/releases/latest) and double-click it.

That's it. Cabinet installs itself to `%LOCALAPPDATA%\Cabinet`, starts with Windows, lives in the tray (behind the `^` arrow), and updates itself from this repo's releases.

## What it does

Games patched with OnlineFix talk to Steam as AppID 480 (Spacewar), so Steam credits your hours and achievements to Spacewar instead of the real game. Cabinet fixes that:

- **Live playtime**: while the game runs, a hidden helper connects to Steam as the real game, so Steam counts the hours itself and shows them right away.
- **Achievements and stats**: copied from OnlineFix's local files (`C:\Users\Public\Documents\OnlineFix\<appid>\Stats`) into the real game's entry in the Steam client.
- **Missing achievement lists**: if Steam has never downloaded a game's achievement list, Cabinet fetches it automatically.
- **Overview**: right-click the tray icon → *Games* for achievements unlocked, hours played and last sync per game.
- **Fallback**: if live tracking can't start, playtime is recorded and added to Steam the next time Steam is closed.

Achievements live in your local Steam client. Steam only uploads them to your public profile for games your account owns.

## Tray menu

| Item | |
|---|---|
| Games | Per-game achievements, hours, last sync. Click to open in Steam. |
| Sync now | Force a full sync. |
| Check for updates | Install the latest release now. |
| Open log | `%LOCALAPPDATA%\Cabinet\log.txt` |
| Uninstall | Remove from startup. |

Before Cabinet first changes a Steam file, it keeps a copy in `%LOCALAPPDATA%\Cabinet\backup`.

## Build from source

Needs only Windows (uses the .NET Framework compiler that ships with it):

```cmd
build.cmd
```

This produces `Cabinet.exe` (tray app) and `Cabinet-cli.exe` (debug commands: `--version`, `--sync <appid>`, `--dump <file>`, `--running`). Every push to `main` is built by GitHub Actions and published as a new release.
