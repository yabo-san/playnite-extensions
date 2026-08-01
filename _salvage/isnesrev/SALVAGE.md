# iSNESrev — salvaged, not retired

**This does not build here and is not wired up.** It is parked next to the engine
it calls, so both survive the old repo.

## What it was

A standalone Tauri app for the SNES-reimplementation shelf — deliberately NOT
yabo-launcher, and described in its own README as "yabo's one shippable
single-purpose product". The loop:

> drag a SNES ROM in → it becomes a playable game → configure shader + controls → Play

The drop forwarded the file to the yabo engine's `--ingest`, which hashed it
(SHA-1), reverse-looked-up the catalog card whose `dataFiles` declared that hash,
installed the port if needed, and BPS-patched the data into place.

## Why it is parked

The Tauri shell is a standalone launcher UI, and that direction is retired. The
backend it depends on — `--ingest`, `--list-json`, the gate — is the engine now
sitting in `../yabo-engine`. Neither half stands alone, so both are kept together.

## What is actually worth reviving

Three differentiators, and the first is the real one:

1. **The rebindable keybind UI.** These reimplementation ports notoriously cannot
   rebind keys; `zelda3.ini` carries a sectioned `[KeyMap]` that has to be edited
   by hand. Nothing else solves this. It is a genuine gap, and it does NOT need a
   Tauri app — it needs to be attached to a game Playnite already knows about.
2. A bundled **GLSL shader picker**.
3. One-button **"Create Steam shortcut"**, so the port lands in Steam and
   therefore in Playnite. Worth noting this was already FEEDING Playnite rather
   than competing with it — which is the argument for the whole thing becoming an
   extension.

The obvious shape if it returns: a Playnite extension in the same mould as
GlazeWM — right-click a snesrev game, rebind its keys, pick a shader. Ingest
(drag-a-ROM) is a product decision rather than a port of this code, and stays
with the engine.

## What is here

686 lines of hand-written source:

| | lines |
|---|---|
| `src/app.js` | 324 |
| `src-tauri/src/lib.rs` | 103 |
| `src/bridge.js` | 96 |
| `src/index.html` | 83 |
| `src/styles.css` | 76 |
| `src-tauri/src/main.rs` | 4 |

Plus `package.json`, `Cargo.toml`, `tauri.conf.json`, `build.rs` and the
capabilities manifest.

**Not copied**, because all of it regenerates: `src-tauri/icons/` (50 files of
`tauri init` scaffold), `src-tauri/gen/schemas/` (generated), and `target/` +
`node_modules/` (2.6 GB of build output). Run `tauri init` if the shell is ever
rebuilt.

`src/bridge.js` is the WORKING-TREE version, which carries an uncommitted
one-line fix that was never committed to the old repo: `--list-json` gained
`--all`, so it asks the engine for the full catalog rather than the gated subset.
That fix is real and is preserved here deliberately.
