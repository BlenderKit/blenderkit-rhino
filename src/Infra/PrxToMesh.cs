using System;
using System.IO;
using Rhino.Geometry;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// Convert <see cref="PrxFormat.PrxData"/> into a Rhino
    /// <see cref="Mesh"/> that <see cref="ProxyDisplayConduit"/> can draw
    /// in place of the original asset's full-resolution geometry.
    ///
    /// PRX mesh data is a flat triangle list (every 3 consecutive
    /// positions = one triangle) so the Mesh built here is just
    /// vertices + sequential triangle indices. Normals are copied
    /// straight across when the PRX supplied them; otherwise Rhino
    /// computes face normals when first drawn.
    ///
    /// Lines and points sections of a PRX are not consumed by this
    /// converter — the conduit only draws shaded meshes today. Wiring
    /// those in would be a follow-up that draws lines/points via
    /// <c>e.Display.DrawLines</c> / <c>DrawPoints</c> directly.
    /// </summary>
    public static class PrxToMesh
    {
        /// <summary>
        /// Build a Rhino mesh from the PRX file at <paramref name="path"/>.
        /// Returns null when the file is missing, can't be parsed, or
        /// contains no mesh triangles.
        /// </summary>
        public static Mesh TryLoad(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var data = PrxFormat.ReadFile(path);
                return BuildMesh(data);
            }
            catch (Exception ex)
            {
                BkLog.W($"PrxToMesh: failed to load '{path}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Build a Rhino mesh from already-parsed PRX data. Returns null
        /// when the data has no triangulable mesh content.
        /// </summary>
        public static Mesh BuildMesh(PrxFormat.PrxData data)
        {
            if (data == null) return null;
            int triCount = data.MeshTriangleCount;
            if (triCount <= 0) return null;

            var mesh = new Mesh();
            int vertCount = triCount * 3;

            // Vertices: copy floats straight in. PRX stores world-space
            // coordinates so no transform is needed here. Position
            // ordering inside a triangle is (v0, v1, v2) sequentially.
            for (int v = 0; v < vertCount; v++)
            {
                float x = data.MeshPositions[v * 3 + 0];
                float y = data.MeshPositions[v * 3 + 1];
                float z = data.MeshPositions[v * 3 + 2];
                mesh.Vertices.Add(x, y, z);
            }

            // Faces: i'th triangle = (3i, 3i+1, 3i+2). Rhino's MeshFace
            // ctor takes three or four int indices; using the 3-int
            // overload keeps the mesh as triangles.
            for (int t = 0; t < triCount; t++)
                mesh.Faces.AddFace(t * 3, t * 3 + 1, t * 3 + 2);

            // Optional per-vertex normals. PRX guarantees alignment with
            // MeshPositions when MeshNormals is populated (one normal
            // per triangle vertex). If counts don't match — defensive
            // skip; Rhino computes face normals on demand.
            if (data.MeshNormals.Count == data.MeshPositions.Count)
            {
                for (int v = 0; v < vertCount; v++)
                {
                    float nx = data.MeshNormals[v * 3 + 0];
                    float ny = data.MeshNormals[v * 3 + 1];
                    float nz = data.MeshNormals[v * 3 + 2];
                    mesh.Normals.Add(nx, ny, nz);
                }
            }

            // Optional per-vertex colours. PRX FC stores RGB.
            if (data.MeshColors.Count == vertCount * 3)
            {
                for (int v = 0; v < vertCount; v++)
                {
                    int r = (int)Math.Round(data.MeshColors[v * 3 + 0] * 255);
                    int g = (int)Math.Round(data.MeshColors[v * 3 + 1] * 255);
                    int b = (int)Math.Round(data.MeshColors[v * 3 + 2] * 255);
                    mesh.VertexColors.Add(System.Drawing.Color.FromArgb(
                        Clamp255(r), Clamp255(g), Clamp255(b)));
                }
            }

            mesh.Compact();
            return mesh;
        }

        private static int Clamp255(int v) => v < 0 ? 0 : (v > 255 ? 255 : v);
    }
}
