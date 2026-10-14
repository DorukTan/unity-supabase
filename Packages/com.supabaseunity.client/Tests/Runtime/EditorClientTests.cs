using System;
using System.Collections;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Supabase.Unity.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Supabase.Unity.Tests
{
    public sealed class EditorClientTests
    {
        [SupabaseTable("scores")]
        private sealed class Score
        {
            [SupabaseColumn("score")] public int Value { get; set; }
        }

        private sealed class InlineProgress : IProgress<float>
        {
            internal float Last = -1f;
            internal Action Reported;
            public void Report(float value)
            {
                Last = value;
                if (Reported != null) Reported();
            }
        }

        [Test]
        public void Create_CopiesConnectionOptionsAndIsolatesSessionState()
        {
            var count = SupabaseEditorClient.ActiveClientCount;
            var hosts = Resources.FindObjectsOfTypeAll<SupabaseRuntimeHost>().Length;
            var options = EditorOptions();
            options.PersistSession = true;
            options.AutoConnectRealtime = true;
            options.AutoRefreshToken = true;
            options.Headers.Add("x-editor-test", "copied");
            using (var client = SupabaseEditorClient.Create(options))
            {
                Assert.AreNotSame(options, client.Options);
                Assert.AreNotSame(options.Endpoints, client.Options.Endpoints);
                Assert.IsTrue(options.PersistSession);
                Assert.IsTrue(options.AutoConnectRealtime);
                Assert.IsTrue(options.AutoRefreshToken);
                Assert.IsFalse(client.Options.PersistSession);
                Assert.IsFalse(client.Options.AutoRefreshToken);
                Assert.IsFalse(client.Options.AutoConnectRealtime);
                Assert.IsInstanceOf<MemorySessionStore>(client.Options.SessionStore);
                Assert.IsInstanceOf<MemorySessionStore>(client.Options.PkceStore);
                Assert.AreEqual("copied", client.Options.Headers["x-editor-test"]);
                client.Options.Headers["x-editor-test"] = "changed";
                Assert.AreEqual("copied", options.Headers["x-editor-test"]);
                // Editor clients retain manual refresh even if the public options are changed later.
                client.Options.AutoRefreshToken = true;
                Assert.IsTrue(client.InitializeAsync().GetAwaiter().GetResult().IsSuccess);
                Assert.AreEqual(hosts, Resources.FindObjectsOfTypeAll<SupabaseRuntimeHost>().Length);
                Assert.AreEqual(count + 1, SupabaseEditorClient.ActiveClientCount);
            }
            Assert.AreEqual(count, SupabaseEditorClient.ActiveClientCount);
        }

        [Test]
        public void Create_RejectsCustomTransportWithoutTakingOwnership()
        {
            var transport = new RecordingHttpTransport();
            var options = EditorOptions();
            options.HttpTransport = transport;
            Assert.Throws<ArgumentException>(delegate { SupabaseEditorClient.Create(options); });
            Assert.AreSame(transport, options.HttpTransport);
        }

        [Test]
        public void InvalidConfiguration_DoesNotRegisterClient()
        {
            var count = SupabaseEditorClient.ActiveClientCount;
            Assert.Throws<ArgumentNullException>(delegate
            {
                SupabaseEditorClient.Create((SupabaseClientOptions)null);
            });
            Assert.Throws<SupabaseConfigurationException>(delegate
            {
                SupabaseEditorClient.Create("https://example.supabase.co", "sb_secret_test");
            });
            Assert.AreEqual(count, SupabaseEditorClient.ActiveClientCount);
        }

        [Test]
        public void UnsupportedBackgroundFlows_DoNotCreateRuntimeHost()
        {
            var hosts = Resources.FindObjectsOfTypeAll<SupabaseRuntimeHost>().Length;
            using (var client = SupabaseEditorClient.Create(EditorOptions()))
            {
                var realtime = client.Realtime.ConnectAsync().GetAwaiter().GetResult();
                Assert.AreEqual(SupabaseErrorKind.UnsupportedPlatform, realtime.Error.Kind);
                var oauth = client.Auth.SignInWithOAuthAsync("github", new AuthOAuthOptions { OpenBrowser = true });
                Assert.IsTrue(oauth.IsFaulted);
                Assert.IsInstanceOf<PlatformNotSupportedException>(oauth.Exception.GetBaseException());
                Assert.AreEqual(hosts, Resources.FindObjectsOfTypeAll<SupabaseRuntimeHost>().Length);
            }
        }

        [UnityTest]
        public IEnumerator ClientHttpCalls_CompleteOutsidePlayModeWithAuthNotifications()
        {
            Assert.IsFalse(Application.isPlaying);
            var hosts = Resources.FindObjectsOfTypeAll<SupabaseRuntimeHost>().Length;
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            var notifications = 0;
            using (var fixture = new LoopbackSupabaseFixture(delegate(ContractHttpRequest request)
            {
                if (request.Target.StartsWith("/auth/v1/token?", StringComparison.Ordinal))
                    return ContractHttpResponse.Json(200, AuthTests.SessionJson);
                if (request.Target.StartsWith("/rest/v1/scores?", StringComparison.Ordinal))
                    return ContractHttpResponse.Json(200, "[{\"score\":42}]");
                if (request.Target == "/storage/v1/object/avatars/probe.bin")
                    return ContractHttpResponse.Json(200, "{\"path\":\"probe.bin\"}");
                return ContractHttpResponse.Json(200, "{\"accepted\":true}");
            }))
            using (var client = SupabaseEditorClient.Create(Options(fixture)))
            {
                client.Auth.StateChanged += delegate
                {
                    Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId);
                    notifications++;
                };
                var initialized = client.InitializeAsync();
                yield return Wait(initialized);
                Assert.IsTrue(initialized.Result.IsSuccess);
                var auth = client.Auth.SignInWithPasswordAsync("editor@example.com", "password");
                yield return Wait(auth);
                Assert.IsTrue(auth.Result.IsSuccess);
                Assert.AreEqual(2, notifications);
                var rows = client.From<Score>().GetAsync();
                yield return Wait(rows);
                Assert.IsTrue(rows.Result.IsSuccess);
                Assert.AreEqual(42, rows.Result.Data[0].Value);
                var upload = client.Storage.From("avatars").UploadAsync("probe.bin", new byte[] { 1, 2, 3 });
                yield return Wait(upload);
                Assert.IsTrue(upload.Result.IsSuccess);
                var function = client.Functions.InvokeAsync("health");
                yield return Wait(function);
                Assert.IsTrue(function.Result.IsSuccess);
                fixture.ThrowIfFaulted();
                Assert.AreEqual(4, fixture.Requests.Count);
                Assert.AreEqual("sb_publishable_test-value", fixture.Requests[0].Headers["apikey"]);
                Assert.IsFalse(fixture.Requests[0].Headers.ContainsKey("Authorization"));
                for (var i = 1; i < 4; i++)
                    Assert.AreEqual("Bearer access-one", fixture.Requests[i].Headers["Authorization"]);
                Assert.AreEqual(hosts, Resources.FindObjectsOfTypeAll<SupabaseRuntimeHost>().Length);
            }
        }

        [UnityTest]
        public IEnumerator Transport_PreservesBodyHeadersProgressAndDownloadChunks()
        {
            using (var fixture = new LoopbackSupabaseFixture(delegate
            {
                var response = ContractHttpResponse.Json(201, "{\"created\":true}");
                response.Headers.Add("X-Editor-Response", "received");
                return response;
            }))
            using (var transport = new EditorHttpTransport())
            {
                var upload = new InlineProgress();
                var download = new InlineProgress();
                var chunks = new System.Collections.Generic.List<byte>();
                var request = new SupabaseHttpRequest
                {
                    Uri = new Uri(fixture.ProjectUrl, "/probe"),
                    Method = SupabaseHttpMethod.Patch,
                    ContentType = "application/json",
                    Body = Encoding.UTF8.GetBytes("{\"score\":42}"),
                    UploadProgress = upload,
                    DownloadProgress = download,
                    DownloadChunk = delegate(byte[] chunk) { chunks.AddRange(chunk); }
                };
                request.Headers.Add("x-editor-request", "sent");
                var task = transport.SendAsync(request);
                yield return Wait(task);
                Assert.AreEqual(201, task.Result.StatusCode);
                Assert.AreEqual("received", task.Result.Headers["X-Editor-Response"]);
                Assert.AreEqual("{\"created\":true}", task.Result.Text);
                CollectionAssert.AreEqual(task.Result.Body, chunks);
                Assert.AreEqual(1f, upload.Last);
                Assert.AreEqual(1f, download.Last);
                Assert.AreEqual(0, transport.ActiveRequestCount);
                fixture.ThrowIfFaulted();
                Assert.AreEqual("PATCH", fixture.Requests[0].Method);
                Assert.AreEqual("sent", fixture.Requests[0].Headers["x-editor-request"]);
                Assert.AreEqual("application/json", fixture.Requests[0].Headers["Content-Type"]);
                Assert.AreEqual("{\"score\":42}", fixture.Requests[0].Text);
            }
        }

        [UnityTest]
        public IEnumerator HttpFailure_UsesServiceErrorAndRedactsResponse()
        {
            using (var fixture = new LoopbackSupabaseFixture(delegate
            {
                return ContractHttpResponse.Json(500,
                    "{\"message\":\"failed\",\"access_token\":\"editor-access-token\"}");
            }))
            using (var client = SupabaseEditorClient.Create(Options(fixture)))
            {
                var task = client.Functions.InvokeAsync("health");
                yield return Wait(task);
                Assert.IsFalse(task.Result.IsSuccess);
                Assert.AreEqual(SupabaseErrorKind.Http, task.Result.Error.Kind);
                Assert.AreEqual(500, task.Result.Error.StatusCode);
                StringAssert.DoesNotContain("editor-access-token", task.Result.Error.RawResponse);
                fixture.ThrowIfFaulted();
            }
        }

        [UnityTest]
        public IEnumerator Transport_ReportsConnectionFailure()
        {
            Uri unavailable;
            using (var fixture = new LoopbackSupabaseFixture(delegate { return ContractHttpResponse.Json(200, "{}"); }))
                unavailable = fixture.ProjectUrl;
            using (var transport = new EditorHttpTransport())
            {
                var task = transport.SendAsync(new SupabaseHttpRequest { Uri = unavailable });
                yield return Wait(task);
                Assert.AreEqual(0, task.Result.StatusCode);
                Assert.IsNotEmpty(task.Result.TransportError);
                Assert.AreEqual(0, transport.ActiveRequestCount);
            }
        }

        [UnityTest]
        public IEnumerator Transport_ReportsTimeout()
        {
            using (var fixture = new LoopbackSupabaseFixture(delegate
            {
                Thread.Sleep(1600);
                return ContractHttpResponse.Json(200, "{}");
            }))
            using (var transport = new EditorHttpTransport())
            {
                var task = transport.SendAsync(new SupabaseHttpRequest
                {
                    Uri = fixture.ProjectUrl,
                    Timeout = TimeSpan.FromSeconds(1)
                });
                yield return Wait(task);
                Assert.IsTrue(task.Result.TimedOut, task.Result.TransportError);
                Assert.AreEqual(0, transport.ActiveRequestCount);
            }
        }

        [Test]
        public void PreCancelledRequest_DoesNotStartNetworkWork()
        {
            using (var transport = new EditorHttpTransport())
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                var task = transport.SendAsync(new SupabaseHttpRequest { Uri = new Uri("http://127.0.0.1:1") },
                    cancellation.Token);
                Assert.IsTrue(task.IsCanceled);
                Assert.AreEqual(0, transport.ActiveRequestCount);
            }
        }

        [UnityTest]
        public IEnumerator Cancellation_AbortsActiveRequest()
        {
            using (var release = new ManualResetEventSlim())
            using (var fixture = BlockingFixture(release))
            using (var transport = new EditorHttpTransport())
            using (var cancellation = new CancellationTokenSource())
            {
                try
                {
                    var task = transport.SendAsync(new SupabaseHttpRequest { Uri = fixture.ProjectUrl }, cancellation.Token);
                    yield return WaitForRequest(fixture);
                    cancellation.Cancel();
                    yield return Wait(task);
                    Assert.IsTrue(task.IsCanceled);
                    Assert.AreEqual(0, transport.ActiveRequestCount);
                }
                finally { release.Set(); }
            }
        }

        [UnityTest]
        public IEnumerator ClientDisposal_CancelsAllRequests()
        {
            yield return CheckLifecycleCleanup(delegate(SupabaseClient client) { client.Dispose(); });
        }

        [UnityTest]
        public IEnumerator ReloadCleanup_CancelsAllRequests()
        {
            yield return CheckLifecycleCleanup(delegate { SupabaseEditorClient.DisposeAll(); });
        }

        [UnityTest]
        public IEnumerator LeavingEditMode_CancelsAllRequests()
        {
            yield return CheckLifecycleCleanup(delegate
            {
                SupabaseEditorClient.OnPlayModeStateChanged(PlayModeStateChange.ExitingEditMode);
            });
        }

        [UnityTest]
        public IEnumerator ProgressCallback_CanDisposeTransport()
        {
            using (var release = new ManualResetEventSlim())
            using (var fixture = BlockingFixture(release))
            using (var transport = new EditorHttpTransport())
            {
                try
                {
                    var progress = new InlineProgress { Reported = transport.Dispose };
                    var task = transport.SendAsync(new SupabaseHttpRequest
                    {
                        Uri = fixture.ProjectUrl,
                        UploadProgress = progress
                    });
                    yield return Wait(task);
                    Assert.IsTrue(task.IsCanceled);
                    Assert.AreEqual(0, transport.ActiveRequestCount);
                }
                finally { release.Set(); }
            }
        }

        [UnityTest]
        public IEnumerator WorkerThreadCalls_FailBeforeUsingUnityApis()
        {
            using (var transport = new EditorHttpTransport())
            {
                var task = Task.Run(delegate
                {
                    Assert.Throws<InvalidOperationException>(delegate
                    {
                        SupabaseEditorClient.Create("https://example.supabase.co", "sb_publishable_test-value");
                    });
                    Assert.Throws<InvalidOperationException>(delegate
                    {
                        transport.SendAsync(new SupabaseHttpRequest { Uri = new Uri("http://127.0.0.1:1") });
                    });
                });
                yield return Wait(task);
                task.GetAwaiter().GetResult();
            }
        }

        [Test]
        public void RepeatedCreateDispose_RemovesClientRegistrations()
        {
            var count = SupabaseEditorClient.ActiveClientCount;
            for (var i = 0; i < 20; i++)
            {
                var client = SupabaseEditorClient.Create(EditorOptions());
                client.InitializeAsync().GetAwaiter().GetResult();
                client.Dispose();
                client.Dispose();
                Assert.AreEqual(count, SupabaseEditorClient.ActiveClientCount);
            }
        }

        private static IEnumerator CheckLifecycleCleanup(Action<SupabaseClient> cleanup)
        {
            var count = SupabaseEditorClient.ActiveClientCount;
            using (var release = new ManualResetEventSlim())
            using (var fixture = BlockingFixture(release))
            using (var client = SupabaseEditorClient.Create(Options(fixture)))
            {
                try
                {
                    var first = client.Auth.SignInWithPasswordAsync("editor@example.com", "password");
                    var second = client.Functions.InvokeAsync("second");
                    yield return WaitForRequest(fixture);
                    cleanup(client);
                    yield return Wait(first);
                    yield return Wait(second);
                    Assert.IsTrue(first.IsCanceled);
                    Assert.IsTrue(second.IsCanceled);
                    Assert.IsNull(client.Auth.CurrentSession);
                    Assert.AreEqual(0, ((EditorHttpTransport)client.Options.HttpTransport).ActiveRequestCount);
                    Assert.AreEqual(count, SupabaseEditorClient.ActiveClientCount);
                    Assert.Throws<ObjectDisposedException>(delegate { client.From<Score>(); });
                }
                finally { release.Set(); }
            }
        }

        private static SupabaseClientOptions EditorOptions()
        {
            var options = ConfigurationTests.ValidOptions();
            options.AuthCallbackProvider = null;
            return options;
        }

        private static SupabaseClientOptions Options(LoopbackSupabaseFixture fixture)
        {
            var options = EditorOptions();
            options.ProjectUrl = fixture.ProjectUrl.AbsoluteUri;
            return options;
        }

        private static LoopbackSupabaseFixture BlockingFixture(ManualResetEventSlim release)
        {
            return new LoopbackSupabaseFixture(delegate
            {
                release.Wait(TimeSpan.FromSeconds(10));
                return ContractHttpResponse.Json(200, "{}");
            });
        }

        private static IEnumerator Wait(Task task)
        {
            var deadline = EditorApplication.timeSinceStartup + 10d;
            while (!task.IsCompleted)
            {
                Assert.Less(EditorApplication.timeSinceStartup, deadline, "Editor request did not settle.");
                yield return null;
            }
        }

        private static IEnumerator WaitForRequest(LoopbackSupabaseFixture fixture)
        {
            var deadline = EditorApplication.timeSinceStartup + 10d;
            while (fixture.Requests.Count == 0)
            {
                Assert.Less(EditorApplication.timeSinceStartup, deadline, "Fixture did not receive the request.");
                yield return null;
            }
        }
    }
}
