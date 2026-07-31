using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GithubLauncher.Services
{
    /// <summary>
    /// [yabo-launcher fork] Non-Steam-shortcut presence path. Adds a yabo game to Steam as a
    /// "non-Steam game" so Steam shows "Playing &lt;game&gt;" (native playtime + the Steam overlay),
    /// launched through our own <c>yabo-launcher.exe --play "&lt;game&gt;"</c>.
    ///
    /// This is a self-contained reader/writer for Steam's BINARY shortcuts.vdf format:
    ///   - markers:   0x00 = nested object,  0x01 = string,  0x02 = int32 (little-endian)
    ///   - keys + string values are NUL-terminated UTF-8
    ///   - the file is one root object  \x00 "shortcuts" \x00  { "0"{...} "1"{...} ... }
    ///   - every object is terminated by 0x08; the root therefore ends with 0x08 0x08
    ///     (one 0x08 closes the "shortcuts" object, one closes the root).
    ///
    /// Steam reads shortcuts.vdf only at startup, so callers must tell the user to RESTART Steam.
    ///
    /// Steam path discovery is delegated to <see cref="SteamContentLocator.GetSteamPath"/>
    /// (HKCU\Software\Valve\Steam\SteamPath). userdata\&lt;id&gt;\config\shortcuts.vdf is then located
    /// per profile (0, 1, or many — see <see cref="GetShortcutsVdfPaths"/>).
    /// </summary>
    public static class SteamShortcutService
    {
        /// <summary>Tag stamped on every shortcut we create so we can find/list/remove only ours.</summary>
        public const string YaboTag = "yabo-launcher";

        // ---- binary value model -------------------------------------------------------------

        public abstract class VdfValue { }

        public sealed class VdfString : VdfValue
        {
            public string Value;
            public VdfString(string value) => Value = value ?? string.Empty;
        }

        public sealed class VdfInt : VdfValue
        {
            public int Value;
            public VdfInt(int value) => Value = value;
        }

        public sealed class VdfObject : VdfValue
        {
            // Ordered key/value pairs (shortcuts.vdf is order-sensitive: "0","1","2"...).
            public List<KeyValuePair<string, VdfValue>> Items { get; } = new();

            public VdfValue? Get(string key) =>
                Items.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

            public string? GetString(string key) => Get(key) is VdfString s ? s.Value : null;

            public void Set(string key, VdfValue value)
            {
                for (int i = 0; i < Items.Count; i++)
                {
                    if (string.Equals(Items[i].Key, key, StringComparison.OrdinalIgnoreCase))
                    {
                        Items[i] = new KeyValuePair<string, VdfValue>(Items[i].Key, value);
                        return;
                    }
                }
                Items.Add(new KeyValuePair<string, VdfValue>(key, value));
            }
        }

        // ---- Steam path / profile discovery ------------------------------------------------

        /// <summary>
        /// Every userdata\&lt;id&gt;\config\shortcuts.vdf path under the Steam install. Handles 0, 1, or
        /// many profiles. Paths are returned even if the file doesn't exist yet (an add creates it),
        /// ordered most-recently-modified first so a single-target caller writes the active profile.
        /// </summary>
        public static IReadOnlyList<string> GetShortcutsVdfPaths()
        {
            var result = new List<string>();
            var steam = SteamContentLocator.GetSteamPath();
            if (string.IsNullOrWhiteSpace(steam))
                return result;

            var userdata = Path.Combine(steam, "userdata");
            if (!Directory.Exists(userdata))
                return result;

            string[] profiles;
            try { profiles = Directory.GetDirectories(userdata); }
            catch { return result; }

            foreach (var profile in profiles)
            {
                // numeric account-id folders only (skip "0", "ac", etc. -> "0" is anonymous, still valid)
                var name = Path.GetFileName(profile);
                if (string.IsNullOrEmpty(name) || !name.All(char.IsDigit))
                    continue;
                result.Add(Path.Combine(profile, "config", "shortcuts.vdf"));
            }

            // Most-recently-touched profile first (existing file mtime, else the config dir's).
            return result
                .OrderByDescending(p =>
                {
                    try
                    {
                        if (File.Exists(p)) return File.GetLastWriteTimeUtc(p);
                        var dir = Path.GetDirectoryName(p);
                        return dir != null && Directory.Exists(dir)
                            ? Directory.GetLastWriteTimeUtc(dir)
                            : DateTime.MinValue;
                    }
                    catch { return DateTime.MinValue; }
                })
                .ToList();
        }

        // ---- binary read --------------------------------------------------------------------

        /// <summary>Parse a shortcuts.vdf file into its root object. Empty root if file is absent.</summary>
        public static VdfObject Read(string path)
        {
            if (!File.Exists(path))
                return NewEmptyRoot();

            var bytes = File.ReadAllBytes(path);
            int pos = 0;
            return ReadObject(bytes, ref pos);
        }

        /// <summary>A fresh root containing an empty "shortcuts" object.</summary>
        public static VdfObject NewEmptyRoot()
        {
            var root = new VdfObject();
            root.Items.Add(new KeyValuePair<string, VdfValue>("shortcuts", new VdfObject()));
            return root;
        }

        private static VdfObject ReadObject(byte[] b, ref int pos)
        {
            var obj = new VdfObject();
            while (pos < b.Length)
            {
                byte marker = b[pos++];
                if (marker == 0x08) // end-of-object
                    break;

                string key = ReadCString(b, ref pos);
                switch (marker)
                {
                    case 0x00: // nested object
                        obj.Items.Add(new KeyValuePair<string, VdfValue>(key, ReadObject(b, ref pos)));
                        break;
                    case 0x01: // string
                        obj.Items.Add(new KeyValuePair<string, VdfValue>(key, new VdfString(ReadCString(b, ref pos))));
                        break;
                    case 0x02: // int32 LE
                        int v = b[pos] | (b[pos + 1] << 8) | (b[pos + 2] << 16) | (b[pos + 3] << 24);
                        pos += 4;
                        obj.Items.Add(new KeyValuePair<string, VdfValue>(key, new VdfInt(v)));
                        break;
                    default:
                        throw new InvalidDataException($"Unknown shortcuts.vdf marker 0x{marker:X2} at offset {pos - 1}.");
                }
            }
            return obj;
        }

        private static string ReadCString(byte[] b, ref int pos)
        {
            int start = pos;
            while (pos < b.Length && b[pos] != 0x00) pos++;
            string s = Encoding.UTF8.GetString(b, start, pos - start);
            if (pos < b.Length) pos++; // skip NUL
            return s;
        }

        // ---- binary write -------------------------------------------------------------------

        /// <summary>
        /// Serialize the root to bytes and write atomically, backing up any existing file to
        /// &lt;path&gt;.bak first so a partial/corrupt write can never lose the user's real shortcuts.
        /// </summary>
        public static void Write(string path, VdfObject root)
        {
            var bytes = Serialize(root);

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // Back up the existing file before touching it.
            if (File.Exists(path))
            {
                try { File.Copy(path, path + ".bak", overwrite: true); }
                catch { /* best-effort backup; do not block the write */ }
            }

            // Write to a temp file then move into place (atomic on the same volume).
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(path))
                File.Replace(tmp, path, null);
            else
                File.Move(tmp, path);
        }

        public static byte[] Serialize(VdfObject root)
        {
            using var ms = new MemoryStream();
            WriteObjectBody(ms, root);
            return ms.ToArray();
        }

        // Writes an object's members followed by its terminating 0x08.
        private static void WriteObjectBody(Stream s, VdfObject obj)
        {
            foreach (var kv in obj.Items)
            {
                switch (kv.Value)
                {
                    case VdfObject o:
                        s.WriteByte(0x00);
                        WriteCString(s, kv.Key);
                        WriteObjectBody(s, o);
                        break;
                    case VdfString str:
                        s.WriteByte(0x01);
                        WriteCString(s, kv.Key);
                        WriteCString(s, str.Value);
                        break;
                    case VdfInt i:
                        s.WriteByte(0x02);
                        WriteCString(s, kv.Key);
                        s.WriteByte((byte)(i.Value & 0xFF));
                        s.WriteByte((byte)((i.Value >> 8) & 0xFF));
                        s.WriteByte((byte)((i.Value >> 16) & 0xFF));
                        s.WriteByte((byte)((i.Value >> 24) & 0xFF));
                        break;
                }
            }
            s.WriteByte(0x08); // end-of-object
        }

        private static void WriteCString(Stream s, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            s.Write(bytes, 0, bytes.Length);
            s.WriteByte(0x00);
        }

        // ---- shortcut entry helpers ---------------------------------------------------------

        /// <summary>The "shortcuts" child object of the root (created if missing).</summary>
        public static VdfObject GetShortcutsObject(VdfObject root)
        {
            if (root.Get("shortcuts") is VdfObject existing)
                return existing;
            var created = new VdfObject();
            root.Set("shortcuts", created);
            return created;
        }

        /// <summary>True if a shortcut entry carries our yabo tag.</summary>
        public static bool IsYaboEntry(VdfObject entry)
        {
            if (entry.Get("tags") is not VdfObject tags)
                return false;
            return tags.Items.Any(kv => kv.Value is VdfString s &&
                string.Equals(s.Value, YaboTag, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The display name (AppName) of a shortcut entry, or "".</summary>
        public static string EntryName(VdfObject entry) =>
            entry.GetString("AppName") ?? entry.GetString("appname") ?? string.Empty;

        /// <summary>The LaunchOptions of a shortcut entry, or "".</summary>
        public static string EntryLaunchOptions(VdfObject entry) =>
            entry.GetString("LaunchOptions") ?? string.Empty;

        /// <summary>
        /// Steam non-Steam-app id: CRC32(exe + appname) with the high bit set. Deterministic so the
        /// same game maps to the same shortcut on re-add (grid art keyed off it stays attached).
        /// </summary>
        public static uint ComputeAppId(string exe, string appName)
        {
            uint crc = Crc32(Encoding.UTF8.GetBytes(exe + appName));
            return crc | 0x80000000u;
        }

        private static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (var b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
            return ~crc;
        }

        /// <summary>
        /// Build a fully-populated non-Steam shortcut entry. Exe is quoted; LaunchOptions is
        /// <c>--play "&lt;game&gt;"</c>; icon is a local file path (or "" if none).
        /// </summary>
        public static VdfObject BuildEntry(string appName, string quotedExe, string startDir, string launchOptions, string iconPath)
        {
            uint appId = ComputeAppId(quotedExe, appName);

            var e = new VdfObject();
            e.Set("appid", new VdfInt(unchecked((int)appId)));
            e.Set("AppName", new VdfString(appName));
            e.Set("Exe", new VdfString(quotedExe));
            e.Set("StartDir", new VdfString(startDir));
            e.Set("icon", new VdfString(iconPath ?? string.Empty));
            e.Set("ShortcutPath", new VdfString(string.Empty));
            e.Set("LaunchOptions", new VdfString(launchOptions));
            e.Set("IsHidden", new VdfInt(0));
            e.Set("AllowDesktopConfig", new VdfInt(1));
            e.Set("AllowOverlay", new VdfInt(1));
            e.Set("OpenVR", new VdfInt(0));
            e.Set("Devkit", new VdfInt(0));
            e.Set("DevkitGameID", new VdfString(string.Empty));
            e.Set("DevkitOverrideAppID", new VdfInt(0));
            e.Set("LastPlayTime", new VdfInt(0));

            var tags = new VdfObject();
            tags.Items.Add(new KeyValuePair<string, VdfValue>("0", new VdfString(YaboTag)));
            e.Set("tags", tags);

            return e;
        }

        /// <summary>Re-key the entries of the "shortcuts" object to a contiguous 0,1,2... sequence.</summary>
        public static void Reindex(VdfObject shortcuts)
        {
            for (int i = 0; i < shortcuts.Items.Count; i++)
                shortcuts.Items[i] = new KeyValuePair<string, VdfValue>(i.ToString(), shortcuts.Items[i].Value);
        }

        /// <summary>
        /// Add (idempotently) a yabo shortcut for <paramref name="appName"/> to the root. Returns false
        /// (no change) if a yabo entry for that name already exists. The caller persists via Write().
        /// </summary>
        public static bool AddShortcut(VdfObject root, string appName, string quotedExe, string startDir, string launchOptions, string iconPath)
        {
            var shortcuts = GetShortcutsObject(root);

            bool exists = shortcuts.Items.Any(kv =>
                kv.Value is VdfObject e &&
                IsYaboEntry(e) &&
                string.Equals(EntryName(e), appName, StringComparison.OrdinalIgnoreCase));
            if (exists)
                return false;

            var entry = BuildEntry(appName, quotedExe, startDir, launchOptions, iconPath);
            shortcuts.Items.Add(new KeyValuePair<string, VdfValue>(
                shortcuts.Items.Count.ToString(), entry));
            Reindex(shortcuts);
            return true;
        }

        /// <summary>
        /// Remove the yabo shortcut(s) for <paramref name="appName"/>. Returns the number removed.
        /// </summary>
        public static int RemoveShortcut(VdfObject root, string appName)
        {
            var shortcuts = GetShortcutsObject(root);
            int before = shortcuts.Items.Count;

            var kept = shortcuts.Items.Where(kv =>
                !(kv.Value is VdfObject e &&
                  IsYaboEntry(e) &&
                  string.Equals(EntryName(e), appName, StringComparison.OrdinalIgnoreCase))).ToList();

            shortcuts.Items.Clear();
            shortcuts.Items.AddRange(kept);
            Reindex(shortcuts);
            return before - shortcuts.Items.Count;
        }

        /// <summary>All yabo shortcut entries in the root (AppName + LaunchOptions + Exe).</summary>
        public static IReadOnlyList<VdfObject> ListYaboEntries(VdfObject root)
        {
            var shortcuts = GetShortcutsObject(root);
            return shortcuts.Items
                .Select(kv => kv.Value)
                .OfType<VdfObject>()
                .Where(IsYaboEntry)
                .ToList();
        }

        /// <summary>Quote a path for the Exe field the way Steam expects (wrapped in double quotes).</summary>
        public static string QuoteExe(string path) => "\"" + (path ?? string.Empty).Replace("\"", string.Empty) + "\"";

        /// <summary>The LaunchOptions string for our presence path: --play "&lt;game&gt;".</summary>
        public static string BuildLaunchOptions(string appName) =>
            "--play \"" + (appName ?? string.Empty).Replace("\"", string.Empty) + "\"";
    }
}
