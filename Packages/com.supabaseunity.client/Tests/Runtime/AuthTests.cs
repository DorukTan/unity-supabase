using System;
using System.Collections;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Supabase.Unity.Tests
{
    public sealed class AuthTests
    {
        internal const string SessionJson =
            "{\"access_token\":\"access-one\",\"token_type\":\"bearer\",\"expires_in\":3600," +
            "\"refresh_token\":\"refresh-one\",\"user\":{\"id\":\"user-1\",\"email\":\"a@b.co\"}}";

        internal static SupabaseClient NewClient(
            IHttpTransport transport,
            ISessionStore sessionStore = null)
        {
            var options = ConfigurationTests.ValidOptions();
            options.HttpTransport = transport;
            if (sessionStore != null)
                options.SessionStore = sessionStore;
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
                Assert.AreEqual("invalid_credentials", result.Error.Code);
                Assert.AreEqual("Invalid login credentials", result.Error.Message);
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
        public void CanceledRefreshWaiter_DoesNotCancelSharedRefresh()
        {
            RunWithoutUnitySynchronizationContext(delegate
            {
                var transport = new GatedHttpTransport().Enqueue(200, SessionJson);
                using (var client = NewClient(transport))
                using (var cancellation = new CancellationTokenSource())
                {
                    client.Auth.SignInWithPasswordAsync("a@b.co", "secret").GetAwaiter().GetResult();
                    var before = transport.RequestCount;

                    var canceledWaiter = client.Auth.RefreshSessionAsync(cancellation.Token);
                    var activeWaiter = client.Auth.RefreshSessionAsync();
                    cancellation.Cancel();

                    Assert.Throws<OperationCanceledException>(delegate
                    {
                        canceledWaiter.GetAwaiter().GetResult();
                    });
                    Assert.IsFalse(activeWaiter.IsCompleted,
                        "Canceling one waiter must leave the shared refresh running.");
                    Assert.AreEqual(before + 1, transport.RequestCount);

                    transport.Release(200, SessionJson.Replace("access-one", "access-two"));

                    Assert.IsTrue(activeWaiter.GetAwaiter().GetResult().IsSuccess);
                    Assert.AreEqual("access-two", client.Auth.CurrentSession.AccessToken);
                    Assert.AreEqual(before + 1, transport.RequestCount,
                        "Waiter cancellation must not start a replacement refresh request.");
                }
            });
        }

        [Test]
        public void Dispose_CancelsInFlightRefresh()
        {
            RunWithoutUnitySynchronizationContext(delegate
            {
                var transport = new GatedHttpTransport().Enqueue(200, SessionJson);
                using (var client = NewClient(transport))
                {
                    client.Auth.SignInWithPasswordAsync("a@b.co", "secret").GetAwaiter().GetResult();

                    var refresh = client.Auth.RefreshSessionAsync();
                    Assert.IsFalse(refresh.IsCompleted);

                    client.Dispose();

                    Assert.Catch<OperationCanceledException>(delegate
                    {
                        refresh.GetAwaiter().GetResult();
                    }, "Disposing the client must cancel its shared refresh request.");
                }
            });
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
        public void SignOutOthers_PreservesCurrentSession()
        {
            var store = new MemorySessionStore();
            var transport = new RecordingHttpTransport()
                .Enqueue(200, SessionJson)
                .Enqueue(204, string.Empty);
            var options = ConfigurationTests.ValidOptions();
            options.HttpTransport = transport;
            options.SessionStore = store;
            using (var client = new SupabaseClient(options))
            {
                client.Auth.SignInWithPasswordAsync("a@b.co", "secret").GetAwaiter().GetResult();
                var signedOutEvents = 0;
                client.Auth.StateChanged += delegate(object sender, AuthStateChangedEventArgs args)
                {
                    if (args.Event == AuthChangeEvent.SignedOut)
                        signedOutEvents++;
                };

                var result = client.Auth.SignOutAsync(AuthSignOutScope.Others)
                    .GetAwaiter().GetResult();

                Assert.IsTrue(result.IsSuccess);
                Assert.IsNotNull(client.Auth.CurrentSession,
                    "Signing out other sessions must keep the current session active.");
                Assert.AreEqual("access-one", client.Auth.CurrentSession.AccessToken);
                CollectionAssert.IsNotEmpty(store.Keys,
                    "Signing out other sessions must keep the current session persisted.");
                Assert.AreEqual(0, signedOutEvents);
                StringAssert.Contains("scope=others", transport.LastRequest.Uri.Query);
            }
        }

        [Test]
        public void RefreshCompletingAfterSignOut_DoesNotRestoreSession()
        {
            RunWithoutUnitySynchronizationContext(delegate
            {
                var refreshed = SessionJson.Replace("access-one", "access-two")
                    .Replace("refresh-one", "refresh-two");
                var transport = new RefreshGatedHttpTransport()
                    .Enqueue(200, SessionJson)
                    .Enqueue(204, string.Empty);
                using (var client = NewClient(transport))
                {
                    client.Auth.SignInWithPasswordAsync("a@b.co", "secret").GetAwaiter().GetResult();
                    var refresh = client.Auth.RefreshSessionAsync();
                    Assert.IsFalse(refresh.IsCompleted);

                    var signedOut = client.Auth.SignOutAsync(AuthSignOutScope.Local)
                        .GetAwaiter().GetResult();
                    Assert.IsTrue(signedOut.IsSuccess);
                    Assert.IsNull(client.Auth.CurrentSession);

                    transport.ReleaseRefresh(200, refreshed);
                    var refreshResult = refresh.GetAwaiter().GetResult();

                    Assert.IsFalse(refreshResult.IsSuccess);
                    Assert.AreEqual("auth_operation_superseded", refreshResult.Error.Code);
                    Assert.IsNull(client.Auth.CurrentSession,
                        "An older refresh must not resurrect a signed-out session.");
                }
            });
        }

        [Test]
        public void FailedRefreshAfterNewSignIn_DoesNotClearNewSession()
        {
            RunWithoutUnitySynchronizationContext(delegate
            {
                var newer = SessionJson.Replace("access-one", "access-new")
                    .Replace("refresh-one", "refresh-new");
                var transport = new RefreshGatedHttpTransport()
                    .Enqueue(200, SessionJson)
                    .Enqueue(200, newer);
                using (var client = NewClient(transport))
                {
                    client.Auth.SignInWithPasswordAsync("a@b.co", "secret").GetAwaiter().GetResult();
                    var refresh = client.Auth.RefreshSessionAsync();
                    Assert.IsFalse(refresh.IsCompleted);

                    var signedIn = client.Auth.SignInWithPasswordAsync("a@b.co", "new-secret")
                        .GetAwaiter().GetResult();
                    Assert.IsTrue(signedIn.IsSuccess);
                    Assert.AreEqual("access-new", client.Auth.CurrentSession.AccessToken);

                    transport.ReleaseRefresh(401,
                        "{\"error_code\":\"refresh_token_already_used\",\"msg\":\"Invalid Refresh Token\"}");
                    var refreshResult = refresh.GetAwaiter().GetResult();

                    Assert.IsFalse(refreshResult.IsSuccess);
                    Assert.AreEqual("access-new", client.Auth.CurrentSession.AccessToken,
                        "Failure of an older refresh must not clear a newer sign-in.");
                }
            });
        }

        [Test]
        public void OlderSignInCompletingLast_DoesNotOverwriteNewerSignIn()
        {
            RunWithoutUnitySynchronizationContext(delegate
            {
                var newer = SessionJson.Replace("access-one", "access-new")
                    .Replace("refresh-one", "refresh-new");
                var transport = new OneRequestGatedHttpTransport("grant_type=password")
                    .Enqueue(200, newer);
                using (var client = NewClient(transport))
                {
                    var olderSignIn = client.Auth.SignInWithPasswordAsync("old@b.co", "old-secret");
                    Assert.IsFalse(olderSignIn.IsCompleted);

                    var newerSignIn = client.Auth.SignInWithPasswordAsync("new@b.co", "new-secret")
                        .GetAwaiter().GetResult();
                    Assert.IsTrue(newerSignIn.IsSuccess);
                    Assert.AreEqual("access-new", client.Auth.CurrentSession.AccessToken);

                    transport.Release(200, SessionJson);
                    var olderResult = olderSignIn.GetAwaiter().GetResult();

                    Assert.IsFalse(olderResult.IsSuccess);
                    Assert.AreEqual("auth_operation_superseded", olderResult.Error.Code);
                    Assert.AreEqual("access-new", client.Auth.CurrentSession.AccessToken,
                        "A slower older sign-in must not replace the newer session.");
                }
            });
        }

        [Test]
        public void SignInCompletingAfterSignOut_DoesNotRestoreSession()
        {
            RunWithoutUnitySynchronizationContext(delegate
            {
                var transport = new OneRequestGatedHttpTransport("grant_type=password");
                using (var client = NewClient(transport))
                {
                    var signIn = client.Auth.SignInWithPasswordAsync("a@b.co", "secret");
                    Assert.IsFalse(signIn.IsCompleted);

                    var signOut = client.Auth.SignOutAsync(AuthSignOutScope.Local)
                        .GetAwaiter().GetResult();
                    Assert.IsTrue(signOut.IsSuccess);

                    transport.Release(200, SessionJson);
                    var signInResult = signIn.GetAwaiter().GetResult();

                    Assert.IsFalse(signInResult.IsSuccess);
                    Assert.AreEqual("auth_operation_superseded", signInResult.Error.Code);
                    Assert.IsNull(client.Auth.CurrentSession,
                        "A sign-in started before sign-out must not restore a session afterward.");
                }
            });
        }

        [Test]
        public void SignOutCompletingAfterNewSignIn_DoesNotClearNewSession()
        {
            RunWithoutUnitySynchronizationContext(delegate
            {
                var newer = SessionJson.Replace("access-one", "access-new")
                    .Replace("refresh-one", "refresh-new");
                var transport = new OneRequestGatedHttpTransport("/logout")
                    .Enqueue(200, SessionJson)
                    .Enqueue(200, newer);
                using (var client = NewClient(transport))
                {
                    client.Auth.SignInWithPasswordAsync("old@b.co", "old-secret")
                        .GetAwaiter().GetResult();
                    var signedOutEvents = 0;
                    client.Auth.StateChanged += delegate(object sender, AuthStateChangedEventArgs args)
                    {
                        if (args.Event == AuthChangeEvent.SignedOut)
                            signedOutEvents++;
                    };

                    var signOut = client.Auth.SignOutAsync(AuthSignOutScope.Global);
                    Assert.IsFalse(signOut.IsCompleted);

                    var newerSignIn = client.Auth.SignInWithPasswordAsync("new@b.co", "new-secret")
                        .GetAwaiter().GetResult();
                    Assert.IsTrue(newerSignIn.IsSuccess);

                    transport.Release(204, string.Empty);
                    var signOutResult = signOut.GetAwaiter().GetResult();
                    Assert.IsFalse(signOutResult.IsSuccess);
                    Assert.AreEqual("auth_operation_superseded", signOutResult.Error.Code);

                    Assert.AreEqual("access-new", client.Auth.CurrentSession.AccessToken,
                        "An older sign-out response must not clear a newer session.");
                    Assert.AreEqual(0, signedOutEvents,
                        "A superseded sign-out must not emit SignedOut for the newer session.");
                }
            });
        }

        [Test]
        public void SetSessionCompletingAfterSignOut_DoesNotRestoreSession()
        {
            RunWithoutUnitySynchronizationContext(delegate
            {
                var transport = new OneRequestGatedHttpTransport("/user")
                    .Enqueue(200, SessionJson)
                    .Enqueue(204, string.Empty);
                using (var client = NewClient(transport))
                {
                    client.Auth.SignInWithPasswordAsync("a@b.co", "secret")
                        .GetAwaiter().GetResult();

                    var setSession = client.Auth.SetSessionAsync("access-new", "refresh-new");
                    Assert.IsFalse(setSession.IsCompleted);
                    Assert.AreEqual("access-one", client.Auth.CurrentSession.AccessToken,
                        "A candidate session must not be exposed before its user is verified.");

                    var signOut = client.Auth.SignOutAsync(AuthSignOutScope.Local)
                        .GetAwaiter().GetResult();
                    Assert.IsTrue(signOut.IsSuccess);

                    transport.Release(200, "{\"id\":\"user-new\",\"email\":\"new@b.co\"}");
                    var setSessionResult = setSession.GetAwaiter().GetResult();

                    Assert.IsFalse(setSessionResult.IsSuccess);
                    Assert.AreEqual("auth_operation_superseded", setSessionResult.Error.Code);
                    Assert.IsNull(client.Auth.CurrentSession,
                        "A verified candidate must not restore a session after sign-out.");
                }
            });
        }

        [Test]
        public void GetUserCompletingAfterAccountSwitch_DoesNotReplaceNewUser()
        {
            RunWithoutUnitySynchronizationContext(delegate
            {
                var newer = SessionJson.Replace("access-one", "access-new")
                    .Replace("refresh-one", "refresh-new")
                    .Replace("user-1", "user-2")
                    .Replace("a@b.co", "new@b.co");
                var transport = new OneRequestGatedHttpTransport("/user")
                    .Enqueue(200, SessionJson)
                    .Enqueue(200, newer);
                using (var client = NewClient(transport))
                {
                    client.Auth.SignInWithPasswordAsync("old@b.co", "old-secret")
                        .GetAwaiter().GetResult();
                    var getUser = client.Auth.GetUserAsync();
                    Assert.IsFalse(getUser.IsCompleted);

                    var newSignIn = client.Auth.SignInWithPasswordAsync("new@b.co", "new-secret")
                        .GetAwaiter().GetResult();
                    Assert.IsTrue(newSignIn.IsSuccess);

                    transport.Release(200, "{\"id\":\"user-1\",\"email\":\"stale@b.co\"}");
                    var getUserResult = getUser.GetAwaiter().GetResult();

                    Assert.IsFalse(getUserResult.IsSuccess);
                    Assert.AreEqual("auth_operation_superseded", getUserResult.Error.Code);
                    Assert.AreEqual("user-2", client.Auth.CurrentUser.Id);
                    Assert.AreEqual("new@b.co", client.Auth.CurrentUser.Email,
                        "A profile response for the previous account must not modify the new account.");
                }
            });
        }

        [Test]
        public void UpdateUserCompletingAfterSignOut_DoesNotRestoreUser()
        {
            RunWithoutUnitySynchronizationContext(delegate
            {
                var transport = new OneRequestGatedHttpTransport("/user")
                    .Enqueue(200, SessionJson)
                    .Enqueue(204, string.Empty);
                using (var client = NewClient(transport))
                {
                    client.Auth.SignInWithPasswordAsync("a@b.co", "secret")
                        .GetAwaiter().GetResult();
                    var userUpdatedEvents = 0;
                    client.Auth.StateChanged += delegate(object sender, AuthStateChangedEventArgs args)
                    {
                        if (args.Event == AuthChangeEvent.UserUpdated)
                            userUpdatedEvents++;
                    };

                    var update = client.Auth.UpdateUserAsync(new JObject { ["email"] = "changed@b.co" });
                    Assert.IsFalse(update.IsCompleted);

                    var signOut = client.Auth.SignOutAsync(AuthSignOutScope.Local)
                        .GetAwaiter().GetResult();
                    Assert.IsTrue(signOut.IsSuccess);

                    transport.Release(200, "{\"id\":\"user-1\",\"email\":\"changed@b.co\"}");
                    var updateResult = update.GetAwaiter().GetResult();

                    Assert.IsFalse(updateResult.IsSuccess);
                    Assert.AreEqual("auth_operation_superseded", updateResult.Error.Code);
                    Assert.IsNull(client.Auth.CurrentSession);
                    Assert.AreEqual(0, userUpdatedEvents,
                        "A stale profile update must not emit UserUpdated after sign-out.");
                }
            });
        }

        [Test]
        public void UnlinkIdentityCompletingAfterAccountSwitch_DoesNotReplaceNewUser()
        {
            RunWithoutUnitySynchronizationContext(delegate
            {
                var newer = SessionJson.Replace("access-one", "access-new")
                    .Replace("refresh-one", "refresh-new")
                    .Replace("user-1", "user-2")
                    .Replace("a@b.co", "new@b.co");
                var transport = new OneRequestGatedHttpTransport("/user")
                    .Enqueue(200, SessionJson)
                    .Enqueue(200, newer);
                using (var client = NewClient(transport))
                {
                    client.Auth.SignInWithPasswordAsync("old@b.co", "old-secret")
                        .GetAwaiter().GetResult();
                    var userUpdatedEvents = 0;
                    client.Auth.StateChanged += delegate(object sender, AuthStateChangedEventArgs args)
                    {
                        if (args.Event == AuthChangeEvent.UserUpdated)
                            userUpdatedEvents++;
                    };

                    var unlink = client.Auth.UnlinkIdentityAsync("identity-1");
                    Assert.IsFalse(unlink.IsCompleted);

                    var newSignIn = client.Auth.SignInWithPasswordAsync("new@b.co", "new-secret")
                        .GetAwaiter().GetResult();
                    Assert.IsTrue(newSignIn.IsSuccess);

                    transport.Release(200, "{\"id\":\"user-1\",\"email\":\"old@b.co\"}");
                    var unlinkResult = unlink.GetAwaiter().GetResult();

                    Assert.IsFalse(unlinkResult.IsSuccess);
                    Assert.AreEqual("auth_operation_superseded", unlinkResult.Error.Code);
                    Assert.AreEqual("user-2", client.Auth.CurrentUser.Id);
                    Assert.AreEqual(0, userUpdatedEvents,
                        "A stale identity response must not emit UserUpdated for the new account.");
                }
            });
        }

        [Test]
        public void UpdateUserCompletingAfterSameUserRefresh_UpdatesRefreshedSession()
        {
            RunWithoutUnitySynchronizationContext(delegate
            {
                var refreshed = SessionJson.Replace("access-one", "access-two")
                    .Replace("refresh-one", "refresh-two");
                var transport = new OneRequestGatedHttpTransport("/user")
                    .Enqueue(200, SessionJson)
                    .Enqueue(200, refreshed);
                using (var client = NewClient(transport))
                {
                    client.Auth.SignInWithPasswordAsync("a@b.co", "secret")
                        .GetAwaiter().GetResult();
                    var userUpdatedEvents = 0;
                    client.Auth.StateChanged += delegate(object sender, AuthStateChangedEventArgs args)
                    {
                        if (args.Event == AuthChangeEvent.UserUpdated)
                            userUpdatedEvents++;
                    };

                    var update = client.Auth.UpdateUserAsync(new JObject { ["email"] = "changed@b.co" });
                    Assert.IsFalse(update.IsCompleted);

                    var refresh = client.Auth.RefreshSessionAsync().GetAwaiter().GetResult();
                    Assert.IsTrue(refresh.IsSuccess);
                    Assert.AreEqual("access-two", client.Auth.CurrentSession.AccessToken);

                    transport.Release(200, "{\"id\":\"user-1\",\"email\":\"changed@b.co\"}");
                    var updateResult = update.GetAwaiter().GetResult();

                    Assert.IsTrue(updateResult.IsSuccess);
                    Assert.AreEqual("access-two", client.Auth.CurrentSession.AccessToken,
                        "The profile update must preserve the refreshed tokens.");
                    Assert.AreEqual("changed@b.co", client.Auth.CurrentUser.Email);
                    Assert.AreEqual(1, userUpdatedEvents);
                }
            });
        }

        [Test]
        public void OlderGetUserResponse_DoesNotOverwriteNewerUpdate()
        {
            RunWithoutUnitySynchronizationContext(delegate
            {
                var transport = new OneRequestGatedHttpTransport("/user")
                    .Enqueue(200, SessionJson)
                    .Enqueue(200, "{\"id\":\"user-1\",\"email\":\"changed@b.co\"}");
                using (var client = NewClient(transport))
                {
                    client.Auth.SignInWithPasswordAsync("a@b.co", "secret")
                        .GetAwaiter().GetResult();

                    var getUser = client.Auth.GetUserAsync();
                    Assert.IsFalse(getUser.IsCompleted);

                    var update = client.Auth.UpdateUserAsync(new JObject { ["email"] = "changed@b.co" })
                        .GetAwaiter().GetResult();
                    Assert.IsTrue(update.IsSuccess);
                    Assert.AreEqual("changed@b.co", client.Auth.CurrentUser.Email);

                    transport.Release(200, "{\"id\":\"user-1\",\"email\":\"stale@b.co\"}");
                    var getUserResult = getUser.GetAwaiter().GetResult();

                    Assert.IsFalse(getUserResult.IsSuccess);
                    Assert.AreEqual("auth_operation_superseded", getUserResult.Error.Code);
                    Assert.AreEqual("changed@b.co", client.Auth.CurrentUser.Email,
                        "An older profile fetch must not overwrite a newer profile update.");
                }
            });
        }

        [Test]
        public void RefreshCompletingAfterUpdateUser_PreservesUpdatedUser()
        {
            RunWithoutUnitySynchronizationContext(delegate
            {
                var refreshed = SessionJson.Replace("access-one", "access-two")
                    .Replace("refresh-one", "refresh-two");
                var transport = new RefreshGatedHttpTransport()
                    .Enqueue(200, SessionJson)
                    .Enqueue(200, "{\"id\":\"user-1\",\"email\":\"changed@b.co\"}");
                using (var client = NewClient(transport))
                {
                    client.Auth.SignInWithPasswordAsync("a@b.co", "secret")
                        .GetAwaiter().GetResult();

                    var refresh = client.Auth.RefreshSessionAsync();
                    Assert.IsFalse(refresh.IsCompleted);

                    var update = client.Auth.UpdateUserAsync(new JObject { ["email"] = "changed@b.co" })
                        .GetAwaiter().GetResult();
                    Assert.IsTrue(update.IsSuccess);
                    Assert.AreEqual("changed@b.co", client.Auth.CurrentUser.Email);

                    transport.ReleaseRefresh(200, refreshed);
                    var refreshResult = refresh.GetAwaiter().GetResult();

                    Assert.IsTrue(refreshResult.IsSuccess);
                    Assert.AreEqual("access-two", client.Auth.CurrentSession.AccessToken);
                    Assert.AreEqual("changed@b.co", client.Auth.CurrentUser.Email,
                        "A refresh response must not restore the user snapshot from before an update.");
                }
            });
        }

        [Test]
        public void OlderSessionWriteCompletingLast_DoesNotOverwriteNewerSignIn()
        {
            RunWithoutUnitySynchronizationContext(delegate
            {
                var newer = SessionJson.Replace("access-one", "access-new")
                    .Replace("refresh-one", "refresh-new")
                    .Replace("user-1", "user-2");
                var store = new GatedSessionStore();
                var transport = new RecordingHttpTransport()
                    .Enqueue(200, SessionJson)
                    .Enqueue(200, newer);
                using (var client = NewClient(transport, store))
                {
                    store.GateNextMutation();
                    var olderSignIn = client.Auth.SignInWithPasswordAsync("old@b.co", "old-secret");
                    Assert.IsFalse(olderSignIn.IsCompleted);

                    var newerSignIn = client.Auth.SignInWithPasswordAsync("new@b.co", "new-secret");
                    store.ReleaseMutation();

                    var olderResult = olderSignIn.GetAwaiter().GetResult();
                    var newerResult = newerSignIn.GetAwaiter().GetResult();
                    Assert.IsFalse(olderResult.IsSuccess);
                    Assert.AreEqual("auth_operation_superseded", olderResult.Error.Code);
                    Assert.IsTrue(newerResult.IsSuccess);
                    Assert.AreEqual("access-new", client.Auth.CurrentSession.AccessToken);
                    Assert.AreEqual("access-new", StoredAccessToken(store),
                        "A restart must restore the newer sign-in, not the write that finished last.");
                }
            });
        }

        [Test]
        public void SignOutRemoval_DoesNotLoseToOlderSessionWrite()
        {
            RunWithoutUnitySynchronizationContext(delegate
            {
                var store = new GatedSessionStore();
                var transport = new RecordingHttpTransport()
                    .Enqueue(200, SessionJson)
                    .Enqueue(204, string.Empty);
                using (var client = NewClient(transport, store))
                {
                    store.GateNextMutation();
                    var signIn = client.Auth.SignInWithPasswordAsync("a@b.co", "secret");
                    Assert.IsFalse(signIn.IsCompleted);

                    var signOut = client.Auth.SignOutAsync(AuthSignOutScope.Local);
                    store.ReleaseMutation();

                    var signInResult = signIn.GetAwaiter().GetResult();
                    var signOutResult = signOut.GetAwaiter().GetResult();
                    Assert.IsFalse(signInResult.IsSuccess);
                    Assert.AreEqual("auth_operation_superseded", signInResult.Error.Code);
                    Assert.IsTrue(signOutResult.IsSuccess);
                    Assert.IsNull(client.Auth.CurrentSession);
                    Assert.IsNull(store.StoredValue,
                        "A completed sign-out must remain signed out after an older write finishes.");
                }
            });
        }

        [Test]
        public void FailedRefreshRemoval_DoesNotEraseNewerPersistedSignIn()
        {
            RunWithoutUnitySynchronizationContext(delegate
            {
                var newer = SessionJson.Replace("access-one", "access-new")
                    .Replace("refresh-one", "refresh-new")
                    .Replace("user-1", "user-2");
                var store = new GatedSessionStore();
                var transport = new RefreshGatedHttpTransport()
                    .Enqueue(200, SessionJson)
                    .Enqueue(200, newer);
                using (var client = NewClient(transport, store))
                {
                    client.Auth.SignInWithPasswordAsync("old@b.co", "old-secret")
                        .GetAwaiter().GetResult();
                    store.GateNextMutation();

                    var refresh = client.Auth.RefreshSessionAsync();
                    transport.ReleaseRefresh(401,
                        "{\"error_code\":\"refresh_token_already_used\",\"msg\":\"Invalid Refresh Token\"}");
                    Assert.IsFalse(refresh.IsCompleted);

                    var newerSignIn = client.Auth.SignInWithPasswordAsync("new@b.co", "new-secret");
                    store.ReleaseMutation();

                    var refreshResult = refresh.GetAwaiter().GetResult();
                    var newerResult = newerSignIn.GetAwaiter().GetResult();
                    Assert.IsFalse(refreshResult.IsSuccess);
                    Assert.IsTrue(newerResult.IsSuccess);
                    Assert.AreEqual("access-new", client.Auth.CurrentSession.AccessToken);
                    Assert.AreEqual("access-new", StoredAccessToken(store),
                        "An older failed refresh must not erase the newer persisted session.");
                }
            });
        }

        [Test]
        public void InitializeReadCompletingAfterSignIn_DoesNotRestoreOlderSession()
        {
            RunWithoutUnitySynchronizationContext(delegate
            {
                var newer = SessionJson.Replace("access-one", "access-new")
                    .Replace("refresh-one", "refresh-new")
                    .Replace("user-1", "user-2");
                var store = new GatedSessionStore();
                using (var seed = NewClient(new RecordingHttpTransport().Enqueue(200, SessionJson), store))
                    seed.Auth.SignInWithPasswordAsync("old@b.co", "old-secret").GetAwaiter().GetResult();

                store.GateNextGet();
                using (var client = NewClient(new RecordingHttpTransport().Enqueue(200, newer), store))
                {
                    var initialize = client.Auth.InitializeAsync();
                    Assert.IsFalse(initialize.IsCompleted);

                    var signIn = client.Auth.SignInWithPasswordAsync("new@b.co", "new-secret");
                    store.ReleaseGet();

                    Assert.IsTrue(initialize.GetAwaiter().GetResult().IsSuccess);
                    Assert.IsTrue(signIn.GetAwaiter().GetResult().IsSuccess);
                    Assert.AreEqual("access-new", client.Auth.CurrentSession.AccessToken,
                        "A delayed startup read must not replace a session adopted while it was pending.");
                    Assert.AreEqual("access-new", StoredAccessToken(store));
                }
            });
        }

        [Test]
        public void InitializeReadCompletingAfterSignOut_DoesNotRestoreRemovedSession()
        {
            RunWithoutUnitySynchronizationContext(delegate
            {
                var store = new GatedSessionStore();
                using (var seed = NewClient(new RecordingHttpTransport().Enqueue(200, SessionJson), store))
                    seed.Auth.SignInWithPasswordAsync("a@b.co", "secret").GetAwaiter().GetResult();

                store.GateNextGet();
                using (var client = NewClient(new RecordingHttpTransport(), store))
                {
                    var initialize = client.Auth.InitializeAsync();
                    Assert.IsFalse(initialize.IsCompleted);

                    var signOut = client.Auth.SignOutAsync(AuthSignOutScope.Local);
                    store.ReleaseGet();

                    Assert.IsTrue(initialize.GetAwaiter().GetResult().IsSuccess);
                    Assert.IsTrue(signOut.GetAwaiter().GetResult().IsSuccess);
                    Assert.IsNull(client.Auth.CurrentSession,
                        "A delayed startup read must not restore a session removed while it was pending.");
                    Assert.IsNull(store.StoredValue);
                }
            });
        }

        [Test]
        public void CancellationAfterSignInAdoption_DoesNotInterruptDurableCommit()
        {
            RunWithoutUnitySynchronizationContext(delegate
            {
                var store = new GatedSessionStore();
                var transport = new RecordingHttpTransport().Enqueue(200, SessionJson);
                using (var cancellation = new CancellationTokenSource())
                using (var client = NewClient(transport, store))
                {
                    store.GateNextMutation();
                    var signIn = client.Auth.SignInWithPasswordAsync(
                        "a@b.co", "secret", cancellation.Token);
                    Assert.IsFalse(signIn.IsCompleted);

                    cancellation.Cancel();
                    store.ReleaseMutation();

                    var result = signIn.GetAwaiter().GetResult();
                    Assert.IsTrue(result.IsSuccess,
                        "A session already accepted in memory must finish its durable commit.");
                    Assert.AreEqual("access-one", StoredAccessToken(store));
                }
            });
        }

        [Test]
        public void CancellationAfterSignOutAdoption_DoesNotInterruptDurableRemoval()
        {
            RunWithoutUnitySynchronizationContext(delegate
            {
                var store = new GatedSessionStore();
                var transport = new RecordingHttpTransport()
                    .Enqueue(200, SessionJson)
                    .Enqueue(204, string.Empty);
                using (var cancellation = new CancellationTokenSource())
                using (var client = NewClient(transport, store))
                {
                    client.Auth.SignInWithPasswordAsync("a@b.co", "secret")
                        .GetAwaiter().GetResult();
                    store.GateNextMutation();
                    var signOut = client.Auth.SignOutAsync(
                        AuthSignOutScope.Local, cancellation.Token);
                    Assert.IsFalse(signOut.IsCompleted);

                    cancellation.Cancel();
                    store.ReleaseMutation();

                    var result = signOut.GetAwaiter().GetResult();
                    Assert.IsTrue(result.IsSuccess,
                        "A sign-out already accepted in memory must finish its durable removal.");
                    Assert.IsNull(client.Auth.CurrentSession);
                    Assert.IsNull(store.StoredValue);
                }
            });
        }

        [UnityTest]
        public IEnumerator RefreshPersistenceCompletingAfterSignOut_DoesNotEmitStaleRefresh()
        {
            var refreshed = SessionJson.Replace("access-one", "access-two")
                .Replace("refresh-one", "refresh-two");
            var store = new GatedSessionStore();
            var transport = new RefreshGatedHttpTransport()
                .Enqueue(200, SessionJson)
                .Enqueue(204, string.Empty);
            using (var client = NewClient(transport, store))
            {
                var initialSignIn = client.Auth.SignInWithPasswordAsync("a@b.co", "secret");
                while (!initialSignIn.IsCompleted)
                    yield return null;
                Assert.IsTrue(initialSignIn.GetAwaiter().GetResult().IsSuccess);

                var tokenRefreshed = 0;
                var signedOut = 0;
                client.Auth.StateChanged += delegate(object sender, AuthStateChangedEventArgs args)
                {
                    if (args.Event == AuthChangeEvent.TokenRefreshed)
                        tokenRefreshed++;
                    if (args.Event == AuthChangeEvent.SignedOut)
                        signedOut++;
                };

                var refresh = client.Auth.RefreshSessionAsync();
                store.GateNextMutation();
                transport.ReleaseRefresh(200, refreshed);
                while (!store.GatedMutationStarted)
                    yield return null;
                Assert.IsFalse(refresh.IsCompleted);

                var signOut = client.Auth.SignOutAsync(AuthSignOutScope.Local);
                Assert.IsFalse(signOut.IsCompleted);
                store.ReleaseMutation();
                while (!refresh.IsCompleted || !signOut.IsCompleted)
                    yield return null;
                yield return null;

                var refreshResult = refresh.GetAwaiter().GetResult();
                var signOutResult = signOut.GetAwaiter().GetResult();
                Assert.IsFalse(refreshResult.IsSuccess);
                Assert.AreEqual("auth_operation_superseded", refreshResult.Error.Code);
                Assert.IsTrue(signOutResult.IsSuccess);
                Assert.AreEqual(0, tokenRefreshed,
                    "A refresh superseded during persistence must not emit a stale event.");
                Assert.AreEqual(1, signedOut);
                Assert.IsNull(client.Auth.CurrentSession);
                Assert.IsNull(store.StoredValue);
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
                Assert.AreEqual("pkce_verifier_missing", result.Error.Code);
            }
        }

        [Test]
        public void OAuthCallbackError_PreservesStandardCodeAndDescription()
        {
            using (var client = NewClient(new RecordingHttpTransport()))
            {
                var callback = new Uri(
                    "mygame://auth#error=access_denied&error_description=Player%20cancelled");

                var result = client.Auth.HandleAuthCallbackAsync(callback)
                    .GetAwaiter().GetResult();

                Assert.IsFalse(result.IsSuccess);
                Assert.AreEqual("access_denied", result.Error.Code);
                Assert.AreEqual("Player cancelled", result.Error.Message);
            }
        }

        private static string FindPkceKey(MemorySessionStore store)
        {
            foreach (var key in store.Keys)
                if (key.EndsWith(".pkce", StringComparison.Ordinal))
                    return key;
            throw new AssertionException("No PKCE key was written to the store.");
        }

        private static string StoredAccessToken(GatedSessionStore store)
        {
            return store.StoredValue == null
                ? null
                : (string)JObject.Parse(store.StoredValue)["access_token"];
        }

        private static void RunWithoutUnitySynchronizationContext(Action action)
        {
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try { action(); }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        }
    }
}
