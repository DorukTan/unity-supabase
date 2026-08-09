using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Supabase.Unity
{
    public enum SupabaseLogLevel
    {
        Debug,
        Information,
        Warning,
        Error
    }

    public interface ISupabaseLogger
    {
        void Log(SupabaseLogLevel level, string message, Exception exception = null);
    }

    public sealed class NullSupabaseLogger : ISupabaseLogger
    {
        public static readonly NullSupabaseLogger Instance = new NullSupabaseLogger();

        private NullSupabaseLogger()
        {
        }

        public void Log(SupabaseLogLevel level, string message, Exception exception = null)
        {
        }
    }

    public interface ISessionStore
    {
        Task<string> GetAsync(string key, CancellationToken cancellationToken = default(CancellationToken));
        Task SetAsync(string key, string value, CancellationToken cancellationToken = default(CancellationToken));
        Task RemoveAsync(string key, CancellationToken cancellationToken = default(CancellationToken));
    }

    public enum SupabaseWebSocketState
    {
        Closed,
        Connecting,
        Open,
        Closing,
        Faulted
    }

    public interface IWebSocketTransport : IDisposable
    {
        SupabaseWebSocketState State { get; }
        event Action Opened;
        event Action<string> MessageReceived;
        event Action<int, string> Closed;
        event Action<Exception> Error;

        Task ConnectAsync(
            Uri uri,
            IReadOnlyDictionary<string, string> headers,
            CancellationToken cancellationToken = default(CancellationToken));

        Task SendTextAsync(string message, CancellationToken cancellationToken = default(CancellationToken));
        Task CloseAsync(int code = 1000, string reason = "Normal closure", CancellationToken cancellationToken = default(CancellationToken));
    }

    public interface IAuthCallbackProvider
    {
        event Action<Uri> CallbackReceived;
        Uri InitialCallback { get; }
        void Open(Uri authorizationUri);
    }

    public interface IAuthCallbackSanitizer
    {
        void ClearSensitiveCallback(Uri callbackUri);
    }
}
