# playnite-extensions

Playnite extensions and themes. Playnite is the hub; each extension teaches it
about one thing it does not already know.

These lived in `yabo-san/yabo-launcher` (checked out locally as `ports-launcher`),
which is a **fork of SirDiabo/GithubLauncher**. That was a problem beyond the
confusing name: a GithubLauncher *extension* sitting inside a fork *of*
GithubLauncher is a trap, and nothing here could ever be published without
dragging someone else's git history along. The standalone launcher is not being
rebuilt, so the live work moved out and the old repo keeps the frozen launcher.

History was not carried across — it is a fork's history, not this project's. What
matters is in the commit messages, and the originals remain in the old repo.

## Library extensions

Each imports one launcher's games. All are `Type: GameLibrary`, reference only
the Playnite SDK, and are independent of each other.

| | reads | state |
|---|---|---|
| **RohanKar** | the y4bo launcher's `playnite-export.json` | reads the export; see its README |
| **Hydra** | its LevelDB, via a Node helper | works — 5 entries |
| **GithubLauncher** | its own CLI (`--list` / `--run`) | 0.9 — see below |
| **YaboLibrary** | the yabo gate/staging engine | from the launcher era |

**RohanKar** reads the `playnite-export.json` the launcher rewrites on every
library change, and watches it. It used to scan the install folder and guess
each game's exe, because `library.db` was empty on a real install; the launcher
now exports the exe it picked, so the guessing is gone. Install, uninstall and
"open in launcher" run through the launcher's own CLI. Details in
`RohanKar/README.md`.

**Hydra** delegates the read to Node because its LevelDB values are
Snappy-compressed and there is no .NET reader worth vendoring; the helper uses
`classic-level`, the same library Hydra opens the database with. Hydra must be
closed during a refresh — LevelDB takes an exclusive lock — and the plugin checks
for the process and says so rather than failing obscurely.

**GithubLauncher** drives the launcher's own documented CLI rather than its files,
because `--run <name>` updates the game before launching and a raw path cannot.
It is 0.9 for one honest reason: the CLI is verified, but the *shape* of `--list`
output with games in it is not, because the library it was built against is
empty. Parsing lives in `ListParser` with no Playnite dependency so it can be run
standalone against captured output, and the raw output is logged so the first real
run reveals the true format instead of failing quietly.

## Other extensions

- **GlazeWM** — `GenericPlugin`. Right-click a game and send it to a screen. Writes
  a `display:<monitor>` tag that a Playnite global script reads to strip window
  chrome and place the game via the tiling WM. It never changes the primary
  display, and it leaves alone any game carrying Display Helper's own tag, so
  Display Helper stays the fallback for titles needing exclusive fullscreen.
- **YaboDev** — dev-side tagging that feeds the gate.

## Themes

Themes are XAML and carry a `theme.yaml` rather than an `extension.yaml`.

- **YaboTheme** — "Mythos Cider" 2.0, the fork actually in use.
- **MythosFast** — "Mythos (Fast)" 2.0, the upstream it forks from, vendored for
  diffing.
- **YaboGlass** — the earlier ShaderGlass-era skin. **Superseded** (shaders moved
  to native GLSL); kept so it is not lost, not because it is current.

## Building

Each extension is a standalone project:

```
cd GlazeWM
dotnet build -c Release      # -> bin\Release\net462\<Name>.dll
```

Deploy by copying the `.dll` and `extension.yaml` into
`%APPDATA%\Playnite\Extensions\<Name>\`, then restart Playnite.

Themes are XAML and need no build — copy the folder into
`%APPDATA%\Playnite\Themes\Desktop\<Name>\`.

## Publishing

Nothing here is published. Each extension is self-contained precisely so that any
one of them *could* be, without untangling it from the rest.
