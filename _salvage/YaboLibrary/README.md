# Yabo Launcher — Playnite Library plugin (scaffold)

A Playnite **GameLibrary** plugin that imports the yabo-launcher port catalog and
shells out to the engine CLI for all actions. The CLI is the source of truth; this
plugin never touches `apps.json` / the Library folder directly.

Generated as a buildable scaffold from `dev/playnite-extensions-plan.md` (Extension 1).
**Not built or run** here (no Playnite SDK in the authoring env).

## Files

| File | Purpose |
|---|---|
| `extension.yaml` | Manifest: `Type: GameLibrary`, string `Id`, `Module: YaboLibrary.dll`, `Icon`. |
| `YaboLibrary.csproj` | net462 class lib, `PackageReference PlayniteSDK`, outputs `YaboLibrary.dll`. |
| `YaboLibraryPlugin.cs` | `: LibraryPlugin`. Fixed `Id` Guid, `GetGames`, install/uninstall/play actions, `GetMetadataDownloader`, settings hooks. |
| `YaboCatalog.cs` | Catalog model (`--list-json` shape) + `YaboCli` process runner (stdout capture, cancel-aware). |
| `YaboControllers.cs` | `YaboInstallController` (`--download` → `InvokeOnInstalled`) and `YaboUninstallController` (`--uninstall` → `InvokeOnUninstalled`). |
| `YaboMetadataProvider.cs` | `LibraryMetadataProvider` re-emitting our `cover` so "Store" locks the box vs SteamGridDB. |
| `YaboLibrarySettings.cs` | Settings model + `ISettings` view-model (exe path, import-hidden; persist + verify). |
| `YaboLibrarySettingsView.xaml(.cs)` | WPF settings view. |

## What compiles-conceptually (logic is complete, SDK-API-pending)

- **Import (P0/P2):** `GetGames` runs `--list-json`, parses the catalog, and yields one
  `GameMetadata` per entry with `GameId = folderName`, `Name`, `Source = MetadataNameProperty("Yabo Launcher")`,
  `CoverImage = MetadataFile(cover)`, `IsInstalled`, repo/link entries.
  - Multi-game ports (`games[]` length > 1) → multiple `GameAction { Type=File, Path=exe,
    Arguments=--play "<name>" --rom <g>, IsPlayAction=true }` = the Play-button dropdown.
  - Link-outs (`externalUrl`) → one `GameAction Type=URL`, `IsInstalled=false`.
  - `args.CancelToken` honored in the import loop and the process wait.
- **Cover lock (P0):** both layers — direct `CoverImage` at import **and** `GetMetadataDownloader`
  → `YaboMetadataProvider` re-emitting `cover` (the "Store" source outranks SGDB).
- **Controllers (P1):** install/uninstall shell out and call `InvokeOnInstalled`/`InvokeOnUninstalled`;
  single-game ports also get an `AutomaticPlayController` (`TrackingMode=Process`) for playtime tracking.
- **Settings (P3 partial):** exe-path + import-hidden, with `VerifySettings` validating the exe.

## OWNER must finish / verify

1. **Pin the PlayniteSDK version.** `YaboLibrary.csproj` references `PlayniteSDK Version="*"`.
   Set it to the version matching your Playnite release, then restore/build.
2. **Set the exe path.** There is no auto-discovery — open the plugin settings and point
   `ExePath` at the real `yabo-launcher.exe` (the build that ships the catalog). Until then,
   import yields nothing (logged, no crash).
3. **Pack to `.pext`.** `Toolbox.exe pack <this-dir> <out-dir>` after a Release build. Add an
   `icon.png` to the folder (manifest `Icon: icon.png`); none is shipped yet.
4. **The Guid is FIXED — do not change it.** `Id = 1a390433-18ff-42bd-8741-8c1778a7cab9`.
   It becomes every game's `PluginId`; changing it orphans the library.
5. **`build-all.ps1` / solution wiring.** This project is standalone and intentionally not
   added to `GithubLauncher.sln` (it targets net462 + the Playnite SDK, separate from the
   Avalonia engine). Wire it into your own build/pack pipeline if desired.

## Spots I was unsure of (verify against your SDK build)

- **`AutomaticPlayActionType.File`** (in `GetPlayActions`) — the design doc names this enum
  member; if your SDK exposes it differently (e.g. `GenericPlayActionType`), adjust the one
  line at `YaboLibraryPlugin.GetPlayActions`. This was the explicit "pick one and note it" case.
- **`InvokeOnInstalled(new GameInstalledEventArgs(new GameInstallationData{...}))`** and
  **`InvokeOnUninstalled(new GameUninstalledEventArgs())`** — exact ctor/arg shapes vary slightly
  across SDK versions. If they don't resolve, check `InstallController`/`UninstallController` base.
- **CLI name vs folderName identity.** `GameId = folderName` (stable). But `--play/--download/--uninstall`
  take the catalog **`name`**, not `folderName`. The scaffold uses the Playnite display name
  (== catalog name at import) as the CLI argument via `ResolveCliName`. If a user renames a game in
  Playnite, that breaks. **Harden** by persisting the catalog `name` per game (e.g. a plugin
  game record keyed by `GameId`, or a hidden field) instead of trusting the display name.
- **Install dir resolution.** `YaboInstallController` derives the install dir from `--path "<name>"`
  (prints the exe; its folder = install dir). If your CLI prints differently, adjust `ResolveInstallDir`.
- **`--list-json` field names.** Modeled from the design doc / task spec
  (`name, folderName, repository, category, status, cover, externalUrl, dataState, dataFiles[], games[]`).
  Confirm `status`'s exact installed/not-installed string values against real output — `IsInstalled`
  treats anything other than `notInstalled`/`not-installed` as installed.
- **Stale-prune (P3) not implemented.** The diff-and-remove-stale-on-refresh step from the design
  doc is left out (it's opt-in/off-by-default there and needs DB access tuning).
- **Newtonsoft.Json** is assumed available transitively via the Playnite SDK (Playnite ships it).
  If the build can't find it, add an explicit `PackageReference Include="Newtonsoft.Json"`.
