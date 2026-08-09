using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Supabase.Unity
{
    public sealed class NativeWebSocketTransport : IWebSocketTransport
    {
        private ClientWebSocket socket;
        private CancellationTokenSource lifetime;
        private bool disposed;

        public SupabaseWebSocketState State { get; private set; } = SupabaseWebSocketState.Closed;
        public event Action Opened;
        public event Action<string> MessageReceived;
        public event Action<int, string> Closed;
        public event Action<Exception> Error;

        public async Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            if (State == SupabaseWebSocketState.Open || State == SupabaseWebSocketState.Connecting)
                return;
            State = SupabaseWebSocketState.Connecting;
            socket = new ClientWebSocket();
            if (headers != null)
                foreach (var header in headers)
                    socket.Options.SetRequestHeader(header.Key, header.Value);
            if (lifetime != null)
            {
                lifetime.Cancel();
                lifetime.Dispose();
            }
            lifetime = new CancellationTokenSource();
            try
            {
                await socket.ConnectAsync(uri, cancellationToken);
                State = SupabaseWebSocketState.Open;
                Raise(Opened);
                ReceiveLoopAsync(lifetime.Token).Forget(RaiseError);
            }
            catch
            {
                State = SupabaseWebSocketState.Faulted;
                throw;
            }
        }

        public async Task SendTextAsync(string message,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            if (socket == null || socket.State != WebSocketState.Open)
                throw new InvalidOperationException("The WebSocket is not open.");
            var bytes = Encoding.UTF8.GetBytes(message ?? string.Empty);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                cancellationToken);
        }

        public async Task CloseAsync(int code = 1000, string reason = "Normal closure",
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (socket == null) return;
            State = SupabaseWebSocketState.Closing;
            if (lifetime != null) lifetime.Cancel();
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                await socket.CloseAsync((WebSocketCloseStatus)code, reason, cancellationToken);
            State = SupabaseWebSocketState.Closed;
            RaiseClosed(code, reason);
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (!cancellationToken.IsCancellationRequested && socket != null &&
                       socket.State == WebSocketState.Open)
                {
                    using (var stream = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                State = SupabaseWebSocketState.Closed;
                                RaiseClosed((int)(result.CloseStatus ?? WebSocketCloseStatus.Empty),
                                    result.CloseStatusDescription);
                                return;
                            }
                            stream.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            var handler = MessageReceived;
                            if (handler != null) handler(Encoding.UTF8.GetString(stream.ToArray()));
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                if (cancellationToken.IsCancellationRequested || disposed)
                    return;
                State = SupabaseWebSocketState.Faulted;
                RaiseError(exception);
                RaiseClosed(1006, "WebSocket transport error");
            }
        }

        private void Raise(Action handler) { if (handler != null) handler(); }
        private void RaiseClosed(int code, string reason)
        {
            var handler = Closed;
            if (handler != null) handler(code, reason);
        }
        private void RaiseError(Exception exception)
        {
            var handler = Error;
            if (handler != null) handler(exception);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (lifetime != null) lifetime.Cancel();
            if (lifetime != null) lifetime.Dispose();
            if (socket != null) socket.Dispose();
            State = SupabaseWebSocketState.Closed;
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException("NativeWebSocketTransport");
        }
    }

    internal static class SupabaseTaskForgetExtensions
    {
        internal static async void Forget(this Task task, Action<Exception> onError)
        {
            try { await task; }
            catch (Exception exception) { if (onError != null) onError(exception); }
        }
    }
}
