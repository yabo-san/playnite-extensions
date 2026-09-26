using System;
using System.IO;
using System.Linq;
using Xunit;
using Yabo.Shared;

namespace Shared.Tests
{
    public class DropCipherTests
    {
        // Known-answer vector generated outside .NET (Python `cryptography`, AES-ECB over
        // hand-built Ctr64LE counter blocks: LE64(iv[0..8] + i) || iv[8..16]), so the
        // test proves the counter layout, not just that AES is AES.
        private static readonly byte[] Key = Hex("0102030405060708090a0b0c0d0e0f10");
        private static readonly byte[] Iv = Hex("f0debc9a78563412ffeeddccbbaa9988");
        private static readonly byte[] Plain = Hex("44726f70206368756e6b20706c61696e746578742c20666f727479206279746573206c6f6e672e2e");
        private static readonly byte[] Cipher = Hex("ef40dd4be2e7a50238b080e190b3faf30e47dd838eb42c48d69139f561ce56f69168f4e78ee00551");
        private const string PlainSha256 = "39a32cbd1882ffe5b3e5b3d7fb9da0524f0d1ae9a5e9b7e95a28c9c804c6b768";

        [Fact]
        public void Ctr64LE_decrypts_the_known_answer_vector()
        {
            var buf = (byte[])Cipher.Clone();
            using (var c = new DropCtrCipher(Key, Iv))
            {
                c.Apply(buf, 0, buf.Length);
            }
            Assert.Equal(Plain, buf);
        }

        [Fact]
        public void Keystream_is_continuous_across_calls_of_any_size()
        {
            // The client streams chunks in 1 MiB reads that do not align to 16 bytes;
            // the cipher has to carry its keystream position across calls.
            var buf = (byte[])Cipher.Clone();
            using (var c = new DropCtrCipher(Key, Iv))
            {
                c.Apply(buf, 0, 7);
                c.Apply(buf, 7, 20);
                c.Apply(buf, 27, buf.Length - 27);
            }
            Assert.Equal(Plain, buf);
        }

        [Fact]
        public void The_counter_lives_in_the_first_eight_bytes_little_endian()
        {
            // Flip a byte in the SECOND half of the IV: every block changes.
            // Flip the LOW byte of the first half: block 0 changes, and so does block 1,
            // because the counter is added to that half. Decrypting with either wrong
            // IV must therefore differ from the plaintext in the first block.
            var badTail = (byte[])Iv.Clone(); badTail[15] ^= 1;
            var badHead = (byte[])Iv.Clone(); badHead[0] ^= 1;
            foreach (var iv in new[] { badTail, badHead })
            {
                var buf = (byte[])Cipher.Clone();
                using (var c = new DropCtrCipher(Key, iv)) c.Apply(buf, 0, buf.Length);
                Assert.NotEqual(Plain.Take(16), buf.Take(16));
            }
        }

        [Fact]
        public void Chunk_writer_places_files_at_their_offsets_and_verifies_the_checksum()
        {
            var dir = Path.Combine(Path.GetTempPath(), "drop-chunk-" + Guid.NewGuid().ToString("N"));
            try
            {
                // One chunk holding two slices: the first 24 bytes of a.bin at offset 0 and
                // the remaining 16 bytes as b/c.bin at offset 8 (a later chunk would fill 0..8).
                var chunk = new DropManifestChunk { Id = "c1", Checksum = PlainSha256, Iv = Iv };
                chunk.Files.Add(new DropManifestFile { Filename = "a.bin", Start = 0, Length = 24 });
                chunk.Files.Add(new DropManifestFile { Filename = "b/c.bin", Start = 8, Length = 16 });

                DropChunkWriter.Write(new MemoryStream(Cipher), chunk, Key, dir, f => true);

                Assert.Equal(Plain.Take(24), File.ReadAllBytes(Path.Combine(dir, "a.bin")));
                var c = File.ReadAllBytes(Path.Combine(dir, "b", "c.bin"));
                Assert.Equal(24, c.Length);
                Assert.Equal(Plain.Skip(24).Take(16), c.Skip(8));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Chunk_writer_rejects_a_bad_checksum_and_skips_files_it_is_told_not_to_write()
        {
            var dir = Path.Combine(Path.GetTempPath(), "drop-chunk-" + Guid.NewGuid().ToString("N"));
            try
            {
                var chunk = new DropManifestChunk { Id = "c1", Checksum = "00", Iv = Iv };
                chunk.Files.Add(new DropManifestFile { Filename = "a.bin", Start = 0, Length = 40 });
                Assert.Throws<InvalidDataException>(() =>
                    DropChunkWriter.Write(new MemoryStream(Cipher), chunk, Key, dir, f => false));
                Assert.False(File.Exists(Path.Combine(dir, "a.bin")));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        private static byte[] Hex(string s) =>
            Enumerable.Range(0, s.Length / 2).Select(i => Convert.ToByte(s.Substring(i * 2, 2), 16)).ToArray();
    }
}
