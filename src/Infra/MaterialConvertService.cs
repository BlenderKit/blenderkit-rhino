using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// POST /run_blender_script on the local Go client to run our
    /// Rhino-owned extraction script
    /// (blendkit_rhino_material_extract.py) on a downloaded material
    /// .blend. The script unpacks textures using the Blender addon's
    /// canonical pattern and writes a flat JSON manifest with each
    /// Principled BSDF channel's value or texture path; the Rhino-side
    /// <see cref="MaterialJsonImporter"/> rebuilds the equivalent Rhino
    /// PBR Material with bitmap-texture children.
    ///
    /// The Go client used to carry the extraction script as an embedded
    /// Python string (blender_material.go); we moved it Rhino-side so
    /// renderer-specific logic lives next to its consumer. The client
    /// is now host-agnostic again.
    /// </summary>
    public static class MaterialConvertService
    {
        public static async Task<string> StartAsync(string blendPath, int appId)
        {
            var jsonPath = blendPath + ".material.json";

            var scriptPath = ResolveScriptPath("blendkit_rhino_material_extract.py");
            if (string.IsNullOrEmpty(scriptPath))
                throw new FileNotFoundException(
                    "blendkit_rhino_material_extract.py missing — re-deploy the plugin.");

            // Host-specific recipe — stays in the plug-in (not bundled with
            // the client) because it builds a Rhino-PBR-shaped JSON. We pass
            // its absolute path via script_path and tell the client which
            // Blender to spawn via blender_exe_path.
            var blenderExe = BlenderService.FindBlenderExe();
            var payload = new
            {
                script_path      = scriptPath,
                blender_exe_path = blenderExe ?? "",
                blend_path       = blendPath,
                output_path      = jsonPath,
                status_message   = "Extracting material…",
                @params = new
                {
                    output_path = jsonPath,
                },
                app_id           = appId,
                addon_version    = SearchService.AddonVersion,
                platform_version = "Rhino 8",
                software         = "Rhino",
            };
            var body = await ClientLib.PostJsonAsync("/run_blender_script", payload);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("task_id", out var tid)
                ? tid.GetString() ?? "" : "";
        }

        /// <summary>
        /// Resolve the absolute path of one of our deployed _bg.py
        /// scripts. The .rhp deploys the python files to
        /// <c>&lt;plugin&gt;/python/</c>; first preference is the
        /// directory next to the loaded assembly.
        /// </summary>
        public static string ResolveScriptPath(string fileName)
        {
            try
            {
                var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(asmDir))
                {
                    var candidates = new[]
                    {
                        Path.Combine(asmDir, "python", fileName),
                        Path.Combine(asmDir, fileName),
                        Path.Combine(asmDir, "..", "python", fileName),
                        Path.Combine(asmDir, "..", "..", "python", fileName),
                    };
                    foreach (var c in candidates)
                    {
                        var full = Path.GetFullPath(c);
                        if (File.Exists(full)) return full;
                    }
                }
            }
            catch (Exception ex)
            {
                BkLog.W("ResolveScriptPath: " + ex.Message);
            }
            return null;
        }
    }
}
