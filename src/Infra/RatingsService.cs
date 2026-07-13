using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// Wrapper for the Blendkit rating endpoints exposed by the Go client.
    /// Bookmarks are modeled server-side as a rating with type "bookmarks"
    /// and value 1 (set) / 0 (clear), matching what the Blender addon does.
    /// </summary>
    public static class RatingsService
    {
        public static Task<JsonElement> SetBookmarkAsync(string assetId, bool bookmarked, string apiKey)
            => SendRatingAsync(assetId, "bookmarks", bookmarked ? 1 : 0, apiKey);

        // Blendkit's quality rating is on a 1-10 scale (not 1-5 stars).
        // Web UI usually shows it as 5 half-star slots → 10 increments.
        public static Task<JsonElement> SendQualityAsync(string assetId, int rating1To10, string apiKey)
            => SendRatingAsync(assetId, "quality", rating1To10, apiKey);

        public static Task<JsonElement> SendWorkingHoursAsync(string assetId, double hours, string apiKey)
            => SendRatingAsync(assetId, "working_hours", hours, apiKey);

        private static async Task<JsonElement> SendRatingAsync(
            string assetId, string ratingType, double ratingValue, string apiKey)
        {
            var payload = new
            {
                asset_id = assetId,
                rating_type = ratingType,
                rating_value = ratingValue,
                api_key = apiKey ?? "",
                addon_version = SearchService.AddonVersion,
                platform_version = "Rhino 8",
                app_id = Process.GetCurrentProcess().Id,
            };
            var body = await ClientLib.PostJsonAsync("/ratings/send_rating", payload);
            // The Go client returns 200 with an empty body on success — the
            // real result arrives as a `send_rating` task on /report. Guard
            // against JsonDocument.Parse barfing on whitespace.
            if (string.IsNullOrWhiteSpace(body)) return default;
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
    }
}
