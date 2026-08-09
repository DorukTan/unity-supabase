using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Supabase.Unity
{
    [Serializable]
    public sealed class SupabaseEndpointOptions
    {
        public string AuthUrl;
        public string DatabaseUrl;
        public string RealtimeUrl;
        public string StorageUrl;
        public string FunctionsUrl;

        internal SupabaseResolvedEndpoints Resolve(Uri projectUri)
        {
            var root = projectUri.AbsoluteUri.TrimEnd('/');
            return new SupabaseResolvedEndpoints(
                ResolveHttp(AuthUrl, root + "/auth/v1"),
                ResolveHttp(DatabaseUrl, root + "/rest/v1"),
                ResolveRealtime(RealtimeUrl, root),
                ResolveHttp(StorageUrl, root + "/storage/v1"),
                ResolveHttp(FunctionsUrl, root + "/functions/v1"));
        }

        private static Uri ResolveHttp(string configured, string fallback)
        {
            return new Uri(string.IsNullOrWhiteSpace(configured) ? fallback : configured.TrimEnd('/'));
        }

        private static Uri ResolveRealtime(string configured, string root)
        {
            if (!string.IsNullOrWhiteSpace(configured))
                return new Uri(configured.TrimEnd('/'));
            var builder = new UriBuilder(root);
            builder.Scheme = builder.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
            builder.Path = builder.Path.TrimEnd('/') + "/realtime/v1";
            return builder.Uri;
        }
    }

    public sealed class SupabaseResolvedEndpoints
    {
        public Uri Auth { get; private set; }
        public Uri Database { get; private set; }
        public Uri Realtime { get; private set; }
        public Uri Storage { get; private set; }
        public Uri Functions { get; private set; }

        internal SupabaseResolvedEndpoints(Uri auth, Uri database, Uri realtime, Uri storage, Uri functions)
        {
            Auth = auth;
            Database = database;
            Realtime = realtime;
            Storage = storage;
            Functions = functions;
        }
    }

    public sealed class SupabaseClientOptions
    {
        public string ProjectUrl { get; set; }
        public string PublishableKey { get; set; }
        public string DefaultSchema { get; set; } = "public";
        public bool PersistSession { get; set; }
        public bool AutoRefreshToken { get; set; } = true;
        public bool AutoConnectRealtime { get; set; }
        public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan RealtimeHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(25);
        public SupabaseEndpointOptions Endpoints { get; set; } = new SupabaseEndpointOptions();
        public Dictionary<string, string> Headers { get; private set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public IHttpTransport HttpTransport { get; set; }
        public ISessionStore SessionStore { get; set; }

        /// <summary>
        /// Storage for in-flight OAuth PKCE verifiers. Defaults to durable platform storage
        /// regardless of <see cref="PersistSession"/>, because the app is routinely evicted
        /// while the player is in the browser. A verifier is short-lived and useless to an
        /// attacker once exchanged, so persisting it does not carry the risk that persisting
        /// a refresh token does.
        /// </summary>
        public ISessionStore PkceStore { get; set; }

        public Func<IWebSocketTransport> WebSocketTransportFactory { get; set; }
        public IAuthCallbackProvider AuthCallbackProvider { get; set; }
        public ISupabaseLogger Logger { get; set; } = NullSupabaseLogger.Instance;

        public SupabaseResolvedEndpoints ValidateAndResolve()
        {
            ProjectUrl = string.IsNullOrWhiteSpace(ProjectUrl) ? ProjectUrl : ProjectUrl.Trim();
            PublishableKey = string.IsNullOrWhiteSpace(PublishableKey) ? PublishableKey : PublishableKey.Trim();
            Uri projectUri;
            if (!Uri.TryCreate(ProjectUrl, UriKind.Absolute, out projectUri) ||
                (projectUri.Scheme != Uri.UriSchemeHttps && projectUri.Scheme != Uri.UriSchemeHttp))
                throw new SupabaseConfigurationException("ProjectUrl must be an absolute HTTP or HTTPS URL.");
            if (!string.IsNullOrEmpty(projectUri.UserInfo) || !string.IsNullOrEmpty(projectUri.Query) ||
                !string.IsNullOrEmpty(projectUri.Fragment))
                throw new SupabaseConfigurationException(
                    "ProjectUrl cannot contain user credentials, a query string, or a fragment.");

            var isLocal = projectUri.IsLoopback || projectUri.Host == "localhost";
            if (projectUri.Scheme != Uri.UriSchemeHttps && !isLocal)
                throw new SupabaseConfigurationException("Non-local Supabase projects must use HTTPS.");

            SupabaseKeyValidator.ValidateClientKey(PublishableKey);

            if (string.IsNullOrWhiteSpace(DefaultSchema))
                DefaultSchema = "public";
            if (HttpTimeout <= TimeSpan.Zero)
                throw new SupabaseConfigurationException("HttpTimeout must be greater than zero.");

            if (Endpoints == null)
                Endpoints = new SupabaseEndpointOptions();
            if (Logger == null)
                Logger = NullSupabaseLogger.Instance;

            var resolved = Endpoints.Resolve(projectUri);
            ValidateHttpEndpoint("Auth", resolved.Auth);
            ValidateHttpEndpoint("Database", resolved.Database);
            ValidateHttpEndpoint("Storage", resolved.Storage);
            ValidateHttpEndpoint("Functions", resolved.Functions);
            ValidateRealtimeEndpoint(resolved.Realtime);
            return resolved;
        }

        private static void ValidateHttpEndpoint(string name, Uri endpoint)
        {
            if (endpoint == null || !endpoint.IsAbsoluteUri ||
                (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
                throw new SupabaseConfigurationException(name + " endpoint must be an absolute HTTP or HTTPS URL.");
            if (!string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) ||
                !string.IsNullOrEmpty(endpoint.Fragment))
                throw new SupabaseConfigurationException(
                    name + " endpoint cannot contain user credentials, a query string, or a fragment.");
            if (endpoint.Scheme != Uri.UriSchemeHttps && !IsLocal(endpoint))
                throw new SupabaseConfigurationException(name + " endpoint must use HTTPS outside loopback development.");
        }

        private static void ValidateRealtimeEndpoint(Uri endpoint)
        {
            if (endpoint == null || !endpoint.IsAbsoluteUri ||
                (endpoint.Scheme != "wss" && endpoint.Scheme != "ws"))
                throw new SupabaseConfigurationException("Realtime endpoint must be an absolute WS or WSS URL.");
            if (!string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) ||
                !string.IsNullOrEmpty(endpoint.Fragment))
                throw new SupabaseConfigurationException(
                    "Realtime endpoint cannot contain user credentials, a query string, or a fragment.");
            if (endpoint.Scheme != "wss" && !IsLocal(endpoint))
                throw new SupabaseConfigurationException(
                    "Realtime endpoint must use WSS outside loopback development.");
        }

        private static bool IsLocal(Uri endpoint)
        {
            return endpoint.IsLoopback || string.Equals(endpoint.Host, "localhost",
                StringComparison.OrdinalIgnoreCase);
        }
    }

    public static class SupabaseKeyValidator
    {
        public static void ValidateClientKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new SupabaseConfigurationException("A Supabase publishable or legacy anon key is required.");

            var trimmed = key.Trim();
            if (IsSecretKey(trimmed))
                throw new SupabaseConfigurationException("Supabase secret keys must never be included in a Unity client build.");

            if (IsPublishableKey(trimmed))
                return;

            var parts = trimmed.Split('.');
            if (parts.Length != 3)
                throw new SupabaseConfigurationException("The key is neither an sb_publishable key nor a valid legacy anon JWT.");

            try
            {
                var payload = DecodeBase64Url(parts[1]);
                var json = JObject.Parse(payload);
                var role = (string)json["role"];
                if (string.Equals(role, "service_role", StringComparison.OrdinalIgnoreCase))
                    throw new SupabaseConfigurationException("Legacy service_role keys must never be included in a Unity client build.");
                if (!string.Equals(role, "anon", StringComparison.OrdinalIgnoreCase))
                    throw new SupabaseConfigurationException("Legacy client JWT must use the anon role.");
            }
            catch (SupabaseConfigurationException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new SupabaseConfigurationException("The legacy anon key could not be decoded: " + exception.Message);
            }
        }

        internal static bool IsPublishableKey(string key)
        {
            return !string.IsNullOrWhiteSpace(key) &&
                   key.Trim().StartsWith("sb_publishable_", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsElevatedKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return false;
            var trimmed = key.Trim();
            if (IsSecretKey(trimmed))
                return true;

            string role;
            if (!TryGetJwtRole(trimmed, out role))
                return false;
            return string.Equals(role, "service_role", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryGetJwtRole(string key, out string role)
        {
            role = null;
            if (string.IsNullOrWhiteSpace(key))
                return false;
            var parts = key.Trim().Split('.');
            if (parts.Length != 3)
                return false;
            try
            {
                role = (string)JObject.Parse(DecodeBase64Url(parts[1]))["role"];
                return !string.IsNullOrWhiteSpace(role);
            }
            catch
            {
                return false;
            }
        }

        internal static void RejectElevatedKey(string value, string context)
        {
            if (IsElevatedKey(value) || (!string.IsNullOrWhiteSpace(value) &&
                value.IndexOf("sb_secret_", StringComparison.OrdinalIgnoreCase) >= 0))
                throw new SupabaseConfigurationException(
                    "An elevated Supabase key cannot be used as " + context + " in a Unity client.");
        }

        private static bool IsSecretKey(string key)
        {
            return key.StartsWith("sb_secret_", StringComparison.OrdinalIgnoreCase);
        }

        private static string DecodeBase64Url(string value)
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        }
    }
}
