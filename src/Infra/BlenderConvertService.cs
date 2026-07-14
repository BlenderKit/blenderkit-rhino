using System.IO;
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
            var glbPath = Path.ChangeExtension(blendPath, ".glb");
            var blenderExe = BlenderService.FindBlenderExe();
            var payload = new
            {
                script_id        = "export_glb",
                blender_exe_path = blenderExe ?? "",
                blend_path       = blendPath,
                output_path      = glbPath,
                status_message   = "Converting…",
                @params = new
                {
                    output_path    = glbPath,
                    yup            = true,
                    draco          = false,
                    export_apply   = true,
                    texture_max_px = textureMaxPx,
                    image_format   = imageFormat ?? "AUTO",
                    image_quality  = imageQuality,
                },
                app_id           = appId,
                addon_version    = SearchService.AddonVersion,
                platform_version = "Rhino 8",
                software         = "Rhino",
            };
            var body = await ClientLib.PostJsonAsync("/run_blender_script", payload);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("task_id", out var tid)
                ? tid.GetString() ?? ""
                : "";
        }
    }
}
