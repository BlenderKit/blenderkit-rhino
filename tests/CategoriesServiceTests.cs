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
        // Trimmed shape of the BlenderKit /categories response we care about.
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
    }
}
