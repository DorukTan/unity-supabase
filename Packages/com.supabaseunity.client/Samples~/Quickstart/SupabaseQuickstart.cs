using System;
using Supabase.Unity;
using UnityEngine;

namespace Supabase.Unity.Samples
{
    public sealed class SupabaseQuickstart : MonoBehaviour
    {
        [SerializeField] private SupabaseSettings settings;
        private SupabaseClient client;
        private RealtimeChannel channel;

        private async void Start()
        {
            client = new SupabaseClient(settings);
            var initialized = await client.InitializeAsync();
            if (!initialized.IsSuccess)
            {
                Debug.LogError(initialized.Error);
                return;
            }

            var result = await client.From<ScoreRow>().Order("score", false).Limit(20).GetAsync();
            if (result.IsSuccess)
                Debug.Log("Loaded " + result.Data.Count + " score rows.");
            else
                Debug.LogError(result.Error);

            channel = client.Realtime.Channel("scores");
            channel.OnPostgresChanges(new RealtimePostgresChangeFilter
            {
                Schema = "public", Table = "scores", Event = RealtimePostgresEvent.All
            }, change => Debug.Log("Score " + change.Event + " on " + change.Schema + "." + change.Table));
            var subscribed = await channel.SubscribeAsync();
            if (!subscribed.IsSuccess) Debug.LogError(subscribed.Error);
        }

        private async void OnDestroy()
        {
            if (channel != null) await channel.UnsubscribeAsync();
            if (client != null) client.Dispose();
        }
    }

    [Serializable, SupabaseTable("scores")]
    public sealed class ScoreRow
    {
        [SupabaseColumn("id")] public long Id { get; set; }
        [SupabaseColumn("player_name")] public string PlayerName { get; set; }
        [SupabaseColumn("score")] public int Score { get; set; }
    }
}
