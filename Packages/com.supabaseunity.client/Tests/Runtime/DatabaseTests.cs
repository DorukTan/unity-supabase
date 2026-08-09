using System;
using System.Text;
using NUnit.Framework;

namespace Supabase.Unity.Tests
{
    public sealed class DatabaseTests
    {
        [SupabaseTable("score_rows")]
        private sealed class Score
        {
            [SupabaseColumn("player_name")] public string PlayerName { get; set; }
            [SupabaseColumn("score")] public int Value { get; set; }
        }

        [Test]
        public void Query_EncodesFiltersAndSendsClientHeaders()
        {
            var transport = new RecordingHttpTransport();
            var options = ConfigurationTests.ValidOptions();
            options.HttpTransport = transport;
            using (var client = new SupabaseClient(options))
            {
                var result = client.From<Score>()
                    .Select("player_name,score").Gte("score", 100).Order("score", false).Range(10, 19)
                    .Count(PostgrestCount.Exact).GetAsync().GetAwaiter().GetResult();

                Assert.IsTrue(result.IsSuccess);
                var uri = Uri.UnescapeDataString(transport.LastRequest.Uri.AbsoluteUri);
                StringAssert.Contains("/rest/v1/score_rows", uri);
                StringAssert.Contains("select=player_name,score", uri);
                StringAssert.Contains("score=gte.100", uri);
                StringAssert.Contains("order=score.desc.nullslast", uri);
                StringAssert.Contains("limit=10", uri);
                StringAssert.Contains("offset=10", uri);
                Assert.AreEqual(options.PublishableKey, transport.LastRequest.Headers["apikey"]);
                Assert.IsFalse(transport.LastRequest.Headers.ContainsKey("Authorization"));
                Assert.AreEqual("count=exact", transport.LastRequest.Headers["Prefer"]);
            }
        }

        [Test]
        public void Insert_UsesColumnNamesAndRepresentationPreference()
        {
            var transport = new RecordingHttpTransport
            {
                Response = delegate { return new SupabaseHttpResponse
                {
                    StatusCode = 201,
                    Body = Encoding.UTF8.GetBytes("[{\"player_name\":\"Ada\",\"score\":42}]")
                }; }
            };
            var options = ConfigurationTests.ValidOptions();
            options.HttpTransport = transport;
            using (var client = new SupabaseClient(options))
            {
                var result = client.From<Score>().InsertAsync(new Score { PlayerName = "Ada", Value = 42 })
                    .GetAwaiter().GetResult();
                Assert.IsTrue(result.IsSuccess);
                var json = Encoding.UTF8.GetString(transport.LastRequest.Body);
                StringAssert.Contains("\"player_name\":\"Ada\"", json);
                StringAssert.Contains("\"score\":42", json);
                StringAssert.Contains("return=representation", transport.LastRequest.Headers["Prefer"]);
            }
        }
    }
}
