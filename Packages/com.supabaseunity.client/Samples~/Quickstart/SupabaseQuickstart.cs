using System;
using Supabase.Unity;
using UnityEngine;

namespace Supabase.Unity.Samples
{
    public sealed class SupabaseQuickstart : MonoBehaviour
    {
        [SerializeField] private SupabaseSettings settings;
        private SupabaseClient client;

        private async void Start()
        {
            if (settings == null)
            {
                Debug.LogError(
                    "Supabase Quickstart: assign a SupabaseSettings asset to this component.", this);
                return;
            }

            try
            {
                client = new SupabaseClient(settings);
                var initialized = await client.InitializeAsync();
                if (!initialized.IsSuccess)
                {
                    LogFailure("initialize", initialized.Error);
                    return;
                }

                var result = await client.From<ScoreRow>()
                    .Select("id,player_name,score")
                    .Order("score", false)
                    .Limit(20)
                    .GetAsync();
                if (result.IsSuccess)
                {
                    Debug.Log("Supabase Quickstart: loaded " + result.Data.Count + " score rows.", this);
                    foreach (var score in result.Data)
                        Debug.Log(score.PlayerName + ": " + score.Score, this);
                }
                else
                {
                    LogFailure("load scores", result.Error);
                }

                var channel = client.Realtime.Channel("quickstart-scores");
                channel.OnPostgresChanges(new RealtimePostgresChangeFilter
                {
                    Schema = "public",
                    Table = "scores",
                    Event = RealtimePostgresEvent.All
                }, change => Debug.Log(
                    "Supabase Quickstart: " + change.Event + " on " + change.Schema + "." + change.Table,
                    this));
                var subscribed = await channel.SubscribeAsync();
                if (!subscribed.IsSuccess)
                    LogFailure("subscribe to score changes", subscribed.Error);
                else
                    Debug.Log("Supabase Quickstart: listening for changes to public.scores.", this);
            }
            catch (SupabaseConfigurationException exception)
            {
                Debug.LogError("Supabase Quickstart configuration: " + exception.Message, this);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }

        private void OnDestroy()
        {
            if (client != null)
                client.Dispose();
        }

        private void LogFailure(string action, SupabaseError error)
        {
            Debug.LogError(
                "Supabase Quickstart could not " + action + ": " +
                (error == null ? "unknown error" : error.ToString()), this);
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
