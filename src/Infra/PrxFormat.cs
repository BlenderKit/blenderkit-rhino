using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// Port of <c>blenderkit/bl_proxor/prx_format.py</c> — a lightweight
    /// proxy-geometry format the Blender add-on ships for drag-drop
    /// previews and viewport stand-ins. The Rhino plug-in only needs the
    /// reader; the writer / generator stays in the add-on.
    ///
    /// File variants the reader accepts:
    ///   * <c>.prx</c>  — text. Header line <c># PROXOR_VERSION 1.0.0</c>,
    ///                    object markers <c>@name</c>, optional bounding box
    ///                    line <c>BB minX maxX minY maxY minZ maxZ</c>,
    ///                    then named blocks (<c>F</c> for face vertices,
    ///                    <c>FC</c> face colours, <c>FN</c> face normals,
    ///                    <c>P</c> points, <c>PC</c> point colours, <c>L</c>
    ///                    lines, <c>LC</c> line colours). Coordinates are
    ///                    stored normalised to the bbox.
    ///   * <c>.prxc</c> — gzip-compressed payload. Two flavours inside:
    ///       - PRXQ2 quantized binary (PRX_FORMAT_PRXQ2 magic). u16-packed
    ///         positions + colours + normals; smallest on disk.
    ///       - text PRX (see above), just gzip-wrapped.
    ///   * legacy <c>.prxc</c> with base64-wrapped gzip — also accepted.
    ///
    /// Returned <see cref="PrxData"/> exposes the three drawable sections
    /// (mesh / lines / points) as flat arrays in WORLD-SPACE coordinates.
    /// The mesh section is a flat triangle list — every 3 consecutive
    /// positions form one tri, normals and colours are per-vertex.
    /// </summary>
    public static class PrxFormat
    {
        // Quantized-binary header: 6 bytes magic, 1 byte format-version,
        // 6 floats bbox (minX,maxX,minY,maxY,minZ,maxZ), 7 uint32 counts
        // (meshPos, meshCol, meshNrm, linePos, lineCol, pointPos, pointCol).
        // Magic literally reads "PRXQ2\0".
        private static readonly byte[] PRXQ2_MAGIC = new byte[] { (byte)'P', (byte)'R', (byte)'X', (byte)'Q', (byte)'2', 0 };
        private const int PRXQ2_HEADER_SIZE = 6 + 1 + 6 * 4 + 7 * 4; // 59

        private const float EPSILON = 1e-9f;
        private const float U16_MAX = 65535f;
        private const float U8_MAX = 255f;

        /// <summary>Top-level file → in-memory result.</summary>
        public static PrxData ReadFile(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            if (!File.Exists(path)) throw new FileNotFoundException("PRX file not found", path);

            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".prxc")
            {
                var raw = File.ReadAllBytes(path);
                var decoded = DecodePrxcPayload(raw);
                // Quantized payload starts with the PRXQ2 magic; legacy
                // gzipped-text payloads don't.
                if (StartsWith(decoded, PRXQ2_MAGIC))
                    return DecodeQuantized(decoded);
                // Fall through to text-PRX decode on a UTF-8 view of the
                // decompressed bytes.
                var text = Encoding.UTF8.GetString(decoded);
                return ReadTextLines(text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));
            }

            // Plain text PRX.
            var lines = File.ReadAllLines(path);
            return ReadTextLines(lines);
        }

        // ---------- public data shape -------------------------------------

        public class PrxData
        {
            // Mesh: every 3 consecutive Positions form one triangle.
            // Colors and Normals are per-vertex when present; either may
            // be empty.
            public List<float> MeshPositions = new();
            public List<float> MeshColors    = new();
            public List<float> MeshNormals   = new();

            // Lines: every 2 consecutive Positions form one line segment.
            public List<float> LinePositions = new();
            public List<float> LineColors    = new();

            // Points: each Position is one point. Colors carry RGBA.
            public List<float> PointPositions = new();
            public List<float> PointColors    = new();

            /// <summary>Convenience — true if any drawable section has data.</summary>
            public bool IsEmpty =>
                MeshPositions.Count == 0
                && LinePositions.Count == 0
                && PointPositions.Count == 0;

            public int MeshTriangleCount => MeshPositions.Count / 9;  // 3 verts × 3 floats
        }

        // ---------- .prxc payload decoding --------------------------------

        private static byte[] DecodePrxcPayload(byte[] payload)
        {
            // Preferred: raw gzip.
            try { return GzipDecompress(payload); } catch { /* fall through */ }
            // Legacy: base64 wrapper around gzip.
            try
            {
                var ascii = Encoding.ASCII.GetString(payload).Trim();
                var decoded = Convert.FromBase64String(ascii);
                return GzipDecompress(decoded);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Unsupported .prxc payload encoding", ex);
            }
        }

        private static byte[] GzipDecompress(byte[] payload)
        {
            using var input = new MemoryStream(payload);
            using var gz = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gz.CopyTo(output);
            return output.ToArray();
        }

        // ---------- quantized binary (PRXQ2) ------------------------------

        private static PrxData DecodeQuantized(byte[] payload)
        {
            if (payload.Length < PRXQ2_HEADER_SIZE)
                throw new InvalidDataException("PRXQ2 payload truncated (header)");
            using var ms = new MemoryStream(payload);
            using var br = new BinaryReader(ms);

            var magic = br.ReadBytes(6);
            if (!StartsWith(magic, PRXQ2_MAGIC))
                throw new InvalidDataException("PRXQ2 magic mismatch");
            // formatVersion is currently 1; reserved for future shape changes.
            _ = br.ReadByte();

            // Bounding box (world-space) used to de-quantize positions.
            float bb0 = br.ReadSingle(); // minX
            float bb1 = br.ReadSingle(); // maxX
            float bb2 = br.ReadSingle(); // minY
            float bb3 = br.ReadSingle(); // maxY
            float bb4 = br.ReadSingle(); // minZ
            float bb5 = br.ReadSingle(); // maxZ
            float spanX = Math.Max(bb1 - bb0, EPSILON);
            float spanY = Math.Max(bb3 - bb2, EPSILON);
            float spanZ = Math.Max(bb5 - bb4, EPSILON);

            int meshPosCount  = (int)br.ReadUInt32();
            int meshColCount  = (int)br.ReadUInt32();
            int meshNrmCount  = (int)br.ReadUInt32();
            int linePosCount  = (int)br.ReadUInt32();
            int lineColCount  = (int)br.ReadUInt32();
            int pointPosCount = (int)br.ReadUInt32();
            int pointColCount = (int)br.ReadUInt32();

            var data = new PrxData();
            // Position blobs are u16-per-axis (6 bytes per vertex).
            ReadDequantPositions(br, meshPosCount, bb0, bb2, bb4, spanX, spanY, spanZ, data.MeshPositions);
            ReadDequantColorsRgb (br, meshColCount, data.MeshColors);
            // Normals are u16 per axis in [0..1] remapped to [-1..1].
            ReadDequantNormals   (br, meshNrmCount, data.MeshNormals);
            ReadDequantPositions(br, linePosCount,  bb0, bb2, bb4, spanX, spanY, spanZ, data.LinePositions);
            ReadDequantColorsRgb (br, lineColCount, data.LineColors);
            ReadDequantPositions(br, pointPosCount, bb0, bb2, bb4, spanX, spanY, spanZ, data.PointPositions);
            ReadDequantColorsRgba(br, pointColCount, data.PointColors);

            return data;
        }

        private static void ReadDequantPositions(BinaryReader br, int count,
            float minX, float minY, float minZ, float spanX, float spanY, float spanZ,
            List<float> sink)
        {
            for (int i = 0; i < count; i++)
            {
                ushort qx = br.ReadUInt16();
                ushort qy = br.ReadUInt16();
                ushort qz = br.ReadUInt16();
                sink.Add(minX + (qx / U16_MAX) * spanX);
                sink.Add(minY + (qy / U16_MAX) * spanY);
                sink.Add(minZ + (qz / U16_MAX) * spanZ);
            }
        }

        private static void ReadDequantNormals(BinaryReader br, int count, List<float> sink)
        {
            for (int i = 0; i < count; i++)
            {
                ushort qx = br.ReadUInt16();
                ushort qy = br.ReadUInt16();
                ushort qz = br.ReadUInt16();
                // [0..1] → [-1..1].
                sink.Add((qx / U16_MAX) * 2f - 1f);
                sink.Add((qy / U16_MAX) * 2f - 1f);
                sink.Add((qz / U16_MAX) * 2f - 1f);
            }
        }

        private static void ReadDequantColorsRgb(BinaryReader br, int count, List<float> sink)
        {
            for (int i = 0; i < count; i++)
            {
                sink.Add(br.ReadByte() / U8_MAX);
                sink.Add(br.ReadByte() / U8_MAX);
                sink.Add(br.ReadByte() / U8_MAX);
            }
        }

        private static void ReadDequantColorsRgba(BinaryReader br, int count, List<float> sink)
        {
            for (int i = 0; i < count; i++)
            {
                sink.Add(br.ReadByte() / U8_MAX);
                sink.Add(br.ReadByte() / U8_MAX);
                sink.Add(br.ReadByte() / U8_MAX);
                sink.Add(br.ReadByte() / U8_MAX);
            }
        }

        // ---------- text PRX ----------------------------------------------

        // Text format walks line-by-line. The parser collects per-object
        // bounding boxes + blocks (F/FC/FN/P/PC/L/LC), then converts each
        // object's normalised coordinates back to world space using its
        // bbox. Outputs are concatenated across objects.
        private static PrxData ReadTextLines(IEnumerable<string> rawLines)
        {
            var objects = new List<TextObject>();
            TextObject current = null;
            string currentBlock = null;

            foreach (var raw in rawLines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (line.StartsWith("@"))
                {
                    if (current != null) objects.Add(current);
                    current = new TextObject { Name = line.Substring(1).Trim() };
                    currentBlock = null;
                    continue;
                }
                if (current == null) continue;

                var tokens = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) continue;
                var head = tokens[0];

                if (head == "BB")
                {
                    current.BBox = ParseFloats(tokens, 1);
                    currentBlock = null;
                    continue;
                }
                if (head == "O" || head == "C")
                {
                    // Origin / object colour — captured for completeness
                    // but the Rhino path only needs them indirectly via
                    // default colouring elsewhere. Keep parsing them so
                    // we don't lose the next block.
                    currentBlock = null;
                    continue;
                }
                if (IsBlockHeader(head) && tokens.Length == 1)
                {
                    currentBlock = head;
                    current.GetSection(head).Add(new List<float[]>());
                    continue;
                }
                if (currentBlock != null)
                {
                    var values = ParseFloats(tokens, 0);
                    if (values.Length > 0)
                    {
                        var sections = current.GetSection(currentBlock);
                        if (sections.Count == 0) sections.Add(new List<float[]>());
                        sections[sections.Count - 1].Add(values);
                    }
                }
            }
            if (current != null) objects.Add(current);

            // Merge objects into the flat PrxData buffers, de-normalising
            // by each object's own bbox.
            var data = new PrxData();
            foreach (var obj in objects) MergeObjectInto(obj, data);
            return data;
        }

        private static bool IsBlockHeader(string head)
            => head == "F" || head == "FC" || head == "FN"
            || head == "P" || head == "PC"
            || head == "L" || head == "LC";

        private static float[] ParseFloats(string[] tokens, int start)
        {
            int len = tokens.Length - start;
            if (len <= 0) return Array.Empty<float>();
            var result = new float[len];
            for (int i = 0; i < len; i++)
            {
                // PRX uses '.'-decimal regardless of locale.
                if (!float.TryParse(tokens[start + i],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out result[i]))
                    result[i] = 0f;
            }
            return result;
        }

        private sealed class TextObject
        {
            public string Name;
            public float[] BBox;
            public Dictionary<string, List<List<float[]>>> Blocks = new();

            public List<List<float[]>> GetSection(string head)
            {
                if (!Blocks.TryGetValue(head, out var s))
                {
                    s = new List<List<float[]>>();
                    Blocks[head] = s;
                }
                return s;
            }
        }

        private static void MergeObjectInto(TextObject obj, PrxData sink)
        {
            if (obj.BBox == null || obj.BBox.Length < 6) return;
            float minX = obj.BBox[0], spanX = Math.Max(obj.BBox[1] - obj.BBox[0], EPSILON);
            float minY = obj.BBox[2], spanY = Math.Max(obj.BBox[3] - obj.BBox[2], EPSILON);
            float minZ = obj.BBox[4], spanZ = Math.Max(obj.BBox[5] - obj.BBox[4], EPSILON);

            // Mesh faces: F + FC + FN.
            FlattenAndDenormPositions(obj.Blocks, "F", minX, minY, minZ, spanX, spanY, spanZ, sink.MeshPositions);
            FlattenColors          (obj.Blocks, "FC", 3, sink.MeshColors);
            FlattenAndDenormNormals(obj.Blocks, "FN", sink.MeshNormals);

            // Lines: L is per-section polylines; emit each consecutive
            // pair of vertices as a line segment, matching the Python
            // _build_output_data path.
            if (obj.Blocks.TryGetValue("L", out var lineSections))
            {
                obj.Blocks.TryGetValue("LC", out var lineColorSections);
                for (int s = 0; s < lineSections.Count; s++)
                {
                    var section = lineSections[s];
                    if (section.Count < 2) continue;
                    var pts = DenormPositionsList(section, minX, minY, minZ, spanX, spanY, spanZ);
                    var colSection = (lineColorSections != null && s < lineColorSections.Count)
                        ? lineColorSections[s] : null;
                    for (int seg = 0; seg < pts.Count - 1; seg++)
                    {
                        // emit (pts[seg], pts[seg+1])
                        sink.LinePositions.Add(pts[seg][0]);
                        sink.LinePositions.Add(pts[seg][1]);
                        sink.LinePositions.Add(pts[seg][2]);
                        sink.LinePositions.Add(pts[seg + 1][0]);
                        sink.LinePositions.Add(pts[seg + 1][1]);
                        sink.LinePositions.Add(pts[seg + 1][2]);
                        if (colSection != null && seg < colSection.Count)
                        {
                            var c = colSection[seg];
                            for (int k = 0; k < 3 && k < c.Length; k++) sink.LineColors.Add(c[k]);
                            for (int k = 0; k < 3 && k < c.Length; k++) sink.LineColors.Add(c[k]);
                        }
                    }
                }
            }

            // Points: P + PC (RGBA).
            FlattenAndDenormPositions(obj.Blocks, "P", minX, minY, minZ, spanX, spanY, spanZ, sink.PointPositions);
            FlattenColors          (obj.Blocks, "PC", 4, sink.PointColors);
        }

        private static List<float[]> DenormPositionsList(List<float[]> normalised,
            float minX, float minY, float minZ, float spanX, float spanY, float spanZ)
        {
            var result = new List<float[]>(normalised.Count);
            foreach (var p in normalised)
            {
                if (p.Length < 3) continue;
                result.Add(new[]
                {
                    minX + p[0] * spanX,
                    minY + p[1] * spanY,
                    minZ + p[2] * spanZ,
                });
            }
            return result;
        }

        private static void FlattenAndDenormPositions(
            Dictionary<string, List<List<float[]>>> blocks, string key,
            float minX, float minY, float minZ, float spanX, float spanY, float spanZ,
            List<float> sink)
        {
            if (!blocks.TryGetValue(key, out var sections)) return;
            foreach (var section in sections)
            {
                foreach (var p in section)
                {
                    if (p.Length < 3) continue;
                    sink.Add(minX + p[0] * spanX);
                    sink.Add(minY + p[1] * spanY);
                    sink.Add(minZ + p[2] * spanZ);
                }
            }
        }

        private static void FlattenAndDenormNormals(
            Dictionary<string, List<List<float[]>>> blocks, string key, List<float> sink)
        {
            if (!blocks.TryGetValue(key, out var sections)) return;
            foreach (var section in sections)
            {
                foreach (var n in section)
                {
                    if (n.Length < 3) continue;
                    // Normalised normals stored as [0..1]; map back to [-1..1].
                    sink.Add(n[0] * 2f - 1f);
                    sink.Add(n[1] * 2f - 1f);
                    sink.Add(n[2] * 2f - 1f);
                }
            }
        }

        private static void FlattenColors(
            Dictionary<string, List<List<float[]>>> blocks, string key, int components,
            List<float> sink)
        {
            if (!blocks.TryGetValue(key, out var sections)) return;
            foreach (var section in sections)
            {
                foreach (var c in section)
                {
                    int avail = Math.Min(c.Length, components);
                    for (int k = 0; k < avail; k++) sink.Add(Clamp01(c[k]));
                    // Pad missing channels with last value (or 0).
                    for (int k = avail; k < components; k++)
                        sink.Add(avail > 0 ? Clamp01(c[avail - 1]) : 0f);
                }
            }
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        private static bool StartsWith(byte[] haystack, byte[] needle)
        {
            if (haystack == null || haystack.Length < needle.Length) return false;
            for (int i = 0; i < needle.Length; i++)
                if (haystack[i] != needle[i]) return false;
            return true;
        }
    }
}
