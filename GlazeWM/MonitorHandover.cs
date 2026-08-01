using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Playnite.SDK;

namespace GlazeWMPlaynite
{
    /// <summary>
    /// Hands a monitor over to a game, and takes it back when the game exits.
    ///
    /// This is the whole feature, and it deliberately NEVER TOUCHES A GAME WINDOW.
    /// Moving, resizing and re-styling game windows was tried at length and does
    /// not work for the titles that matter: Dishonored re-asserts its own style and
    /// position roughly every 2.5 seconds, so anything done to it is undone a
    /// moment later and the attempts show up as flicker.
    ///
    /// What DOES work is knowing where the game will go and getting out of its way:
    ///
    ///   * A game lands on the PRIMARY display. Windows puts the primary at the
    ///     desktop origin, and a game that pins itself pins to (0,0).
    ///   * Display Helper's only trick is SetPrimaryDisplay (its DLL imports
    ///     CDS_SET_PRIMARY / setAsPrimaryDevice and does nothing else with windows),
    ///     and it runs BEFORE the game starts. So if Display Helper is configured,
    ///     the primary already IS the screen it chose.
    ///
    /// One rule covers both: whatever is primary when the game starts is where the
    /// game is going. Move our workspaces off it, and put them back afterwards.
    /// </summary>
    internal static class MonitorHandover
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        /// <summary>Workspaces we moved, per game, so they can be put back exactly.</summary>
        private static readonly Dictionary<Guid, List<Displaced>> Held =
            new Dictionary<Guid, List<Displaced>>();

        private class Displaced
        {
            public string Workspace;
            public int FromMonitor;
        }

        /// <summary>The primary display before we changed it, so it can be put back.</summary>
        private static readonly Dictionary<Guid, string> PriorPrimary =
            new Dictionary<Guid, string>();

        // ─── talking to GlazeWM ──────────────────────────────────────────────

        private static string Run(params string[] args)
        {
            var psi = new ProcessStartInfo("glazewm", string.Join(" ", args))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    if (p == null) return null;
                    var stdout = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    return stdout;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "GlazeWM: command failed: " + string.Join(" ", args));
                return null;
            }
        }

        private static JArray Monitors()
        {
            var json = Run("query", "monitors");
            if (string.IsNullOrWhiteSpace(json)) return new JArray();
            try { return JObject.Parse(json)["data"]?["monitors"] as JArray ?? new JArray(); }
            catch { return new JArray(); }
        }

        // ─── which monitor is the game taking ────────────────────────────────

        /// <summary>
        /// The monitor at the desktop origin. That is the primary, and therefore
        /// where the game is going — including when Display Helper has just moved
        /// the primary to the screen the user picked for this game.
        /// </summary>
        private static int PrimaryIndex(JArray mons)
        {
            for (int i = 0; i < mons.Count; i++)
            {
                if ((int?)mons[i]["x"] == 0 && (int?)mons[i]["y"] == 0) return i;
            }
            return -1;
        }

        /// <summary>
        /// An explicit override, when the user tagged the game with a screen of
        /// ours rather than leaving it to Display Helper. Matched against the
        /// friendly names in ~/.config/monitors.json, then hardware id, then
        /// resolution — never the GDI name, which drifts.
        /// </summary>
        private static int NamedIndex(JArray mons, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return -1;
            var want = name.Trim().ToLowerInvariant();

            if (want == "primary") return PrimaryIndex(mons);

            var alias = Aliases();
            JObject entry;
            if (alias.TryGetValue(want, out entry))
            {
                var hw = ((string)entry["hardwareId"] ?? "").ToLowerInvariant();
                var res = ((string)entry["resolution"] ?? "").ToLowerInvariant();

                for (int i = 0; i < mons.Count; i++)
                {
                    var mhw = ((string)mons[i]["hardwareId"] ?? "").ToLowerInvariant();
                    if (hw.Length > 0 && mhw == hw) return i;
                }
                for (int i = 0; i < mons.Count; i++)
                {
                    var mres = mons[i]["width"] + "x" + mons[i]["height"];
                    if (res.Length > 0 && mres.ToLowerInvariant() == res) return i;
                }
            }

            for (int i = 0; i < mons.Count; i++)
            {
                if (((string)mons[i]["hardwareId"] ?? "").ToLowerInvariant() == want) return i;
                if ((mons[i]["width"] + "x" + mons[i]["height"]).ToLowerInvariant() == want) return i;
            }
            return -1;
        }

        private static Dictionary<string, JObject> Aliases()
        {
            var result = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config", "monitors.json");
                if (!File.Exists(path)) return result;

                var mons = JObject.Parse(File.ReadAllText(path))["monitors"] as JObject;
                if (mons == null) return result;
                foreach (var p in mons.Properties())
                {
                    var o = p.Value as JObject;
                    if (o != null) result[p.Name] = o;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "GlazeWM: could not read monitors.json");
            }
            return result;
        }

        // ─── the handover ────────────────────────────────────────────────────

        /// <summary>
        /// Move our workspaces off the monitor this game is taking.
        ///
        /// Only workspaces WITH WINDOWS are moved. An empty workspace on the
        /// game's screen is harmless — there is nothing on it to see — and GlazeWM
        /// will not move an empty workspace back afterwards, so displacing one
        /// strands it permanently.
        /// </summary>
        public static void Claim(Guid gameId, string gameName, string explicitMonitor)
        {
            var mons = Monitors();
            if (mons.Count == 0) return;

            int target = NamedIndex(mons, explicitMonitor);

            // A NAMED screen means the user picked one, so MAKE IT PRIMARY. That is
            // the only thing that reliably decides where a game opens - a game that
            // pins its own window pins it to (0,0), which is the primary by
            // definition. It is also all Display Helper ever did, so doing it here
            // means the user picks "acer" rather than \\.\DISPLAY2, a GDI name that
            // means nothing to a human and gets renumbered by driver resets.
            if (target >= 0)
            {
                var w = (int?)mons[target]["width"] ?? 0;
                var h = (int?)mons[target]["height"] ?? 0;
                var device = PrimaryDisplay.DeviceForResolution(w, h);
                var before = PrimaryDisplay.Current();

                if (device != null && !string.Equals(device, before, StringComparison.OrdinalIgnoreCase))
                {
                    if (PrimaryDisplay.Set(device))
                    {
                        lock (PriorPrimary) { PriorPrimary[gameId] = before; }
                        Logger.Info($"GlazeWM: made {device} primary for {gameName} (was {before}).");
                        // The desktop just moved; re-read it before touching workspaces.
                        System.Threading.Thread.Sleep(1200);
                        mons = Monitors();
                        target = NamedIndex(mons, explicitMonitor);
                    }
                }
            }

            if (target < 0) target = PrimaryIndex(mons);
            if (target < 0)
            {
                Logger.Warn("GlazeWM: no monitor at the desktop origin; not claiming anything.");
                return;
            }

            Logger.Info($"GlazeWM: {gameName} is taking monitor {target} " +
                        $"({mons[target]["width"]}x{mons[target]["height"]}).");

            var moved = new List<Displaced>();

            // Bounded: each pass relocates at most one workspace.
            for (int pass = 0; pass < 12; pass++)
            {
                mons = Monitors();
                if (target >= mons.Count) break;

                var occupied = (mons[target]["children"] as JArray ?? new JArray())
                    .Where(ws => (ws["children"] as JArray ?? new JArray()).Count > 0)
                    .Select(ws => (string)ws["name"])
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList();

                if (occupied.Count == 0) break;

                var name = occupied[0];
                if (!MoveAway(name, target))
                {
                    Logger.Warn($"GlazeWM: could not move workspace {name} off monitor {target}; leaving it.");
                    break;
                }
                moved.Add(new Displaced { Workspace = name, FromMonitor = target });
            }

            lock (Held) { Held[gameId] = moved; }
            Logger.Info($"GlazeWM: moved {moved.Count} workspace(s) off monitor {target} for {gameName}.");
        }

        /// <summary>Put back exactly what we moved, and nothing else.</summary>
        public static void Release(Guid gameId, string gameName)
        {
            // Put the primary display back FIRST. Everything else is expressed in
            // desktop coordinates, and changing the primary moves them all.
            string before = null;
            lock (PriorPrimary)
            {
                if (PriorPrimary.TryGetValue(gameId, out before)) PriorPrimary.Remove(gameId);
            }
            if (!string.IsNullOrEmpty(before))
            {
                if (PrimaryDisplay.Set(before))
                {
                    Logger.Info($"GlazeWM: primary display returned to {before} after {gameName}.");
                    System.Threading.Thread.Sleep(1200);
                }
            }

            List<Displaced> moved;
            lock (Held)
            {
                if (!Held.TryGetValue(gameId, out moved)) return;
                Held.Remove(gameId);
            }

            if (moved.Count == 0)
            {
                Logger.Info($"GlazeWM: nothing to give back after {gameName}.");
                return;
            }

            foreach (var d in moved)
            {
                MoveTo(d.Workspace, d.FromMonitor);
            }
            Logger.Info($"GlazeWM: returned {moved.Count} workspace(s) after {gameName}.");
        }

        /// <summary>
        /// Push a workspace off a monitor. Directions are tried and VERIFIED,
        /// because `move-workspace --direction` only resolves to a geometrically
        /// adjacent monitor and silently does nothing when there isn't one.
        /// </summary>
        private static bool MoveAway(string workspace, int fromMonitor)
        {
            foreach (var dir in new[] { "right", "down", "left", "up" })
            {
                Run("command", "focus", "--workspace", workspace);
                Run("command", "move-workspace", "--direction", dir);
                if (IndexOf(workspace) != fromMonitor) return true;
            }
            return false;
        }

        /// <summary>
        /// Put a workspace onto a specific monitor. Focus that monitor first — a
        /// workspace materialises on whichever monitor is focused, which is far
        /// more reliable than stepping it there by direction.
        /// </summary>
        private static bool MoveTo(string workspace, int monitor)
        {
            if (IndexOf(workspace) == monitor) return true;

            Run("command", "focus", "--monitor", monitor.ToString());
            Run("command", "focus", "--workspace", workspace);
            if (IndexOf(workspace) == monitor) return true;

            foreach (var dir in new[] { "left", "up", "right", "down" })
            {
                Run("command", "focus", "--workspace", workspace);
                Run("command", "move-workspace", "--direction", dir);
                if (IndexOf(workspace) == monitor) return true;
            }
            Logger.Warn($"GlazeWM: could not return workspace {workspace} to monitor {monitor}.");
            return false;
        }

        private static int IndexOf(string workspace)
        {
            var mons = Monitors();
            for (int i = 0; i < mons.Count; i++)
            {
                foreach (var ws in mons[i]["children"] as JArray ?? new JArray())
                {
                    if ((string)ws["name"] == workspace) return i;
                }
            }
            return -1;
        }
    }
}
