using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// OAuth2 (PKCE S256) login flow against blenderkit.com.
    ///
    /// Mirrors the Blender addon's bkit_oauth.py:
    ///   1. Generate code_verifier + code_challenge + state.
    ///   2. POST them to the Go client at /oauth2/verification_data.
    ///   3. Open the browser to /o/authorize with the challenge.
    ///   4. blenderkit.com redirects to localhost:&lt;port&gt;/consumer/exchange/
    ///      which the Go client handles, exchanging the auth code for tokens.
    ///   5. The Go client emits a `login` task carrying access_token; the
    ///      panel watches /report for it and persists the key.
    /// </summary>
    public static class AuthService
    {
        // Same OAuth client_id the Blender addon uses — shared registration.
        private const string ClientId = "IdFRwa3SGA8eMpzhRVFMg5Ts8sPK93xBjif93x0F";
        private const string AuthorizeBase = "https://www.blenderkit.com/o/authorize";

        public static async Task BeginAsync(int appId, string addonVersion)
        {
            var verifier = GenerateVerifier();
            var challenge = ComputeChallenge(verifier);
            var state = GenerateState();

            await ClientLib.PostJsonAsync("/oauth2/verification_data", new
            {
                app_id = appId,
                api_key = "",
                addon_version = addonVersion,
                blender_version = "4.2.0",
                platform_version = "Rhino 8",
                code_verifier = verifier,
                state = state,
            });

            var port = ClientLib.ActivePort ?? 62485;
            var redirect = WebUtility.UrlEncode($"http://localhost:{port}/consumer/exchange/");
            var url = $"{AuthorizeBase}?client_id={ClientId}&response_type=code&state={state}" +
                      $"&redirect_uri={redirect}&code_challenge={challenge}&code_challenge_method=S256";

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        public static string ExtractAccessToken(JsonElement loginResult)
        {
            if (loginResult.ValueKind != JsonValueKind.Object) return "";
            return loginResult.TryGetProperty("access_token", out var t) ? (t.GetString() ?? "") : "";
        }

        public static string ExtractRefreshToken(JsonElement loginResult)
        {
            if (loginResult.ValueKind != JsonValueKind.Object) return "";
            return loginResult.TryGetProperty("refresh_token", out var t) ? (t.GetString() ?? "") : "";
        }

        // --- Persistence: same JSON file the Python preferences module uses ---
        public static string ConfigPath
        {
            get
            {
                var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appdata, "BlenderKit", "config.json");
            }
        }

        public static (string accessToken, string refreshToken) LoadTokens()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return ("", "");
                using var s = File.OpenRead(ConfigPath);
                using var doc = JsonDocument.Parse(s);
                var r = doc.RootElement;
                var ak = r.TryGetProperty("api_key", out var k) ? (k.GetString() ?? "") : "";
                var rk = r.TryGetProperty("refresh_token", out var rt) ? (rt.GetString() ?? "") : "";
                return (ak, rk);
            }
            catch { return ("", ""); }
        }

        public static void SaveTokens(string accessToken, string refreshToken)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                var json = JsonSerializer.Serialize(new
                {
                    api_key = accessToken,
                    refresh_token = refreshToken,
                }, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch { /* don't crash on persistence failure */ }
        }

        // --- PKCE helpers ---
        // Made internal/public so the test assembly can verify spec
        // conformance without going through the OAuth network roundtrip.
        public static string GenerateVerifier()
        {
            const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var rng = RandomNumberGenerator.Create();
            var bytes = new byte[128];
            rng.GetBytes(bytes);
            var sb = new StringBuilder(128);
            for (int i = 0; i < 128; i++) sb.Append(alphabet[bytes[i] % alphabet.Length]);
            return sb.ToString();
        }

        public static string ComputeChallenge(string verifier)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(verifier));
            return Convert.ToBase64String(hash)
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static string GenerateState()
        {
            var bytes = new byte[24];
            RandomNumberGenerator.Create().GetBytes(bytes);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}
