using System;
using System.IO;
using Blendkit.Rhino.Infra;
using Xunit;

namespace Blendkit.Rhino.Tests
{
    /// <summary>
    /// Lock down the token-refresh contract:
    ///   * <see cref="AuthService.NeedsRefresh"/> precondition chain
    ///     matches bkit_oauth.ensure_token_refresh in the Blender addon.
    ///   * <see cref="AuthService.LoadTokens"/> / <see cref="AuthService.SaveTokens"/>
    ///     round-trip preserves <c>expires_at</c>, and gracefully handles
    ///     legacy config.json files that pre-date the field.
    ///
    /// NeedsRefresh tests are pure-function (no IO, no clock); we pass the
    /// "now" timestamp explicitly so the suite doesn't need a fake clock.
    /// </summary>
    public class AuthServiceTokenRefreshTests
    {
        // Reserve constant copied here so changing the public constant
        // shows up as a test failure rather than a silent behaviour shift.
        private const long ReserveSeconds = 60L * 60 * 24 * 3;
        private const long Now = 1_700_000_000L; // arbitrary fixed "now"

        [Fact]
        public void NeedsRefresh_false_when_token_has_plenty_of_life_left()
        {
            // Token expires a year out — well past the 3-day reserve.
            var expiresAt = Now + (60L * 60 * 24 * 365);
            Assert.False(AuthService.NeedsRefresh("ak", "rk", expiresAt, Now));
        }

        [Fact]
        public void NeedsRefresh_true_when_token_inside_reserve_window()
        {
            // Token expires in 2 days, reserve is 3 days → time to refresh.
            var expiresAt = Now + (60L * 60 * 24 * 2);
            Assert.True(AuthService.NeedsRefresh("ak", "rk", expiresAt, Now));
        }

        [Fact]
        public void NeedsRefresh_true_at_exact_reserve_boundary()
        {
            // expiresAt == now + ReserveSeconds → boundary case, refresh.
            // Mirrors the addon's `time.time() + REFRESH_RESERVE < timeout`
            // (note: < not ≤, so we return true at equality, matching the
            // negation of "more than reserve away").
            var expiresAt = Now + ReserveSeconds;
            Assert.True(AuthService.NeedsRefresh("ak", "rk", expiresAt, Now));
        }

        [Fact]
        public void NeedsRefresh_true_when_token_already_expired()
        {
            // Past expiry: definitely refresh (still has a chance via the
            // refresh_token even after access_token's gone).
            var expiresAt = Now - (60L * 60); // 1 hour ago
            Assert.True(AuthService.NeedsRefresh("ak", "rk", expiresAt, Now));
        }

        [Fact]
        public void NeedsRefresh_false_when_not_logged_in()
        {
            // Empty access_token = anonymous. Don't try to refresh.
            Assert.False(AuthService.NeedsRefresh("", "rk", Now + 10, Now));
            Assert.False(AuthService.NeedsRefresh(null, "rk", Now + 10, Now));
        }

        [Fact]
        public void NeedsRefresh_false_when_no_refresh_token()
        {
            // Manually-pasted permanent API key — no refresh_token to
            // refresh against. Mirrors the addon's
            // `if preferences.api_key_refresh == "": return False`.
            Assert.False(AuthService.NeedsRefresh("ak", "", Now + 10, Now));
            Assert.False(AuthService.NeedsRefresh("ak", null, Now + 10, Now));
        }

        [Fact]
        public void NeedsRefresh_false_when_expires_at_unknown()
        {
            // expires_at=0 = "we don't know when this token expires"
            // (config.json from 0.1.2 or earlier, or a login response that
            // omitted expires_in). Skip refresh until a real value lands —
            // the reactive "Invalid token." path still recovers if needed.
            Assert.False(AuthService.NeedsRefresh("ak", "rk", 0, Now));
        }

        [Fact]
        public void SaveTokens_then_LoadTokens_round_trips_all_three_fields()
        {
            using var tmp = new TempConfigDir();
            var expiresAt = Now + (60L * 60 * 24 * 30); // 30 days out
            AuthService.SaveTokens("access_v1", "refresh_v1", expiresAt);
            var (ak, rk, ea) = AuthService.LoadTokens();
            Assert.Equal("access_v1", ak);
            Assert.Equal("refresh_v1", rk);
            Assert.Equal(expiresAt, ea);
        }

        [Fact]
        public void SaveTokens_with_default_expires_at_stores_zero()
        {
            // Logout path: SaveTokens("","") with no expires_at arg.
            using var tmp = new TempConfigDir();
            AuthService.SaveTokens("", "");
            var (ak, rk, ea) = AuthService.LoadTokens();
            Assert.Equal("", ak);
            Assert.Equal("", rk);
            Assert.Equal(0, ea);
        }

        [Fact]
        public void LoadTokens_treats_missing_expires_at_as_zero()
        {
            // A config.json written by 0.1.2 or earlier won't have
            // `expires_at`. LoadTokens must tolerate that without
            // crashing — and return 0 so NeedsRefresh stays false.
            using var tmp = new TempConfigDir();
            File.WriteAllText(AuthService.ConfigPath, "{\"api_key\":\"old\",\"refresh_token\":\"r\"}");
            var (ak, rk, ea) = AuthService.LoadTokens();
            Assert.Equal("old", ak);
            Assert.Equal("r", rk);
            Assert.Equal(0, ea);
            // And NeedsRefresh on this legacy state is false — won't
            // accidentally trigger a refresh storm at first launch after
            // upgrade from 0.1.2.
            Assert.False(AuthService.NeedsRefresh(ak, rk, ea, Now));
        }

        [Fact]
        public void LoadTokens_returns_zero_when_file_missing()
        {
            // Fresh install, no config.json yet → tuple of empties + 0.
            using var tmp = new TempConfigDir();
            if (File.Exists(AuthService.ConfigPath)) File.Delete(AuthService.ConfigPath);
            var (ak, rk, ea) = AuthService.LoadTokens();
            Assert.Equal("", ak);
            Assert.Equal("", rk);
            Assert.Equal(0, ea);
        }

        [Fact]
        public void ExtractExpiresIn_reads_number_value()
        {
            // Standard OAuth response shape: "expires_in" is a number of
            // seconds. Our pin documents both number and string accepted,
            // since BlenderKit's Go client has been seen to emit strings.
            var doc = System.Text.Json.JsonDocument.Parse("{\"expires_in\":36000}");
            Assert.Equal(36000, AuthService.ExtractExpiresIn(doc.RootElement));
        }

        [Fact]
        public void ExtractExpiresIn_reads_string_value()
        {
            var doc = System.Text.Json.JsonDocument.Parse("{\"expires_in\":\"36000\"}");
            Assert.Equal(36000, AuthService.ExtractExpiresIn(doc.RootElement));
        }

        [Fact]
        public void ExtractExpiresIn_returns_zero_when_field_missing()
        {
            var doc = System.Text.Json.JsonDocument.Parse("{\"access_token\":\"a\"}");
            Assert.Equal(0, AuthService.ExtractExpiresIn(doc.RootElement));
        }

        /// <summary>
        /// Redirects %APPDATA% to a temp dir for the duration of a test, so
        /// SaveTokens / LoadTokens touch a sandboxed config.json instead
        /// of the developer's real one. Restores the env var on Dispose.
        /// </summary>
        private sealed class TempConfigDir : IDisposable
        {
            private readonly string _originalAppData;
            private readonly string _tempRoot;

            public TempConfigDir()
            {
                _originalAppData = Environment.GetEnvironmentVariable("APPDATA");
                _tempRoot = Path.Combine(Path.GetTempPath(), "blendkit-test-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_tempRoot);
                Environment.SetEnvironmentVariable("APPDATA", _tempRoot);
            }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable("APPDATA", _originalAppData);
                try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best effort */ }
            }
        }
    }
}
