using System.Reflection;
using System.Security.Cryptography;
using ZstdSharp;

namespace GithubLauncher.Services
{
    /// <summary>
    /// [yabo-launcher fork] Sibling of <see cref="BpsPatchService"/> / <see cref="CompileBuildService"/> for
    /// the "smas-extract" build step — the native replacement for qurious-pixel/SMAS_Launcher.
    ///
    /// The snesrev/smw engine (smw.exe — the same prebuilt binary the "Super Mario World" entry uses) ALSO
    /// runs Super Mario Bros. 1 and Lost Levels: smw.exe takes an optional ROM argument and runs it on its
    /// SNES core. snesrev ships the two SMB ROMs as zstd frames (other/smb1.zst, other/smbll.zst) that were
    /// compressed using the user's Super Mario All-Stars ROM as the zstd dictionary, so distributing them
    /// is legal — they're useless without the user's own SMAS ROM. Decompressing each frame with smas.sfc
    /// as the raw-content dictionary reproduces the original smb1.sfc / smbll.sfc byte-for-byte (mirrors
    /// snesrev/smw's other/extract.py and SMAS_Launcher's extract_smas()).
    ///
    /// We ship the two .zst frames as embedded resources (they're &lt;7 KB each and are NOT in the smw_0.1.zip
    /// release), decompress them with the placed smas.sfc, and verify the SHA-1s. The .sfc outputs are then
    /// passed to smw.exe as the ROM argument; smw.exe still needs smw_assets.dat (produced separately by the
    /// bps-patch step from the user's standalone smw.sfc), which is why the catalog entry carries BOTH ROMs.
    ///
    /// Verified: decompressing the bundled smb1.zst / smbll.zst against "Super Mario All-Stars (USA).sfc"
    /// (sha1 c05817c5...) yields 524,288-byte ROMs with the exact SHA-1s below.
    /// </summary>
    public static class SmasExtractService
    {
        private const string SmasSha1  = "c05817c5b7df2fbfe631563e0b37237156a8f6b6"; // Super Mario All-Stars (USA)
        private const string Smb1Sha1  = "4a5278150f3395419d68cb02a42f7c3c62cdf8b4";
        private const string SmbllSha1 = "493e14812af7a92d0eacf00ba8bb6d3a266302ca";

        /// <summary>
        /// Decompress the bundled SMB1 + Lost Levels zstd frames using <paramref name="smasRomPath"/> as the
        /// dictionary, writing <c>smb1.sfc</c> and <c>smbll.sfc</c> into <paramref name="installDir"/>.
        /// Idempotent: skips a target that already exists and verifies its SHA-1. Throws on a genuine failure
        /// (wrong SMAS ROM, missing embedded payload, hash mismatch) so the caller can surface a clear error.
        /// </summary>
        public static void Extract(string installDir, string smasRomPath, string gameName)
        {
            if (!File.Exists(smasRomPath))
                throw new FileNotFoundException($"SMAS ROM not found for extract: {smasRomPath} (provide the ROM first).");

            byte[] smas = File.ReadAllBytes(smasRomPath);
            var smasHash = Sha1(smas);
            if (!string.Equals(smasHash, SmasSha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Super Mario All-Stars ROM mismatch: expected sha1 {SmasSha1}, got {smasHash}. " +
                    "Provide the USA Super Mario All-Stars ROM.");

            ExtractOne(installDir, smas, "smb1.zst",  "smb1.sfc",  Smb1Sha1,  gameName);
            ExtractOne(installDir, smas, "smbll.zst", "smbll.sfc", SmbllSha1, gameName);
        }

        private static void ExtractOne(string installDir, byte[] smasDict, string zstResource, string outName,
            string expectSha1, string gameName)
        {
            var outPath = Path.Combine(installDir, outName);
            if (File.Exists(outPath) && string.Equals(Sha1(File.ReadAllBytes(outPath)), expectSha1, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"'{gameName}': smas-extract — '{outName}' already present, skipping.");
                return;
            }

            byte[] compressed = ReadEmbedded(zstResource);
            byte[] outBuf;
            using (var d = new Decompressor())
            {
                d.LoadDictionary(smasDict);                 // raw-content dictionary = the SMAS ROM bytes
                outBuf = d.Unwrap(compressed).ToArray();
            }

            var outHash = Sha1(outBuf);
            if (!string.Equals(outHash, expectSha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"'{outName}' hash mismatch after extract: expected {expectSha1}, got {outHash}.");

            File.WriteAllBytes(outPath, outBuf);
            Log.Info($"'{gameName}': smas-extract — produced '{outName}' ({outBuf.Length} bytes).");
        }

        /// <summary>Read an embedded SMAS .zst payload by file name (resource is namespaced, so match on suffix).</summary>
        private static byte[] ReadEmbedded(string fileName)
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase)
                                  || n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
            if (name == null)
                throw new FileNotFoundException($"Embedded SMAS payload not found: {fileName}");
            using var s = asm.GetManifestResourceStream(name)
                ?? throw new FileNotFoundException($"Could not open embedded SMAS payload: {name}");
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        private static string Sha1(byte[] b) => Convert.ToHexString(SHA1.HashData(b)).ToLowerInvariant();
    }
}
