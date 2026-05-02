using System.Collections.Generic;
using System.Text.Json;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// One BlenderKit search tab. The panel keeps a list of these and
    /// swaps the active one's state into the visible UI controls when the
    /// user clicks a tab. Mirrors the Blender addon's multi-tab asset bar
    /// (Ctrl+T new tab, Ctrl+W close, etc.).
    /// </summary>
    public class TabState
    {
        public string Query = "";
        public string AssetType = "MODEL";
        public string CategorySlug = "";
        public int AuthorId;
        public string AuthorName = "";
        public List<JsonElement> Hits = new List<JsonElement>();
        public string NextUrl;
        public int ResultCount;
        // Title used on the tab button. Falls back to query/Tab N at render.
        public string TitleOverride;

        // Browser-style per-tab navigation history. Each entry is a
        // snapshot of the search-defining state (query / asset type /
        // category / author) at the time the user ran a search. When
        // the user clicks Back we pop into Forward and restore the
        // previous; Forward unwinds it. Filter checkboxes/dropdowns are
        // intentionally NOT captured here — they're transient and the
        // user expects them to follow the visible UI, not the tab.
        public Stack<HistoryEntry> Back = new Stack<HistoryEntry>();
        public Stack<HistoryEntry> Forward = new Stack<HistoryEntry>();
    }

    /// <summary>Snapshot of tab-defining state for the back/forward stacks.</summary>
    public class HistoryEntry
    {
        public string Query = "";
        public string AssetType = "MODEL";
        public string CategorySlug = "";
        public int AuthorId;
        public string AuthorName = "";
    }
}
