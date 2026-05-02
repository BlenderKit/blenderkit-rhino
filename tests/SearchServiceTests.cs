using Blendkit.Rhino.Infra;
using Xunit;

namespace Blendkit.Rhino.Tests
{
    /// <summary>
    /// URL builder regression tests. The search URL has broken silently
    /// twice now (wrong operator on quality_count, missing keyword/category
    /// when nextUrl was reused). These nail down the expected wire format.
    /// </summary>
    public class SearchServiceTests
    {
        private static SearchService.Filters Empty() => new SearchService.Filters
        {
            // Make sure UI defaults don't leak into tests — explicit values.
            GltfOnly = false, FreeOnly = false, Animated = false,
            Order = "", License = "", QualityMin = 0, Category = "",
        };

        [Fact]
        public void Empty_query_no_category_uses_recency_default()
        {
            var url = SearchService.BuildUrlQuery("", "MODEL", 15, Empty());
            // No keyword + no category → smart default: -last_blend_upload.
            Assert.Contains("+order:-last_blend_upload", url);
            Assert.Contains("?query=", url);
            Assert.Contains("+asset_type:model", url);
        }

        [Fact]
        public void Keyword_query_uses_score_relevance()
        {
            var url = SearchService.BuildUrlQuery("chair", "MODEL", 15, Empty());
            Assert.Contains("?query=chair", url);
            Assert.Contains("+order:_score", url);
            Assert.DoesNotContain("-last_blend_upload", url);
        }

        [Fact]
        public void Category_no_query_uses_score_then_relevance()
        {
            var f = Empty(); f.Category = "furniture";
            var url = SearchService.BuildUrlQuery("", "MODEL", 15, f);
            Assert.Contains("+category_subtree:furniture", url);
            Assert.Contains("+order:-score,_score", url);
        }

        [Fact]
        public void Explicit_order_overrides_smart_default()
        {
            var f = Empty(); f.Order = "-download_count";
            var url = SearchService.BuildUrlQuery("chair", "MODEL", 15, f);
            Assert.Contains("+order:-download_count", url);
            Assert.DoesNotContain("+order:_score", url);
        }

        [Fact]
        public void Quality_filter_uses_gte_suffix_not_operator()
        {
            // Regression: we briefly used `+quality_count:>=4` which the API
            // either ignored or rejected. Correct form is the _gte suffix.
            var f = Empty(); f.QualityMin = 4;
            var url = SearchService.BuildUrlQuery("", "MODEL", 15, f);
            Assert.Contains("+quality_count_gte:4", url);
            Assert.DoesNotContain("quality_count:>=", url);
        }

        [Fact]
        public void Quality_zero_omits_filter()
        {
            var f = Empty(); f.QualityMin = 0;
            var url = SearchService.BuildUrlQuery("chair", "MODEL", 15, f);
            Assert.DoesNotContain("quality_count", url);
        }

        [Fact]
        public void Free_only_emits_is_free_true()
        {
            var f = Empty(); f.FreeOnly = true;
            var url = SearchService.BuildUrlQuery("", "MODEL", 15, f);
            Assert.Contains("+is_free:true", url);
        }

        [Fact]
        public void Animated_only_emits_animated_true()
        {
            var f = Empty(); f.Animated = true;
            var url = SearchService.BuildUrlQuery("", "MODEL", 15, f);
            Assert.Contains("+animated:true", url);
        }

        [Fact]
        public void License_filter_emits_license_value()
        {
            var f = Empty(); f.License = "cc-zero";
            var url = SearchService.BuildUrlQuery("", "MODEL", 15, f);
            Assert.Contains("+license:cc-zero", url);
        }

        [Fact]
        public void GltfOnly_filter_uses_godot_upload_date_gte()
        {
            var f = Empty(); f.GltfOnly = true;
            var url = SearchService.BuildUrlQuery("", "MODEL", 15, f);
            Assert.Contains("+last_gltf_godot_upload_gte:", url);
        }

        [Fact]
        public void Asset_type_lowercases_for_url()
        {
            var url = SearchService.BuildUrlQuery("", "MATERIAL", 15, Empty());
            Assert.Contains("+asset_type:material", url);
            Assert.DoesNotContain("MATERIAL", url);
        }

        [Fact]
        public void Page_size_and_addon_version_always_present()
        {
            var url = SearchService.BuildUrlQuery("", "MODEL", 30, Empty());
            Assert.Contains("&page_size=30", url);
            Assert.Contains("&addon_version=" + SearchService.AddonVersion, url);
            Assert.Contains("&dict_parameters=1", url);
        }

        [Fact]
        public void Query_is_url_encoded()
        {
            var url = SearchService.BuildUrlQuery("oak chair & sofa", "MODEL", 15, Empty());
            // Spaces must round-trip; & in keyword must not break param parsing.
            Assert.Contains("?query=oak%20chair%20%26%20sofa", url);
        }

        [Fact]
        public void BookmarksOnly_emits_bookmarks_rating()
        {
            var f = Empty(); f.BookmarksOnly = true;
            var url = SearchService.BuildUrlQuery("", "MODEL", 15, f);
            Assert.Contains("+bookmarks_rating:1", url);
        }

        [Fact]
        public void Style_filter_uses_modelStyle_field()
        {
            // Server ES field is `modelStyle`, not `style`.
            var f = Empty(); f.Style = "REALISTIC";
            var url = SearchService.BuildUrlQuery("", "MODEL", 15, f);
            Assert.Contains("+modelStyle:REALISTIC", url);
        }

        [Fact]
        public void Condition_filter_emits_condition()
        {
            var f = Empty(); f.Condition = "NEW";
            var url = SearchService.BuildUrlQuery("", "MODEL", 15, f);
            Assert.Contains("+condition:NEW", url);
        }

        [Fact]
        public void Design_year_range_uses_gte_lte_pair()
        {
            var f = Empty(); f.DesignYearMin = 2018; f.DesignYearMax = 2024;
            var url = SearchService.BuildUrlQuery("", "MODEL", 15, f);
            Assert.Contains("+designYear_gte:2018", url);
            Assert.Contains("+designYear_lte:2024", url);
        }

        [Fact]
        public void Polycount_range_uses_faceCount_field()
        {
            // Server field name is faceCount, not polycount.
            var f = Empty(); f.PolycountMin = 10000; f.PolycountMax = 100000;
            var url = SearchService.BuildUrlQuery("", "MODEL", 15, f);
            Assert.Contains("+faceCount_gte:10000", url);
            Assert.Contains("+faceCount_lte:100000", url);
        }

        [Fact]
        public void Texture_resolution_range_uses_textureResolutionMax_field()
        {
            var f = Empty(); f.TextureResolutionMin = 1024; f.TextureResolutionMax = 4096;
            var url = SearchService.BuildUrlQuery("", "MODEL", 15, f);
            Assert.Contains("+textureResolutionMax_gte:1024", url);
            Assert.Contains("+textureResolutionMax_lte:4096", url);
        }

        [Fact]
        public void Keyword_query_mixes_author_hits_into_results()
        {
            // When the user types a keyword and isn't already filtering by
            // author, the server should return author hits alongside model
            // hits — same trick the Blender addon uses.
            var url = SearchService.BuildUrlQuery("chair", "MODEL", 15, Empty());
            Assert.Contains("+asset_type:model,author", url);
        }

        [Fact]
        public void Empty_query_does_not_request_authors()
        {
            var url = SearchService.BuildUrlQuery("", "MODEL", 15, Empty());
            // Empty queries (default landing) shouldn't drag authors in.
            Assert.Contains("+asset_type:model", url);
            Assert.DoesNotContain("model,author", url);
        }

        [Fact]
        public void Author_id_filter_emits_author_id_and_skips_author_mix()
        {
            var f = Empty(); f.AuthorId = 4321;
            var url = SearchService.BuildUrlQuery("chair", "MODEL", 15, f);
            Assert.Contains("+author_id:4321", url);
            // Already filtering by author → don't also mix author hits in.
            Assert.DoesNotContain("model,author", url);
        }

        [Fact]
        public void Range_filter_zero_omits_bound()
        {
            var f = Empty(); f.PolycountMin = 5000; f.PolycountMax = 0;
            var url = SearchService.BuildUrlQuery("", "MODEL", 15, f);
            Assert.Contains("+faceCount_gte:5000", url);
            Assert.DoesNotContain("faceCount_lte", url);
        }

        // -------- Asset-type-specific filter gating --------
        // Mirrors blenderkit/search.py:build_query_HDR / build_query_material
        // — sending model-only fields (faceCount, modelStyle, designYear,
        // animated) on a non-model search returns 0 hits because those
        // fields don't exist on those asset types.

        [Fact]
        public void Hdr_search_strips_polycount_filter()
        {
            // Default UI sets PolycountMax even when user hasn't touched
            // the filter (as a sane "max 10K faces" model default). On an
            // HDR query that wipes out every result.
            var f = Empty(); f.PolycountMin = 0; f.PolycountMax = 10000;
            var url = SearchService.BuildUrlQuery("sky", "HDR", 15, f);
            Assert.DoesNotContain("faceCount", url);
        }

        [Fact]
        public void Hdr_search_strips_model_style_designyear_animated()
        {
            var f = Empty();
            f.Style = "REALISTIC";
            f.Condition = "NEW";
            f.DesignYearMin = 2020; f.DesignYearMax = 2024;
            f.Animated = true;
            f.GltfOnly = true;
            var url = SearchService.BuildUrlQuery("sky", "HDR", 15, f);
            Assert.DoesNotContain("modelStyle", url);
            Assert.DoesNotContain("condition", url);
            Assert.DoesNotContain("designYear", url);
            Assert.DoesNotContain("animated:true", url);
            Assert.DoesNotContain("last_gltf_godot_upload", url);
        }

        [Fact]
        public void Hdr_search_keeps_texture_resolution_filter()
        {
            var f = Empty();
            f.TextureResolutionMin = 1024; f.TextureResolutionMax = 4096;
            var url = SearchService.BuildUrlQuery("sky", "HDR", 15, f);
            Assert.Contains("+textureResolutionMax_gte:1024", url);
            Assert.Contains("+textureResolutionMax_lte:4096", url);
        }

        [Fact]
        public void Material_search_strips_polycount_and_animated()
        {
            var f = Empty();
            f.PolycountMin = 0; f.PolycountMax = 10000;
            f.Animated = true;
            f.DesignYearMin = 2020;
            var url = SearchService.BuildUrlQuery("wood", "MATERIAL", 15, f);
            Assert.DoesNotContain("faceCount", url);
            Assert.DoesNotContain("animated:true", url);
            Assert.DoesNotContain("designYear", url);
        }

        [Fact]
        public void Material_search_procedural_emits_size_cap()
        {
            // PROCEDURAL: server-side filter is `files_size_lte:1MB`
            // (matches blenderkit/search.py:build_query_material).
            var f = Empty(); f.Procedural = "PROCEDURAL";
            var url = SearchService.BuildUrlQuery("wood", "MATERIAL", 15, f);
            Assert.Contains("+files_size_lte:1048576", url);
        }

        [Fact]
        public void Material_search_texture_based_forces_texture_present()
        {
            // TEXTURE_BASED: textureResolutionMax_gte:0 is the addon's
            // trick to force assets that have at least one texture.
            var f = Empty(); f.Procedural = "TEXTURE_BASED";
            var url = SearchService.BuildUrlQuery("wood", "MATERIAL", 15, f);
            Assert.Contains("+textureResolutionMax_gte:0", url);
        }

        [Fact]
        public void Hdr_search_true_hdr_emits_trueHDR_filter()
        {
            var f = Empty(); f.TrueHdr = true;
            var url = SearchService.BuildUrlQuery("sky", "HDR", 15, f);
            Assert.Contains("+trueHDR:true", url);
        }

        [Fact]
        public void Model_search_geometry_nodes_emits_modifiers_nodes()
        {
            var f = Empty(); f.GeometryNodes = true;
            var url = SearchService.BuildUrlQuery("vase", "MODEL", 15, f);
            Assert.Contains("+modifiers:nodes", url);
        }

        [Fact]
        public void Own_only_emits_author_id_for_logged_in_user()
        {
            var f = Empty(); f.OwnUserId = 12345;
            var url = SearchService.BuildUrlQuery("wood", "MATERIAL", 15, f);
            Assert.Contains("+author_id:12345", url);
        }

        [Fact]
        public void Hdr_search_strips_geometry_nodes_filter()
        {
            // Geometry-nodes is model-only; HDR query shouldn't carry it.
            var f = Empty(); f.GeometryNodes = true;
            var url = SearchService.BuildUrlQuery("sky", "HDR", 15, f);
            Assert.DoesNotContain("modifiers:nodes", url);
        }

        [Fact]
        public void Material_search_strips_geometry_nodes_filter()
        {
            var f = Empty(); f.GeometryNodes = true;
            var url = SearchService.BuildUrlQuery("wood", "MATERIAL", 15, f);
            Assert.DoesNotContain("modifiers:nodes", url);
        }

        [Fact]
        public void Material_search_keeps_texture_resolution()
        {
            var f = Empty();
            f.TextureResolutionMin = 2048;
            var url = SearchService.BuildUrlQuery("wood", "MATERIAL", 15, f);
            Assert.Contains("+textureResolutionMax_gte:2048", url);
        }

        [Fact]
        public void Common_filters_apply_to_all_asset_types()
        {
            // QualityMin / FreeOnly / BookmarksOnly / Category / License
            // come from build_query_common — they should fire on HDR and
            // material searches too.
            var f = Empty();
            f.QualityMin = 4;
            f.FreeOnly = true;
            f.BookmarksOnly = true;
            f.License = "cc-zero";
            f.Category = "studio";
            foreach (var at in new[] { "HDR", "MATERIAL", "MODEL" })
            {
                var url = SearchService.BuildUrlQuery("sky", at, 15, f);
                Assert.Contains("+quality_count_gte:4", url);
                Assert.Contains("+is_free:true", url);
                Assert.Contains("+bookmarks_rating:1", url);
                Assert.Contains("+license:cc-zero", url);
                Assert.Contains("+category_subtree:studio", url);
            }
        }

        [Fact]
        public void Printable_search_treated_like_model_for_polycount()
        {
            // Printable assets are essentially models for filter purposes
            // (faceCount makes sense for them).
            var f = Empty(); f.PolycountMax = 50000;
            var url = SearchService.BuildUrlQuery("vase", "PRINTABLE", 15, f);
            Assert.Contains("+faceCount_lte:50000", url);
        }
    }
}
