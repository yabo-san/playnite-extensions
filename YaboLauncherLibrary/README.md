# y4bo Launcher for Playnite

A Playnite library plugin for the y4bo launcher. It reads the launcher's
`playnite-export.json` (never `library.db`), keeps Playnite in step with it,
and asks the launcher to install, uninstall or open a game.

It replaces **YaboLibrary**: same extension Id and same plugin Guid
(`1a390433-18ff-42bd-8741-8c1778a7cab9`), so installing it upgrades that
extension in place and every game YaboLibrary imported, with its art, stays
attached.

## Install

1. Download `YaboLauncherLibrary_<version>.pext` from the
   [releases](https://github.com/yabo-san/playnite-extensions/releases)
   (tags `yabo-launcher-library-v*`).
2. Open it (or drag it onto Playnite) and restart Playnite when asked.
3. Start the y4bo launcher once, so `playnite-export.json` exists, then run
   **Update Game Library** in Playnite.

## Settings

Add-ons, Extension settings, y4bo Launcher. Both paths can stay empty.

| setting | default | what |
|---|---|---|
| Export path | `%APPDATA%\rohankar-launcher\playnite-export.json` | The file the launcher writes next to `library.db`. |
| Launcher exe | `%LOCALAPPDATA%\Programs\rohankar-launcher\RohanKar Launcher.exe` | Run with `--install`, `--uninstall` and `--launch`. |

Saving checks that the exe exists and that the export's folder does.

## What refreshes when

- **The launcher changes its library** (install, uninstall, add, remove,
  favourite, collections, a launch): it rewrites the export, the plugin sees
  the file change and updates Playnite within a second. New games are
  imported; existing ones get install state, install folder, Play action,
  playtime and last played.
- **Playnite starts** or you run **Update Game Library**: the whole export is
  read again.
- **Never touched on a game you already have:** its name, cover, background,
  tags and anything else you edited in Playnite. A cover or background is only
  set from the launcher on a game that has none.
- **A game the export stops listing** stays in Playnite, marked not installed.

## Actions

- **Play** runs the game's exe with its arguments in its working folder.
- **Install** runs `launcher --install <id>`. If the item needs a choice (a
  title with several archives, a port), the launcher opens on it instead; the
  install finishes in Playnite as soon as the export shows it installed.
- **Uninstall** runs `launcher --uninstall <id>`: the folder goes to the
  Recycle Bin, the entry stays in the launcher.
- **Play on a game with nothing to run** runs `launcher --launch <id>`, which
  opens the launcher on that item.

## Ids

GameIds are the export's ids (archive.org identifier, `quiver:<repository>`,
or a UUID for a manual entry). A game YaboLibrary imported keeps its old
GameId: a record is matched to it by the port's `folderName`, then by a link to
its repository, then by name, and the match is remembered in `game-ids.json`
in the plugin's data folder. The export format is documented in the launcher,
`docs/PLAYNITE-EXPORT.md`.

## Build and test

```
dotnet test YaboLauncherLibrary.Tests            # parser, id matching, GameMetadata mapping
dotnet build YaboLauncherLibrary -c Release      # -> bin/Release/net462/YaboLauncherLibrary.dll
bash YaboLauncherLibrary/pack.sh                 # -> bin/YaboLauncherLibrary_<version>.pext
```

Both run on Linux or Windows; the tests use a fixture export and no Playnite.
