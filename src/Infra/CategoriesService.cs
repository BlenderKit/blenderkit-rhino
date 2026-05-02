using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// Holds the BlenderKit category tree fetched by the Go client.
    ///
    /// The tree lives in process-static state because the API is the same for
    /// every panel instance and refetching is wasteful.
    ///
    /// Top-level entries are asset types ("model", "material", "hdr", …);
    /// their `children` are the actual visible categories. We flatten each
    /// asset-type subtree into a list of (display label, slug) pairs the UI
    /// can drop into a DropDown without needing a tree control.
    /// </summary>
    public class CategoryNode
    {
        public string Name = "";
        public string Slug = "";
        public List<CategoryNode> Children = new List<CategoryNode>();
    }

    public static class CategoriesService
    {
        // Top-level asset-type slug → flat list of (label, slug). Used by the
        // legacy DropDown UI; kept for backwards compat.
        private static readonly Dictionary<string, List<(string Label, string Slug)>> _byType
            = new Dictionary<string, List<(string, string)>>();
        // Same data shaped as a real tree, for the cascading category picker.
        private static readonly Dictionary<string, List<CategoryNode>> _treeByType
            = new Dictionary<string, List<CategoryNode>>();
        public static event EventHandler Updated;

        /// <summary>Replace the in-memory tree with the result of a `categories_update` task.</summary>
        /// <param name="result">
        /// Either the raw top-level array
        /// (<c>[{name, slug, children, ...}, ...]</c> — what the Go client
        /// currently sends as task.result), OR an object wrapping the array
        /// under one of the historical keys (<c>results</c>, <c>categories</c>,
        /// <c>data</c>). Handling both makes the ingest survive API/payload
        /// reshuffles that have broken category loading three or four times.
        /// </param>
        public static void Ingest(JsonElement result)
        {
            // Unwrap common envelope shapes so a future Go client update
            // that decides to wrap the array can't silently break the panel.
            if (result.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "results", "categories", "data" })
                {
                    if (result.TryGetProperty(key, out var inner)
                        && inner.ValueKind == JsonValueKind.Array)
                    {
                        result = inner;
                        break;
                    }
                }
            }
            if (result.ValueKind != JsonValueKind.Array) return;
            var freshFlat = new Dictionary<string, List<(string, string)>>();
            var freshTree = new Dictionary<string, List<CategoryNode>>();
            foreach (var top in result.EnumerateArray())
            {
                var slug = top.TryGetProperty("slug", out var s) ? (s.GetString() ?? "") : "";
                if (string.IsNullOrEmpty(slug)) continue;
                var flat = new List<(string, string)> { ("(any)", "") };
                var tree = new List<CategoryNode>();
                if (top.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in ch.EnumerateArray())
                    {
                        Walk(c, prefix: "", flat);
                        var node = WalkTree(c);
                        if (node != null) tree.Add(node);
                    }
                }
                var key = slug.ToUpperInvariant();
                freshFlat[key] = flat;
                freshTree[key] = tree;
            }
            lock (_byType)
            {
                _byType.Clear();
                foreach (var kv in freshFlat) _byType[kv.Key] = kv.Value;
                _treeByType.Clear();
                foreach (var kv in freshTree) _treeByType[kv.Key] = kv.Value;
            }
            Updated?.Invoke(null, EventArgs.Empty);
        }

        private static CategoryNode WalkTree(JsonElement node)
        {
            var name = node.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
            var slug = node.TryGetProperty("slug", out var s) ? (s.GetString() ?? "") : "";
            if (string.IsNullOrEmpty(slug)) return null;
            var cat = new CategoryNode { Name = name, Slug = slug };
            if (node.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in ch.EnumerateArray())
                {
                    var child = WalkTree(c);
                    if (child != null) cat.Children.Add(child);
                }
            }
            return cat;
        }

        public static IReadOnlyList<CategoryNode> TreeForAssetType(string assetType)
        {
            lock (_byType)
            {
                if (_treeByType.TryGetValue(assetType.ToUpperInvariant(), out var t)) return t;
            }
            return new List<CategoryNode>();
        }

        private static void Walk(JsonElement node, string prefix, List<(string, string)> list)
        {
            var name = node.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
            var slug = node.TryGetProperty("slug", out var s) ? (s.GetString() ?? "") : "";
            if (string.IsNullOrEmpty(slug)) return;
            var label = string.IsNullOrEmpty(prefix) ? name : prefix + " / " + name;
            list.Add((label, slug));
            if (node.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in ch.EnumerateArray()) Walk(c, label, list);
            }
        }

        public static IReadOnlyList<(string Label, string Slug)> ForAssetType(string assetType)
        {
            lock (_byType)
            {
                if (_byType.TryGetValue(assetType.ToUpperInvariant(), out var list)) return list;
            }
            return new List<(string, string)> { ("(any)", "") };
        }
    }
}
