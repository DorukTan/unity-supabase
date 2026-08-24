using UnityEngine;

namespace Supabase.Unity
{
    [CreateAssetMenu(fileName = "SupabaseSettings", menuName = "Supabase/Settings", order = 100)]
    public sealed class SupabaseSettings : ScriptableObject
    {
        [Header("Connection")]
        [Tooltip("Project URL from the Supabase Dashboard Connect dialog, such as https://example.supabase.co.")]
        [SerializeField] private string projectUrl;
        [Tooltip("Client-safe sb_publishable_ key from the Supabase Dashboard Connect dialog. Legacy anon JWTs are also supported.")]
        [SerializeField] private string publishableKey;
        [Tooltip("PostgREST schema used when a query does not specify one. Usually public.")]
        [SerializeField] private string defaultSchema = "public";

        [Header("Runtime")]
        [Tooltip("Restore Auth sessions between launches. Read the session-storage guide before enabling this.")]
        [SerializeField] private bool persistSession;
        [Tooltip("Refresh an authenticated session shortly before its access token expires.")]
        [SerializeField] private bool autoRefreshToken = true;
        [Tooltip("Connect the Realtime socket during InitializeAsync instead of on the first channel subscription.")]
        [SerializeField] private bool autoConnectRealtime;
        [Tooltip("Maximum duration of an HTTP request before it returns a timeout result.")]
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
