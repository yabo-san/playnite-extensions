using System;
using System.IO;
using System.Security.Cryptography;

namespace Yabo.Shared
{
    /// <summary>
    /// The cipher Drop's desktop client uses on every chunk it downloads, reproduced
    /// exactly: AES-128 in counter mode with the RustCrypto <c>Ctr64LE</c> flavour.
    ///
    /// <c>Ctr64LE</c> is not the CTR most libraries ship. The 16-byte IV is split in
    /// two 64-bit halves; the FIRST half is a little-endian counter that increments
    /// per block (wrapping), the SECOND half is copied through untouched. Read off
    /// <c>RustCrypto/block-modes ctr/src/flavors/ctr64.rs</c>: <c>from_nonce</c>
    /// takes <c>u64::from_le_bytes</c> of chunk 0 and <c>current_block</c> writes
    /// <c>ctr.wrapping_add(nonce[0]).to_le_bytes()</c>. .NET has no CTR mode at all
    /// on net462, so the keystream is AES-ECB over those counter blocks, XORed in.
    ///
    /// A new cipher per chunk: Drop's client constructs <c>Aes128Ctr64LE::new(key, iv)</c>
    /// with the version's key and the chunk's IV (<c>download_logic.rs:103</c>), so the
    /// counter restarts at 0 for every chunk.
    /// </summary>
    public sealed class DropCtrCipher : IDisposable
    {
        private readonly ICryptoTransform _ecb;
        private readonly byte[] _block = new byte[16];
        private readonly byte[] _keystream = new byte[16];
        private readonly byte[] _ivTail = new byte[8];
        private ulong _nonce0;
        private ulong _counter;
        private int _used = 16; // position inside _keystream; 16 = exhausted

        public DropCtrCipher(byte[] key, byte[] iv)
        {
            if (key == null || key.Length != 16) throw new ArgumentException("AES-128 key must be 16 bytes.", nameof(key));
            if (iv == null || iv.Length != 16) throw new ArgumentException("IV must be 16 bytes.", nameof(iv));

            var aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = key;
            _ecb = aes.CreateEncryptor();

            _nonce0 = BitConverter.ToUInt64(LittleEndian(iv, 0), 0);
            Array.Copy(iv, 8, _ivTail, 0, 8);
            _counter = 0;
        }

        /// <summary>Decrypts (or encrypts; CTR is symmetric) <paramref name="count"/> bytes in place.</summary>
        public void Apply(byte[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (_used == 16)
                {
                    NextBlock();
                }
                buffer[offset + i] ^= _keystream[_used++];
            }
        }

        private void NextBlock()
        {
            unchecked
            {
                var ctr = _nonce0 + _counter;
                var le = BitConverter.GetBytes(ctr);
                if (!BitConverter.IsLittleEndian) Array.Reverse(le);
                Array.Copy(le, 0, _block, 0, 8);
            }
            Array.Copy(_ivTail, 0, _block, 8, 8);
            _ecb.TransformBlock(_block, 0, 16, _keystream, 0);
            _counter++;
            _used = 0;
        }

        private static byte[] LittleEndian(byte[] src, int offset)
        {
            var b = new byte[8];
            Array.Copy(src, offset, b, 0, 8);
            if (!BitConverter.IsLittleEndian) Array.Reverse(b);
            return b;
        }

        public void Dispose()
        {
            _ecb.Dispose();
        }
    }

    /// <summary>
    /// Writes one downloaded chunk to disk the way Drop's client does
    /// (<c>download_logic.rs</c>): the chunk body is a concatenation of file slices in
    /// manifest order, each decrypted with the chunk cipher, hashed with SHA-256 over
    /// the PLAINTEXT of the whole chunk, and written at <c>file.start</c> inside the
    /// target file. Files the caller does not want (another version owns them, per
    /// <c>fileList</c>) are still consumed from the stream so the keystream and hash
    /// stay aligned, but not written.
    /// </summary>
    public static class DropChunkWriter
    {
        public static void Write(
            Stream chunkBody,
            DropManifestChunk chunk,
            byte[] versionKey,
            string installDir,
            Func<string, bool> shouldWriteFile)
        {
            using (var cipher = new DropCtrCipher(versionKey, chunk.Iv))
            using (var sha = SHA256.Create())
            {
                var buf = new byte[1 << 20];
                foreach (var f in chunk.Files)
                {
                    var path = Path.Combine(installDir, f.Filename.Replace('/', Path.DirectorySeparatorChar));
                    FileStream target = null;
                    if (shouldWriteFile == null || shouldWriteFile(f.Filename))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        target = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
                        target.Seek(f.Start, SeekOrigin.Begin);
                    }
                    try
                    {
                        long remaining = f.Length;
                        while (remaining > 0)
                        {
                            int want = (int)Math.Min(buf.Length, remaining);
                            int got = chunkBody.Read(buf, 0, want);
                            if (got <= 0) throw new EndOfStreamException("Chunk body ended before its files did: " + chunk.Id);
                            cipher.Apply(buf, 0, got);
                            sha.TransformBlock(buf, 0, got, null, 0);
                            target?.Write(buf, 0, got);
                            remaining -= got;
                        }
                    }
                    finally
                    {
                        target?.Dispose();
                    }
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                var digest = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                if (!string.Equals(digest, chunk.Checksum, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Chunk " + chunk.Id + " failed its checksum: expected " + chunk.Checksum + ", got " + digest);
                }
            }
        }
    }
}
