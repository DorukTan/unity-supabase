using System;
using System.Threading;
using System.Threading.Tasks;

namespace Supabase.Unity
{
    /// <summary>Entry point for the client-safe Supabase APIs available to a Unity player.</summary>
    public sealed class SupabaseClient : IDisposable
    {
        private readonly IHttpTransport transport;
        private readonly bool ownsTransport;
        private readonly IDisposable ownedCallbackProvider;
        private bool disposed;

        public SupabaseClientOptions Options { get; private set; }
        public SupabaseResolvedEndpoints Endpoints { get; private set; }
        public IAuthClient Auth { get; private set; }
        public DatabaseClient Database { get; private set; }
        public StorageClient Storage { get; private set; }
        public FunctionsClient Functions { get; private set; }
        public RealtimeClient Realtime { get; private set; }

        public SupabaseClient(string projectUrl, string publishableKey)
            : this(new SupabaseClientOptions { ProjectUrl = projectUrl, PublishableKey = publishableKey })
        {
        }

        public SupabaseClient(SupabaseSettings settings)
            : this(settings == null ? throw new ArgumentNullException("settings") : settings.ToOptions())
        {
        }

        public SupabaseClient(SupabaseClientOptions options)
        {
            Options = options ?? throw new ArgumentNullException("options");
            Endpoints = Options.ValidateAndResolve();
            transport = Options.HttpTransport ?? new UnityWebRequestTransport();
            ownsTransport = Options.HttpTransport == null;
            var sessionStore = Options.SessionStore ?? PlatformSessionStore.Create(Options.PersistSession);
            var pkceStore = Options.PkceStore ?? PlatformSessionStore.CreateDurable();
            var callbackProvider = Options.AuthCallbackProvider;
            if (callbackProvider == null)
            {
                var defaultProvider = new DefaultAuthCallbackProvider();
                callbackProvider = defaultProvider;
                ownedCallbackProvider = defaultProvider;
            }

            Auth = new AuthClient(Options, Endpoints.Auth, transport, sessionStore, pkceStore, callbackProvider);
            Func<string> token = delegate
            {
                var session = Auth.CurrentSession;
                return session == null ? null : session.AccessToken;
            };
            Database = new DatabaseClient(Options, Endpoints.Database, transport, token);
            Storage = new StorageClient(Options, Endpoints.Storage, transport, token);
            Functions = new FunctionsClient(Options, Endpoints.Functions, transport, token);
            Realtime = new RealtimeClient(Options, Endpoints.Realtime, token);
            Auth.StateChanged += OnAuthStateChanged;
        }

        public async Task<SupabaseResult> InitializeAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            var auth = await Auth.InitializeAsync(cancellationToken);
            if (!auth.IsSuccess)
                return SupabaseResult.Failure(auth.Error, auth.Metadata);

            if (Options.AutoConnectRealtime)
            {
                var realtime = await Realtime.ConnectAsync(cancellationToken);
                if (!realtime.IsSuccess)
                    return realtime;
            }
            return SupabaseResult.Success();
        }

        public PostgrestQuery<T> From<T>(string table = null)
        {
            ThrowIfDisposed();
            return Database.From<T>(table);
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            Auth.StateChanged -= OnAuthStateChanged;
            Realtime.Dispose();
            Auth.Dispose();
            if (ownsTransport)
                transport.Dispose();
            if (ownedCallbackProvider != null)
                ownedCallbackProvider.Dispose();
        }

        private void OnAuthStateChanged(object sender, AuthStateChangedEventArgs eventArgs)
        {
            if (!Realtime.IsConnected)
                return;
            var update = eventArgs.Session != null && !string.IsNullOrWhiteSpace(eventArgs.Session.AccessToken)
                ? Realtime.SetAuthAsync(eventArgs.Session.AccessToken)
                : Realtime.ReconnectAsync();
            update.Forget(delegate(Exception exception)
            {
                Options.Logger.Log(SupabaseLogLevel.Warning,
                    "Realtime channels could not adopt the changed Auth state.", exception);
            });
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
                throw new ObjectDisposedException("SupabaseClient");
        }
    }
}
