// ---------------------------------------------------------------------------
//  GlazeWMPlugin.cs — right-click → send this game to a screen.
// ---------------------------------------------------------------------------
//  A GlazeWM extension for Playnite. Borderless Gaming and Display Helper are
//  REFERENCES it learned from, not dependencies -- neither needs to be installed.
//
//  This extension only TAGS. The actual work happens in the global "Game
//  started" script: it finds the window, strips the chrome so a tiling WM can
//  treat it as an ordinary window, and hands placement to GlazeWM.
//
//  The menu is built from ~/.config/monitors.json when it exists, otherwise from
//  `glazewm query monitors` directly - so it works with no setup at all, and
//  naming your screens is an upgrade rather than a prerequisite.
//
//  Tags written:
//     display:<monitor>    place the game on that screen
//     display:exclusive    leave the window alone entirely — the game wants
//                          exclusive fullscreen, so the WM evacuates that screen
//                          instead of fighting for the window
//
//  Monitor names come from ~/.config/monitors.json, NOT from \\.\DISPLAYn —
//  those get renumbered by driver resets and have already drifted once on this
//  machine, silently killing a set of Display Helper assignments.
//
//  Why not just use Display Helper's own "[RC] Display:" feature: that feature
//  is what makes Display Helper ACT. It sets the target monitor primary before
//  launch, which re-anchors the whole desktop coordinate space and sends the WM
//  re-laying-out every monitor. Our tag stays inert data.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace GlazeWMPlaynite
{
    public class GlazeWMPlugin : GenericPlugin
    {
        private const string Section   = "GlazeWM";
        private const string TagPrefix = "display:";
        private const string TagExclusive = "display:exclusive";

        private static readonly ILogger Logger = LogManager.GetLogger();

        public override Guid Id { get; } = Guid.Parse("29dd6716-dc8d-43b1-ab90-29954cfea2e7");

        public GlazeWMPlugin(IPlayniteAPI api) : base(api)
        {
            Properties = new GenericPluginProperties { HasSettings = false };
        }

        /// <summary>
        /// A game started: hand it the monitor it is going to take.
        ///
        /// This is the whole feature, and it runs INSIDE the plugin — no global
        /// scripts, no PowerShell, no Python, nothing to paste into Playnite's
        /// settings. Install the extension and it works.
        ///
        /// Which monitor: the tag on the game if it has one, otherwise the PRIMARY
        /// display. Primary is the right default because that is where a game goes
        /// when nobody has said otherwise — and because Display Helper works by
        /// switching the primary before launch, so if the user configured the game
        /// there, the primary already IS their chosen screen.
        ///
        /// The game's WINDOW is never touched. That was tried at length and does
        /// not work for the titles that matter.
        /// </summary>
        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            try
            {
                var game = args.Game;
                if (game == null) return;

                // An explicit display:<monitor> tag overrides the primary. Skip
                // display:manage, which is a modifier rather than a screen name.
                string named = null;
                if (game.Tags != null)
                {
                    named = game.Tags
                        .Select(t => t.Name)
                        .Where(n => n != null && n.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase))
                        .Select(n => n.Substring(TagPrefix.Length))
                        .FirstOrDefault(n => !n.Equals("manage", StringComparison.OrdinalIgnoreCase)
                                          && !n.Equals("exclusive", StringComparison.OrdinalIgnoreCase));
                }

                // OPT-IN ONLY. An untagged game is left completely alone - no
                // primary switch, no workspace moves, nothing. Doing things to
                // games nobody asked about caused more damage than it fixed: it
                // shunted workspaces off monitors they could not be returned to,
                // and fought games over windows they were never going to give up.
                //
                // An untagged game must be indistinguishable from this extension
                // not being installed.
                if (string.IsNullOrEmpty(named))
                {
                    Logger.Info($"GlazeWM: {game.Name} has no display: tag — leaving it alone.");
                    return;
                }

                MonitorHandover.Claim(game.Id, game.Name, named);
            }
            catch (Exception ex)
            {
                // Never let this break a game launch.
                Logger.Error(ex, "GlazeWM: failed to claim a monitor.");
            }
        }

        /// <summary>The game exited: give the monitor back, exactly as it was.</summary>
        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            try
            {
                var game = args.Game;
                if (game == null) return;
                MonitorHandover.Release(game.Id, game.Name);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "GlazeWM: failed to return the monitor.");
            }
        }

        private static string MonitorsFile =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                         ".config", "monitors.json");

        /// <summary>
        /// Monitor names for the menu.
        ///
        /// Friendly names from ~/.config/monitors.json when it exists, otherwise
        /// the hardware ids GlazeWM already reports (ACR0414, GSM772B, ...).
        ///
        /// The fallback matters: monitors.json is optional and a fresh install
        /// won't have one. Without this the menu would be empty on first run and
        /// the extension would look broken. Hardware ids are ugly but they WORK
        /// with zero setup — the worker resolves either form — so naming is an
        /// upgrade rather than a prerequisite.
        /// </summary>
        private static List<string> MonitorNames()
        {
            try
            {
                if (File.Exists(MonitorsFile))
                {
                    var mons = JObject.Parse(File.ReadAllText(MonitorsFile))["monitors"] as JObject;
                    var named = mons?.Properties().Select(p => p.Name).ToList();
                    if (named != null && named.Count > 0) return named;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "GlazeWM: could not read monitors.json — falling back to hardware ids.");
            }

            return MonitorsFromGlazeWM();
        }

        /// <summary>Hardware ids straight from `glazewm query monitors`.</summary>
        private static List<string> MonitorsFromGlazeWM()
        {
            var names = new List<string>();
            try
            {
                var psi = new ProcessStartInfo("glazewm", "query monitors")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return names;
                    var json = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(5000);
                    var arr = JObject.Parse(json)["data"]?["monitors"] as JArray;
                    if (arr == null) return names;

                    foreach (var m in arr)
                    {
                        var id = (string)m["hardwareId"];
                        // A panel with no EDID reports the useless, non-unique
                        // 'Default_Monitor'. Fall back to its resolution, which at
                        // least identifies it to a human and to the worker.
                        if (string.IsNullOrWhiteSpace(id) || id == "Default_Monitor")
                            id = $"{m["width"]}x{m["height"]}";
                        if (!string.IsNullOrWhiteSpace(id) && !names.Contains(id)) names.Add(id);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "GlazeWM: could not query monitors — is GlazeWM running?");
            }
            return names;
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            var games = args?.Games?.Where(g => g != null).ToList();
            if (games == null || games.Count == 0) yield break;

            var names = MonitorNames();
            if (names.Count == 0)
            {
                yield return new GameMenuItem
                {
                    MenuSection = Section,
                    Description = "No monitors found \u2014 is GlazeWM running?",
                    Action = _ => PlayniteApi.Dialogs.ShowMessage(
                        "Could not get a monitor list.\n\n" +
                        "GlazeWM must be running - the menu is built from `glazewm query monitors`.\n\n" +
                        "Optionally create ~/.config/monitors.json to use friendly names like " +
                        "crt or ultrawide instead of hardware ids.",
                        "GlazeWM")
                };
                yield break;
            }

            foreach (var monitor in names)
            {
                var target = monitor;   // capture per iteration
                yield return new GameMenuItem
                {
                    MenuSection = Section,
                    Description = $"Send to {target}",
                    Action = _ => Apply(games, TagPrefix + target)
                };
            }

            yield return new GameMenuItem
            {
                MenuSection = Section,
                Description = "Exclusive fullscreen (hands off)",
                Action = _ => Apply(games, TagExclusive)
            };

            yield return new GameMenuItem
            {
                MenuSection = Section,
                Description = "Clear",
                Action = _ => Apply(games, null)
            };
        }

        /// <summary>
        /// Set exactly one display tag per game (or none, when tag is null). Any
        /// existing display: tag is removed first, so they are mutually exclusive
        /// by construction and cannot accumulate.
        /// </summary>
        private void Apply(List<Game> games, string tag)
        {
            try
            {
                var tagId = Guid.Empty;
                if (tag != null)
                {
                    var existing = PlayniteApi.Database.Tags.FirstOrDefault(t =>
                        string.Equals(t.Name, tag, StringComparison.OrdinalIgnoreCase));
                    tagId = (existing ?? PlayniteApi.Database.Tags.Add(tag)).Id;
                }

                // Every display: tag in the DB — the set we clear from.
                var ours = new HashSet<Guid>(PlayniteApi.Database.Tags
                    .Where(t => t.Name != null &&
                                t.Name.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase))
                    .Select(t => t.Id));

                var changed = 0;
                foreach (var game in games)
                {
                    var dirty = false;

                    if (game.TagIds != null)
                    {
                        foreach (var id in game.TagIds.Where(ours.Contains).ToList())
                        {
                            game.TagIds.Remove(id);
                            dirty = true;
                        }
                    }

                    if (tagId != Guid.Empty)
                    {
                        if (game.TagIds == null) game.TagIds = new List<Guid>();
                        if (!game.TagIds.Contains(tagId)) { game.TagIds.Add(tagId); dirty = true; }
                    }

                    if (dirty) { PlayniteApi.Database.Games.Update(game); changed++; }
                }

                var what = tag ?? "(cleared)";
                Logger.Info($"GlazeWM: '{what}' applied to {changed} game(s).");
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "glazewm-display-tag",
                    changed == 1 ? $"{games[0].Name} → {what}" : $"{changed} games → {what}",
                    NotificationType.Info));
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "GlazeWM: failed to apply tag.");
                PlayniteApi.Dialogs.ShowErrorMessage(ex.Message, "GlazeWM");
            }
        }
    }
}



