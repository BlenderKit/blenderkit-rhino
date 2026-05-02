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
        public static async Task<string> StartAsync(string blendPath, int appId)
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
                    output_path  = glbPath,
                    yup          = true,
                    draco        = false,
                    export_apply = true,
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
