# _salvage — the yabo-launcher engine, kept on purpose

**Nothing in here builds, and nothing in here is wired up.** It is reference
source, parked so it survives independently of the repo it came from.

## Why it is here

`yabo-launcher` (a fork of SirDiabo/GithubLauncher, checked out locally as
`ports-launcher`) is dead as a standalone application. It is not being rebuilt.

But one part of it is worth keeping: the **collision / marriage** model. If that
ever comes back, it comes back as a Playnite extension shipped with a bootstrap
script — and *that* would be what "yabo-launcher" means from then on. The name is
reserved for that, not for the old app.

## What "collision / marriage" means

The launcher's job was fetching game ports from the internet, and ports come in
three shapes:

1. **Self-contained** — an Internet Archive repack that already holds everything.
2. **Self-contained** — a GitHub release binary that already holds everything.
3. **Married** — a GitHub engine release that needs game DATA it cannot legally
   ship, so the engine and its data are fetched separately and combined on disk.

The third is the fragile, interesting one, and the reason this code is kept.
Two sources land in one folder and can **collide** — same filenames, different
provenance — so the merge has to be deliberate rather than a blind copy. The
proven implementation is `EnsureMarriedDataAsync`.

Read in this order:

- `Models/GameInfo.cs` — the model. Where a game comes from, what it needs, and
  what "married" means for it. 24 references to the marriage/collision path.
- `Services/InternetArchiveInstallService.cs` — the IA side, and the merge
  itself. 12 references.
- `Services/CLIHandler.cs` — how it was all orchestrated.

## Other pieces worth not losing

- `Services/SteamContentLocator.cs` — finds installed Steam apps by appid. Reuse
  it rather than hand-rolling discovery.
- `Services/InstallAdoptionService.cs` — adopting games already on disk by
  scanning folders. Same problem the RohanKar extension solves; worth diffing the
  two approaches.
- `Services/GateService.cs` — the gate/staging model the whole engine ran on.
- `Services/BpsPatchService.cs` — BPS patching, needed by the SNES decomp ports.
- `Services/FeedSyncService.cs` — the publish/subscribe catalog feed.

## isnesrev

`isnesrev/` is here too, and it is a different case — see its `SALVAGE.md`.

It was a standalone Tauri app for the SNES-reimplementation shelf, deliberately
NOT yabo-launcher, built around "drag a SNES ROM in → it becomes a playable
game". Its Tauri shell is retired along with every other standalone launcher UI,
but it called this engine's `--ingest` / `--list-json` / gate, so the two halves
only make sense together.

The part worth reviving is its **rebindable keybind UI** — these reimplementation
ports notoriously cannot rebind keys (`zelda3.ini` has a hand-edited `[KeyMap]`),
nothing else solves it, and solving it does not require an app. It wants to be a
Playnite extension in the same mould as GlazeWM.

## What was NOT salvaged

The Tauri UI (`ui/`), the admin console, the installer, the shaders and the
assets all stay in the old repo. They are the parts that made it an application,
and the application is what is being retired.

The old repo is not deleted — this is a hedge against it being cleaned up later,
not a replacement for it.
