using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace GithubLauncher
{
    /// <summary>
    /// [yabo-launcher fork] Engine side of the Winamp `.wsz` skin loader.
    ///
    /// A `.wsz` is just a ZIP of classic Winamp 2.x skin assets: a fixed set of
    /// `.bmp` sprite sheets (main window, EQ, buttons, etc.) plus two text configs
    /// (pledit.txt = playlist colours, viscolor.txt = visualiser palette). The Tauri
    /// WebView2 UI renders the sprites itself; this service only EXTRACTS them, caches
    /// the raw files under `&lt;exe dir&gt;\skins\&lt;skinname&gt;\`, and produces a JSON
    /// manifest the UI consumes (each sprite as a `data:image/bmp;base64,...` URI —
    /// Chromium renders BMP natively, so no image-conversion dependency is needed).
    /// </summary>
    public static class WinampSkinService
    {
        /// <summary>The standard Winamp skin sprite sheets, keyed by the manifest field name
        /// → the candidate file name(s) (case-insensitive). A skin may omit any of these.</summary>
        private static readonly (string Field, string[] Files)[] SpriteSheets = new[]
        {
            ("eqmain",   new[] { "eqmain.bmp" }),
            ("main",     new[] { "main.bmp" }),
            ("titlebar", new[] { "titlebar.bmp" }),
            ("cbuttons", new[] { "cbuttons.bmp" }),
            ("text",     new[] { "text.bmp" }),
            ("numbers",  new[] { "numbers.bmp", "nums_ex.bmp" }),
            ("pledit",   new[] { "pledit.bmp" }),
            ("volume",   new[] { "volume.bmp" }),
            ("balance",  new[] { "balance.bmp" }),
            ("posbar",   new[] { "posbar.bmp" }),
            ("shufrep",  new[] { "shufrep.bmp" }),
            ("monoster", new[] { "monoster.bmp" }),
            ("playpaus", new[] { "playpaus.bmp" }),
        };

        /// <summary>Resolved skin manifest: the data the UI needs to render a skin.</summary>
        public sealed class SkinManifest
        {
            public string name { get; set; } = string.Empty;
            /// <summary>field-name → `data:image/bmp;base64,...`; only sheets actually present.</summary>
            public Dictionary<string, string> sprites { get; set; } = new();
            public string? pleditTxt { get; set; }
            public string? viscolorTxt { get; set; }
        }

        private static string SkinsRoot =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "skins");

        /// <summary>
        /// Load a `.wsz` (ZIP) skin: validate, extract the standard bitmaps + text configs to
        /// `&lt;exe dir&gt;\skins\&lt;skinname&gt;\`, record the skin name in settings, and return its manifest.
        /// Throws on a missing / invalid / non-zip file (callers turn that into a clean CLI error).
        /// </summary>
        public static SkinManifest LoadSkin(string wszPath)
        {
            if (string.IsNullOrWhiteSpace(wszPath))
                throw new ArgumentException("No skin path was provided.");
            if (!File.Exists(wszPath))
                throw new FileNotFoundException($"Skin file not found: {wszPath}");

            // Skin name = the .wsz file's base name (e.g. "Bento.wsz" -> "Bento").
            var skinName = Path.GetFileNameWithoutExtension(wszPath);
            if (string.IsNullOrWhiteSpace(skinName))
                throw new ArgumentException($"Could not derive a skin name from: {wszPath}");

            var skinDir = Path.Combine(SkinsRoot, SanitizeName(skinName));

            // Fresh extract — clear any stale cache for this skin name.
            if (Directory.Exists(skinDir))
            {
                try { Directory.Delete(skinDir, recursive: true); } catch { /* best-effort */ }
            }
            Directory.CreateDirectory(skinDir);

            // Open as a ZIP. A non-zip / corrupt file throws InvalidDataException here.
            using (var zip = ZipFile.OpenRead(wszPath))
            {
                foreach (var entry in zip.Entries)
                {
                    // Skip directory entries.
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    var lower = entry.Name.ToLowerInvariant();
                    bool isWanted =
                        SpriteSheets.Any(s => s.Files.Any(f => string.Equals(f, lower, StringComparison.OrdinalIgnoreCase)))
                        || lower == "pledit.txt"
                        || lower == "viscolor.txt";
                    if (!isWanted) continue;

                    // Flatten to the skin dir under the plain file name (skins are flat anyway).
                    var dest = Path.Combine(skinDir, entry.Name);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }

            var manifest = BuildManifest(skinName, skinDir);

            // Record the current skin in settings.
            var settings = AppSettings.Load();
            settings.CurrentSkin = skinName;
            AppSettings.Save(settings);

            return manifest;
        }

        /// <summary>
        /// Build the manifest for the CURRENT skin from its already-extracted cache dir.
        /// Returns null when no skin is set or its cache dir is missing.
        /// </summary>
        public static SkinManifest? GetCurrentSkin()
        {
            var settings = AppSettings.Load();
            var name = settings.CurrentSkin;
            if (string.IsNullOrWhiteSpace(name)) return null;

            var skinDir = Path.Combine(SkinsRoot, SanitizeName(name));
            if (!Directory.Exists(skinDir)) return null;

            return BuildManifest(name, skinDir);
        }

        /// <summary>Clear the current skin from settings (UI falls back to its CSS skin).</summary>
        public static void ClearSkin()
        {
            var settings = AppSettings.Load();
            settings.CurrentSkin = string.Empty;
            AppSettings.Save(settings);
        }

        /// <summary>Read the cached skin dir and assemble the manifest (sprites as data URIs + text configs).</summary>
        private static SkinManifest BuildManifest(string name, string skinDir)
        {
            var manifest = new SkinManifest { name = name };

            // Index the files actually present in the cache dir, case-insensitive.
            var present = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(skinDir))
                present[Path.GetFileName(path)] = path;

            foreach (var (field, files) in SpriteSheets)
            {
                foreach (var candidate in files)
                {
                    if (present.TryGetValue(candidate, out var path))
                    {
                        var bytes = File.ReadAllBytes(path);
                        manifest.sprites[field] = "data:image/bmp;base64," + Convert.ToBase64String(bytes);
                        break; // first match wins (e.g. numbers.bmp over nums_ex.bmp)
                    }
                }
            }

            manifest.pleditTxt = present.TryGetValue("pledit.txt", out var pl) ? File.ReadAllText(pl) : null;
            manifest.viscolorTxt = present.TryGetValue("viscolor.txt", out var vc) ? File.ReadAllText(vc) : null;

            return manifest;
        }

        /// <summary>Strip characters that aren't valid in a directory name.</summary>
        private static string SanitizeName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? "skin" : cleaned;
        }
    }
}
