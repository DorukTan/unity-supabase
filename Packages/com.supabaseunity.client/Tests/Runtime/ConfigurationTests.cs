using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;

namespace Supabase.Unity.Tests
{
    public sealed class ConfigurationTests
    {
        [Test]
        public void PublishableKey_IsAccepted()
        {
            var options = ValidOptions();
            Assert.DoesNotThrow(delegate { options.ValidateAndResolve(); });
        }

        [Test]
        public void PublishableKey_IsNeverSentAsBearerToken()
        {
            var options = ValidOptions();
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer stale-value",
                ["apikey"] = "stale-value"
            };

            SupabaseHttp.ApplyClientHeaders(headers, options, null);

            Assert.AreEqual(options.PublishableKey, headers["apikey"]);
            Assert.IsFalse(headers.ContainsKey("Authorization"));
        }

        [Test]
        public void LegacyAnonJwt_IsSentAsBearerToken()
        {
            var options = ValidOptions();
            options.PublishableKey = JwtWithRole("anon");
            options.ValidateAndResolve();
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            SupabaseHttp.ApplyClientHeaders(headers, options, null);

            Assert.AreEqual("Bearer " + options.PublishableKey, headers["Authorization"]);
        }

        [Test]
        public void ElevatedPerRequestHeader_IsRejected()
        {
            var headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer sb_secret_test"
            };

            Assert.Throws<SupabaseConfigurationException>(delegate
            {
                SupabaseHttp.ValidateAdditionalHeaders(headers, "sb_publishable_test-value");
            });
        }

        [Test]
        public void ErrorRedaction_RemovesJwtBearerAndSecretKeys()
        {
            var jwt = JwtWithRole("authenticated");
            var redacted = SupabaseHttp.Redact(
                "Authorization: Bearer " + jwt + " secret=sb_secret_test");

            StringAssert.DoesNotContain(jwt, redacted);
            StringAssert.DoesNotContain("sb_secret_", redacted);
            StringAssert.Contains("[REDACTED]", redacted);
        }

        [Test]
        public void SessionPersistence_IsOptIn()
        {
            Assert.IsFalse(new SupabaseClientOptions().PersistSession);
        }

        [Test]
        public void SecretKey_IsRejected()
        {
            var options = ValidOptions();
            options.PublishableKey = "sb_secret_test";
            var exception = Assert.Throws<SupabaseConfigurationException>(delegate { options.ValidateAndResolve(); });
            StringAssert.Contains("must never", exception.Message);
        }

        [Test]
        public void NonLocalHttp_IsRejected()
        {
            var options = ValidOptions();
            options.ProjectUrl = "http://example.supabase.co";
            Assert.Throws<SupabaseConfigurationException>(delegate { options.ValidateAndResolve(); });
        }

        [Test]
        public void InsecureCustomServiceEndpoint_IsRejected()
        {
            var options = ValidOptions();
            options.Endpoints.AuthUrl = "http://auth.example.com";

            Assert.Throws<SupabaseConfigurationException>(delegate { options.ValidateAndResolve(); });
        }

        [Test]
        public void EndpointCredentialsAndQueryStrings_AreRejected()
        {
            var options = ValidOptions();
            options.ProjectUrl = "https://user:password@example.supabase.co?token=value";

            Assert.Throws<SupabaseConfigurationException>(delegate { options.ValidateAndResolve(); });
        }

        internal static SupabaseClientOptions ValidOptions()
        {
            return new SupabaseClientOptions
            {
                ProjectUrl = "https://example.supabase.co",
                PublishableKey = "sb_publishable_test-value",
                PersistSession = false,
                AuthCallbackProvider = new FakeCallbackProvider()
            };
        }

        private static string JwtWithRole(string role)
        {
            return Base64Url("{\"alg\":\"HS256\",\"typ\":\"JWT\"}") + "." +
                   Base64Url("{\"role\":\"" + role + "\"}") + ".test-signature";
        }

        private static string Base64Url(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}
