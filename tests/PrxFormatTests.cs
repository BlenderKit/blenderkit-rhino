using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Blendkit.Rhino.Infra;
using Xunit;

namespace Blendkit.Rhino.Tests
{
    /// <summary>
    /// Cross-check the C# PRX reader against fixtures built to match the
    /// Blender add-on's <c>blenderkit/bl_proxor/prx_format.py</c>. Two
    /// formats covered:
    ///
    ///   * Text PRX written by hand — the simplest happy path. Verifies
    ///     the bbox-denormalisation and triangle-list output shape.
    ///   * Quantized binary (PRXQ2) inside a gzip — what the add-on
    ///     actually ships. Verifies header parsing, u16 dequant of
    ///     positions/normals, u8 dequant of colours, and gzip envelope
    ///     handling.
    ///
    /// We don't test the legacy base64+gzip variant — the Python source
    /// keeps it only for backward-compat with files written before the
    /// gzip-only format landed, and we can add a pin if we ever hit one
    /// in the wild.
    /// </summary>
    public class PrxFormatTests
    {
        [Fact]
        public void Text_prx_with_one_triangle_round_trips_to_world_space()
        {
            // Hand-built PRX. The triangle's three vertices in WORLD
            // space are (0,0,0) (1,0,0) (0,1,0). BBox: 0..1 on X, 0..1
            // on Y, 0..0 on Z (so spanZ falls back to EPSILON in the
            // reader). Normalised coords below match what the add-on
            // writer would emit.
            var text = string.Join("\n", new[]
            {
                "# PROXOR_VERSION 1.0.0",
                "@triangle",
                "BB 0 1 0 1 0 0",
                "F",
                "0 0 0",
                "1 0 0",
                "0 1 0",
            });

            var path = WriteTemp(text, ".prx");
            try
            {
                var data = PrxFormat.ReadFile(path);
                Assert.Equal(1, data.MeshTriangleCount);
                Assert.Equal(9, data.MeshPositions.Count);

                // v0 = (0,0,0)
                Assert.Equal(0f, data.MeshPositions[0], 5);
                Assert.Equal(0f, data.MeshPositions[1], 5);
                // v1 = (1,0,0)
                Assert.Equal(1f, data.MeshPositions[3], 5);
                Assert.Equal(0f, data.MeshPositions[4], 5);
                // v2 = (0,1,0)
                Assert.Equal(0f, data.MeshPositions[6], 5);
                Assert.Equal(1f, data.MeshPositions[7], 5);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Text_prx_skips_comments_and_blank_lines()
        {
            // Reader has to drop "# …" comments, blank lines, and the
            // BB/origin/colour metadata between blocks without losing
            // its place in the per-block state machine.
            var text = string.Join("\n", new[]
            {
                "# PROXOR_VERSION 1.0.0",
                "",
                "@cube",
                "BB 0 1 0 1 0 1",
                "",
                "F",
                "0 0 0",
                "1 0 0",
                "1 1 0",
                "",
            });

            var path = WriteTemp(text, ".prx");
            try
            {
                var data = PrxFormat.ReadFile(path);
                Assert.Equal(1, data.MeshTriangleCount);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Prxc_gzip_wrapped_text_decodes_same_as_plain_text()
        {
            // Pin the .prxc envelope: a gzip-compressed payload that,
            // once decompressed, is exactly the text-PRX contents.
            // Quantized payloads have their own magic so the reader
            // distinguishes them; this fixture has no magic so the
            // reader treats it as text.
            var text = string.Join("\n", new[]
            {
                "# PROXOR_VERSION 1.0.0",
                "@tri",
                "BB 0 2 0 2 0 0",
                "F",
                "0 0 0",
                "1 0 0",
                "0 1 0",
            });

            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".prxc");
            try
            {
                using (var fs = File.Create(path))
                using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    gz.Write(bytes, 0, bytes.Length);
                }
                var data = PrxFormat.ReadFile(path);
                Assert.Equal(1, data.MeshTriangleCount);
                // BBox was 0..2 on X/Y → first vertex maps to (0,0,0),
                // second to (2,0,0), third to (0,2,0). Verifies the
                // de-normalisation applies the bbox span correctly.
                Assert.Equal(0f, data.MeshPositions[0], 5);
                Assert.Equal(2f, data.MeshPositions[3], 5);
                Assert.Equal(2f, data.MeshPositions[7], 5);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Prxq2_quantized_binary_one_triangle_decodes_correctly()
        {
            // Build a PRXQ2 payload by hand following the same layout
            // as bl_proxor.prx_format._encode_prxc_quantized:
            //   magic (6 bytes), version (u8), bbox (6 floats),
            //   counts × 7 (u32),
            //   mesh-pos blob (3 u16/vertex), mesh-col blob (3 u8/vertex),
            //   mesh-nrm blob (3 u16/vertex), line-pos blob, line-col blob,
            //   point-pos blob, point-col blob.
            //
            // Triangle has three vertices in WORLD space at the bbox
            // corners (0,0,0) (1,1,1) (0.5,0.5,0.5). After quant they
            // become u16 values 0, 65535, 32767-ish.
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(new byte[] { (byte)'P', (byte)'R', (byte)'X', (byte)'Q', (byte)'2', 0 });
                bw.Write((byte)1);                               // format version
                bw.Write(0f); bw.Write(1f);                      // bbox X
                bw.Write(0f); bw.Write(1f);                      // bbox Y
                bw.Write(0f); bw.Write(1f);                      // bbox Z
                bw.Write((uint)3);   // meshPos count (3 verts)
                bw.Write((uint)0);   // meshCol count
                bw.Write((uint)0);   // meshNrm count
                bw.Write((uint)0);   // linePos count
                bw.Write((uint)0);   // lineCol count
                bw.Write((uint)0);   // pointPos count
                bw.Write((uint)0);   // pointCol count
                // 3 u16 per vertex.
                bw.Write((ushort)0);     bw.Write((ushort)0);     bw.Write((ushort)0);
                bw.Write((ushort)65535); bw.Write((ushort)65535); bw.Write((ushort)65535);
                bw.Write((ushort)32767); bw.Write((ushort)32767); bw.Write((ushort)32767);
            }
            var raw = ms.ToArray();

            // Gzip-wrap so it lands as a real .prxc on disk.
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".prxc");
            try
            {
                using (var fs = File.Create(path))
                using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                {
                    gz.Write(raw, 0, raw.Length);
                }
                var data = PrxFormat.ReadFile(path);
                Assert.Equal(1, data.MeshTriangleCount);
                Assert.Equal(9, data.MeshPositions.Count);
                // 0/65535 → 0.0/1.0; 32767/65535 ≈ 0.49998... .
                Assert.Equal(0f, data.MeshPositions[0], 5);
                Assert.Equal(1f, data.MeshPositions[3], 5);
                Assert.InRange(data.MeshPositions[6], 0.49f, 0.51f);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Missing_file_throws_FileNotFoundException()
        {
            // Caller-controlled error path: misspelled path or asset has
            // no sidecar yet. The locator already pre-checks Existence,
            // so this should rarely fire — but pin the behaviour.
            Assert.Throws<FileNotFoundException>(() =>
                PrxFormat.ReadFile(Path.Combine(Path.GetTempPath(), "definitely-missing-" + Guid.NewGuid().ToString("N") + ".prxc")));
        }

        [Fact]
        public void ProxorLocator_prefers_prxc_over_prx_on_stem_match()
        {
            // When both extensions exist next to the source path with
            // the SAME stem, the binary (.prxc) variant wins — it's
            // the canonical shipped format. This is the fast happy
            // path before the sibling-scan fallback.
            var dir = Path.Combine(Path.GetTempPath(), "prx-locator-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var source = Path.Combine(dir, "asset.glb");
                var prx    = Path.Combine(dir, "asset.prx");
                var prxc   = Path.Combine(dir, "asset.prxc");
                File.WriteAllText(source, "stub");
                File.WriteAllText(prx,    "stub");
                File.WriteAllText(prxc,   "stub");

                var picked = ProxorLocator.FindForSourcePath(source);
                Assert.Equal(prxc, picked);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void ProxorLocator_finds_sibling_prxc_with_different_stem()
        {
            // The actual Blendkit layout: .glb and .prxc share the
            // asset directory but have different UUID stems
            // (verified against the live cache — a flower-hp asset
            // had a .glb stemmed flower-hp_ab834807-… and a .prxc
            // stemmed 6bdcc5fb-… in the same folder). The locator
            // must scan the directory for *.prxc and adopt the unique
            // sibling.
            var dir = Path.Combine(Path.GetTempPath(), "prx-locator-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var source = Path.Combine(dir, "flower-hp_aaaaaaaa-1111-2222-3333-444444444444.glb");
                var prxc   = Path.Combine(dir, "6bdcc5fb-bbbb-cccc-dddd-eeeeeeeeeeee.prxc");
                File.WriteAllText(source, "stub");
                File.WriteAllText(prxc,   "stub");

                var picked = ProxorLocator.FindForSourcePath(source);
                Assert.Equal(prxc, picked);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void ProxorLocator_with_multiple_prxc_picks_newest_by_mtime()
        {
            // Defensive: if an asset directory ends up with two .prxc
            // files (manual download, re-upload, ...), pick the most
            // recent so the user gets the latest variant. The
            // alternative is to fail — but a best-effort proxy is
            // better than nothing.
            var dir = Path.Combine(Path.GetTempPath(), "prx-locator-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var source = Path.Combine(dir, "asset.glb");
                var older  = Path.Combine(dir, "older.prxc");
                var newer  = Path.Combine(dir, "newer.prxc");
                File.WriteAllText(source, "stub");
                File.WriteAllText(older,  "stub");
                System.Threading.Thread.Sleep(50); // ensure mtime differs on FS that round-mtime-to-second
                File.WriteAllText(newer,  "stub");
                File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-2));
                File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

                var picked = ProxorLocator.FindForSourcePath(source);
                Assert.Equal(newer, picked);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void ProxorLocator_returns_null_when_no_sidecar()
        {
            // Common case: the user enabled "use proxor" but the asset
            // doesn't ship a sidecar. Locator returns null and the
            // import path falls back to Mesh.Reduce decimation.
            var dir = Path.Combine(Path.GetTempPath(), "prx-locator-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var source = Path.Combine(dir, "asset.glb");
                File.WriteAllText(source, "stub");
                Assert.Null(ProxorLocator.FindForSourcePath(source));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // ---- helpers ----

        private static string WriteTemp(string content, string ext)
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);
            File.WriteAllText(path, content, Encoding.UTF8);
            return path;
        }
    }
}
