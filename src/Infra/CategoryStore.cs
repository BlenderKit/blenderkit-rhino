using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// Per-asset-type category memory + active-slug tracking, extracted
    /// from <c>BlendkitPanel</c> so the logic can be exercised in unit
    /// tests without spinning up an Eto.Forms panel.
    ///
    /// Mirrors the per-prop-block pattern from
    /// <c>blenderkit/utils.py:get_search_props()</c> in the Blender
    /// add-on: each asset type carries its own category state. Switching
    /// type stashes the outgoing slug and restores the incoming one;
    /// neither blank nor non-blank slugs leak across types.
    ///
    /// CRITICAL CONTRACT (the regression we keep hitting):
    /// <list type="bullet">
    ///   <item><c>SetActiveSlug</c> writes to the active type's entry. Reads of
    ///         <see cref="ActiveSlug"/> from then on see that value until
    ///         <c>SetActiveType</c> swaps it out.</item>
    ///   <item>No method clears <see cref="ActiveSlug"/> as a side effect.
    ///         Clearing only happens when (a) the caller writes
    ///         <c>SetActiveSlug("")</c> explicitly, or (b)
    ///         <c>SetActiveType</c> swaps to a type whose stored slug is
    ///         empty.</item>
    /// </list>
    /// </summary>
    public sealed class CategoryStore
    {
        private readonly Dictionary<string, string> _byType =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private string _activeType = "";
        // _activeSlug duplicates _byType[_activeType] but we keep it as a
        // separate field so `ActiveSlug` reads are O(1) hot-path with no
        // dict lookup, and so writes via SetActiveSlug don't churn the
        // dict on every keystroke (only flushed back on SetActiveType /
        // explicit Flush before serialize).
        private string _activeSlug = "";

        /// <summary>The slug for whichever asset type is currently active.</summary>
        public string ActiveSlug => _activeSlug;
        /// <summary>The asset type the store is currently tracking. Empty if not yet primed.</summary>
        public string ActiveType => _activeType;

        /// <summary>
        /// Set the active type for the first time. Used at construction
        /// to prime the store from settings without the side effect of
        /// stashing whatever <see cref="_activeSlug"/> happened to be at
        /// that moment (which is "" for an unconstructed instance).
        /// </summary>
        public void PrimeActiveType(string type)
        {
            _activeType = (type ?? "").ToUpperInvariant();
            _activeSlug = _byType.TryGetValue(_activeType, out var s) ? (s ?? "") : "";
        }

        /// <summary>
        /// Switch the active asset type. Stashes the outgoing slug into
        /// the per-type dict and loads whatever was stored for the
        /// incoming type (empty if no entry yet). No-op when the new
        /// type equals the current one.
        /// </summary>
        public void SetActiveType(string type)
        {
            var newKey = (type ?? "").ToUpperInvariant();
            if (string.Equals(_activeType, newKey, StringComparison.OrdinalIgnoreCase))
                return;
            // Stash outgoing slug (only if we actually had a previous type).
            if (!string.IsNullOrEmpty(_activeType))
                _byType[_activeType] = _activeSlug ?? "";
            _activeType = newKey;
            _activeSlug = _byType.TryGetValue(newKey, out var s) ? (s ?? "") : "";
        }

        /// <summary>
        /// Set the slug for the currently-active type. Updates both the
        /// hot field and the dict so a read-back is consistent regardless
        /// of which is consulted first by callers / tests.
        /// </summary>
        public void SetActiveSlug(string slug)
        {
            _activeSlug = slug ?? "";
            if (!string.IsNullOrEmpty(_activeType))
                _byType[_activeType] = _activeSlug;
        }

        /// <summary>Read the slug stored for an arbitrary type. Empty string when no entry.</summary>
        public string GetSlugForType(string type)
        {
            var key = (type ?? "").ToUpperInvariant();
            if (string.Equals(_activeType, key, StringComparison.OrdinalIgnoreCase))
                return _activeSlug ?? "";
            return _byType.TryGetValue(key, out var s) ? (s ?? "") : "";
        }

        /// <summary>Persist current state into a small JSON object.</summary>
        public string SerializeJson()
        {
            // Flush hot field into the dict so the snapshot is complete.
            if (!string.IsNullOrEmpty(_activeType))
                _byType[_activeType] = _activeSlug ?? "";
            return JsonSerializer.Serialize(_byType);
        }

        /// <summary>
        /// Replace the dict contents from a JSON snapshot. Idempotent on
        /// invalid input (caller's existing state is untouched on parse
        /// failure). Active type / slug are NOT reset — caller decides
        /// whether to PrimeActiveType after a load.
        /// </summary>
        public void LoadJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            Dictionary<string, string> parsed;
            try { parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
            catch { return; }
            if (parsed == null) return;
            _byType.Clear();
            foreach (var kv in parsed)
                _byType[kv.Key.ToUpperInvariant()] = kv.Value ?? "";
            // Refresh active slug from the loaded dict if a type is active.
            if (!string.IsNullOrEmpty(_activeType))
                _activeSlug = _byType.TryGetValue(_activeType, out var s) ? (s ?? "") : "";
        }

        /// <summary>Migrate a legacy single-string slug under the given type.</summary>
        public void IngestLegacy(string type, string slug)
        {
            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(slug)) return;
            _byType[type.ToUpperInvariant()] = slug;
            if (string.Equals(_activeType, type, StringComparison.OrdinalIgnoreCase))
                _activeSlug = slug;
        }
    }
}
