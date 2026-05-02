using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// POST /blend_to_glb on the local Go client. The Go side spawns Blender,
    /// streams progress as task updates, and finishes with result.file_path
    /// pointing at the .glb. Mirrors DownloadService — caller subscribes to
    /// task updates via the existing /report poller and dispatches by
    /// task_type "blend_to_glb".
    /// </summary>
    public static class BlenderConvertService
    {
        public static async Task<string> StartAsync(string blendPath, int appId)
        {
            var glbPath = Path.ChangeExtension(blendPath, ".glb");
            var payload = new
            {
                blend_path = blendPath,
                glb_path = glbPath,
                app_id = appId,
                addon_version = SearchService.AddonVersion,
                platform_version = "Rhino 8",
                software = "Rhino",
            };
            var body = await ClientLib.PostJsonAsync("/blend_to_glb", payload);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("task_id", out var tid)
                ? tid.GetString() ?? ""
                : "";
        }
    }
}
