# playnite-extensions

What ships from here, each as its own Playnite package:

| | | get it |
|---|---|---|
| **RohanKar** | library plugin for the [y4bo launcher](https://github.com/yabo-san/RohanKar-Launcher): reads its `playnite-export.json`; install, uninstall and open-in-launcher go through the launcher's CLI | `.pext` on [Releases](../../releases) |
| **Drop** | library plugin for a [Drop](https://github.com/Drop-OSS/drop) server: device-code sign-in, install through Drop's manifest protocol, "Send to Drop" for any game | `.pext` on Releases |
| **Hydra** | library plugin that reads Hydra's LevelDB through a Node helper | build it (`dotnet build Hydra -c Release`) |
| **MythosFast** | the Mythos theme with the grid OpacityMask fix (bansakai/Mythos#34) | `.pthm` on Releases |

`net462`, Playnite SDK 6.16, built and released by GitHub Actions. `_salvage/` is parked source that does not build.
