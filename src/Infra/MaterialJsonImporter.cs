using System;
using System.IO;
using System.Text.Json;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Render;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// Read the JSON sidecar produced by our Blender extraction script
    /// (rhino/python/blendkit_rhino_material_to_cycles_xml.py — which
    /// also writes JSON next to the XML for this loader) and rebuild the
    /// asset as a Rhino PBR <c>Material</c> with bitmap-texture children.
    ///
    /// We previously routed through RhinoCyclesCore.Materials.XmlMaterial
    /// (typeId 8B544B3E-...) so the Cycles XML graph could carry the
    /// principled BSDF directly. That path looked clean but Rhino-Cycles'
    /// XML parser has the image-texture filename branch wrapped in
    /// <c>#if DISABLEFORNOW</c> (see CCSycles/ShaderNodes/TextureNode.cs):
    /// the `src=` attribute is silently ignored. The XmlMaterial is only
    /// usable for procedural-only graphs. The standard Rhino PBR
    /// material, on the other hand, accepts bitmap textures via the
    /// public RenderContent API and renders correctly in both Rendered
    /// and Raytraced modes — so that's our target.
    ///
    /// Expected JSON shape (one Principled BSDF flattened):
    ///   {
    ///     "name": "...",
    ///     "base_color_texture": "C:/.../tex.jpg" | null,
    ///     "base_color_rgba": [r,g,b,a]          | null,
    ///     "metallic_texture": ...,  "metallic": ...,
    ///     "roughness_texture": ..., "roughness": ...,
    ///     "normal_texture": ...,
    ///     "emission_texture": ..., "emission_rgba": ...,
    ///     "emission_strength": ...,
    ///     "alpha_texture": ...,    "alpha": ...
    ///   }
    /// </summary>
    public static class MaterialJsonImporter
    {
        /// <summary>
        /// Auto-detect entry point. The Blender extraction script writes
        /// both <c>&lt;blend&gt;.cycles.xml</c> and <c>&lt;xml&gt;.json</c>.
        /// The XML is dead code for textures so we ignore it; the JSON
        /// is the source of truth. If the caller hands us the XML path,
        /// we look for the sidecar JSON next to it.
        /// </summary>
        public static int ImportFromOutput(string outputPath)
        {
            if (string.IsNullOrEmpty(outputPath)) return -1;
            try
            {
                if (outputPath.EndsWith(".cycles.xml", StringComparison.OrdinalIgnoreCase)
                    || outputPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    var sidecar = outputPath + ".json";
                    if (File.Exists(sidecar)) return ImportFromJson(sidecar);
                    var alt = outputPath.Replace(".cycles.xml", ".material.json", StringComparison.OrdinalIgnoreCase);
                    if (File.Exists(alt)) return ImportFromJson(alt);
                    BkLog.W($"ImportFromOutput: XML present but no sidecar JSON near '{outputPath}'");
                    return -1;
                }
            }
            catch (Exception ex) { BkLog.W("ImportFromOutput XML branch: " + ex.Message); }
            return ImportFromJson(outputPath);
        }

        /// <summary>
        /// Build a PBR <see cref="Material"/> from the Blender-side JSON
        /// at <paramref name="jsonPath"/>, register it as a
        /// <see cref="RenderMaterial"/> in the active doc, and assign
        /// to any selected objects. Returns the doc-Materials index, or
        /// -1 on failure.
        /// </summary>
        public static int ImportFromJson(string jsonPath)
        {
            // Wrap the entire import in a single Rhino undo record so
            // Ctrl+Z rolls the material out of the doc in one step
            // (otherwise the user has to undo the Material.Add and
            // each per-object MaterialIndex change separately).
            int result = -1;
            var doc0 = RhinoDoc.ActiveDoc;
            uint serial = 0;
            try { if (doc0 != null) serial = doc0.BeginUndoRecord("Blendkit: Add material"); } catch { }
            try
            {
                if (!File.Exists(jsonPath)) return -1;
                using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                var root = doc.RootElement;

                var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? (n.GetString() ?? "Blendkit material")
                    : "Blendkit material";

                // Per-channel reads. Missing texture path → null; unbinds
                // (we'll let the constant scalar/colour drive the slot).
                var baseTex   = TryReadString(root, "base_color_texture");
                var bc        = TryReadRgba(root, "base_color_rgba", 0.8f, 0.8f, 0.8f, 1f);
                var metTex    = TryReadString(root, "metallic_texture");
                double met    = TryReadDouble(root, "metallic", out var mv) ? mv : 0.0;
                var roughTex  = TryReadString(root, "roughness_texture");
                double rough  = TryReadDouble(root, "roughness", out var rv) ? rv : 0.5;
                var normalTex = TryReadString(root, "normal_texture");
                var emTex     = TryReadString(root, "emission_texture");
                var em        = TryReadRgba(root, "emission_rgba", 0f, 0f, 0f, 1f);
                // emission_strength: Blender 4.x default for non-emissive
                // materials is color=(1,1,1) strength=0. Without strength
                // every default-color material would render fullbright.
                double emStrength = TryReadDouble(root, "emission_strength", out var esv) ? esv : 0.0;
                var alphaTex  = TryReadString(root, "alpha_texture");
                double alpha  = TryReadDouble(root, "alpha", out var av) ? av : 1.0;

                // Drop file refs that don't actually exist on disk —
                // Blender's bpy.path.abspath returns a path even for
                // packed images whose source isn't unpacked. Our Python
                // script uses image.unpack(WRITE_ORIGINAL) before walking
                // the graph so this should rarely trigger now, but keep
                // the safety net.
                if (!string.IsNullOrEmpty(baseTex)   && !File.Exists(baseTex))   baseTex   = null;
                if (!string.IsNullOrEmpty(metTex)    && !File.Exists(metTex))    metTex    = null;
                if (!string.IsNullOrEmpty(roughTex)  && !File.Exists(roughTex))  roughTex  = null;
                if (!string.IsNullOrEmpty(normalTex) && !File.Exists(normalTex)) normalTex = null;
                if (!string.IsNullOrEmpty(emTex)     && !File.Exists(emTex))     emTex     = null;
                if (!string.IsNullOrEmpty(alphaTex)  && !File.Exists(alphaTex))  alphaTex  = null;

                var rhDoc = RhinoDoc.ActiveDoc;

                // Build a legacy Material first; ToPhysicallyBased flips
                // it into PBR mode and exposes the per-channel slots.
                // Cycles renders this through its own ShaderConverter
                // (see RhinoCycles/Converters/ShaderConverter.cs:1022)
                // which wires the bitmap children correctly — including
                // the file path the Cycles XmlMaterial route silently
                // dropped.
                var mat = new Material { Name = name };
                mat.ToPhysicallyBased();
                var pbr = mat.PhysicallyBased;
                if (pbr == null) return -1;

                // Constant fallbacks. Per-pixel maps (set below via
                // SetTexture) override these where bound.
                pbr.BaseColor = new Color4f(bc.r, bc.g, bc.b, 1f);
                pbr.Metallic  = met;
                pbr.Roughness = rough;
                pbr.Alpha     = alpha;
                // Emission: tint by strength so non-emissive materials
                // (the default Blender state of 1,1,1 × 0) stay dark.
                pbr.Emission  = new Color4f(
                    (float)(em.r * emStrength),
                    (float)(em.g * emStrength),
                    (float)(em.b * emStrength), 1f);

                void Wire(string path, TextureType type)
                {
                    if (string.IsNullOrEmpty(path)) return;
                    try
                    {
                        var tex = new Texture { FileName = path, Enabled = true };
                        mat.SetTexture(tex, type);
                    }
                    catch (Exception ex) { BkLog.W($"SetTexture({type}) failed: {ex.Message}"); }
                }
                Wire(baseTex,   TextureType.PBR_BaseColor);
                Wire(metTex,    TextureType.PBR_Metallic);
                Wire(roughTex,  TextureType.PBR_Roughness);
                Wire(normalTex, TextureType.Bump);
                Wire(emTex,     TextureType.PBR_Emission);
                Wire(alphaTex,  TextureType.Transparency);

                // Legacy diffuse bitmap so older Rhino display modes
                // (Wireframe + classic Shaded) show the colour map too.
                if (!string.IsNullOrEmpty(baseTex))
                {
                    try { mat.SetBitmapTexture(baseTex); } catch { }
                }

                int idx = rhDoc.Materials.Add(mat);

                // Materials only show up in the Materials panel once
                // they're wrapped as RenderContent. Adding to
                // RhinoDoc.Materials alone keeps them as legacy slots
                // that the panel hides.
                RenderMaterial rm = null;
                try
                {
                    var rendered = idx >= 0 && idx < rhDoc.Materials.Count ? rhDoc.Materials[idx] : mat;
                    rm = RenderMaterial.CreateBasicMaterial(rendered, rhDoc);
                    if (rm != null) rhDoc.RenderMaterials.Add(rm);
                }
                catch (Exception ex) { BkLog.W("RenderMaterials.Add failed: " + ex.Message); }

                // Auto-assign to whatever's selected so the user sees
                // the new material on something immediately. Skip when
                // nothing is selected — it's still in the Materials
                // panel for manual application.
                int assigned = 0;
                try
                {
                    foreach (var sel in rhDoc.Objects.GetSelectedObjects(false, false))
                    {
                        var sa = sel.Attributes.Duplicate();
                        sa.MaterialSource = ObjectMaterialSource.MaterialFromObject;
                        if (rm != null)
                        {
                            sel.RenderMaterial = rm;
                        }
                        else
                        {
                            sa.MaterialIndex = idx;
                        }
                        rhDoc.Objects.ModifyAttributes(sel, sa, true);
                        assigned++;
                    }
                    if (assigned > 0) rhDoc.Views.Redraw();
                }
                catch (Exception ex) { BkLog.W("Auto-assign material failed: " + ex.Message); }

                BkLog.W($"MaterialJsonImporter (PBR): added '{name}' (index {idx}), assigned to {assigned} selected"
                    + (string.IsNullOrEmpty(baseTex) ? "" : $", base bitmap='{Path.GetFileName(baseTex)}'"));
                result = idx;
                return idx;
            }
            catch (Exception ex)
            {
                BkLog.W("MaterialJsonImporter error: " + ex.Message);
                return -1;
            }
            finally
            {
                if (serial != 0)
                {
                    try { doc0.EndUndoRecord(serial); } catch { }
                }
            }
        }

        // ----- JSON readers (defensive: the python script may emit nulls) -----
        private static string TryReadString(JsonElement root, string key)
        {
            if (!root.TryGetProperty(key, out var v)) return null;
            if (v.ValueKind != JsonValueKind.String) return null;
            return v.GetString();
        }

        private static bool TryReadDouble(JsonElement root, string key, out double value)
        {
            value = 0;
            if (!root.TryGetProperty(key, out var v)) return false;
            if (v.ValueKind != JsonValueKind.Number) return false;
            value = v.GetDouble();
            return true;
        }

        private static (float r, float g, float b, float a) TryReadRgba(
            JsonElement root, string key, float dr, float dg, float db, float da)
        {
            if (!root.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return (dr, dg, db, da);
            float r = dr, g = dg, b = db, a = da;
            int i = 0;
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Number) return (dr, dg, db, da);
                var f = (float)e.GetDouble();
                if (i == 0) r = f; else if (i == 1) g = f; else if (i == 2) b = f; else if (i == 3) a = f;
                i++;
            }
            if (i < 3) return (dr, dg, db, da);
            return (r, g, b, a);
        }
    }
}
