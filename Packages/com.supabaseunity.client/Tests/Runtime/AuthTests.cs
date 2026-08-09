using System;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Supabase.Unity.Tests
{
    public sealed class AuthTests
    {
        internal const string SessionJson =
            "{\"access_token\":\"access-one\",\"token_type\":\"bearer\",\"expires_in\":3600," +
            "\"refresh_token\":\"refresh-one\",\"user\":{\"id\":\"user-1\",\"email\":\"a@b.co\"}}";

        internal static SupabaseClient NewClient(IHttpTransport transport)
        {
            var options = ConfigurationTests.ValidOptions();
            options.HttpTransport = transport;
            return new SupabaseClient(options);
        }

        [Test]
        public void SignInWithPassword_AdoptsSessionAndSendsGrantType()
        {
            var transport = new RecordingHttpTransport().Enqueue(200, SessionJson);
            using (var client = NewClient(transport))
            {
                var result = client.Auth.SignInWithPasswordAsync("a@b.co", "secret")
                    .GetAwaiter().GetResult();

                Assert.IsTrue(result.IsSuccess, "Sign-in should succeed.");
                Assert.AreEqual("access-one", result.Data.AccessToken);
                Assert.AreEqual("access-one", client.Auth.CurrentSession.AccessToken);
                Assert.AreEqual("user-1", client.Auth.CurrentUser.Id);
                StringAssert.Contains("grant_type=password", transport.LastRequest.Uri.Query);
            }
        }

        [Test]
        public void SignInWithPassword_FailureLeavesNoSession()
        {
            var transport = new RecordingHttpTransport()
                .Enqueue(400, "{\"error_code\":\"invalid_credentials\",\"msg\":\"Invalid login credentials\"}");
            using (var client = NewClient(transport))
            {
                var result = client.Auth.SignInWithPasswordAsync("a@b.co", "wrong")
                    .GetAwaiter().GetResult();

                Assert.IsFalse(result.IsSuccess);
                Assert.IsNull(client.Auth.CurrentSession);
                Assert.AreEqual(400, result.Error.StatusCode);
            }
        }

        [Test]
        public void SignUpWithPassword_PostsToSignupEndpoint()
        {
            var transport = new RecordingHttpTransport()
                .Enqueue(200, "{\"user\":{\"id\":\"user-1\",\"email\":\"a@b.co\"},\"session\":null}");
            using (var client = NewClient(transport))
            {
                var result = client.Auth.SignUpWithPasswordAsync("a@b.co", "secret")
                    .GetAwaiter().GetResult();

                Assert.IsTrue(result.IsSuccess);
                StringAssert.Contains("/signup", transport.LastRequest.Uri.AbsolutePath);
                Assert.AreEqual(SupabaseHttpMethod.Post, transport.LastRequest.Method);
            }
        }

        [Test]
        public void PasswordCredentials_AreRedactedFromErrors()
        {
            var transport = new RecordingHttpTransport()
                .Enqueue(400, "{\"msg\":\"failed\",\"password\":\"hunter2\"}");
            using (var client = NewClient(transport))
            {
                var result = client.Auth.SignInWithPasswordAsync("a@b.co", "hunter2")
                    .GetAwaiter().GetResult();

                Assert.IsFalse(result.IsSuccess);
                StringAssert.DoesNotContain("hunter2", result.Error.RawResponse ?? string.Empty);
            }
        }

        [Test]
        public void RefreshSession_ReplacesTokensAndSendsRefreshGrant()
        {
            var refreshed = SessionJson.Replace("access-one", "access-two")
                .Replace("refresh-one", "refresh-two");
            var transport = new RecordingHttpTransport()
                .Enqueue(200, SessionJson)
                .Enqueue(200, refreshed);
            using (var client = NewClient(transport))
            {
                client.Auth.SignInWithPasswordAsync("a@b.co", "secret").GetAwaiter().GetResult();

                var result = client.Auth.RefreshSessionAsync().GetAwaiter().GetResult();

                Assert.IsTrue(result.IsSuccess);
                Assert.AreEqual("access-two", client.Auth.CurrentSession.AccessToken);
                StringAssert.Contains("grant_type=refresh_token", transport.LastRequest.Uri.Query);
            }
        }

        [Test]
        public void ConcurrentRefresh_IssuesOneNetworkCall()
        {
            // RecordingHttpTransport completes synchronously, so a refresh made against it is
            // already finished before the next call starts and there is nothing to single-flight.
            // GatedHttpTransport holds the refresh request open so the second call genuinely
            // overlaps the first.
            var transport = new GatedHttpTransport().Enqueue(200, SessionJson);

            // Unity installs a SynchronizationContext that only pumps from the editor loop, so an
            // await resumed while this thread is blocked in GetResult would never run. Clearing it
            // for the duration lets the released continuations complete inline.
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                using (var client = NewClient(transport))
                {
                    client.Auth.SignInWithPasswordAsync("a@b.co", "secret").GetAwaiter().GetResult();
                    var before = transport.RequestCount;

                    var first = client.Auth.RefreshSessionAsync();
                    var second = client.Auth.RefreshSessionAsync();

                    Assert.IsNotNull(first, "RefreshSessionAsync must never return null.");
                    Assert.IsNotNull(second, "RefreshSessionAsync must never return null.");
                    Assert.IsFalse(first.IsCompleted,
                        "The gated refresh must still be in flight for this to test concurrency.");
                    Assert.AreSame(first, second,
                        "An overlapping refresh must join the in-flight one rather than start its own.");
                    Assert.AreEqual(before + 1, transport.RequestCount,
                        "Concurrent refreshes must share a single in-flight request.");

                    transport.Release(200, SessionJson.Replace("access-one", "access-two"));

                    Assert.IsTrue(first.GetAwaiter().GetResult().IsSuccess);
                    Assert.IsTrue(second.GetAwaiter().GetResult().IsSuccess);
                    Assert.AreEqual(before + 1, transport.RequestCount,
                        "Releasing the gate must not produce a second request.");
                    Assert.AreEqual("access-two", client.Auth.CurrentSession.AccessToken);
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }

        [Test]
        public void SignOut_ClearsSession()
        {
            var transport = new RecordingHttpTransport()
                .Enqueue(200, SessionJson)
                .Enqueue(204, string.Empty);
            using (var client = NewClient(transport))
            {
                client.Auth.SignInWithPasswordAsync("a@b.co", "secret").GetAwaiter().GetResult();

                var result = client.Auth.SignOutAsync().GetAwaiter().GetResult();

                Assert.IsTrue(result.IsSuccess);
                Assert.IsNull(client.Auth.CurrentSession);
                Assert.IsNull(client.Auth.CurrentUser);
            }
        }

        [Test]
        public void Initialize_RestoresPersistedSession()
        {
            var store = new MemorySessionStore();
            var options = ConfigurationTests.ValidOptions();
            options.SessionStore = store;
            options.HttpTransport = new RecordingHttpTransport().Enqueue(200, SessionJson);
            using (var first = new SupabaseClient(options))
            {
                first.Auth.SignInWithPasswordAsync("a@b.co", "secret").GetAwaiter().GetResult();
            }

            var second = ConfigurationTests.ValidOptions();
            second.SessionStore = store;
            second.HttpTransport = new RecordingHttpTransport().Enqueue(200, SessionJson);
            using (var client = new SupabaseClient(second))
            {
                client.Auth.InitializeAsync().GetAwaiter().GetResult();

                Assert.IsNotNull(client.Auth.CurrentSession,
                    "A session written to the store must be restored on initialize.");
            }
        }

        [Test]
        public void SignInAnonymously_AdoptsSession()
        {
            var anonymous = SessionJson.Replace("\"email\":\"a@b.co\"", "\"is_anonymous\":true");
            var transport = new RecordingHttpTransport().Enqueue(200, anonymous);
            using (var client = NewClient(transport))
            {
                var result = client.Auth.SignInAnonymouslyAsync().GetAwaiter().GetResult();

                Assert.IsTrue(result.IsSuccess);
                Assert.IsTrue(client.Auth.CurrentUser.IsAnonymous);
            }
        }

        [Test]
        public void VerifyOtp_AdoptsSession()
        {
            var transport = new RecordingHttpTransport().Enqueue(200, SessionJson);
            using (var client = NewClient(transport))
            {
                var result = client.Auth.VerifyOtpAsync("123456", AuthOtpType.Email, "a@b.co")
                    .GetAwaiter().GetResult();

                Assert.IsTrue(result.IsSuccess);
                Assert.AreEqual("access-one", client.Auth.CurrentSession.AccessToken);
                StringAssert.Contains("/verify", transport.LastRequest.Uri.AbsolutePath);
            }
        }

        [Test]
        public void ListIdentities_ParsesIdentityArray()
        {
            var transport = new RecordingHttpTransport()
                .Enqueue(200, SessionJson)
                .Enqueue(200, "{\"id\":\"user-1\",\"identities\":[{\"identity_id\":\"i-1\",\"provider\":\"github\"}]}");
            using (var client = NewClient(transport))
            {
                client.Auth.SignInWithPasswordAsync("a@b.co", "secret").GetAwaiter().GetResult();

                var result = client.Auth.ListIdentitiesAsync().GetAwaiter().GetResult();

                Assert.IsTrue(result.IsSuccess);
                Assert.AreEqual(1, result.Data.Count);
                Assert.AreEqual("github", result.Data[0].Provider);
            }
        }

        [Test]
        public void EnrollMfa_ParsesEnrollment()
        {
            var transport = new RecordingHttpTransport()
                .Enqueue(200, SessionJson)
                .Enqueue(200, "{\"id\":\"factor-1\",\"type\":\"totp\",\"totp\":{\"secret\":\"S\",\"uri\":\"otpauth://x\"}}");
            using (var client = NewClient(transport))
            {
                client.Auth.SignInWithPasswordAsync("a@b.co", "secret").GetAwaiter().GetResult();

                var result = client.Auth.EnrollMfaAsync(new AuthMfaEnrollOptions { FactorType = "totp" })
                    .GetAwaiter().GetResult();

                Assert.IsTrue(result.IsSuccess);
                Assert.AreEqual("factor-1", result.Data.Id);
            }
        }

        [Test]
        public void VerifyMfa_AdoptsElevatedSession()
        {
            var transport = new RecordingHttpTransport()
                .Enqueue(200, SessionJson)
                .Enqueue(200, SessionJson.Replace("access-one", "access-aal2"));
            using (var client = NewClient(transport))
            {
                client.Auth.SignInWithPasswordAsync("a@b.co", "secret").GetAwaiter().GetResult();

                var result = client.Auth.VerifyMfaAsync("factor-1", "challenge-1", "123456")
                    .GetAwaiter().GetResult();

                Assert.IsTrue(result.IsSuccess);
                Assert.AreEqual("access-aal2", client.Auth.CurrentSession.AccessToken);
            }
        }

        [Test]
        public void DisposedClient_RejectsFurtherAuthCalls()
        {
            var transport = new RecordingHttpTransport().Enqueue(200, SessionJson);
            var client = NewClient(transport);
            client.Auth.SignInWithPasswordAsync("a@b.co", "secret").GetAwaiter().GetResult();

            client.Dispose();

            Assert.Throws<ObjectDisposedException>(delegate
            {
                client.Auth.RefreshSessionAsync().GetAwaiter().GetResult();
            }, "Auth calls after disposal must fail loudly rather than hang or no-op.");
        }

        [Test]
        public void OAuthVerifier_SurvivesAppRestartBetweenAuthorizeAndCallback()
        {
            // A durable store stands in for disk. It outlives the client, as real
            // storage would when Android or iOS evicts the app during the browser hop.
            var durable = new MemorySessionStore();

            var startOptions = ConfigurationTests.ValidOptions();
            startOptions.PkceStore = durable;
            startOptions.HttpTransport = new RecordingHttpTransport();
            using (var starting = new SupabaseClient(startOptions))
            {
                var authorize = starting.Auth.SignInWithOAuthAsync("github").GetAwaiter().GetResult();
                Assert.IsTrue(authorize.IsSuccess);
            }

            // The player returns via deep link. The process is new: session state is gone.
            var resumeOptions = ConfigurationTests.ValidOptions();
            resumeOptions.PkceStore = durable;
            resumeOptions.HttpTransport = new RecordingHttpTransport().Enqueue(200, SessionJson);
            using (var resuming = new SupabaseClient(resumeOptions))
            {
                var exchanged = resuming.Auth.ExchangeCodeForSessionAsync("auth-code-1")
                    .GetAwaiter().GetResult();

                Assert.IsTrue(exchanged.IsSuccess,
                    "The PKCE verifier must survive the app being evicted during OAuth.");
                Assert.AreEqual("access-one", resuming.Auth.CurrentSession.AccessToken);
            }
        }

        [Test]
        public void OAuthVerifier_IsRemovedAfterSuccessfulExchange()
        {
            var durable = new MemorySessionStore();
            var options = ConfigurationTests.ValidOptions();
            options.PkceStore = durable;
            options.HttpTransport = new RecordingHttpTransport().Enqueue(200, SessionJson);
            using (var client = new SupabaseClient(options))
            {
                client.Auth.SignInWithOAuthAsync("github").GetAwaiter().GetResult();
                client.Auth.ExchangeCodeForSessionAsync("auth-code-1").GetAwaiter().GetResult();

                var second = client.Auth.ExchangeCodeForSessionAsync("auth-code-1")
                    .GetAwaiter().GetResult();

                Assert.IsFalse(second.IsSuccess,
                    "A verifier must be single-use and removed after a successful exchange.");
            }
        }

        [Test]
        public void ExpiredOAuthVerifier_IsRejected()
        {
            var durable = new MemorySessionStore();
            var options = ConfigurationTests.ValidOptions();
            options.PkceStore = durable;
            options.HttpTransport = new RecordingHttpTransport().Enqueue(200, SessionJson);
            using (var client = new SupabaseClient(options))
            {
                client.Auth.SignInWithOAuthAsync("github").GetAwaiter().GetResult();

                // Rewrite the stored envelope so it expired one second ago.
                var key = FindPkceKey(durable);
                var stored = JObject.Parse(durable.GetAsync(key).GetAwaiter().GetResult());
                stored["exp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1;
                durable.SetAsync(key, stored.ToString(Newtonsoft.Json.Formatting.None))
                    .GetAwaiter().GetResult();

                var result = client.Auth.ExchangeCodeForSessionAsync("auth-code-1")
                    .GetAwaiter().GetResult();

                Assert.IsFalse(result.IsSuccess, "An expired verifier must not be usable.");
            }
        }

        private static string FindPkceKey(MemorySessionStore store)
        {
            foreach (var key in store.Keys)
                if (key.EndsWith(".pkce", StringComparison.Ordinal))
                    return key;
            throw new AssertionException("No PKCE key was written to the store.");
        }
    }
}
