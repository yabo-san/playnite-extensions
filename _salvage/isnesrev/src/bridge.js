// iSNESrev → yabo engine bridge.
//
// Adapted from D:\REPOS\ports-launcher\ui\src\bridge.js (the proven yabo-ui bridge). Same spawn
// contract: every capability is reached by running the engine exe via Tauri's shell plugin with the
// "engine" scope declared in src-tauri/capabilities/default.json. No launcher logic lives here.
//
// iSNESrev only needs a focused slice: --ingest, --list-json (snesrev grid), --get-config /
// --set-config (zelda3.ini [KeyMap]), --list-glsl-shaders (shader picker), and pickFile.

const T = () => window.__TAURI__;
const isTauri = () => !!window.__TAURI__;

// Run the engine with args; resolve full stdout (string). Rejects with stderr on non-zero exit.
// Reads stdout as RAW bytes + streaming TextDecoder so the engine's multi-byte banner (────, →, ✓)
// and accented game names never throw "invalid utf-8 sequence" (the documented yabo-ui fix).
async function engineStream(args, onLine) {
  if (!isTauri()) throw new Error("not running in Tauri (no engine bridge)");
  const cmd = T().shell.Command.create("engine", args, { encoding: "raw" });
  const dec = new TextDecoder("utf-8");
  let pending = "", buf = "", err = "";
  const feed = (chunk) => {
    pending += (typeof chunk === "string")
      ? chunk
      : dec.decode(chunk instanceof Uint8Array ? chunk : new Uint8Array(chunk), { stream: true });
    let nl;
    while ((nl = pending.indexOf("\n")) >= 0) {
      const line = pending.slice(0, nl).replace(/\r$/, "");
      pending = pending.slice(nl + 1);
      buf += line + "\n";
      if (onLine) { try { onLine(line); } catch (_) {} }
    }
  };
  cmd.stdout.on("data", feed);
  cmd.stderr.on("data", (c) => { err += (typeof c === "string") ? c : dec.decode(c instanceof Uint8Array ? c : new Uint8Array(c), { stream: true }); });
  return await new Promise((resolve, reject) => {
    cmd.on("close", (data) => {
      const code = data && typeof data.code === "number" ? data.code : data;
      const tail = pending + dec.decode();
      if (tail) { buf += tail; if (onLine) { try { onLine(tail.replace(/\r$/, "")); } catch (_) {} } }
      if (code === 0) resolve(buf);
      else reject(new Error(err.trim() || `engine ${args.join(" ")} exited ${code}`));
    });
    cmd.on("error", (e) => reject(new Error(String(e))));
    cmd.spawn().catch(reject);
  });
}

function engine(args) { return engineStream(args, null); }

export const bridge = {
  isTauri,

  /** OpenEmu-style drag-in: identify a dropped ROM by sha1, install its port if needed, stage + BPS-patch
   *  it — all engine-side. `onLine` streams the engine's progress lines to the UI log/console. Resolves with
   *  full stdout on success (exit 0); rejects with the engine's stderr (e.g. "Unrecognized ROM …"). */
  ingest(file, onLine) { return engineStream(["--ingest", file], onLine); },

  /** Full catalog + live status + resolved cover. --list-json emits { build, rid, games:[...] };
   *  we unwrap to the games array (tolerating a legacy bare [...]). Returns [] off-Tauri. */
  async listGames() {
    if (!isTauri()) return [];
    const parsed = JSON.parse(await engine(["--list-json", "--all"]));
    if (Array.isArray(parsed)) return parsed;
    return (parsed && Array.isArray(parsed.games)) ? parsed.games : [];
  },

  /** Parse a game's config .ini → { iniName, sections:{ Section:{ key:val } } }. {sections:{}} if none.
   *  For zelda3 the [KeyMap] section's `Controls` line is the 12-button rebind target. */
  async getConfig(name) { return JSON.parse(await engine(["--get-config", name]).catch(() => '{"sections":{}}')); },
  /** Rewrite ONE "Section.Key" in the .ini in place (preserves comments/ordering). */
  setConfig(name, sectionDotKey, value) { return engine(["--set-config", name, sectionDotKey, String(value)]); },

  /** Bundled built-in GLSL shaders for the snesrev OpenGL renderer: [{id, path, label}]. [] on failure. */
  async listGlslShaders() { return JSON.parse(await engine(["--list-glsl-shaders", "--json"]).catch(() => "[]")); },

  /** THE unified Play action: install-if-needed → stage → apply config/shader → launch the port.
   *  `name` is the game's folderName OR display name (engine FindGame accepts either). Streams progress. */
  play(name, onLine) { return engineStream(["--play", name], onLine); },

  /** Add a non-Steam Steam shortcut for `name` (folderName/display name). Steam reads shortcuts.vdf at
   *  startup, so the engine prints a "RESTART STEAM" reminder. Resolves with the engine's stdout. */
  addSteamShortcut(name) { return engine(["--add-steam-shortcut", name]); },

  /** List our yabo non-Steam shortcuts already in shortcuts.vdf: [{name, launchOptions, ...}]. [] on failure. */
  async listSteamShortcuts() { return JSON.parse(await engine(["--list-steam-shortcuts", "--json"]).catch(() => "[]")); },

  /** Deep-link launch args from the Rust side: { game, tab }. {game:"",tab:""} off-Tauri or when not deep-linked. */
  async launchArgs() {
    if (!isTauri()) return { game: "", tab: "" };
    try { return await T().core.invoke("launch_args"); }
    catch { return { game: "", tab: "" }; }
  },

  /** Native open-file dialog → absolute path string, or null if cancelled. */
  async pickFile(title) {
    if (!isTauri()) return null;
    const sel = await T().dialog.open({
      title: title || "Pick a SNES ROM",
      multiple: false,
      directory: false,
      filters: [{ name: "SNES ROM", extensions: ["sfc", "smc"] }],
    });
    return Array.isArray(sel) ? (sel[0] || null) : (sel || null);
  },

  /** Off-screen log line (writes to D:\games\yabo-portable\logs\isnesrev-ui.log via the Rust command). */
  log(line) { try { if (isTauri()) T().core.invoke("ui_log", { line }); } catch (_) {} },

  openUrl(url) { return T().shell.open(url); },
};

export default bridge;
