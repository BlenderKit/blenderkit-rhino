using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// Thin HTTP wrapper for the local Go client at 127.0.0.1:&lt;port&gt;.
    ///
    /// Port discovery mirrors the Blender addon's candidate list so a single
    /// Go client instance can serve both hosts simultaneously.
    /// </summary>
    public static class ClientLib
    {
        // Same fallback list used by the Blender addon — see
        // blenderkit/client_lib.py and client/main.go.
        public static readonly int[] CandidatePorts = new[]
        {
            62485, 65425, 55428, 49452, 35452, 25152, 5152, 1234
        };

        private static readonly HttpClient Http = new HttpClient();
        public static int? ActivePort { get; private set; }

        /// <summary>
        /// Forget the cached port so the next request re-runs the
        /// candidate scan. Call this from request failure paths when a
        /// connection refused / network error suggests the client died.
        /// </summary>
        public static void InvalidatePort() => ActivePort = null;

        public static async Task<int?> DiscoverPortAsync(CancellationToken ct = default)
        {
            foreach (var port in CandidatePorts)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromMilliseconds(500));
                    var resp = await Http.GetAsync($"http://127.0.0.1:{port}/", cts.Token);
                    // Client returns something (maybe 404) but it answered.
                    ActivePort = port;
                    return port;
                }
                catch { /* try next */ }
            }
            return null;
        }

        public static string BaseUrl
        {
            get
            {
                if (ActivePort == null)
                    throw new InvalidOperationException("Go client port not discovered yet.");
                return $"http://127.0.0.1:{ActivePort}";
            }
        }

        public static async Task<string> PostJsonAsync(string path, object payload, CancellationToken ct = default)
        {
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            try
            {
                var resp = await Http.PostAsync(BaseUrl + path, content, ct);
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync();
            }
            catch (HttpRequestException) when (LooksLikeClientDied())
            {
                // Connection refused / no route → drop cached port so
                // the next request re-runs discovery. Re-throw so the
                // caller still sees the failure for this attempt.
                InvalidatePort();
                throw;
            }
        }

        public static async Task<string> GetAsync(string path, CancellationToken ct = default)
        {
            try
            {
                var resp = await Http.GetAsync(BaseUrl + path, ct);
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync();
            }
            catch (HttpRequestException) when (LooksLikeClientDied())
            {
                InvalidatePort();
                throw;
            }
        }

        // Filter pred for the catch above. The actual HttpRequestException
        // doesn't carry a code we can match cleanly across .NET versions;
        // checking the inner SocketException gives a reliable signal that
        // the local listener went away.
        private static bool LooksLikeClientDied()
        {
            // Always invalidate on HttpRequestException for the local
            // client — any failure to reach 127.0.0.1 means our cached
            // port is stale.
            return ActivePort != null;
        }
    }
}
