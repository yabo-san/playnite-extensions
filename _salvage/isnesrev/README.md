# iSNESrev

A **standalone** Tauri app (NOT yabo-launcher) for the SNES-reimplementation shelf. yabo's one
shippable single-purpose product.

Core loop: **drag a SNES ROM in → it becomes a playable game → configure shader + controls → Play.**
The drop forwards the file to the yabo engine's `--ingest` command, which hashes it (SHA-1), reverse-
looks-up the catalog card whose `dataFiles` declare that hash, installs the port if needed, and
BPS-patches the data into place. From the shelf you click a game to open its config view.

iSNESrev's differentiators (vs the engine alone): a **rebindable keybind UI** (these reimpl ports
infamously can't rebind keys; `zelda3.ini` has a sectioned `[KeyMap]`), a **bundled GLSL shader
picker**, and a one-button **"Create Steam shortcut"** so the port lands in Steam → Playnite.

## Views

**Home** — drag-a-ROM dropzone + "Pick a ROM…" + the snesrev shelf grid (from `--list-json`,
filtered to snesrev repos / `.sfc`/`.smc` dataFiles). Click any card to open its config.

**Game** (per game, three tabs):
- **Controls** — reads `[KeyMap] Controls` from the port's `.ini` (`--get-config`), renders the 12
  button slots (Up,Down,Left,Right,Select,Start,A,B,X,Y,L,R). **Click a slot, press a key → it
  rebinds and saves immediately** via `--set-config "<game>" "KeyMap.Controls" "<csv>"`.
- **Shader** — built-in GLSL shaders from `--list-glsl-shaders --json`. Picking one **applies + saves**
  it to `Graphics.Shader` via `--set-config` (and reflects the game's current value on load).
- **Play** — **▶ Play** runs the unified engine `--play "<game>"` (install→stage→config→shader→launch),
  and **＋ Create Steam shortcut** adds a non-Steam shortcut (see below).

## Deep-link (per-game focus, for Playnite)

Launch the app scoped to one game + tab:

```
isnesrev.exe --game <folderName> --tab <shaders|controls|play>
```

- `--game` accepts the port's **folderName OR display name** (the engine's `FindGame` matches either).
- `--tab` is optional; it defaults to **`controls`** when `--game` is given without a tab. Unknown tab
  values fall back to `controls`.
- With no `--game`, the app opens the normal Home view.

Parsing happens Rust-side (`src-tauri/src/lib.rs` → `parse_launch_args`, exposed via the
`launch_args` Tauri command); the frontend calls `bridge.launchArgs()` on boot and routes to
`openGame(game, tab)`. This lets Playnite open iSNESrev focused on a specific game's config.

Example (Playnite "Configure" action): `isnesrev.exe --game zelda3 --tab controls`

## "Create Steam shortcut"

The **Play** tab's button shells the engine's existing
`--add-steam-shortcut "<name>"` command (in `Services/CLIHandler.cs` →
`Services/SteamShortcutService.cs`). The engine resolves the port, writes a non-Steam shortcut into
every Steam profile's `shortcuts.vdf` (backing it up first), and points its launch options back at
the engine's own `--play "<name>"`. **Steam must be restarted** to pick up `shortcuts.vdf` (the
engine prints that reminder, which we surface). Once imported, Playnite's Steam library sees it too.

No new engine command was needed — `--add-steam-shortcut` already exists.

## Architecture

- `src/` — web frontend (vanilla HTML/CSS/JS, no framework).
  - `bridge.js` — the ONLY link to the engine. Spawns `yabo-launcher.exe` via Tauri's shell plugin
    (scope `engine`, declared in `src-tauri/capabilities/default.json`). Adds `play`,
    `addSteamShortcut`, `listSteamShortcuts`, and `launchArgs`.
  - `app.js` — wiring (drag-drop, shelf grid, game view, tabs, rebind, shader apply, play, steam).
- `src-tauri/` — Rust shell: hosts the webview, exposes `ui_log` + `launch_args` commands. Drag-drop
  is handled natively by Tauri (`dragDropEnabled: true`); no custom Rust needed.

The engine path is **hard-wired** to `D:\games\yabo-portable\yabo-launcher.exe` in
`src-tauri/capabilities/default.json` (the portable deploy). Change it there if you relocate the engine.

## Build / Run

Prereqs: Node (for `@tauri-apps/cli`) and the Rust toolchain (`cargo`).

```powershell
cd D:\REPOS\ports-launcher\isnesrev
npm install            # first time only — pulls @tauri-apps/cli + api/plugins
npm run dev            # dev build with hot webview (opens the iSNESrev window)
# or a release build:
npm run build          # → src-tauri/target/release/isnesrev.exe + an NSIS installer
```

To smoke-test ingest without the GUI, the engine CLI is directly callable:

```powershell
D:\games\yabo-portable\yabo-launcher.exe --ingest "D:\games\yabo-portable\Library\zelda3.sfc"
```
