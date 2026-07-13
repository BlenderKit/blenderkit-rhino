using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// POST /blender/asset_download, returning the task_id the Go client
    /// issues. Results (progress updates, final file_paths) flow back through
    /// the /report poller.
    /// </summary>
    public static class DownloadService
    {
        public static readonly string[] RhinoImportExts = new[]
        {
            ".gltf", ".glb", ".obj", ".fbx", ".stl", ".3dm", ".dae", ".3ds", ".step", ".iges",
            // Image-based environments (HDR / EXR). Rhino's Render system
            // accepts these as background environment textures.
            ".hdr", ".exr",
        };

        public static bool IsHdrImage(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".hdr" || ext == ".exr";
        }

        /// <summary>True if the file is a .blend that Rhino can't import
        /// directly — we'll need to convert via Blender first.</summary>
        public static bool IsBlend(string path) =>
            string.Equals(Path.GetExtension(path), ".blend", StringComparison.OrdinalIgnoreCase);

        // Process-wide scene UUID. Good enough for v1. TODO: persist per RhinoDoc.
        private static readonly string SceneUuid = Guid.NewGuid().ToString();

        /// <summary>
        /// Start a download for a single search-result hit.
        /// <paramref name="hitJson"/> is the raw per-asset JSON element from
        /// the search result — we pass its `files` array straight through to
        /// the Go client so the server decides which variant to serve.
        /// </summary>
        public static async Task<string> StartAsync(JsonElement hitJson, string apiKey,
            string globalDir, string resolution = "resolution_2K")
        {
            var pid = Process.GetCurrentProcess().Id;
            var assetType = hitJson.TryGetProperty("assetType", out var at) ? at.GetString() : "model";
            var downloadDir = Path.Combine(globalDir, $"{assetType?.ToLowerInvariant()}s");
            Directory.CreateDirectory(downloadDir);

            // Preserve the raw files array and metadata by cloning the element.
            var assetData = new
            {
                name = hitJson.TryGetProperty("name", out var n) ? n.GetString() : "",
                id = hitJson.TryGetProperty("id", out var id) ? id.GetString() : "",
                assetType = assetType,
                resolution = resolution,
                available_resolutions = hitJson.TryGetProperty("availableResolutions", out var ar)
                    ? JsonSerializer.Deserialize<int[]>(ar.GetRawText()) ?? Array.Empty<int>()
                    : Array.Empty<int>(),
                files = hitJson.TryGetProperty("files", out var f)
                    ? JsonSerializer.Deserialize<object[]>(f.GetRawText()) ?? Array.Empty<object>()
                    : Array.Empty<object>(),
            };

            var payload = new
            {
                addon_version = SearchService.AddonVersion,
                platform_version = "Rhino 8",
                app_id = pid,
                download_dirs = new[] { downloadDir },
                resolution = resolution,
                asset_data = assetData,
                PREFS = new
                {
                    api_key = apiKey ?? "",
                    api_key_refresh = "",
                    api_key_timeout = 0,
                    // Blendkit API rejects downloads without a scene_uuid (it
                    // uses it for per-project stats). Generate one per Rhino
                    // session — stable across downloads in the same process,
                    // resets on Rhino restart. Later we should persist this on
                    // the active RhinoDoc via user data.
                    scene_id = SceneUuid,
                    app_id = pid,
                    unpack_files = false,
                    write_asset_metadata = false,
                    resolution = resolution,
                    project_subdir = "",
                    global_dir = globalDir,
                    binary_path = "",
                    addon_dir = "",
                    addon_module_name = "blendkit_rhino",
                },
            };

            var body = await ClientLib.PostJsonAsync("/blender/asset_download", payload);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("task_id", out var tid) ? tid.GetString() ?? "" : "";
        }

        public static bool IsRhinoImportable(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            foreach (var e in RhinoImportExts) if (e == ext) return true;
            return false;
        }
    }
}
