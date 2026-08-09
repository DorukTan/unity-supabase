using UnityEngine;

namespace Supabase.Unity
{
    [CreateAssetMenu(fileName = "SupabaseSettings", menuName = "Supabase/Settings", order = 100)]
    public sealed class SupabaseSettings : ScriptableObject
    {
        [SerializeField] private string projectUrl;
        [SerializeField] private string publishableKey;
        [SerializeField] private string defaultSchema = "public";
        [SerializeField] private bool persistSession;
        [SerializeField] private bool autoRefreshToken = true;
        [SerializeField] private bool autoConnectRealtime;
        [SerializeField, Min(1f)] private float httpTimeoutSeconds = 30f;

        public string ProjectUrl { get { return projectUrl; } }
        public string PublishableKey { get { return publishableKey; } }
        public string DefaultSchema { get { return defaultSchema; } }

        public SupabaseClientOptions ToOptions()
        {
            return new SupabaseClientOptions
            {
                ProjectUrl = projectUrl,
                PublishableKey = publishableKey,
                DefaultSchema = string.IsNullOrWhiteSpace(defaultSchema) ? "public" : defaultSchema,
                PersistSession = persistSession,
                AutoRefreshToken = autoRefreshToken,
                AutoConnectRealtime = autoConnectRealtime,
                HttpTimeout = System.TimeSpan.FromSeconds(Mathf.Max(1f, httpTimeoutSeconds))
            };
        }
    }
}
