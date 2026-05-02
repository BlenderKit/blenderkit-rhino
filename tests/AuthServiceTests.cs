using System.Security.Cryptography;
using System.Text;
using Blendkit.Rhino.Infra;
using Xunit;

namespace Blendkit.Rhino.Tests
{
    /// <summary>
    /// PKCE (RFC 7636) conformance for our OAuth flow against
    /// blenderkit.com. The login round-trip will reject mismatched challenges
    /// silently with a 400 from the OAuth server, so it pays to lock down
    /// the verifier/challenge contract here.
    /// </summary>
    public class AuthServiceTests
    {
        [Fact]
        public void Verifier_is_128_chars_unreserved()
        {
            var v = AuthService.GenerateVerifier();
            Assert.Equal(128, v.Length);
            // RFC 7636: code_verifier = high-entropy [A-Z / a-z / 0-9 / -._~]
            // Our impl uses the alphanumeric subset; that's a strict subset
            // so still spec-compliant.
            foreach (var c in v)
                Assert.True(char.IsLetterOrDigit(c), $"unexpected char: {c}");
        }

        [Fact]
        public void Verifier_is_unique_across_calls()
        {
            // 128 chars from a 62-char alphabet: collision probability is
            // astronomically low — if two consecutive calls return the same
            // verifier the RNG is broken.
            var a = AuthService.GenerateVerifier();
            var b = AuthService.GenerateVerifier();
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Challenge_is_base64url_sha256_no_padding()
        {
            var verifier = "test-verifier-12345";
            var challenge = AuthService.ComputeChallenge(verifier);

            // Base64-URL: characters in [A-Za-z0-9_-] only (no '+', '/', '=').
            foreach (var c in challenge)
            {
                Assert.True(char.IsLetterOrDigit(c) || c == '-' || c == '_',
                    $"non-base64url char: {c}");
            }
            Assert.DoesNotContain("=", challenge);

            // Verify it's actually base64-url(sha256(verifier)).
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(verifier));
            var expected = System.Convert.ToBase64String(hash)
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Assert.Equal(expected, challenge);
        }

        [Fact]
        public void Challenge_is_43_chars_for_256bit_hash()
        {
            // SHA-256 produces 32 bytes; base64 of 32 bytes = 44 chars with
            // one '=' pad → 43 chars unpadded. Must hold for any verifier.
            var c = AuthService.ComputeChallenge("anything");
            Assert.Equal(43, c.Length);
        }

        [Fact]
        public void Different_verifiers_produce_different_challenges()
        {
            var a = AuthService.ComputeChallenge("alpha");
            var b = AuthService.ComputeChallenge("beta");
            Assert.NotEqual(a, b);
        }
    }
}
