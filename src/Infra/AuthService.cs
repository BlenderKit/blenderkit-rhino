using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// OAuth2 (PKCE S256) login flow against blendkit.com.
    ///
    /// Mirrors the Blender addon's bkit_oauth.py:
    ///   1. Generate code_verifier + code_challenge + state.
    ///   2. POST them to the Go client at /oauth2/verification_data.
    ///   3. Open the browser to /o/authorize with the challenge.
    ///   4. blendkit.com redirects to localhost:&lt;port&gt;/consumer/exchange/
    ///      which the Go client handles, exchanging the auth code for tokens.
    ///   5. The Go client emits a `login` task carrying access_token; the
    ///      panel watches /report for it and persists the key.
    ///
    /// Token refresh (matches bkit_oauth.py:ensure_token_refresh and
    /// timer.py:task_error_overdrive):
    ///   * Each persisted login records an `expires_at` Unix-seconds
    ///     timestamp computed from the OAuth response's `expires_in`.
    ///   * <see cref="NeedsRefresh"/> returns true when the token has
    ///     less than <see cref="RefreshReserveSeconds"/> (3 days) of life
    ///     left — same threshold the Blender addon uses, so a session
    ///     left open for a week doesn't suddenly start 401'ing.
    ///   * <see cref="RefreshTokenAsync"/> GETs /refresh_token on the Go
    ///     client (yes, GET with a JSON body — that's the addon's
    ///     contract; see client_lib.refresh_token). The Go client emits
    ///     a `token_refresh` task back with the new tokens; the panel
    ///     handles it identically to a fresh login.
    /// </summary>
    public static class AuthService
    {
        // Same OAuth client_id the Blender addon uses — shared registration.
        private const string ClientId = "IdFRwa3SGA8eMpzhRVFMg5Ts8sPK93xBjif93x0F";
        private const string AuthorizeBase = "https://www.blendkit.com/o/authorize";

        // Refresh the token once it has fewer than 3 days of life left.
        // Mirrors REFRESH_RESERVE in the Blender addon's bkit_oauth.py.
        // Keeping the threshold generous means a user who opens Rhino
        // every few days never sees an "invalid token" error in practice
        // — the proactive check at panel startup catches it first.
        public const long RefreshReserveSeconds = 60L * 60 * 24 * 3;

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

        /// <summary>
        /// Pull <c>expires_in</c> (seconds-from-now) out of the OAuth login
        /// or refresh response. Returns 0 if the field is missing — callers
        /// treat 0 as "no expiry recorded" and skip the proactive refresh
        /// check until the next login lands a real value. Mirrors the
        /// addon's <c>oauth_response["expires_in"]</c> in
        /// bkit_oauth.write_tokens.
        /// </summary>
        public static long ExtractExpiresIn(JsonElement loginResult)
        {
            if (loginResult.ValueKind != JsonValueKind.Object) return 0;
            if (!loginResult.TryGetProperty("expires_in", out var e)) return 0;
            return e.ValueKind switch
            {
                JsonValueKind.Number => e.TryGetInt64(out var v) ? v : 0,
                JsonValueKind.String => long.TryParse(e.GetString(), out var v) ? v : 0,
                _ => 0,
            };
        }

        // --- Persistence: same JSON file the Python preferences module uses ---
        // Folder stays "BlenderKit" (not "Blendkit") after the brand rename:
        // this file is shared with the Blender add-on and holds existing
        // installs' saved login token. See Settings.Path for the rationale.
        public static string ConfigPath
        {
            get
            {
                var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appdata, "BlenderKit", "config.json");
            }
        }

        /// <summary>
        /// Read access_token + refresh_token + expires_at (Unix seconds)
        /// from config.json. Returns zero <paramref name="expiresAt"/> when
        /// the field is missing — config.json files written by earlier
        /// builds (0.1.2 and before) won't have it, and we don't want to
        /// force-logout those users; <see cref="NeedsRefresh"/> treats 0
        /// as "unknown" and skips refresh until a fresh login fills it in.
        /// </summary>
        public static (string accessToken, string refreshToken, long expiresAt) LoadTokens()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return ("", "", 0);
                using var s = File.OpenRead(ConfigPath);
                using var doc = JsonDocument.Parse(s);
                var r = doc.RootElement;
                var ak = r.TryGetProperty("api_key", out var k) ? (k.GetString() ?? "") : "";
                var rk = r.TryGetProperty("refresh_token", out var rt) ? (rt.GetString() ?? "") : "";
                long ea = 0;
                if (r.TryGetProperty("expires_at", out var ee))
                {
                    if (ee.ValueKind == JsonValueKind.Number && ee.TryGetInt64(out var v)) ea = v;
                    else if (ee.ValueKind == JsonValueKind.String && long.TryParse(ee.GetString(), out v)) ea = v;
                }
                return (ak, rk, ea);
            }
            catch { return ("", "", 0); }
        }

        /// <summary>
        /// Persist tokens + expires_at. Pass <paramref name="expiresAt"/>=0
        /// to record "expiry unknown" — used by the explicit-logout path
        /// in the panel (after which everything is empty / zero anyway).
        /// </summary>
        public static void SaveTokens(string accessToken, string refreshToken, long expiresAt = 0)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                var json = JsonSerializer.Serialize(new
                {
                    api_key = accessToken,
                    refresh_token = refreshToken,
                    expires_at = expiresAt,
                }, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch { /* don't crash on persistence failure */ }
        }

        // --- Refresh logic ---------------------------------------------------

        /// <summary>
        /// Pure predicate, kept separate from any IO so tests can pin it
        /// without faking a clock or filesystem. Mirrors the precondition
        /// chain in bkit_oauth.ensure_token_refresh:
        ///   1. Not logged in (no access token) → no.
        ///   2. No refresh token (manually pasted permanent API key) → no,
        ///      there's nothing to refresh against.
        ///   3. expires_at unknown (0) → no, treat as "don't touch until
        ///      a fresh login records a real timestamp".
        ///   4. expires_at is more than the reserve window away → no.
        /// Otherwise → yes, time to refresh.
        /// </summary>
        public static bool NeedsRefresh(string accessToken, string refreshToken, long expiresAt, long nowUnixSeconds)
            => !string.IsNullOrEmpty(accessToken)
            && !string.IsNullOrEmpty(refreshToken)
            && expiresAt > 0
            && nowUnixSeconds + RefreshReserveSeconds >= expiresAt;

        private static long UnixNow()
            => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        /// <summary>
        /// Fire the Go client's token-refresh endpoint. The Go client makes
        /// the actual OAuth roundtrip to blendkit.com and, on success,
        /// emits a `token_refresh` task whose payload looks identical to
        /// a fresh login — the panel routes both through the same handler.
        ///
        /// The endpoint takes JSON on a GET (matches the addon's
        /// <c>client_lib.refresh_token</c>; weird but established). The
        /// <c>old_api_key</c> field lets the Go client target only Blender
        /// /Rhino sessions that were holding the same key, so users logged
        /// in to multiple accounts across hosts don't trample each other.
        /// </summary>
        public static Task RefreshTokenAsync(string refreshToken, string oldApiKey)
        {
            return ClientLib.SendJsonAsync(HttpMethod.Get, "/refresh_token", new
            {
                app_id = Process.GetCurrentProcess().Id,
                api_key = oldApiKey ?? "",
                addon_version = SearchService.AddonVersion,
                blender_version = "4.2.0",
                platform_version = "Rhino 8",
                refresh_token = refreshToken ?? "",
            });
        }

        /// <summary>
        /// One-shot proactive refresh check. Reads the current tokens from
        /// disk, skips when no refresh is needed, otherwise fires
        /// <see cref="RefreshTokenAsync"/>. The result lands as a
        /// `token_refresh` task on the panel's task dispatcher; this method
        /// completes as soon as the request has been queued with the Go
        /// client, not when the refresh finishes.
        ///
        /// Safe to call repeatedly — the precondition check short-circuits
        /// when there's nothing to do. Called once from the panel
        /// constructor (so a token that expired while Rhino was closed
        /// gets refreshed before the first search) and again from the
        /// "Invalid token." error path (reactive recovery).
        /// </summary>
        public static async Task EnsureTokenRefreshAsync()
        {
            var (ak, rk, ea) = LoadTokens();
            if (!NeedsRefresh(ak, rk, ea, UnixNow())) return;
            try { await RefreshTokenAsync(rk, ak); }
            catch { /* surfaces on the next /report poll as a task error */ }
        }

        /// <summary>
        /// Revoke the refresh token on the server and clear local tokens.
        /// Mirrors bkit_oauth.logout → oauth2_logout → clean_login_data:
        /// hit the Go client's /oauth2/logout endpoint (best-effort) and
        /// wipe the on-disk config either way.
        ///
        /// Local-state clear happens FIRST, so callers can fire-and-forget
        /// this method without worrying about a slow network revoke
        /// leaving the on-disk tokens alive past the UI's "logged out"
        /// state. The refresh token is captured up-front so the server
        /// revoke still has something to invalidate.
        /// </summary>
        public static async Task LogoutAsync()
        {
            var (_, rk, _) = LoadTokens();
            SaveTokens("", "", 0);
            if (string.IsNullOrEmpty(rk)) return;  // nothing to revoke
            try
            {
                await ClientLib.SendJsonAsync(HttpMethod.Get, "/oauth2/logout", new
                {
                    app_id = Process.GetCurrentProcess().Id,
                    api_key = "",
                    addon_version = SearchService.AddonVersion,
                    blender_version = "4.2.0",
                    platform_version = "Rhino 8",
                    refresh_token = rk,
                });
            }
            catch { /* revoke is best-effort; local state already cleared */ }
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
