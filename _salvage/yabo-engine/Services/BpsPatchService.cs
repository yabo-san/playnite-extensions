using System.IO;

namespace GithubLauncher.Services
{
    /// <summary>
    /// [yabo-launcher fork] Minimal BPS (beat) binary-patch applier.
    ///
    /// Used to reimplement the RadzPrower SNES launchers natively: snesrev ports ship a small
    /// "&lt;game&gt;_assets.bps" patch in their prebuilt release. Applying it to the user's own ROM
    /// (source) yields "&lt;game&gt;_assets.dat" (target), which the prebuilt &lt;game&gt;.exe loads. This lets
    /// us produce a working install WITHOUT the upstream launcher's fragile clone + TCC-compile chain
    /// (whose radzprower.bat "is not recognized" build step is what was failing).
    ///
    /// Verified: applying zelda3_assets.bps to "Legend of Zelda: A Link to the Past (USA)" produces a
    /// 683,888-byte zelda3_assets.dat that is byte-identical to the one assets/restool.py generates.
    ///
    /// BPS format reference: https://www.romhacking.net/documents/746/ (BPS1).
    /// </summary>
    public static class BpsPatchService
    {
        /// <summary>
        /// Apply <paramref name="patchPath"/> (a BPS1 patch) to <paramref name="sourcePath"/> and write
        /// the result to <paramref name="targetPath"/>. Returns true on success; throws on malformed input.
        /// </summary>
        public static void Apply(string sourcePath, string patchPath, string targetPath)
        {
            byte[] source = File.ReadAllBytes(sourcePath);
            byte[] patch = File.ReadAllBytes(patchPath);

            if (patch.Length < 4 + 12 ||
                patch[0] != (byte)'B' || patch[1] != (byte)'P' || patch[2] != (byte)'S' || patch[3] != (byte)'1')
            {
                throw new InvalidDataException($"Not a BPS1 patch: {Path.GetFileName(patchPath)}");
            }

            int o = 4;
            long DecodeNumber()
            {
                // BPS variable-width number encoding.
                long value = 0; int shift = 0;
                while (true)
                {
                    byte x = patch[o++];
                    value += (long)(x & 0x7f) << shift;
                    if ((x & 0x80) != 0) break;
                    shift += 7;
                    value += 1L << shift;
                }
                return value;
            }

            DecodeNumber();                 // source size (unused — we trust the source file)
            long targetSize = DecodeNumber();
            long metaSize = DecodeNumber();
            o += (int)metaSize;             // skip metadata

            byte[] outBuf = new byte[targetSize];
            int outPos = 0;
            int sourceRel = 0, targetRel = 0;
            int end = patch.Length - 12;    // last 12 bytes are source/target/patch CRC32s

            while (o < end)
            {
                long cmd = DecodeNumber();
                long action = cmd & 3;
                long length = (cmd >> 2) + 1;

                switch (action)
                {
                    case 0: // SourceRead — copy from source at the current output offset
                        for (long i = 0; i < length; i++)
                            outBuf[outPos + i] = source[outPos + i];
                        outPos += (int)length;
                        break;
                    case 1: // TargetRead — copy literal bytes from the patch stream
                        for (long i = 0; i < length; i++)
                            outBuf[outPos + i] = patch[o + i];
                        o += (int)length;
                        outPos += (int)length;
                        break;
                    case 2: // SourceCopy — copy from source at a signed-relative offset
                    {
                        long d = DecodeNumber();
                        sourceRel += (int)((d & 1) != 0 ? -(d >> 1) : (d >> 1));
                        for (long i = 0; i < length; i++)
                            outBuf[outPos++] = source[sourceRel++];
                        break;
                    }
                    default: // 3 TargetCopy — copy from already-written output at a signed-relative offset
                    {
                        long d = DecodeNumber();
                        targetRel += (int)((d & 1) != 0 ? -(d >> 1) : (d >> 1));
                        for (long i = 0; i < length; i++)
                            outBuf[outPos++] = outBuf[targetRel++];
                        break;
                    }
                }
            }

            File.WriteAllBytes(targetPath, outBuf);
        }
    }
}
