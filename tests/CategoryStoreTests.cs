using Blendkit.Rhino.Infra;
using Xunit;

namespace Blendkit.Rhino.Tests
{
    /// <summary>
    /// Pinning tests for the per-asset-type category memory. The "category
    /// vanished from the URL" bug has surfaced ~5 times in this project's
    /// history; this file exists specifically to prevent the next round.
    ///
    /// Test names spell out the exact symptom they're guarding against,
    /// so an unrelated change that would re-introduce the bug fails with
    /// a self-explanatory line.
    /// </summary>
    public class CategoryStoreTests
    {
        // ---- Single-type behavior (no asset-type swap involved) ----

        [Fact]
        public void Set_then_get_returns_the_slug_just_set()
        {
            var s = new CategoryStore();
            s.PrimeActiveType("MODEL");
            s.SetActiveSlug("furniture");
            Assert.Equal("furniture", s.ActiveSlug);
        }

        [Fact]
        public void ActiveSlug_is_empty_after_priming_unknown_type()
        {
            var s = new CategoryStore();
            s.PrimeActiveType("MODEL");
            Assert.Equal("", s.ActiveSlug);
        }

        [Fact]
        public void SetActiveSlug_with_empty_clears_active_slug()
        {
            var s = new CategoryStore();
            s.PrimeActiveType("MODEL");
            s.SetActiveSlug("furniture");
            s.SetActiveSlug("");
            Assert.Equal("", s.ActiveSlug);
        }

        // ---- Per-asset-type swap behavior ----

        [Fact]
        public void Switching_type_stashes_outgoing_and_loads_incoming()
        {
            var s = new CategoryStore();
            s.PrimeActiveType("MODEL");
            s.SetActiveSlug("furniture");
            s.SetActiveType("HDR");
            // No HDR slug yet — should be empty for HDR.
            Assert.Equal("", s.ActiveSlug);
            s.SetActiveSlug("outdoor");
            Assert.Equal("outdoor", s.ActiveSlug);
            // Flip back — MODEL slug must be restored.
            s.SetActiveType("MODEL");
            Assert.Equal("furniture", s.ActiveSlug);
        }

        [Fact]
        public void Round_trip_through_three_types_preserves_each()
        {
            // Reproduces: pick on MODEL, switch HDR, switch MATERIAL,
            // back to MODEL — MODEL slug must be intact.
            var s = new CategoryStore();
            s.PrimeActiveType("MODEL");
            s.SetActiveSlug("furniture");
            s.SetActiveType("HDR");
            s.SetActiveSlug("outdoor");
            s.SetActiveType("MATERIAL");
            s.SetActiveSlug("wood");
            // Pivot through.
            s.SetActiveType("MODEL");
            Assert.Equal("furniture", s.ActiveSlug);
            s.SetActiveType("HDR");
            Assert.Equal("outdoor", s.ActiveSlug);
            s.SetActiveType("MATERIAL");
            Assert.Equal("wood", s.ActiveSlug);
        }

        [Fact]
        public void SetActiveType_to_same_type_is_noop()
        {
            // Defends against a programmatic re-set (e.g. _assetType
            // dropdown SelectedIndex = current_index) clearing the slug.
            var s = new CategoryStore();
            s.PrimeActiveType("MODEL");
            s.SetActiveSlug("furniture");
            s.SetActiveType("MODEL"); // explicitly set to same type
            Assert.Equal("furniture", s.ActiveSlug);
            s.SetActiveType("model"); // case-insensitive
            Assert.Equal("furniture", s.ActiveSlug);
        }

        // ---- Cross-cut: GetSlugForType reads non-active types correctly ----

        [Fact]
        public void GetSlugForType_returns_active_slug_for_active_type()
        {
            // Without this, a stale dict entry could win over the hot
            // ActiveSlug field. Exact bug we hit in earlier iterations
            // when SetActiveSlug only updated the field, not the dict.
            var s = new CategoryStore();
            s.PrimeActiveType("MODEL");
            s.SetActiveSlug("furniture");
            Assert.Equal("furniture", s.GetSlugForType("MODEL"));
            Assert.Equal("furniture", s.GetSlugForType("model"));
        }

        [Fact]
        public void GetSlugForType_returns_stored_slug_for_inactive_type()
        {
            var s = new CategoryStore();
            s.PrimeActiveType("MODEL");
            s.SetActiveSlug("furniture");
            s.SetActiveType("HDR");
            s.SetActiveSlug("outdoor");
            Assert.Equal("furniture", s.GetSlugForType("MODEL"));
            Assert.Equal("outdoor",   s.GetSlugForType("HDR"));
            Assert.Equal("",          s.GetSlugForType("MATERIAL"));
        }

        // ---- JSON persistence ----

        [Fact]
        public void Serialize_then_load_reproduces_state()
        {
            var s = new CategoryStore();
            s.PrimeActiveType("MODEL");
            s.SetActiveSlug("furniture");
            s.SetActiveType("HDR");
            s.SetActiveSlug("outdoor");

            var json = s.SerializeJson();

            var s2 = new CategoryStore();
            s2.LoadJson(json);
            s2.PrimeActiveType("MODEL");
            Assert.Equal("furniture", s2.ActiveSlug);
            s2.SetActiveType("HDR");
            Assert.Equal("outdoor", s2.ActiveSlug);
        }

        [Fact]
        public void Serialize_flushes_active_slug_into_dict()
        {
            // Bug class: the active slug lived in a hot field that didn't
            // make it into the serialised JSON because Flush wasn't
            // called. Result: restart Rhino, current slug gone.
            var s = new CategoryStore();
            s.PrimeActiveType("MODEL");
            s.SetActiveSlug("furniture");
            // Don't switch type — slug should still serialise.
            var json = s.SerializeJson();
            Assert.Contains("furniture", json);
        }

        [Fact]
        public void LoadJson_garbage_input_does_not_crash_or_corrupt_state()
        {
            var s = new CategoryStore();
            s.PrimeActiveType("MODEL");
            s.SetActiveSlug("furniture");
            s.LoadJson("not actually json {");
            // State preserved.
            Assert.Equal("furniture", s.ActiveSlug);
        }

        [Fact]
        public void IngestLegacy_attributes_old_single_string_to_active_type()
        {
            var s = new CategoryStore();
            s.PrimeActiveType("MODEL");
            s.IngestLegacy("MODEL", "furniture");
            Assert.Equal("furniture", s.ActiveSlug);
            Assert.Equal("furniture", s.GetSlugForType("MODEL"));
        }

        // ---- Critical "URL has the slug" guarantees ----
        // These belong on the panel side but we exercise them from here
        // because they pin the contract the panel relies on.

        [Fact]
        public void After_SetActiveSlug_immediate_GetSlugForType_active_returns_new_value()
        {
            // Simulates SelectCategory → BuildFilters reading the slug
            // synchronously right after. If THIS test fails the URL
            // misses the category — exact regression we keep hitting.
            var s = new CategoryStore();
            s.PrimeActiveType("MODEL");
            s.SetActiveSlug("vehicles");
            Assert.Equal("vehicles", s.ActiveSlug);
            Assert.Equal("vehicles", s.GetSlugForType("MODEL"));
        }

        [Fact]
        public void After_history_restore_pattern_ActiveSlug_is_restored_value_not_dict_value()
        {
            // Reproduces the NavigateHistory restore order:
            //   1. SetActiveType (asset-type-changed handler reaction)
            //   2. SetActiveSlug (entry's slug overwrites whatever the
            //      type swap loaded out of the dict)
            // After step 2, ActiveSlug must reflect step 2's value,
            // even if the dict had a different stale entry.
            var s = new CategoryStore();
            s.PrimeActiveType("MODEL");
            s.SetActiveSlug("furniture");
            // Stash MODEL with stale_furniture, switch to HDR with stash:
            s.SetActiveType("HDR");
            s.SetActiveSlug("stale_outdoor");
            // Pretend a history entry pulls us back to MODEL with a NEW
            // slug that wasn't what's in the dict.
            s.SetActiveType("MODEL");           // step 1 — loads "furniture"
            s.SetActiveSlug("vehicles");         // step 2 — overrides with entry's slug
            Assert.Equal("vehicles", s.ActiveSlug);
            Assert.Equal("vehicles", s.GetSlugForType("MODEL"));
        }
    }
}
