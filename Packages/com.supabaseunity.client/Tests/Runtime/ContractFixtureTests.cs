using System;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Supabase.Unity.Tests
{
    public sealed class ContractFixtureTests
    {
        [SupabaseTable("scores")]
        private sealed class Score
        {
            [SupabaseColumn("id")] public int Id { get; set; }
            [SupabaseColumn("score")] public int Value { get; set; }
        }

        private sealed class FunctionAcknowledgement
        {
            public bool accepted { get; set; }
        }

        [Test]
        public void HttpBoundary_MatchesSupportedServiceContracts()
        {
            using (var fixture = new LoopbackSupabaseFixture(Respond))
            {
                var options = ConfigurationTests.ValidOptions();
                options.ProjectUrl = fixture.ProjectUrl.AbsoluteUri;
                options.HttpTransport = new LoopbackHttpTransport();
                var previousContext = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(null);
                try
                {
                    using (var client = new SupabaseClient(options))
                    {
                        var auth = client.Auth.SignInWithPasswordAsync("starter@example.com", "secret")
                            .GetAwaiter().GetResult();
                        Assert.IsTrue(auth.IsSuccess, auth.Error == null ? null : auth.Error.Message);

                        var rows = client.From<Score>().Eq("id", 7).GetAsync().GetAwaiter().GetResult();
                        Assert.IsTrue(rows.IsSuccess, rows.Error == null ? null : rows.Error.Message);
                        Assert.AreEqual(1, rows.Data.Count);
                        Assert.AreEqual(42, rows.Data[0].Value);
                        Assert.AreEqual(1, rows.Metadata.Count);

                        var upload = client.Storage.From("avatars").UploadAsync("players/7.bin",
                            new byte[] { 1, 2, 3, 4 }, new StorageUploadOptions
                            {
                                ContentType = "application/octet-stream",
                                CacheControl = "60",
                                Upsert = true
                            }).GetAwaiter().GetResult();
                        Assert.IsTrue(upload.IsSuccess,
                            upload.Error == null ? null : upload.Error.Message);
                        Assert.AreEqual("avatars/players/7.bin", upload.Data.FullPath);

                        var invoked = client.Functions.InvokeAsync<FunctionAcknowledgement>("health",
                            new FunctionInvokeOptions { Body = new { player_id = 7 } })
                            .GetAwaiter().GetResult();
                        Assert.IsTrue(invoked.IsSuccess,
                            invoked.Error == null ? null : invoked.Error.Message);
                        Assert.IsTrue(invoked.Data.accepted);
                    }
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                }

                fixture.ThrowIfFaulted();
                AssertRequests(fixture.Requests);
            }
        }

        private static ContractHttpResponse Respond(ContractHttpRequest request)
        {
            if (request.Target.StartsWith("/auth/v1/token?", StringComparison.Ordinal))
                return ContractHttpResponse.Json(200, AuthTests.SessionJson);
            if (request.Target.StartsWith("/rest/v1/scores?", StringComparison.Ordinal))
            {
                var response = ContractHttpResponse.Json(200, "[{\"id\":7,\"score\":42}]");
                response.Headers["Content-Range"] = "0-0/1";
                return response;
            }
            if (request.Target == "/storage/v1/object/avatars/players/7.bin")
                return ContractHttpResponse.Json(200,
                    "{\"id\":\"file-1\",\"path\":\"players/7.bin\",\"fullPath\":\"avatars/players/7.bin\"}");
            if (request.Target == "/functions/v1/health")
                return ContractHttpResponse.Json(200, "{\"accepted\":true}");
            return ContractHttpResponse.Json(404, "{\"message\":\"Unexpected contract route.\"}");
        }

        private static void AssertRequests(System.Collections.Generic.IReadOnlyList<ContractHttpRequest> requests)
        {
            Assert.AreEqual(4, requests.Count);

            var auth = requests[0];
            Assert.AreEqual("POST", auth.Method);
            Assert.AreEqual("/auth/v1/token?grant_type=password",
                Uri.UnescapeDataString(auth.Target));
            AssertCommonHeaders(auth);
            Assert.IsFalse(auth.Headers.ContainsKey("Authorization"));
            var authBody = JObject.Parse(auth.Text);
            Assert.AreEqual("starter@example.com", (string)authBody["email"]);
            Assert.AreEqual("secret", (string)authBody["password"]);

            var database = requests[1];
            Assert.AreEqual("GET", database.Method);
            Assert.AreEqual("/rest/v1/scores?select=*&id=eq.7",
                Uri.UnescapeDataString(database.Target));
            AssertCommonAuthenticatedHeaders(database);
            Assert.AreEqual("public", database.Headers["Accept-Profile"]);

            var storage = requests[2];
            Assert.AreEqual("POST", storage.Method);
            Assert.AreEqual("/storage/v1/object/avatars/players/7.bin", storage.Target);
            AssertCommonAuthenticatedHeaders(storage);
            Assert.AreEqual("application/octet-stream", storage.Headers["Content-Type"]);
            Assert.AreEqual("max-age=60", storage.Headers["cache-control"]);
            Assert.AreEqual("true", storage.Headers["x-upsert"]);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, storage.Body);

            var function = requests[3];
            Assert.AreEqual("POST", function.Method);
            Assert.AreEqual("/functions/v1/health", function.Target);
            AssertCommonAuthenticatedHeaders(function);
            Assert.AreEqual(7, (int)JObject.Parse(function.Text)["player_id"]);
        }

        private static void AssertCommonHeaders(ContractHttpRequest request)
        {
            Assert.AreEqual("sb_publishable_test-value", request.Headers["apikey"]);
            Assert.AreEqual(SupabaseHttp.ClientInfo, request.Headers["X-Client-Info"]);
        }

        private static void AssertCommonAuthenticatedHeaders(ContractHttpRequest request)
        {
            AssertCommonHeaders(request);
            Assert.AreEqual("Bearer access-one", request.Headers["Authorization"]);
        }
    }
}
