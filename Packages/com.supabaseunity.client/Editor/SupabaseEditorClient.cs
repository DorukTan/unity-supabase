using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;

namespace Supabase.Unity.Editor
{
    /// <summary>Creates clients for explicit HTTP operations outside Play mode.</summary>
    [InitializeOnLoad]
    public static class SupabaseEditorClient
    {
        private static readonly int MainThreadId = Thread.CurrentThread.ManagedThreadId;
        private static readonly Dictionary<SupabaseClient, EditorHttpTransport> Clients
            = new Dictionary<SupabaseClient, EditorHttpTransport>();

        internal static int ActiveClientCount { get { return Clients.Count; } }

        static SupabaseEditorClient()
        {
            AssemblyReloadEvents.beforeAssemblyReload += DisposeAll;
            EditorApplication.quitting += DisposeAll;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        public static SupabaseClient Create(string projectUrl, string publishableKey)
        {
            return Create(new SupabaseClientOptions { ProjectUrl = projectUrl, PublishableKey = publishableKey });
        }

        public static SupabaseClient Create(SupabaseSettings settings)
        {
            RequireEditMode();
            if (settings == null) throw new ArgumentNullException("settings");
            return Create(settings.ToOptions());
        }

        /// <summary>
        /// Copies connection options into a client with memory-only stores and no background refresh.
        /// Custom transports, stores and callback providers are not accepted. Call and dispose on
        /// Unity's main thread; dispose from the owning EditorWindow's OnDisable method.
        /// </summary>
        public static SupabaseClient Create(SupabaseClientOptions options)
        {
            RequireEditMode();
            if (options == null) throw new ArgumentNullException("options");
            if (options.HttpTransport != null || options.SessionStore != null || options.PkceStore != null ||
                options.AuthCallbackProvider != null || options.WebSocketTransportFactory != null)
                throw new ArgumentException(
                    "Editor clients own their transports, memory stores and callback provider.", "options");

            var copy = CopyOptions(options);
            copy.ValidateAndResolve();
            var transport = new EditorHttpTransport();
            copy.HttpTransport = transport;
            copy.AuthStateDispatcher = transport.Post;
            try
            {
                var client = new SupabaseClient(copy, true);
                Clients.Add(client, transport);
                client.Disposed += OnClientDisposed;
                return client;
            }
            catch
            {
                transport.Dispose();
                throw;
            }
        }

        private static SupabaseClientOptions CopyOptions(SupabaseClientOptions source)
        {
            var copy = new SupabaseClientOptions
            {
                ProjectUrl = source.ProjectUrl,
                PublishableKey = source.PublishableKey,
                DefaultSchema = source.DefaultSchema,
                HttpTimeout = source.HttpTimeout,
                RealtimeHeartbeatInterval = source.RealtimeHeartbeatInterval,
                Logger = source.Logger,
                PersistSession = false,
                AutoRefreshToken = false,
                AutoConnectRealtime = false,
                DisableAutomaticAuthRefresh = true,
                SessionStore = new MemorySessionStore(),
                PkceStore = new MemorySessionStore(),
                AuthCallbackProvider = new EditorAuthCallbackProvider(),
                WebSocketTransportFactory = delegate
                {
                    throw new PlatformNotSupportedException("Realtime is not supported by Editor clients.");
                },
                Endpoints = source.Endpoints == null ? null : new SupabaseEndpointOptions
                {
                    AuthUrl = source.Endpoints.AuthUrl,
                    DatabaseUrl = source.Endpoints.DatabaseUrl,
                    StorageUrl = source.Endpoints.StorageUrl,
                    FunctionsUrl = source.Endpoints.FunctionsUrl,
                    RealtimeUrl = source.Endpoints.RealtimeUrl
                }
            };
            foreach (var header in source.Headers) copy.Headers.Add(header.Key, header.Value);
            return copy;
        }

        private static void OnClientDisposed(SupabaseClient client)
        {
            client.Disposed -= OnClientDisposed;
            Clients.Remove(client);
        }

        private static void RequireEditMode()
        {
            if (Thread.CurrentThread.ManagedThreadId != MainThreadId)
                throw new InvalidOperationException("Create Editor clients on Unity's main thread.");
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Editor clients can only be created outside Play mode.");
        }

        internal static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode) DisposeAll();
        }

        internal static void DisposeAll()
        {
            foreach (var pair in new Dictionary<SupabaseClient, EditorHttpTransport>(Clients))
            {
                try { pair.Key.Dispose(); }
                catch (Exception exception) { UnityEngine.Debug.LogException(exception); }
                finally
                {
                    pair.Value.Dispose();
                    OnClientDisposed(pair.Key);
                }
            }
        }

        private sealed class EditorAuthCallbackProvider : IAuthCallbackProvider
        {
            public event Action<Uri> CallbackReceived { add { } remove { } }
            public Uri InitialCallback { get { return null; } }

            public void Open(Uri authorizationUri)
            {
                throw new PlatformNotSupportedException(
                    "Automatic browser authentication is not supported by Editor clients.");
            }
        }
    }
}
