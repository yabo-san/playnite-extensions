// iSNESrev — Tauri v2 shell for the SNES-reimplementation shelf.
//
// STANDALONE app (NOT yabo-launcher): its own webview that drives the SAME C# engine CLI
// (D:\games\yabo-portable\yabo-launcher.exe) through the shell plugin. All launcher/ingest logic
// lives in the engine; this Rust layer only hosts the webview, exposes a tiny logging command,
// and surfaces the deep-link launch args (--game / --tab) to the frontend.
//
// The drag-a-ROM loop uses Tauri v2's built-in OS file-drop: the frontend subscribes to
// `getCurrentWebview().onDragDropEvent(...)` (no custom Rust needed) and, on "drop", calls the
// engine `--ingest "<path>"` via the shell bridge. Identification + install + BPS-patch all happen
// engine-side. We never launch a game here.
//
// DEEP-LINK (per-game focus, for Playnite): launch the app as
//     isnesrev.exe --game <folderName> --tab <shaders|controls|play>
// Both flags are optional; --tab defaults to "controls" when --game is given without a tab. The
// frontend calls the `launch_args` command on boot, and if a game is present it opens straight to
// that game's config view on the requested tab.

use std::io::Write;

use serde::Serialize;

/// Parsed deep-link launch args. Defaults (no flags) => open the normal library/drag-in view.
#[derive(Default, Serialize, Clone)]
struct LaunchArgs {
    /// Folder name (or display name) of the game to focus, or "" for the default library view.
    game: String,
    /// Which config tab to open: "shaders" | "controls" | "play". "" => default ("controls").
    tab: String,
}

/// Parse `--game <name>` / `--tab <name>` out of the process args. Tolerant: unknown args are
/// ignored, values that look like another flag are skipped, and quoting is left to the OS shell.
fn parse_launch_args() -> LaunchArgs {
    let argv: Vec<String> = std::env::args().collect();
    let mut out = LaunchArgs::default();
    let mut i = 1;
    while i < argv.len() {
        let a = argv[i].as_str();
        match a {
            "--game" | "-g" => {
                if let Some(v) = argv.get(i + 1) {
                    if !v.starts_with("--") {
                        out.game = v.clone();
                        i += 2;
                        continue;
                    }
                }
            }
            "--tab" | "-t" => {
                if let Some(v) = argv.get(i + 1) {
                    if !v.starts_with("--") {
                        out.tab = v.to_ascii_lowercase();
                        i += 2;
                        continue;
                    }
                }
            }
            _ => {}
        }
        i += 1;
    }
    // A game with no explicit tab opens on the controls tab by default.
    if !out.game.is_empty() && out.tab.is_empty() {
        out.tab = "controls".to_string();
    }
    // Normalize/validate the tab name.
    if !matches!(out.tab.as_str(), "" | "shaders" | "controls" | "play") {
        out.tab = "controls".to_string();
    }
    out
}

/// Frontend boot calls this to learn whether it was deep-linked to a specific game + tab.
#[tauri::command]
fn launch_args() -> LaunchArgs {
    parse_launch_args()
}

// Append a timestamped line from the web UI to a grep-able log file beside the engine's run folder,
// so ingest actions are visible off-screen. Fire-and-forget; never panics.
fn ui_log_path(name: &str) -> std::path::PathBuf {
    // The engine's run folder (where logs already live). Hard-wired to the portable deploy so the
    // iSNESrev shell and the engine share one logs dir, mirroring the yabo-ui convention.
    let mut dir = std::path::PathBuf::from(r"D:\games\yabo-portable\logs");
    let _ = std::fs::create_dir_all(&dir);
    dir.push(name);
    dir
}

#[tauri::command]
fn ui_log(line: String) {
    if let Ok(mut f) = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(ui_log_path("isnesrev-ui.log"))
    {
        let _ = writeln!(f, "{}", line);
    }
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        .plugin(tauri_plugin_fs::init())
        .plugin(tauri_plugin_dialog::init())
        .invoke_handler(tauri::generate_handler![ui_log, launch_args])
        .run(tauri::generate_context!())
        .expect("error while running iSNESrev");
}
