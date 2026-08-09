using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Supabase.Unity
{
    public sealed class WebGlWebSocketTransport : IWebSocketTransport
    {
        private static int nextId;
        private readonly int id;
        private bool disposed;
        private TaskCompletionSource<bool> connection;

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void SupabaseUnity_WebSocketConnect(int id, string url);
        [DllImport("__Internal")] private static extern int SupabaseUnity_WebSocketSend(int id, string message);
        [DllImport("__Internal")] private static extern void SupabaseUnity_WebSocketClose(int id, int code, string reason);
#endif

        public SupabaseWebSocketState State { get; private set; } = SupabaseWebSocketState.Closed;
        public event Action Opened;
        public event Action<string> MessageReceived;
        public event Action<int, string> Closed;
        public event Action<Exception> Error;

        public WebGlWebSocketTransport()
        {
            id = Interlocked.Increment(ref nextId);
            SupabaseRuntimeHost.WebSocketOpened += HandleOpened;
            SupabaseRuntimeHost.WebSocketMessage += HandleMessage;
            SupabaseRuntimeHost.WebSocketClosed += HandleClosed;
            SupabaseRuntimeHost.WebSocketError += HandleError;
        }

        public async Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
#if UNITY_WEBGL && !UNITY_EDITOR
            State = SupabaseWebSocketState.Connecting;
            connection = new TaskCompletionSource<bool>();
            using (cancellationToken.Register(delegate { connection.TrySetCanceled(); }))
            {
                SupabaseUnity_WebSocketConnect(id, uri.AbsoluteUri);
                await connection.Task;
            }
#else
            await Task.Yield();
            throw new PlatformNotSupportedException("WebGlWebSocketTransport can only be used in a WebGL player.");
#endif
        }

        public Task SendTextAsync(string message,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
#if UNITY_WEBGL && !UNITY_EDITOR
            if (State != SupabaseWebSocketState.Open || SupabaseUnity_WebSocketSend(id, message) == 0)
                throw new InvalidOperationException("The browser WebSocket is not open.");
            return Task.CompletedTask;
#else
            throw new PlatformNotSupportedException("WebGlWebSocketTransport can only be used in a WebGL player.");
#endif
        }

        public Task CloseAsync(int code = 1000, string reason = "Normal closure",
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
#if UNITY_WEBGL && !UNITY_EDITOR
            State = SupabaseWebSocketState.Closing;
            SupabaseUnity_WebSocketClose(id, code, reason);
#else
            State = SupabaseWebSocketState.Closed;
#endif
            return Task.CompletedTask;
        }

        private void HandleOpened(int socketId)
        {
            if (socketId != id) return;
            State = SupabaseWebSocketState.Open;
            if (connection != null) connection.TrySetResult(true);
            var handler = Opened; if (handler != null) handler();
        }

        private void HandleMessage(int socketId, string message)
        {
            if (socketId != id) return;
            var handler = MessageReceived; if (handler != null) handler(message);
        }

        private void HandleClosed(int socketId, int code, string reason)
        {
            if (socketId != id) return;
            State = SupabaseWebSocketState.Closed;
            var handler = Closed; if (handler != null) handler(code, reason);
        }

        private void HandleError(int socketId, string message)
        {
            if (socketId != id) return;
            State = SupabaseWebSocketState.Faulted;
            var exception = new InvalidOperationException(message);
            if (connection != null) connection.TrySetException(exception);
            var handler = Error; if (handler != null) handler(exception);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            SupabaseRuntimeHost.WebSocketOpened -= HandleOpened;
            SupabaseRuntimeHost.WebSocketMessage -= HandleMessage;
            SupabaseRuntimeHost.WebSocketClosed -= HandleClosed;
            SupabaseRuntimeHost.WebSocketError -= HandleError;
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException("WebGlWebSocketTransport");
        }
    }
}
