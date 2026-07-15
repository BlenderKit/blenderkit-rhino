using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// POST /run_blender_script with the bundled `export_glb` recipe.
    /// The Go client spawns the Blender we point it at, streams progress
    /// as task updates, and finishes with result.file_path pointing at
    /// the .glb. The calling site stores the task_id in
    /// <see cref="BlendkitPanel"/>'s _pendingConvertActions so the panel
    /// can route "run_blender_script" finishes to <c>HandleConvertTask</c>.
    /// </summary>
    public static class BlenderConvertService
    {
        /// <param name="textureMaxPx">
        /// Downscale every embedded texture whose longest side exceeds this
        /// to fit (0 = no cap). Mirrors the download-resolution setting so
        /// assets that ship an un-downscaled 8K original still produce a
        /// bounded GLB. This is the single biggest lever on GLB size / Rhino
        /// import time — see the recipe header in tools/export_glb.py.
        /// </param>
        /// <param name="imageFormat">
        /// "AUTO" / "JPEG" / "PNG" / "WEBP". Default AUTO (PNG/JPEG per source).
        /// DO NOT use WEBP for the Rhino host: Rhino's `_-Import` command (the
        /// only glTF import path we have — RhinoDoc.Import returns false for
        /// glTF) FAILS on WEBP-textured GLBs and can crash Rhino. The bridge
        /// importer decodes WEBP, but the plug-in uses `_-Import`. AUTO keeps
        /// files small enough (the texture downscale is the real size lever)
        /// while staying import-safe.
        /// </param>
        public static async Task<string> StartAsync(string blendPath, int appId,
            int textureMaxPx = 0, string imageFormat = "AUTO", int imageQuality = 90)
        {
            // Output path carries the texture cap so the client's convert cache
            // (which keys ONLY on output path, ignoring params) re-converts when
            // the resolution changes instead of serving a stale-res .glb.
            var dir = Path.GetDirectoryName(blendPath) ?? "";
            var stem = Path.GetFileNameWithoutExtension(blendPath);
            var glbPath = textureMaxPx > 0
                ? Path.Combine(dir, $"{stem}_t{textureMaxPx}.glb")
                : Path.ChangeExtension(blendPath, ".glb");
            var blenderExe = BlenderService.FindBlenderExe();

            var payload = new Dictionary<string, object>
            {
                ["blender_exe_path"] = blenderExe ?? "",
                ["blend_path"]       = blendPath,
                ["output_path"]      = glbPath,
                ["status_message"]   = "Converting…",
                ["params"] = new
                {
                    output_path    = glbPath,
                    yup            = true,
                    draco          = false,
                    export_apply   = true,
                    texture_max_px = textureMaxPx,
                    image_format   = imageFormat ?? "AUTO",
                    image_quality  = imageQuality,
                },
                ["app_id"]           = appId,
                ["addon_version"]    = SearchService.AddonVersion,
                ["platform_version"] = "Rhino 8",
                ["software"]         = "Rhino",
            };

            // Prefer script_path (our recipe on disk) over script_id (the
            // client's EMBEDDED recipe). The client is SHARED with the Blender
            // add-on on port 62485; if Blender spawned it, its embedded recipe
            // is the Blender-published version with NO Rhino texture downscale.
            // Sending an absolute path makes whatever client is running execute
            // OUR recipe, so downscaling works regardless of client version.
            // Fall back to script_id if the deployed recipe isn't found.
            var recipePath = ResolveRecipePath();
            if (recipePath != null) payload["script_path"] = recipePath;
            else payload["script_id"] = "export_glb";

            var body = await ClientLib.PostJsonAsync("/run_blender_script", payload);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("task_id", out var tid)
                ? tid.GetString() ?? ""
                : "";
        }

        /// <summary>
        /// Absolute path to the export_glb recipe shipped next to the .rhp
        /// (deploy copies it to &lt;plugin&gt;/client/tools/). Returns null if
        /// not found, so the caller falls back to the client's embedded recipe.
        /// </summary>
        private static string ResolveRecipePath()
        {
            try
            {
                var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(asmDir)) return null;
                var p = Path.Combine(asmDir, "client", "tools", "export_glb.py");
                return File.Exists(p) ? p : null;
            }
            catch { return null; }
        }
    }
}
