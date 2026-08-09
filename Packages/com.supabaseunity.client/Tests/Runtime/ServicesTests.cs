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
    }
}
