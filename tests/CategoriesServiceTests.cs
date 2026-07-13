using System.Linq;
using System.Text.Json;
using Blendkit.Rhino.Infra;
using Xunit;

namespace Blendkit.Rhino.Tests
{
    /// <summary>
    /// Exercises the category-tree ingest path. The cascading-menu UI in the
    /// panel walks the tree directly, so a bug here surfaces as missing or
    /// wrongly-nested entries in the dropdown.
    /// </summary>
    public class CategoriesServiceTests
    {
        // Trimmed shape of the Blendkit /categories response we care about.
        private const string SampleResponse = @"
        [
          {
            ""name"": ""Model"", ""slug"": ""model"",
            ""children"": [
              {
                ""name"": ""Furniture"", ""slug"": ""furniture"",
                ""children"": [
                  { ""name"": ""Chair"", ""slug"": ""chair"", ""children"": [] },
                  { ""name"": ""Sofa"",  ""slug"": ""sofa"",  ""children"": [] }
                ]
              },
              {
                ""name"": ""Vehicles"", ""slug"": ""vehicles"",
                ""children"": [
                  { ""name"": ""Car"", ""slug"": ""car"", ""children"": [] }
                ]
              }
            ]
          },
          {
            ""name"": ""Material"", ""slug"": ""material"",
            ""children"": [
              { ""name"": ""Wood"", ""slug"": ""wood"", ""children"": [] }
            ]
          }
        ]";

        private static JsonElement Parse(string s) =>
            JsonDocument.Parse(s).RootElement.Clone();

        [Fact]
        public void Ingest_builds_per_asset_type_tree()
        {
            CategoriesService.Ingest(Parse(SampleResponse));
            var modelTree = CategoriesService.TreeForAssetType("MODEL");
            var matTree = CategoriesService.TreeForAssetType("MATERIAL");

            Assert.Equal(2, modelTree.Count); // Furniture, Vehicles
            Assert.Single(matTree);           // Wood
            Assert.Contains(modelTree, n => n.Slug == "furniture");
            Assert.Contains(modelTree, n => n.Slug == "vehicles");
        }

        [Fact]
        public void Ingest_preserves_child_nesting()
        {
            CategoriesService.Ingest(Parse(SampleResponse));
            var furniture = CategoriesService.TreeForAssetType("MODEL")
                .First(n => n.Slug == "furniture");
            Assert.Equal(2, furniture.Children.Count);
            Assert.Contains(furniture.Children, c => c.Slug == "chair");
            Assert.Contains(furniture.Children, c => c.Slug == "sofa");
        }

        [Fact]
        public void Ingest_is_case_insensitive_on_asset_type_lookup()
        {
            CategoriesService.Ingest(Parse(SampleResponse));
            var lower = CategoriesService.TreeForAssetType("model");
            var upper = CategoriesService.TreeForAssetType("MODEL");
            var mixed = CategoriesService.TreeForAssetType("Model");
            Assert.Same(upper, lower);
            Assert.Same(upper, mixed);
        }

        [Fact]
        public void Ingest_replaces_previous_tree()
        {
            CategoriesService.Ingest(Parse(SampleResponse));
            // Re-ingest with a smaller tree.
            CategoriesService.Ingest(Parse(@"[
                { ""name"": ""Model"", ""slug"": ""model"",
                  ""children"": [{ ""name"": ""Plants"", ""slug"": ""plants"", ""children"": [] }] }
            ]"));
            var tree = CategoriesService.TreeForAssetType("MODEL");
            Assert.Single(tree);
            Assert.Equal("plants", tree[0].Slug);
        }

        [Fact]
        public void Unknown_asset_type_returns_empty_list()
        {
            CategoriesService.Ingest(Parse(SampleResponse));
            var tree = CategoriesService.TreeForAssetType("BRUSH");
            Assert.Empty(tree);
        }

        [Fact]
        public void Skips_nodes_without_slug()
        {
            CategoriesService.Ingest(Parse(@"[
                { ""name"": ""Model"", ""slug"": ""model"",
                  ""children"": [
                    { ""name"": ""NoSlug"", ""children"": [] },
                    { ""name"": ""Has"",     ""slug"": ""has"", ""children"": [] }
                  ]
                }
            ]"));
            var tree = CategoriesService.TreeForAssetType("MODEL");
            Assert.Single(tree);
            Assert.Equal("has", tree[0].Slug);
        }

        [Fact]
        public void Non_array_input_is_ignored()
        {
            // Should not throw — bad input is the API's problem, not ours.
            CategoriesService.Ingest(Parse(@"{""error"": ""nope""}"));
            // No state guarantees here other than "did not crash".
        }

        // --- Regression coverage: the panel hands the raw `result` JSON node
        //     from a /report task envelope. Categories have broken multiple
        //     times by silent reshapes of that result; these tests pin the
        //     contract from the consumer's side so we catch it the next time.

        [Fact]
        public void Ingest_handles_results_envelope()
        {
            // Blendkit's REST /api/v1/categories/ wraps the array in
            // {"count":..., "results":[...]}. The Go client currently
            // unwraps before forwarding, but if it ever stops (or another
            // host adds a wrapper) we still want categories to load.
            CategoriesService.Ingest(Parse(@"{
                ""count"": 1,
                ""results"": [
                  { ""name"": ""Model"", ""slug"": ""model"",
                    ""children"": [{ ""name"": ""Plants"", ""slug"": ""plants"", ""children"": [] }] }
                ]
            }"));
            var tree = CategoriesService.TreeForAssetType("MODEL");
            Assert.Single(tree);
            Assert.Equal("plants", tree[0].Slug);
        }

        [Fact]
        public void Ingest_handles_categories_envelope()
        {
            CategoriesService.Ingest(Parse(@"{
                ""categories"": [
                  { ""name"": ""HDR"", ""slug"": ""hdr"",
                    ""children"": [{ ""name"": ""Sky"", ""slug"": ""sky"", ""children"": [] }] }
                ]
            }"));
            var tree = CategoriesService.TreeForAssetType("HDR");
            Assert.Single(tree);
            Assert.Equal("sky", tree[0].Slug);
        }

        [Fact]
        public void Ingest_tolerates_extra_fields_from_real_api()
        {
            // Shape lifted directly from /api/v1/categories/ — the real
            // Category struct has thumbnail / assetCount / order / active
            // / metaKeywords on every node. Ingest must ignore the noise
            // and pull just name+slug+children.
            CategoriesService.Ingest(Parse(@"
            [
              {
                ""name"": ""Model"", ""slug"": ""model"",
                ""active"": true, ""thumbnail"": ""https://example.com/t.png"",
                ""thumbnailWidth"": 256, ""thumbnailHeight"": 256,
                ""order"": 0, ""alternateTitle"": """", ""alternateUrl"": """",
                ""description"": """", ""metaKeywords"": """", ""metaExtra"": """",
                ""assetCount"": 1234, ""assetCountCumulative"": 9999,
                ""children"": [
                  {
                    ""name"": ""Furniture"", ""slug"": ""furniture"",
                    ""active"": true, ""thumbnail"": """", ""thumbnailWidth"": 0, ""thumbnailHeight"": 0,
                    ""order"": 0, ""alternateTitle"": """", ""alternateUrl"": """",
                    ""description"": """", ""metaKeywords"": """", ""metaExtra"": """",
                    ""assetCount"": 50, ""assetCountCumulative"": 50,
                    ""children"": []
                  }
                ]
              }
            ]"));
            var tree = CategoriesService.TreeForAssetType("MODEL");
            Assert.Single(tree);
            Assert.Equal("furniture", tree[0].Slug);
            Assert.Equal("Furniture", tree[0].Name);
        }

        [Fact]
        public void Ingest_empty_array_clears_tree()
        {
            // Seed.
            CategoriesService.Ingest(Parse(SampleResponse));
            Assert.NotEmpty(CategoriesService.TreeForAssetType("MODEL"));
            // Empty array (e.g. server returns nothing) should reset state.
            CategoriesService.Ingest(Parse(@"[]"));
            Assert.Empty(CategoriesService.TreeForAssetType("MODEL"));
        }
    }
}
