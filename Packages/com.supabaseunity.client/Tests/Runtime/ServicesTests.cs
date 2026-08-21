using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Supabase.Unity.Tests
{
    public sealed class ServicesTests
    {
        private sealed class FunctionValue { public string hello { get; set; } }

        [SupabaseTable("scores")]
        private sealed class ReliabilityRow
        {
            [SupabaseColumn("id")] public int Id { get; set; }
        }

        [Test]
        public void Function_UsesSelectedMethodAndDeserializes()
        {
            var transport = new RecordingHttpTransport
            {
                Response = delegate { return new SupabaseHttpResponse
                {
                    StatusCode = 200, Body = Encoding.UTF8.GetBytes("{\"hello\":\"unity\"}")
                }; }
            };
            var options = ConfigurationTests.ValidOptions();
            options.HttpTransport = transport;
            using (var client = new SupabaseClient(options))
            {
                var result = client.Functions.InvokeAsync<FunctionValue>("hello", new FunctionInvokeOptions
                {
                    Method = SupabaseHttpMethod.Put, Body = new { name = "Unity" }
                }).GetAwaiter().GetResult();
                Assert.IsTrue(result.IsSuccess);
                Assert.AreEqual("unity", result.Data.hello);
                Assert.AreEqual(SupabaseHttpMethod.Put, transport.LastRequest.Method);
            }
        }

        [Test]
        public void HttpServices_MapThrownTransportFailuresConsistently()
        {
            const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.signature";
            var options = ConfigurationTests.ValidOptions();
            options.HttpTransport = new ThrowingHttpTransport(new InvalidOperationException(
                "network unavailable with Bearer " + jwt + " and sb_secret_server-value"));
            using (var client = new SupabaseClient(options))
            {
                var auth = client.Auth.SignInWithPasswordAsync("starter@example.com", "secret")
                    .GetAwaiter().GetResult();
                var database = client.From<ReliabilityRow>().GetAsync().GetAwaiter().GetResult();
                var storage = client.Storage.ListBucketsAsync().GetAwaiter().GetResult();
                var function = client.Functions.InvokeAsync("health").GetAwaiter().GetResult();

                AssertTransportError(auth.Error, SupabaseService.Auth);
                AssertTransportError(database.Error, SupabaseService.Database);
                AssertTransportError(storage.Error, SupabaseService.Storage);
                AssertTransportError(function.Error, SupabaseService.Functions);
            }
        }

        [Test]
        public void HttpServices_MapThrownTimeoutsWithoutSwallowingCancellation()
        {
            var timeoutOptions = ConfigurationTests.ValidOptions();
            timeoutOptions.HttpTransport = new ThrowingHttpTransport(new TimeoutException());
            using (var client = new SupabaseClient(timeoutOptions))
            {
                var result = client.Functions.InvokeAsync("health").GetAwaiter().GetResult();
                Assert.IsFalse(result.IsSuccess);
                Assert.AreEqual(SupabaseErrorKind.Timeout, result.Error.Kind);
                Assert.IsTrue(result.Error.IsRetryable);
            }

            var cancellationOptions = ConfigurationTests.ValidOptions();
            cancellationOptions.HttpTransport = new ThrowingHttpTransport(
                new OperationCanceledException());
            using (var client = new SupabaseClient(cancellationOptions))
            {
                Assert.Catch<OperationCanceledException>(delegate
                {
                    client.Functions.InvokeAsync("health").GetAwaiter().GetResult();
                });
            }
        }

        [Test]
        public void StoragePublicUrl_UsesPublicOrTransformRoute()
        {
            var options = ConfigurationTests.ValidOptions();
            options.HttpTransport = new RecordingHttpTransport();
            using (var client = new SupabaseClient(options))
            {
                var normal = client.Storage.From("avatars").GetPublicUrl("users/a b.png");
                StringAssert.Contains("/storage/v1/object/public/avatars/users/a%20b.png", normal.AbsoluteUri);
                var transformed = client.Storage.From("avatars").GetPublicUrl("a.png",
                    new StorageTransformOptions { Width = 128, Height = 128 });
                StringAssert.Contains("/render/image/public/avatars/a.png", transformed.AbsoluteUri);
                StringAssert.Contains("width=128", transformed.Query);
            }
        }

        [Test]
        public void Realtime_SubscribeUsesPhoenixJoinAndPublishableKey()
        {
            var socket = new RecordingWebSocketTransport();
            var options = ConfigurationTests.ValidOptions();
            options.HttpTransport = new RecordingHttpTransport();
            options.WebSocketTransportFactory = delegate { return socket; };
            using (var client = new SupabaseClient(options))
            {
                var channel = client.Realtime.Channel("scores")
                    .OnPostgresChanges(new RealtimePostgresChangeFilter { Table = "scores" }, delegate { });
                var result = channel.SubscribeAsync().GetAwaiter().GetResult();
                Assert.IsTrue(result.IsSuccess);
                StringAssert.Contains("apikey=sb_publishable_test-value", socket.ConnectedUri.Query);
                StringAssert.Contains("vsn=2.0.0", socket.ConnectedUri.Query);
                Assert.IsFalse(socket.ConnectedHeaders.ContainsKey("Authorization"));
                var join = JArray.Parse(socket.Sent[0]);
                Assert.AreEqual("realtime:scores", (string)join[2]);
                Assert.AreEqual("phx_join", (string)join[3]);
                Assert.AreEqual("scores", (string)join[4]["config"]["postgres_changes"][0]["table"]);
            }
        }

        [Test]
        public void Realtime_ReconnectsAndRejoinsSubscribedChannels()
        {
            var sockets = new List<RecordingWebSocketTransport>();
            var options = ConfigurationTests.ValidOptions();
            options.HttpTransport = new RecordingHttpTransport();
            options.WebSocketTransportFactory = delegate
            {
                var socket = new RecordingWebSocketTransport();
                sockets.Add(socket);
                return socket;
            };
            using (var client = new SupabaseClient(options))
            {
                var channel = client.Realtime.Channel("safe-reconnect");
                Assert.IsTrue(channel.SubscribeAsync().GetAwaiter().GetResult().IsSuccess);

                var reconnected = client.Realtime.ReconnectAsync().GetAwaiter().GetResult();

                Assert.IsTrue(reconnected.IsSuccess);
                Assert.AreEqual(2, sockets.Count);
                Assert.AreEqual(RealtimeChannelState.Joined, channel.State);
                StringAssert.Contains("phx_join", sockets[1].Sent[0]);
            }
        }

        [Test]
        public void Realtime_RepeatedReconnectsKeepOneLiveSubscription()
        {
            var sockets = new List<RecordingWebSocketTransport>();
            var options = ConfigurationTests.ValidOptions();
            options.HttpTransport = new RecordingHttpTransport();
            options.WebSocketTransportFactory = delegate
            {
                var socket = new RecordingWebSocketTransport();
                sockets.Add(socket);
                return socket;
            };
            using (var client = new SupabaseClient(options))
            {
                var channel = client.Realtime.Channel("reconnect-stress");
                Assert.IsTrue(channel.SubscribeAsync().GetAwaiter().GetResult().IsSuccess);

                for (var attempt = 0; attempt < 20; attempt++)
                    Assert.IsTrue(client.Realtime.ReconnectAsync().GetAwaiter().GetResult().IsSuccess);

                Assert.AreEqual(21, sockets.Count);
                Assert.AreEqual(RealtimeChannelState.Joined, channel.State);
                Assert.AreEqual(0, client.Realtime.PendingPushCount);
                for (var index = 0; index < sockets.Count; index++)
                {
                    Assert.AreEqual(1, CountSent(sockets[index], "phx_join"),
                        "Each connection must carry exactly one join for the channel.");
                    Assert.AreEqual(index == sockets.Count - 1
                            ? SupabaseWebSocketState.Open
                            : SupabaseWebSocketState.Closed,
                        sockets[index].State);
                }
            }
        }

        [Test]
        public void Realtime_PostgresSystemNoticeIsExposedWithoutClosingChannel()
        {
            var socket = new RecordingWebSocketTransport();
            var options = ConfigurationTests.ValidOptions();
            options.HttpTransport = new RecordingHttpTransport();
            options.WebSocketTransportFactory = delegate { return socket; };
            using (var client = new SupabaseClient(options))
            {
                var channel = client.Realtime.Channel("system-notice");
                RealtimeSystemMessage observed = null;
                channel.SystemMessageReceived += delegate(RealtimeSystemMessage message)
                {
                    observed = message;
                };
                Assert.IsTrue(channel.SubscribeAsync().GetAwaiter().GetResult().IsSuccess);

                socket.RaiseChannelEvent(channel.JoinReference, channel.Topic, "system", new JObject
                {
                    ["message"] = "Replication is temporarily degraded",
                    ["status"] = "error",
                    ["extension"] = "postgres_changes",
                    ["channel"] = channel.Topic
                });

                Assert.IsNotNull(observed);
                Assert.IsTrue(observed.IsError);
                Assert.AreEqual("postgres_changes", observed.Extension);
                Assert.AreEqual(RealtimeChannelState.Joined, channel.State);
                Assert.AreEqual(1, CountSent(socket, "phx_join"));
            }
        }

        [Test]
        public void Realtime_PhxErrorAndCloseRejoinChannelOnlyOnce()
        {
            var socket = new RecordingWebSocketTransport();
            var options = ConfigurationTests.ValidOptions();
            options.HttpTransport = new RecordingHttpTransport();
            options.WebSocketTransportFactory = delegate { return socket; };
            options.RealtimeRecoveryDelay = delegate
            {
                return System.Threading.Tasks.Task.CompletedTask;
            };
            using (var client = new SupabaseClient(options))
            {
                var channel = client.Realtime.Channel("single-rejoin");
                Assert.IsTrue(channel.SubscribeAsync().GetAwaiter().GetResult().IsSuccess);
                var failedJoinReference = channel.JoinReference;

                socket.RaiseChannelEvent(failedJoinReference, channel.Topic, "phx_error");
                socket.RaiseChannelEvent(failedJoinReference, channel.Topic, "phx_close");

                Assert.AreEqual(RealtimeChannelState.Joined, channel.State);
                Assert.AreEqual(2, CountSent(socket, "phx_join"),
                    "The error/close pair must produce one replacement join.");
            }
        }

        [Test]
        public void Realtime_RateLimitWaitsForCooldownBeforeRejoin()
        {
            var socket = new RecordingWebSocketTransport();
            var observedDelay = TimeSpan.Zero;
            var options = ConfigurationTests.ValidOptions();
            options.HttpTransport = new RecordingHttpTransport();
            options.WebSocketTransportFactory = delegate { return socket; };
            options.RealtimeRecoveryDelay = delegate(TimeSpan delay,
                System.Threading.CancellationToken cancellationToken)
            {
                observedDelay = delay;
                return System.Threading.Tasks.Task.CompletedTask;
            };
            using (var client = new SupabaseClient(options))
            {
                var channel = client.Realtime.Channel("rate-limited");
                Assert.IsTrue(channel.SubscribeAsync().GetAwaiter().GetResult().IsSuccess);
                var failedJoinReference = channel.JoinReference;
                socket.RaiseChannelEvent(failedJoinReference, channel.Topic, "system", new JObject
                {
                    ["message"] = "Too many messages per second",
                    ["status"] = "error",
                    ["extension"] = "system",
                    ["channel"] = channel.Topic
                });

                socket.RaiseChannelEvent(failedJoinReference, channel.Topic, "phx_close");

                Assert.GreaterOrEqual(observedDelay, TimeSpan.FromSeconds(10));
                Assert.AreEqual(RealtimeChannelState.Joined, channel.State);
                Assert.AreEqual(2, CountSent(socket, "phx_join"));
            }
        }

        [Test]
        public void Realtime_NonRetryableSystemErrorStaysClosed()
        {
            var socket = new RecordingWebSocketTransport();
            var recoveryDelays = 0;
            var options = ConfigurationTests.ValidOptions();
            options.HttpTransport = new RecordingHttpTransport();
            options.WebSocketTransportFactory = delegate { return socket; };
            options.RealtimeRecoveryDelay = delegate
            {
                recoveryDelays++;
                return System.Threading.Tasks.Task.CompletedTask;
            };
            using (var client = new SupabaseClient(options))
            {
                var channel = client.Realtime.Channel("invalid-token");
                Assert.IsTrue(channel.SubscribeAsync().GetAwaiter().GetResult().IsSuccess);
                var failedJoinReference = channel.JoinReference;
                socket.RaiseChannelEvent(failedJoinReference, channel.Topic, "system", new JObject
                {
                    ["message"] = "Token has no exp claim",
                    ["status"] = "error",
                    ["extension"] = "system",
                    ["channel"] = channel.Topic
                });

                socket.RaiseChannelEvent(failedJoinReference, channel.Topic, "phx_close");

                Assert.AreEqual(RealtimeChannelState.Closed, channel.State);
                Assert.AreEqual(0, recoveryDelays,
                    "A non-retryable configuration error must not schedule recovery.");
                Assert.AreEqual(1, CountSent(socket, "phx_join"));
            }
        }

        [Test]
        public void Realtime_ExpiredTokenRefreshesBeforeRejoin()
        {
            var refreshedSession = AuthTests.SessionJson.Replace("access-one", "access-two")
                .Replace("refresh-one", "refresh-two");
            var transport = new RecordingHttpTransport()
                .Enqueue(200, AuthTests.SessionJson)
                .Enqueue(200, refreshedSession);
            var socket = new RecordingWebSocketTransport();
            var options = ConfigurationTests.ValidOptions();
            options.HttpTransport = transport;
            options.WebSocketTransportFactory = delegate { return socket; };
            options.RealtimeRecoveryDelay = delegate
            {
                return System.Threading.Tasks.Task.CompletedTask;
            };
            using (var client = new SupabaseClient(options))
            {
                Assert.IsTrue(client.Auth.SignInWithPasswordAsync("a@b.co", "secret")
                    .GetAwaiter().GetResult().IsSuccess);
                var channel = client.Realtime.Channel("expired-token");
                Assert.IsTrue(channel.SubscribeAsync().GetAwaiter().GetResult().IsSuccess);
                var failedJoinReference = channel.JoinReference;
                socket.RaiseChannelEvent(failedJoinReference, channel.Topic, "system", new JObject
                {
                    ["message"] = "Token has expired",
                    ["status"] = "error",
                    ["extension"] = "system",
                    ["channel"] = channel.Topic
                });

                socket.RaiseChannelEvent(failedJoinReference, channel.Topic, "phx_close");

                Assert.AreEqual("access-two", client.Auth.CurrentSession.AccessToken);
                StringAssert.Contains("grant_type=refresh_token", transport.LastRequest.Uri.Query);
                var replacementJoin = LastSent(socket, "phx_join");
                Assert.AreEqual("access-two", (string)replacementJoin[4]["access_token"]);
                Assert.AreEqual(RealtimeChannelState.Joined, channel.State);
            }
        }

        [Test]
        public void Realtime_PostgresBindingIdsIsolateFilteredCallbacks()
        {
            var socket = new RecordingWebSocketTransport
            {
                JoinResponse = new JObject
                {
                    ["postgres_changes"] = new JArray(
                        new JObject { ["id"] = 1, ["event"] = "*", ["schema"] = "public", ["table"] = "scores", ["filter"] = "id=eq.1" },
                        new JObject { ["id"] = 2, ["event"] = "*", ["schema"] = "public", ["table"] = "scores", ["filter"] = "id=eq.2" })
                }
            };
            var options = ConfigurationTests.ValidOptions();
            options.HttpTransport = new RecordingHttpTransport();
            options.WebSocketTransportFactory = delegate { return socket; };
            using (var client = new SupabaseClient(options))
            {
                var first = 0;
                var second = 0;
                var channel = client.Realtime.Channel("filtered")
                    .OnPostgresChanges(new RealtimePostgresChangeFilter
                        { Table = "scores", Filter = "id=eq.1" }, delegate { first++; })
                    .OnPostgresChanges(new RealtimePostgresChangeFilter
                        { Table = "scores", Filter = "id=eq.2" }, delegate { second++; });
                Assert.IsTrue(channel.SubscribeAsync().GetAwaiter().GetResult().IsSuccess);

                channel.Dispatch("postgres_changes", new JObject
                {
                    ["ids"] = new JArray(2),
                    ["data"] = new JObject
                    {
                        ["type"] = "UPDATE", ["schema"] = "public", ["table"] = "scores",
                        ["record"] = new JObject { ["id"] = 2 }
                    }
                });

                Assert.AreEqual(0, first);
                Assert.AreEqual(1, second);
            }
        }

        [Test]
        public void Realtime_PresenceDiffKeepsRemainingMetas()
        {
            var options = ConfigurationTests.ValidOptions();
            options.HttpTransport = new RecordingHttpTransport();
            using (var client = new SupabaseClient(options))
            {
                var channel = client.Realtime.Channel("presence");
                channel.Dispatch("presence_state", new JObject
                {
                    ["player"] = new JObject
                    {
                        ["metas"] = new JArray(
                            new JObject { ["phx_ref"] = "one" },
                            new JObject { ["phx_ref"] = "two" })
                    }
                });
                channel.Dispatch("presence_diff", new JObject
                {
                    ["joins"] = new JObject(),
                    ["leaves"] = new JObject
                    {
                        ["player"] = new JObject
                        {
                            ["metas"] = new JArray(new JObject { ["phx_ref"] = "one" })
                        }
                    }
                });

                var metas = (JArray)channel.PresenceState["player"]["metas"];
                Assert.AreEqual(1, metas.Count);
                Assert.AreEqual("two", (string)metas[0]["phx_ref"]);
            }
        }

        [Test]
        public void Realtime_BroadcastAcknowledgementIsAwaited()
        {
            var socket = new RecordingWebSocketTransport { AcknowledgeBroadcast = true };
            var options = ConfigurationTests.ValidOptions();
            options.HttpTransport = new RecordingHttpTransport();
            options.WebSocketTransportFactory = delegate { return socket; };
            using (var client = new SupabaseClient(options))
            {
                var channel = client.Realtime.Channel("ack", new RealtimeChannelConfig
                    { BroadcastAcknowledge = true });
                Assert.IsTrue(channel.SubscribeAsync().GetAwaiter().GetResult().IsSuccess);
                Assert.IsTrue(channel.SendBroadcastAsync("move", new { x = 1 })
                    .GetAwaiter().GetResult().IsSuccess);
            }
        }

        [Test]
        public void Realtime_DisconnectFailsPendingAcknowledgementImmediately()
        {
            var socket = new RecordingWebSocketTransport();
            var options = ConfigurationTests.ValidOptions();
            options.HttpTransport = new RecordingHttpTransport();
            options.WebSocketTransportFactory = delegate { return socket; };
            using (var client = new SupabaseClient(options))
            {
                var channel = client.Realtime.Channel("pending-disconnect",
                    new RealtimeChannelConfig { BroadcastAcknowledge = true });
                Assert.IsTrue(channel.SubscribeAsync().GetAwaiter().GetResult().IsSuccess);

                var pending = channel.SendBroadcastAsync("move", new { x = 1 });
                Assert.IsFalse(pending.IsCompleted);
                Assert.AreEqual(1, client.Realtime.PendingPushCount);

                Assert.IsTrue(client.Realtime.DisconnectAsync().GetAwaiter().GetResult().IsSuccess);
                var result = pending.GetAwaiter().GetResult();

                Assert.IsFalse(result.IsSuccess);
                Assert.AreEqual(SupabaseErrorKind.Transport, result.Error.Kind);
                Assert.AreEqual(0, client.Realtime.PendingPushCount);
            }
        }

        [Test]
        public void Realtime_DisposeFailsPendingAcknowledgementImmediately()
        {
            var socket = new RecordingWebSocketTransport();
            var options = ConfigurationTests.ValidOptions();
            options.HttpTransport = new RecordingHttpTransport();
            options.WebSocketTransportFactory = delegate { return socket; };
            var client = new SupabaseClient(options);
            try
            {
                var channel = client.Realtime.Channel("pending-dispose",
                    new RealtimeChannelConfig { BroadcastAcknowledge = true });
                Assert.IsTrue(channel.SubscribeAsync().GetAwaiter().GetResult().IsSuccess);

                var pending = channel.SendBroadcastAsync("move", new { x = 1 });
                Assert.IsFalse(pending.IsCompleted);
                Assert.AreEqual(1, client.Realtime.PendingPushCount);

                client.Dispose();
                var result = pending.GetAwaiter().GetResult();

                Assert.IsFalse(result.IsSuccess);
                Assert.AreEqual(SupabaseErrorKind.Transport, result.Error.Kind);
                StringAssert.Contains("disposed", result.Error.Message);
                Assert.AreEqual(0, client.Realtime.PendingPushCount);
            }
            finally
            {
                client.Dispose();
            }
        }

        [Test]
        public void Realtime_CancellationRemovesPendingAcknowledgement()
        {
            var socket = new RecordingWebSocketTransport();
            var options = ConfigurationTests.ValidOptions();
            options.HttpTransport = new RecordingHttpTransport();
            options.WebSocketTransportFactory = delegate { return socket; };
            using (var client = new SupabaseClient(options))
            {
                var channel = client.Realtime.Channel("pending-cancellation",
                    new RealtimeChannelConfig { BroadcastAcknowledge = true });
                Assert.IsTrue(channel.SubscribeAsync().GetAwaiter().GetResult().IsSuccess);
                var previousContext = System.Threading.SynchronizationContext.Current;
                System.Threading.SynchronizationContext.SetSynchronizationContext(null);
                try
                {
                    using (var cancellation = new System.Threading.CancellationTokenSource())
                    {
                        var pending = channel.SendBroadcastAsync("move", new { x = 1 },
                            cancellation.Token);
                        Assert.AreEqual(1, client.Realtime.PendingPushCount);

                        cancellation.Cancel();

                        Assert.Catch<OperationCanceledException>(delegate
                        {
                            pending.GetAwaiter().GetResult();
                        });
                        Assert.AreEqual(0, client.Realtime.PendingPushCount);
                    }
                }
                finally
                {
                    System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);
                }
            }
        }

        private static int CountSent(RecordingWebSocketTransport socket, string eventName)
        {
            var count = 0;
            foreach (var message in socket.Sent)
                if ((string)JArray.Parse(message)[3] == eventName) count++;
            return count;
        }

        private static void AssertTransportError(SupabaseError error, SupabaseService service)
        {
            Assert.IsNotNull(error);
            Assert.AreEqual(SupabaseErrorKind.Transport, error.Kind);
            Assert.AreEqual(service, error.Service);
            Assert.IsTrue(error.IsRetryable);
            StringAssert.DoesNotContain("eyJhbGci", error.Message);
            StringAssert.DoesNotContain("sb_secret_", error.Message);
            StringAssert.Contains("[REDACTED]", error.Message);
        }

        private static JArray LastSent(RecordingWebSocketTransport socket, string eventName)
        {
            for (var index = socket.Sent.Count - 1; index >= 0; index--)
            {
                var message = JArray.Parse(socket.Sent[index]);
                if ((string)message[3] == eventName) return message;
            }
            Assert.Fail("No " + eventName + " message was sent.");
            return null;
        }
    }
}
