using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// Shape a search request for the Go client, send it, and let the caller
    /// watch for the matching task via the ReportPoller.
    /// </summary>
    public static class SearchService
    {
        public const string AddonVersion = "0.1.0";

        private static readonly string[] ApiPrefix = new[] { "https://www.blendkit.com/api/v1" };

        /// <summary>
        /// Bag of filter values the panel passes to the URL builder. Empty
        /// strings / zeros / false all mean "don't include this filter".
        /// </summary>
        public class Filters
        {
            public bool GltfOnly = true;
            public bool FreeOnly;
            public bool Animated;
            // Login-only: filter to assets the current user has bookmarked.
            // Mirrors Blender's `bookmarks_rating:1` URL filter.
            public bool BookmarksOnly;
            public string Order = "";   // e.g. "-created"
            public string License = ""; // e.g. "cc-zero"
            public int QualityMin;      // 0 = any, 3 = "3+ stars" etc.
            public string Category = ""; // category slug
            // Model-only filters mirroring the Blender addon's model panel.
            public string Style = "";       // REALISTIC / STYLIZED / CARTOON / SCI-FI / OTHER / ABSTRACT
            public string Condition = "";   // NEW / USED / AGED / OLD
            public int DesignYearMin;       // 0 = unset
            public int DesignYearMax;       // 0 = unset
            public int PolycountMin;        // faceCount_gte; 0 = unset
            public int PolycountMax;        // faceCount_lte; 0 = unset
            public int TextureResolutionMin; // textureResolutionMax_gte; 0 = unset
            public int TextureResolutionMax; // textureResolutionMax_lte; 0 = unset
            // When set, restrict to assets by this author. Mirrors the Blender
            // addon's `+author_id:<n>` URL filter — set when the user clicks
            // an author chip in the search results.
            public int AuthorId;

            // ---- Per-asset-type extras added 2026-04-30 to match
            // blenderkit/ui_panels.py 1:1 ----

            // MODEL only: Geometry Nodes filter — emits `+modifiers:nodes`.
            public bool GeometryNodes;
            // HDR only: True HDR (linear, 32-bit) — emits `+trueHDR:true`.
            public bool TrueHdr;
            // MATERIAL only: "ANY" (default), "PROCEDURAL", "TEXTURE_BASED".
            //   PROCEDURAL: server filters with `+files_size_lte:1MB` so
            //               huge texture-bundle materials drop out.
            //   TEXTURE_BASED: requires `+textureResolutionMax_gte:0` so
            //                  the asset has a real texture at all.
            public string Procedural = "";
            // Common: restrict results to assets uploaded by the current
            // logged-in user. Filled with the user's id; 0 = off.
            public int OwnUserId;
        }

        public static string BuildUrlQuery(string query, string assetType, int pageSize, Filters f)
        {
            var qs = $"?query={Uri.EscapeDataString(query ?? "")}";
            // If the user typed a keyword but isn't already filtering to a
            // specific author, append `,author` so the server mixes author
            // hits into the result list. Mirrors the Blender addon's
            // search.py:query_to_url behavior.
            var atValue = (assetType ?? "model").ToLowerInvariant();
            if (!string.IsNullOrEmpty(query) && f.AuthorId == 0 && atValue != "author")
                atValue += ",author";
            qs += $"+asset_type:{atValue}";
            // author_id pinning — mutually exclusive with author-mix
            // above (which we already handled). own_only takes precedence
            // when both are set, since it's the more specific intent.
            if (f.OwnUserId > 0) qs += $"+author_id:{f.OwnUserId}";
            else if (f.AuthorId > 0) qs += $"+author_id:{f.AuthorId}";
            // ----- Filters that apply to every asset type -----
            // (build_query_common in blenderkit/search.py)
            if (f.FreeOnly) qs += "+is_free:true";
            if (f.BookmarksOnly) qs += "+bookmarks_rating:1";
            if (!string.IsNullOrEmpty(f.License)) qs += "+license:" + f.License;
            // Blendkit's filter syntax uses suffix _gte/_lte rather than
            // operators (`+key_gte:value`, not `+key:>=value`).
            if (f.QualityMin > 0) qs += $"+quality_count_gte:{f.QualityMin}";
            // Category slug. Two rules from blenderkit/search.py:1341-1351
            // we previously missed:
            //   1. URL-encode the slug. Most Blendkit slugs are
            //      ASCII (kebab-case), but some carry non-ASCII chars
            //      that the server's URL parser refused, returning 0
            //      results without hint.
            //   2. If the slug equals the asset-type root (e.g.
            //      "model" / "material" / "hdr"), the addon nulls it
            //      out — sending category_subtree:<rootName> gives
            //      irrelevant results from that branch's category
            //      stub.
            if (!string.IsNullOrEmpty(f.Category))
            {
                var slug = f.Category;
                bool isRootSlug = slug == "model" || slug == "material"
                    || slug == "scene" || slug == "brush" || slug == "hdr"
                    || slug == "nodegroup" || slug == "printable";
                if (!isRootSlug)
                    qs += "+category_subtree:" + Uri.EscapeDataString(slug);
            }

            // ----- Asset-type-specific filters -----
            // Mirrors the per-type build_query_* functions in
            // blenderkit/search.py. Sending a model filter (faceCount,
            // modelStyle, …) on an HDR or material search yields zero
            // results because those fields don't exist on those types,
            // which is exactly what was wrong with the HDR auto-test.
            var typeBase = atValue.Split(',')[0]; // strip ",author" suffix
            switch (typeBase)
            {
                case "model":
                case "printable":
                    // build_query_model (search.py:1459)
                    if (f.GltfOnly) qs += "+last_gltf_godot_upload_gte:2022-01-01";
                    if (f.Animated) qs += "+animated:true";
                    if (f.GeometryNodes) qs += "+modifiers:nodes";
                    if (!string.IsNullOrEmpty(f.Style)) qs += "+modelStyle:" + f.Style;
                    if (!string.IsNullOrEmpty(f.Condition)) qs += "+condition:" + f.Condition;
                    if (f.DesignYearMin > 0) qs += $"+designYear_gte:{f.DesignYearMin}";
                    if (f.DesignYearMax > 0) qs += $"+designYear_lte:{f.DesignYearMax}";
                    if (f.PolycountMin > 0) qs += $"+faceCount_gte:{f.PolycountMin}";
                    if (f.PolycountMax > 0) qs += $"+faceCount_lte:{f.PolycountMax}";
                    if (f.TextureResolutionMin > 0) qs += $"+textureResolutionMax_gte:{f.TextureResolutionMin}";
                    if (f.TextureResolutionMax > 0) qs += $"+textureResolutionMax_lte:{f.TextureResolutionMax}";
                    break;
                case "hdr":
                    // build_query_HDR (search.py:1506) — true_hdr +
                    // texture resolution.
                    if (f.TrueHdr) qs += "+trueHDR:true";
                    if (f.TextureResolutionMin > 0) qs += $"+textureResolutionMax_gte:{f.TextureResolutionMin}";
                    if (f.TextureResolutionMax > 0) qs += $"+textureResolutionMax_lte:{f.TextureResolutionMax}";
                    break;
                case "material":
                    // build_query_material (search.py:1519) — procedural
                    // radio drives texture-or-not, plus optional texture
                    // resolution range. No style on materials in the
                    // user-facing UI, even though the field exists in
                    // the addon's prop shape.
                    if (string.Equals(f.Procedural, "PROCEDURAL", StringComparison.OrdinalIgnoreCase))
                    {
                        // Procedural materials are tiny — use the same
                        // size cap the Blender addon uses (≤ 1MB).
                        qs += "+files_size_lte:1048576";
                    }
                    else if (string.Equals(f.Procedural, "TEXTURE_BASED", StringComparison.OrdinalIgnoreCase))
                    {
                        // Force "has any texture" via the gte:0 trick.
                        qs += "+textureResolutionMax_gte:0";
                    }
                    if (f.TextureResolutionMin > 0) qs += $"+textureResolutionMax_gte:{f.TextureResolutionMin}";
                    if (f.TextureResolutionMax > 0) qs += $"+textureResolutionMax_lte:{f.TextureResolutionMax}";
                    break;
                default:
                    // brush, scene, nodegroup, etc.: no extra filters.
                    break;
            }

            // Only ever show validated assets in Rhino — pinned for EVERY
            // account, including validators. The Blender add-on lets
            // validators browse uploaded/on-hold assets because they review
            // them there; Rhino has no validation UI, so unvalidated content
            // would just be confusing (and possibly broken for conversion).
            qs += "+verification_status:validated";

            // Mirror blenderkit/search.py:decide_ordering. Behavior the user
            // expects from Blendkit:
            //   no query + no category   → recency. Two keys: blend-based
            //       assets sort by -last_blend_upload; zip-only assets (VDB
            //       volumes etc.) have that null and fall through to
            //       -last_zip_file_upload — same pair the add-on sends.
            //   has category, no query   → -score,_score (BK score, then relevance)
            //   anything else            → _score (pure ES relevance / "best match")
            //   user-picked              → respect verbatim
            string order;
            if (!string.IsNullOrEmpty(f.Order))
                order = f.Order;
            else if (string.IsNullOrEmpty(query) && string.IsNullOrEmpty(f.Category))
                order = "-last_blend_upload,-last_zip_file_upload";
            else if (!string.IsNullOrEmpty(f.Category))
                order = "-score,_score";
            else
                order = "_score";
            qs += "+order:" + order;

            qs += "&dict_parameters=1";
            qs += $"&page_size={pageSize}";
            qs += $"&addon_version={AddonVersion}";
            return ApiPrefix[0] + "/search/" + qs;
        }

        /// <summary>
        /// POST to /blender/asset_search. Returns the task_id the client issued.
        /// Actual results arrive via /report with task_type == "search".
        /// </summary>
        public static async Task<string> StartAsync(string query, string assetType,
            string apiKey, string globalDir, Filters filters, int pageSize = 15,
            string nextUrl = null)
        {
            var pid = Process.GetCurrentProcess().Id;
            var tempDir = Path.Combine(globalDir, "temp");
            Directory.CreateDirectory(tempDir);

            var payload = new
            {
                PREFS = new
                {
                    api_key = apiKey ?? "",
                    api_key_refresh = "",
                    api_key_timeout = 0,
                    scene_id = "",
                    app_id = pid,
                    unpack_files = false,
                    write_asset_metadata = false,
                    resolution = "resolution_2K",
                    project_subdir = "",
                    global_dir = globalDir,
                    binary_path = "",
                    addon_dir = "",
                    addon_module_name = "blendkit_rhino",
                },
                addon_version = AddonVersion,
                platform_version = "Rhino 8",
                api_key = apiKey ?? "",
                app_id = pid,
                asset_type = assetType,
                // Upstream bug: Go client crashes in parseThumbnails if this is
                // empty — StringToBlenderVersion returns nil and the caller
                // (main.go:734) dereferences unchecked. Pass a real-looking
                // Blender version so webp thumbnails are used and parsing
                // doesn't crash. Remove once the Go client is fixed.
                blender_version = "4.2.0",
                get_next = false,
                next = "",
                page_size = pageSize,
                scene_uuid = "",
                tempdir = tempDir,
                urlquery = !string.IsNullOrEmpty(nextUrl)
                    ? nextUrl
                    : BuildUrlQuery(query, assetType, pageSize, filters),
                is_validator = false,
                history_id = "",
            };

            var body = await ClientLib.PostJsonAsync("/blender/asset_search", payload);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("task_id", out var id) ? id.GetString() ?? "" : "";
        }
    }
}
