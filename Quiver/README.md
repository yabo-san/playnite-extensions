# Quiver for Playnite

A Playnite library plugin for [Quiver Launcher](https://github.com/tgeorgiadis/quiver-launcher),
the GithubLauncher fork that installs console recompilations and ports from GitHub and GitLab
releases. Quiver does the fetching; this plugin puts a card in front of each app.

## What it reads

Quiver keeps its state in plain files, so no console output is parsed:

| file | meaning |
| --- | --- |
| `<root>\apps.json` | the app list: name, project, repository, folderName, installPath, icon, tags |
| `<root>\Apps\<folderName>\version.txt` | the app is installed (absent while a download is unfinished) |
| `<root>\Apps\<folderName>\selected_executable.txt` | the exe the user picked when there were several |

`<root>` is `%LOCALAPPDATA%\QuiverLauncher` for the normal (Velopack) install. A portable
Quiver keeps the files beside its exe; set that folder in the plugin settings.

## What it does

- one card per app, installed when Quiver says so, version from `version.txt`
- **Play** runs the selected exe directly; turn on "Play through Quiver" to run
  `QuiverLauncher --run "<name>"` instead, which updates the app first
- **Install** and **Uninstall** call Quiver's `--download` / `--uninstall` and read the result off disk

If an app has several exes and none was picked yet, Play goes through Quiver so its own picker asks.

## Requires

Quiver Launcher. Nothing else.
