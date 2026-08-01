// iSNESrev frontend wiring — STANDALONE SNES drag-in launcher.
//
// Core loop: drag a ROM in (OR pick one OR click a shelf card) → identify+install via the engine
// → configure GLSL shader + rebind controls → Play. A per-game config view (Controls / Shader /
// Play tabs) backs the deep-link entry point.
//
// DEEP-LINK (for Playnite, parsed Rust-side in src-tauri/src/lib.rs, surfaced via bridge.launchArgs()):
//     isnesrev.exe --game <folderName> --tab <shaders|controls|play>
// On boot, if a --game is present we open straight into that game's config view on the given tab
// (default "controls"). --game accepts the folderName OR the display name (engine FindGame matches both).

import bridge from "./bridge.js";

const $ = (id) => document.getElementById(id);
const dropzone = $("dropzone");
const resultEl = $("result");
const gridEl = $("grid");
const homeView = $("homeView");
const gameView = $("gameView");

// The 12 SNES button slots, in the fixed order the snesrev [KeyMap] `Controls` line uses.
const BUTTON_ORDER = ["Up","Down","Left","Right","Select","Start","A","B","X","Y","L","R"];

// snesrev reimplementation repos — used to filter the full catalog down to this shelf.
const SNESREV_REPOS = ["snesrev/zelda3", "snesrev/smw", "snesrev/sm"];
function isSnesrev(g) {
  const repo = (g.repository || g.Repository || "").toLowerCase();
  if (SNESREV_REPOS.some((r) => repo.includes(r.split("/")[1]) && repo.includes("snesrev"))) return true;
  // RadzPrower Zelda-3 launcher + anything declaring an .sfc/.smc dataFile also belongs on the shelf.
  if (repo.includes("zelda-3") || repo.includes("zelda3")) return true;
  const df = g.dataFiles || g.DataFiles || [];
  return df.some((d) => /\.(sfc|smc)$/i.test(d.name || d.Name || ""));
}

const gname = (g) => g.name || g.Name || "";
const gfolder = (g) => g.folderName || g.FolderName || "";
const gstatus = (g) => (g.status || g.Status || "").toString();
function gInstalled(g) {
  const s = gstatus(g);
  return /install/i.test(s) && !/notinstalled/i.test(s.replace(/\s/g, ""));
}

function showResult(text, kind) {
  resultEl.hidden = false;
  resultEl.textContent = text;
  resultEl.className = "result" + (kind ? " " + kind : "");
  resultEl.scrollTop = resultEl.scrollHeight;
}

// ---- the drag-a-ROM ingest loop -------------------------------------------------------------------
let ingesting = false;
async function ingestPath(path) {
  if (!path || ingesting) return;
  ingesting = true;
  dropzone.classList.add("busy");
  bridge.log(`ingest start: ${path}`);
  let out = "";
  showResult(`→ Ingesting ${path}…`, null);
  try {
    out = await bridge.ingest(path, (line) => {
      out += line + "\n";
      showResult(out, null);
    });
    showResult(out.trim() || "Done.", "ok");
    bridge.log(`ingest ok: ${path}`);
    await loadLibrary(); // refresh status (NotInstalled → Installed, data placed)
  } catch (e) {
    showResult(String(e.message || e), "err");
    bridge.log(`ingest FAIL: ${path} — ${e.message || e}`);
  } finally {
    ingesting = false;
    dropzone.classList.remove("busy");
  }
}

// ---- library grid ---------------------------------------------------------------------------------
let SHELF = []; // last-loaded snesrev shelf (so the game view can look a game up by folderName)

async function loadLibrary() {
  let games = [];
  try { games = await bridge.listGames(); }
  catch (e) { gridEl.innerHTML = `<div class="empty">Engine error: ${e.message || e}</div>`; return; }
  SHELF = games.filter(isSnesrev);
  if (!SHELF.length) { gridEl.innerHTML = `<div class="empty">No snesrev games in the catalog.</div>`; return; }
  gridEl.innerHTML = "";
  for (const g of SHELF) {
    const name = gname(g);
    const installed = gInstalled(g);
    const art = g.cover || g.artUrl || g.ArtUrl || "";
    const card = document.createElement("div");
    card.className = "card";
    card.innerHTML =
      `<div class="art" style="${art ? `background-image:url('${art}')` : ""}"></div>` +
      `<div class="meta"><div class="name"></div>` +
      `<div class="status ${installed ? "installed" : ""}"></div></div>`;
    card.querySelector(".name").textContent = name;
    card.querySelector(".status").textContent = installed ? "Installed" : (gstatus(g) || "Not installed");
    card.addEventListener("click", () => openGame(g, "controls"));
    gridEl.appendChild(card);
  }
}

// ===================================================================================================
// GAME VIEW — per-game config (Controls / Shader / Play). Backs both card-click and the deep link.
// ===================================================================================================
let CURRENT = null; // the game object currently focused in the game view

function showHome() {
  gameView.hidden = true;
  homeView.hidden = false;
  CURRENT = null;
}

function findShelfGame(key) {
  if (!key) return null;
  const k = key.toLowerCase();
  return SHELF.find((g) => gfolder(g).toLowerCase() === k || gname(g).toLowerCase() === k) || null;
}

async function openGame(game, tab) {
  CURRENT = game;
  homeView.hidden = true;
  gameView.hidden = false;
  $("gameName").textContent = gname(game);
  $("gameStatus").textContent = gInstalled(game) ? "Installed" : (gstatus(game) || "Not installed");
  const art = game.cover || game.artUrl || game.ArtUrl || "";
  $("gameArt").style.backgroundImage = art ? `url('${art}')` : "";
  $("playResult").hidden = true;
  selectTab(tab || "controls");
  await Promise.all([loadKeymapEditor(), loadShaders()]);
}

function selectTab(tab) {
  const valid = ["controls", "shaders", "play"].includes(tab) ? tab : "controls";
  for (const btn of document.querySelectorAll(".tab"))
    btn.classList.toggle("active", btn.dataset.tab === valid);
  for (const id of ["controls", "shaders", "play"])
    $("tab-" + id).hidden = id !== valid;
}

// ---- Controls tab: REAL click-to-capture rebind, persisted to [KeyMap] Controls --------------------
let KEYMAP = []; // current 12 values, in BUTTON_ORDER
let capturing = -1; // index of the slot currently waiting for a keypress, or -1

async function loadKeymapEditor() {
  const editor = $("keymapEditor");
  const msg = $("keymapMsg");
  msg.textContent = "";
  editor.innerHTML = "";
  capturing = -1;
  if (!CURRENT) return;
  let cfg;
  try { cfg = await bridge.getConfig(gfolder(CURRENT) || gname(CURRENT)); }
  catch { editor.innerHTML = `<span class="kb">no .ini yet — install the game first</span>`; return; }
  const keymap = (cfg.sections && (cfg.sections.KeyMap || cfg.sections.keymap)) || null;
  const controls = keymap && (keymap.Controls || keymap.controls);
  if (!controls) {
    editor.innerHTML = `<span class="kb">no [KeyMap] Controls in this port's .ini</span>`;
    return;
  }
  KEYMAP = controls.split(",").map((s) => s.trim());
  renderKeymap();
}

function renderKeymap() {
  const editor = $("keymapEditor");
  editor.innerHTML = "";
  BUTTON_ORDER.forEach((btn, i) => {
    const slot = document.createElement("button");
    slot.type = "button";
    slot.className = "kb-slot" + (capturing === i ? " capturing" : "");
    slot.innerHTML = `<b>${btn}</b><span class="kb-key">${capturing === i ? "press a key…" : (KEYMAP[i] || "—")}</span>`;
    slot.addEventListener("click", () => beginCapture(i));
    editor.appendChild(slot);
  });
}

function beginCapture(i) {
  capturing = i;
  renderKeymap();
}

// Capture the next keydown into the active slot, then persist the rebuilt CSV.
window.addEventListener("keydown", async (ev) => {
  if (capturing < 0 || gameView.hidden) return;
  ev.preventDefault();
  if (ev.key === "Escape") { capturing = -1; renderKeymap(); return; }
  // snesrev .ini key tokens are single uppercase letters / digits / named keys. Map a few common ones.
  const k = keyTokenFromEvent(ev);
  if (!k) return;
  KEYMAP[capturing] = k;
  const slot = capturing;
  capturing = -1;
  renderKeymap();
  await persistKeymap(slot);
});

function keyTokenFromEvent(ev) {
  const code = ev.code || "";
  if (/^Key([A-Z])$/.test(code)) return code.slice(3);
  if (/^Digit([0-9])$/.test(code)) return code.slice(5);
  const named = {
    ArrowUp: "Up", ArrowDown: "Down", ArrowLeft: "Left", ArrowRight: "Right",
    Enter: "Return", Space: "Space", ShiftLeft: "LShift", ShiftRight: "RShift",
    ControlLeft: "LCtrl", ControlRight: "RCtrl", Backspace: "Backspace", Tab: "Tab",
  };
  return named[code] || (ev.key.length === 1 ? ev.key.toUpperCase() : null);
}

async function persistKeymap(slot) {
  const msg = $("keymapMsg");
  const csv = KEYMAP.join(",");
  try {
    await bridge.setConfig(gfolder(CURRENT) || gname(CURRENT), "KeyMap.Controls", csv);
    msg.textContent = `Saved ${BUTTON_ORDER[slot]} → ${KEYMAP[slot]}`;
    msg.className = "msg ok";
    bridge.log(`rebind ${gname(CURRENT)} ${BUTTON_ORDER[slot]}=${KEYMAP[slot]}`);
  } catch (e) {
    msg.textContent = `Save failed: ${e.message || e}`;
    msg.className = "msg err";
  }
}

// ---- Shader tab: REAL apply, persisted to Graphics.Shader ------------------------------------------
async function loadShaders() {
  const picker = $("shaderPicker");
  const msg = $("shaderMsg");
  msg.textContent = "";
  let shaders = [];
  try { shaders = await bridge.listGlslShaders(); } catch { /* leave default */ }
  picker.innerHTML = `<option value="">— none —</option>`;
  for (const s of shaders) {
    const opt = document.createElement("option");
    opt.value = s.path; opt.textContent = s.label || s.id;
    picker.appendChild(opt);
  }
  // Reflect the game's current Graphics.Shader value, if any.
  if (CURRENT) {
    try {
      const cfg = await bridge.getConfig(gfolder(CURRENT) || gname(CURRENT));
      const gfx = (cfg.sections && (cfg.sections.Graphics || cfg.sections.graphics)) || null;
      const cur = gfx && (gfx.Shader || gfx.shader);
      if (cur && [...picker.options].some((o) => o.value === cur)) picker.value = cur;
    } catch { /* fine */ }
  }
}

async function applyShader() {
  const picker = $("shaderPicker");
  const msg = $("shaderMsg");
  if (!CURRENT) return;
  try {
    await bridge.setConfig(gfolder(CURRENT) || gname(CURRENT), "Graphics.Shader", picker.value);
    msg.textContent = picker.value ? `Shader set: ${picker.options[picker.selectedIndex].textContent}` : "Shader cleared";
    msg.className = "msg ok";
    bridge.log(`shader ${gname(CURRENT)} = ${picker.value || "(none)"}`);
  } catch (e) {
    msg.textContent = `Apply failed: ${e.message || e}`;
    msg.className = "msg err";
  }
}

// ---- Play tab: launch + create Steam shortcut -----------------------------------------------------
let playing = false;
async function playCurrent() {
  if (!CURRENT || playing) return;
  playing = true;
  const out = $("playResult");
  out.hidden = false; out.className = "result"; out.textContent = `→ Launching ${gname(CURRENT)}…`;
  let buf = "";
  try {
    buf = await bridge.play(gfolder(CURRENT) || gname(CURRENT), (line) => {
      buf += line + "\n"; out.textContent = buf;
    });
    out.textContent = buf.trim() || "Launched."; out.className = "result ok";
    await loadLibrary(); // status may have flipped to Installed
  } catch (e) {
    out.textContent = String(e.message || e); out.className = "result err";
  } finally {
    playing = false;
  }
}

async function createSteamShortcut() {
  if (!CURRENT) return;
  const out = $("playResult");
  out.hidden = false; out.className = "result"; out.textContent = "→ Adding Steam shortcut…";
  try {
    const text = await bridge.addSteamShortcut(gname(CURRENT)); // engine matches name OR folderName
    out.textContent = text.trim() || "Added. Restart Steam to see it."; out.className = "result ok";
    bridge.log(`steam-shortcut add ${gname(CURRENT)}`);
  } catch (e) {
    out.textContent = `Steam shortcut failed: ${e.message || e}`; out.className = "result err";
  }
}

// ===================================================================================================
// wire up
// ===================================================================================================
function wireDragDrop() {
  if (!bridge.isTauri()) return;
  try {
    const wv = window.__TAURI__.webview.getCurrentWebview();
    wv.onDragDropEvent((ev) => {
      const t = ev.payload && ev.payload.type;
      if (t === "over" || t === "enter") dropzone.classList.add("drag");
      else if (t === "leave") dropzone.classList.remove("drag");
      else if (t === "drop") {
        dropzone.classList.remove("drag");
        const paths = (ev.payload && ev.payload.paths) || [];
        if (paths.length) { showHome(); ingestPath(paths[0]); }
      }
    });
  } catch (e) {
    bridge.log(`drag-drop wiring failed: ${e.message || e}`);
  }
}

$("pickBtn").addEventListener("click", async () => {
  const p = await bridge.pickFile("Pick a SNES ROM");
  if (p) { showHome(); ingestPath(p); }
});
$("refreshBtn").addEventListener("click", loadLibrary);
$("backBtn").addEventListener("click", showHome);
$("keymapResetBtn").addEventListener("click", loadKeymapEditor);
$("shaderPicker").addEventListener("change", applyShader);
$("playBtn").addEventListener("click", playCurrent);
$("steamBtn").addEventListener("click", createSteamShortcut);
for (const btn of document.querySelectorAll(".tab"))
  btn.addEventListener("click", () => selectTab(btn.dataset.tab));

// ---- boot -----------------------------------------------------------------------------------------
async function boot() {
  wireDragDrop();
  await loadLibrary();

  // Deep-link: --game <folderName> --tab <shaders|controls|play>
  const { game, tab } = await bridge.launchArgs();
  if (game) {
    const target = findShelfGame(game);
    if (target) {
      openGame(target, tab || "controls");
    } else {
      // Game not on the loaded shelf yet — still open the view by synthesizing a minimal record so the
      // engine name-lookups work; the config reads will surface "install first" if it's not present.
      openGame({ name: game, folderName: game }, tab || "controls");
    }
  }

  if (!bridge.isTauri()) {
    showResult("Running outside Tauri — engine bridge unavailable. Launch via `npm run dev` / the built app.", "err");
  }
}
boot();
