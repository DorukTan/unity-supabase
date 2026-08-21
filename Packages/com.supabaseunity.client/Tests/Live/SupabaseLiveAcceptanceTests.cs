using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Supabase.Unity.LiveTests
{
    public sealed class SupabaseLiveAcceptanceTests
    {
        [SupabaseTable("unity_acceptance_scores")]
        private sealed class AcceptanceScore
        {
            [SupabaseColumn("id")] public string Id { get; set; }
            [SupabaseColumn("user_id")] public string UserId { get; set; }
            [SupabaseColumn("run_id")] public string RunId { get; set; }
            [SupabaseColumn("score")] public int Score { get; set; }
        }

        private sealed class FunctionAcknowledgement
        {
            public bool accepted { get; set; }
            public string run_id { get; set; }
        }

        [UnityTest]
        public IEnumerator AuthDatabaseRealtimeStorageAndFunctions_WorkTogether()
        {
            var projectUrl = Environment.GetEnvironmentVariable("SUPABASE_TEST_URL");
            var publishableKey = Environment.GetEnvironmentVariable("SUPABASE_TEST_PUBLISHABLE_KEY");
            if (string.IsNullOrWhiteSpace(projectUrl) || string.IsNullOrWhiteSpace(publishableKey))
                Assert.Ignore("Set SUPABASE_TEST_URL and SUPABASE_TEST_PUBLISHABLE_KEY to run live acceptance.");

            var email = Environment.GetEnvironmentVariable("SUPABASE_TEST_EMAIL");
            var password = Environment.GetEnvironmentVariable("SUPABASE_TEST_PASSWORD");
            if (string.IsNullOrWhiteSpace(email)) email = "unity-acceptance@example.test";
            if (string.IsNullOrWhiteSpace(password)) password = "Unity-Acceptance-123!";

            var runId = Guid.NewGuid().ToString("N");
            var options = new SupabaseClientOptions
            {
                ProjectUrl = projectUrl,
                PublishableKey = publishableKey,
                AutoRefreshToken = false,
                PersistSession = false
            };

            using (var client = new SupabaseClient(options))
            {
                SupabaseResult<AuthResponse> signUp = null;
                yield return Await(client.Auth.SignUpWithPasswordAsync(email, password),
                    delegate(SupabaseResult<AuthResponse> result) { signUp = result; });
                SupabaseResult<AuthSession> auth = null;
                if (signUp.IsSuccess && client.Auth.CurrentSession != null)
                    auth = SupabaseResult<AuthSession>.Success(client.Auth.CurrentSession, signUp.Metadata);
                else
                {
                    yield return Await(client.Auth.SignInWithPasswordAsync(email, password),
                        delegate(SupabaseResult<AuthSession> result) { auth = result; });
                }
                AssertSuccess(auth, "Auth");
                Assert.IsNotNull(auth.Data.User);
                Assert.IsFalse(string.IsNullOrWhiteSpace(auth.Data.User.Id));

                RealtimePostgresChange observedChange = null;
                RealtimeSystemMessage postgresStatus = null;
                var channel = client.Realtime.Channel("acceptance:" + runId)
                    .OnPostgresChanges(new RealtimePostgresChangeFilter
                    {
                        Event = RealtimePostgresEvent.Insert,
                        Schema = "public",
                        Table = "unity_acceptance_scores",
                        Filter = "run_id=eq." + runId
                    }, delegate(RealtimePostgresChange change)
                    {
                        if (change.NewRecord != null &&
                            string.Equals((string)change.NewRecord["run_id"], runId,
                                StringComparison.Ordinal))
                            observedChange = change;
                    });
                channel.SystemMessageReceived += delegate(RealtimeSystemMessage message)
                {
                    if (string.Equals(message.Extension, "postgres_changes",
                            StringComparison.OrdinalIgnoreCase))
                        postgresStatus = message;
                };

                SupabaseResult subscribed = null;
                yield return Await(channel.SubscribeAsync(),
                    delegate(SupabaseResult result) { subscribed = result; });
                AssertSuccess(subscribed, "Realtime subscribe");

                var subscriptionDeadline = Time.realtimeSinceStartup + 15f;
                while ((postgresStatus == null ||
                        !string.Equals(postgresStatus.Status, "ok", StringComparison.OrdinalIgnoreCase)) &&
                       Time.realtimeSinceStartup < subscriptionDeadline)
                    yield return null;
                Assert.IsNotNull(postgresStatus, "Realtime did not report its Postgres subscription status.");
                Assert.AreEqual("ok", postgresStatus.Status,
                    "Realtime Postgres subscription failed: " + postgresStatus.Message);

                SupabaseResult<IReadOnlyList<AcceptanceScore>> inserted = null;
                yield return Await(client.From<AcceptanceScore>().InsertAsync(new AcceptanceScore
                {
                    RunId = runId,
                    Score = 41
                }), delegate(SupabaseResult<IReadOnlyList<AcceptanceScore>> result) { inserted = result; });
                AssertSuccess(inserted, "Database insert");
                Assert.AreEqual(1, inserted.Data.Count);
                Assert.AreEqual(auth.Data.User.Id, inserted.Data[0].UserId);

                var realtimeDeadline = Time.realtimeSinceStartup + 10f;
                while (observedChange == null && Time.realtimeSinceStartup < realtimeDeadline)
                    yield return null;
                Assert.IsNotNull(observedChange, "Realtime did not deliver the inserted row.");

                SupabaseResult<AcceptanceScore> selected = null;
                yield return Await(client.From<AcceptanceScore>().Eq("run_id", runId).SingleAsync(),
                    delegate(SupabaseResult<AcceptanceScore> result) { selected = result; });
                AssertSuccess(selected, "Database select");
                Assert.AreEqual(41, selected.Data.Score);

                SupabaseResult<IReadOnlyList<AcceptanceScore>> updated = null;
                yield return Await(client.From<AcceptanceScore>().Eq("run_id", runId)
                    .UpdateAsync(new { score = 42 }),
                    delegate(SupabaseResult<IReadOnlyList<AcceptanceScore>> result) { updated = result; });
                AssertSuccess(updated, "Database update");
                Assert.AreEqual(42, updated.Data[0].Score);

                var objectPath = auth.Data.User.Id + "/" + runId + ".txt";
                var expectedBytes = Encoding.UTF8.GetBytes("unity-supabase-live-acceptance");
                var bucket = client.Storage.From("unity-acceptance");
                SupabaseResult<StorageFileResult> uploaded = null;
                yield return Await(bucket.UploadAsync(objectPath, expectedBytes,
                    new StorageUploadOptions
                    {
                        ContentType = "text/plain",
                        CacheControl = "60",
                        Upsert = true
                    }), delegate(SupabaseResult<StorageFileResult> result) { uploaded = result; });
                AssertSuccess(uploaded, "Storage upload");

                SupabaseResult<byte[]> downloaded = null;
                yield return Await(bucket.DownloadAsync(objectPath),
                    delegate(SupabaseResult<byte[]> result) { downloaded = result; });
                AssertSuccess(downloaded, "Storage download");
                CollectionAssert.AreEqual(expectedBytes, downloaded.Data);

                SupabaseResult<FunctionAcknowledgement> invoked = null;
                yield return Await(client.Functions.InvokeAsync<FunctionAcknowledgement>(
                    "unity-acceptance", new FunctionInvokeOptions { Body = new { run_id = runId } }),
                    delegate(SupabaseResult<FunctionAcknowledgement> result) { invoked = result; });
                AssertSuccess(invoked, "Edge Function");
                Assert.IsTrue(invoked.Data.accepted);
                Assert.AreEqual(runId, invoked.Data.run_id);

                SupabaseResult<IReadOnlyList<StorageObject>> removedObject = null;
                yield return Await(bucket.RemoveAsync(new[] { objectPath }),
                    delegate(SupabaseResult<IReadOnlyList<StorageObject>> result) { removedObject = result; });
                AssertSuccess(removedObject, "Storage cleanup");

                SupabaseResult<IReadOnlyList<AcceptanceScore>> removedRow = null;
                yield return Await(client.From<AcceptanceScore>().Eq("run_id", runId).DeleteAsync(),
                    delegate(SupabaseResult<IReadOnlyList<AcceptanceScore>> result) { removedRow = result; });
                AssertSuccess(removedRow, "Database cleanup");

                SupabaseResult unsubscribed = null;
                yield return Await(channel.UnsubscribeAsync(),
                    delegate(SupabaseResult result) { unsubscribed = result; });
                AssertSuccess(unsubscribed, "Realtime unsubscribe");
            }
        }

        private static IEnumerator Await<T>(Task<T> task, Action<T> completed)
        {
            while (!task.IsCompleted)
                yield return null;
            if (task.IsCanceled)
                throw new OperationCanceledException("The live acceptance operation was cancelled.");
            if (task.IsFaulted)
                throw task.Exception == null
                    ? new InvalidOperationException("The live acceptance operation failed.")
                    : task.Exception.GetBaseException();
            completed(task.Result);
        }

        private static void AssertSuccess<T>(SupabaseResult<T> result, string operation)
        {
            Assert.IsNotNull(result, operation + " returned no result.");
            Assert.IsTrue(result.IsSuccess,
                operation + " failed: " + (result.Error == null ? "Unknown error." : result.Error.ToString()));
        }

        private static void AssertSuccess(SupabaseResult result, string operation)
        {
            Assert.IsNotNull(result, operation + " returned no result.");
            Assert.IsTrue(result.IsSuccess,
                operation + " failed: " + (result.Error == null ? "Unknown error." : result.Error.ToString()));
        }
    }
}
