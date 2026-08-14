using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Supabase.Unity
{
    public sealed class RealtimeClient : IDisposable
    {
        private readonly SupabaseClientOptions options;
        private readonly Uri endpoint;
        private readonly Func<string> accessToken;
        private readonly Func<CancellationToken, Task<SupabaseResult<AuthSession>>> refreshAccessToken;
        private readonly object gate = new object();
        private readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim connectionGate = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, RealtimeChannel> channels = new Dictionary<string, RealtimeChannel>();
        private readonly Dictionary<string, TaskCompletionSource<SupabaseResult<JObject>>> pending
            = new Dictionary<string, TaskCompletionSource<SupabaseResult<JObject>>>();
        private IWebSocketTransport socket;
        private CancellationTokenSource lifetime;
        private long reference;
        private bool manualClose;
        private bool reconnecting;
        private bool disposed;

        public SupabaseWebSocketState State { get { return socket == null ? SupabaseWebSocketState.Closed : socket.State; } }
        public bool IsConnected { get { return State == SupabaseWebSocketState.Open; } }
        public event Action Connected;
        public event Action<int, string> Disconnected;
        public event Action<Exception> Error;

        internal RealtimeClient(SupabaseClientOptions options, Uri endpoint, Func<string> accessToken,
            Func<CancellationToken, Task<SupabaseResult<AuthSession>>> refreshAccessToken)
        {
            this.options = options;
            this.endpoint = endpoint;
            this.accessToken = accessToken;
            this.refreshAccessToken = refreshAccessToken;
        }

        public async Task<SupabaseResult> ConnectAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            await connectionGate.WaitAsync(cancellationToken);
            try
            {
                if (IsConnected) return SupabaseResult.Success();
                manualClose = false;
                if (lifetime != null)
                {
                    lifetime.Cancel();
                    lifetime.Dispose();
                }
                lifetime = new CancellationTokenSource();
                if (socket != null) DetachAndDispose(socket);
                socket = CreateTransport();
                socket.Opened += OnOpened;
                socket.MessageReceived += OnMessage;
                socket.Closed += OnClosed;
                socket.Error += OnError;
                await socket.ConnectAsync(BuildSocketUri(), BuildHeaders(), cancellationToken);
                StartHeartbeat(lifetime.Token);
                return SupabaseResult.Success();
            }
            catch (OperationCanceledException) { throw; }
            catch (PlatformNotSupportedException exception)
            {
                return SupabaseResult.Failure(SupabaseError.Create(SupabaseService.Realtime,
                    SupabaseErrorKind.UnsupportedPlatform, exception.Message));
            }
            catch (Exception exception)
            {
                return SupabaseResult.Failure(SupabaseError.Create(SupabaseService.Realtime,
                    SupabaseErrorKind.Transport, "Realtime could not connect.", details: exception.Message,
                    retryable: true));
            }
            finally
            {
                connectionGate.Release();
            }
        }

        internal async Task<SupabaseResult> ReconnectAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            RealtimeChannel[] snapshot;
            lock (gate)
            {
                snapshot = new RealtimeChannel[channels.Count];
                channels.Values.CopyTo(snapshot, 0);
            }

            var disconnected = await DisconnectAsync(cancellationToken);
            if (!disconnected.IsSuccess)
                return disconnected;
            var connected = await ConnectAsync(cancellationToken);
            if (!connected.IsSuccess)
                return connected;
            foreach (var channel in snapshot)
            {
                var subscribed = await channel.ResubscribeAsync();
                if (!subscribed.IsSuccess)
                    return subscribed;
            }
            return SupabaseResult.Success();
        }

        public async Task<SupabaseResult> DisconnectAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            manualClose = true;
            if (lifetime != null) lifetime.Cancel();
            RealtimeChannel[] snapshot;
            lock (gate)
            {
                snapshot = new RealtimeChannel[channels.Count];
                channels.Values.CopyTo(snapshot, 0);
            }
            foreach (var channel in snapshot) channel.NotifySocketClosed();
            if (socket == null) return SupabaseResult.Success();
            try
            {
                await socket.CloseAsync(1000, "Client disconnect", cancellationToken);
                return SupabaseResult.Success();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                return SupabaseResult.Failure(SupabaseError.Create(SupabaseService.Realtime,
                    SupabaseErrorKind.Transport, "Realtime could not disconnect cleanly.",
                    details: exception.Message));
            }
        }

        public RealtimeChannel Channel(string topic, RealtimeChannelConfig config = null)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(topic)) throw new ArgumentException("Topic cannot be empty.", "topic");
            var wireTopic = topic.StartsWith("realtime:", StringComparison.Ordinal) ? topic : "realtime:" + topic;
            lock (gate)
            {
                RealtimeChannel existing;
                if (channels.TryGetValue(wireTopic, out existing)) return existing;
                var channel = new RealtimeChannel(this, wireTopic, config ?? new RealtimeChannelConfig());
                channels[wireTopic] = channel;
                return channel;
            }
        }

        public async Task<SupabaseResult> RemoveChannelAsync(RealtimeChannel channel,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (channel == null) throw new ArgumentNullException("channel");
            var result = await channel.UnsubscribeAsync(cancellationToken);
            lock (gate) channels.Remove(channel.Topic);
            return result;
        }

        public async Task<SupabaseResult> RemoveAllChannelsAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            RealtimeChannel[] snapshot;
            lock (gate)
            {
                snapshot = new RealtimeChannel[channels.Count];
                channels.Values.CopyTo(snapshot, 0);
            }
            foreach (var channel in snapshot)
            {
                var result = await channel.UnsubscribeAsync(cancellationToken);
                if (!result.IsSuccess) return result;
            }
            lock (gate) channels.Clear();
            return SupabaseResult.Success();
        }

        public async Task<SupabaseResult> SetAuthAsync(string token = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            token = string.IsNullOrWhiteSpace(token) ? accessToken() : token;
            if (string.IsNullOrWhiteSpace(token)) return SupabaseResult.Success();
            RealtimeChannel[] snapshot;
            lock (gate)
            {
                snapshot = new RealtimeChannel[channels.Count];
                channels.Values.CopyTo(snapshot, 0);
            }
            foreach (var channel in snapshot)
            {
                if (channel.State != RealtimeChannelState.Joined) continue;
                var result = await SendEventAsync(channel.Topic, "access_token",
                    new JObject { ["access_token"] = token }, cancellationToken, channel.JoinReference);
                if (!result.IsSuccess) return result;
            }
            return SupabaseResult.Success();
        }

        internal string NextReference()
        {
            return Interlocked.Increment(ref reference).ToString(CultureInfo.InvariantCulture);
        }

        internal string CurrentAccessToken { get { return accessToken(); } }

        internal int PendingPushCount
        {
            get { lock (gate) return pending.Count; }
        }

        internal async Task RecoverChannelAsync(RealtimeChannel channel,
            RealtimeChannelRecoveryMode mode, CancellationToken cancellationToken)
        {
            var minimumDelay = mode == RealtimeChannelRecoveryMode.RateLimited
                ? TimeSpan.FromSeconds(10)
                : TimeSpan.Zero;
            for (var attempt = 0; !cancellationToken.IsCancellationRequested; attempt++)
            {
                await DelayForChannelRecoveryAsync(attempt, minimumDelay, cancellationToken);

                if (mode == RealtimeChannelRecoveryMode.RefreshToken)
                {
                    if (refreshAccessToken == null)
                    {
                        LogRecoveryStopped(channel, "Auth token refresh is unavailable.");
                        return;
                    }
                    var refreshed = await refreshAccessToken(cancellationToken);
                    if (!refreshed.IsSuccess)
                    {
                        if (refreshed.Error != null && refreshed.Error.IsRetryable)
                            continue;
                        LogRecoveryStopped(channel, refreshed.Error == null
                            ? "Auth token refresh failed."
                            : refreshed.Error.Message);
                        return;
                    }
                    mode = RealtimeChannelRecoveryMode.Transient;
                    minimumDelay = TimeSpan.Zero;
                }

                // A socket-wide close has its own reconnect loop and will rejoin every channel.
                if (!IsConnected)
                    return;

                var result = await channel.RejoinAfterChannelFailureAsync(cancellationToken);
                if (result.IsSuccess)
                    return;

                var errorMessage = result.Error == null ? null : result.Error.Message;
                if (Contains(errorMessage, "token has expired"))
                {
                    mode = RealtimeChannelRecoveryMode.RefreshToken;
                    continue;
                }
                if (Contains(errorMessage, "too many messages") || Contains(errorMessage, "rate limit"))
                {
                    minimumDelay = TimeSpan.FromSeconds(10);
                    continue;
                }
                if ((result.Error != null && result.Error.IsRetryable) ||
                    Contains(errorMessage, "database") || Contains(errorMessage, "timeout") ||
                    Contains(errorMessage, "server requested disconnect"))
                    continue;

                LogRecoveryStopped(channel, string.IsNullOrWhiteSpace(errorMessage)
                    ? "The channel join was rejected."
                    : errorMessage);
                return;
            }
        }

        internal void ReportRecoveryError(Exception exception)
        {
            if (!(exception is OperationCanceledException))
                OnError(exception);
        }

        internal async Task<SupabaseResult<JObject>> PushAsync(string topic, string eventName,
            JObject payload, TimeSpan timeout, CancellationToken cancellationToken,
            string joinReference = null, string messageReference = null)
        {
            if (!IsConnected)
            {
                var connected = await ConnectAsync(cancellationToken);
                if (!connected.IsSuccess) return SupabaseResult<JObject>.Failure(connected.Error);
            }
            messageReference = messageReference ?? NextReference();
            var completion = new TaskCompletionSource<SupabaseResult<JObject>>();
            lock (gate) pending[messageReference] = completion;
            try
            {
                var send = await SendRawAsync(new JArray(
                    joinReference == null ? JValue.CreateNull() : new JValue(joinReference),
                    messageReference, topic, eventName, payload ?? new JObject()), cancellationToken);
                if (!send.IsSuccess)
                    return SupabaseResult<JObject>.Failure(send.Error);
                if (completion.Task.IsCompleted)
                    return await completion.Task;
                using (cancellationToken.Register(delegate { completion.TrySetCanceled(); }))
                {
                    var timer = SupabaseRuntimeHost.Delay(timeout, CancellationToken.None);
                    var finished = await Task.WhenAny(completion.Task, timer);
                    if (finished == completion.Task) return await completion.Task;
                }
                return SupabaseResult<JObject>.Failure(SupabaseError.Create(SupabaseService.Realtime,
                    SupabaseErrorKind.Timeout, "Realtime did not acknowledge the message before timeout.",
                    retryable: true));
            }
            finally
            {
                lock (gate) pending.Remove(messageReference);
            }
        }

        internal async Task<SupabaseResult> SendEventAsync(string topic, string eventName,
            JObject payload, CancellationToken cancellationToken, string joinReference = null)
        {
            if (!IsConnected)
                return SupabaseResult.Failure(SupabaseError.Create(SupabaseService.Realtime,
                    SupabaseErrorKind.Transport, "Realtime is not connected.", retryable: true));
            var messageReference = NextReference();
            return await SendRawAsync(new JArray(joinReference == null ? JValue.CreateNull() : new JValue(joinReference), messageReference, topic, eventName,
                payload ?? new JObject()), cancellationToken);
        }

        private async Task<SupabaseResult> SendRawAsync(JArray message, CancellationToken cancellationToken)
        {
            await sendGate.WaitAsync(cancellationToken);
            try
            {
                await socket.SendTextAsync(message.ToString(Newtonsoft.Json.Formatting.None), cancellationToken);
                return SupabaseResult.Success();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                return SupabaseResult.Failure(SupabaseError.Create(SupabaseService.Realtime,
                    SupabaseErrorKind.Transport, "Realtime message could not be sent.",
                    details: exception.Message, retryable: true));
            }
            finally { sendGate.Release(); }
        }

        private Task DelayForChannelRecoveryAsync(int attempt, TimeSpan minimumDelay,
            CancellationToken cancellationToken)
        {
            var delays = new[] { 1, 2, 5, 10 };
            var delay = TimeSpan.FromSeconds(delays[Math.Min(attempt, delays.Length - 1)]);
            if (delay < minimumDelay)
                delay = minimumDelay;
            return options.RealtimeRecoveryDelay == null
                ? SupabaseRuntimeHost.Delay(delay, cancellationToken)
                : options.RealtimeRecoveryDelay(delay, cancellationToken);
        }

        private void LogRecoveryStopped(RealtimeChannel channel, string reason)
        {
            options.Logger.Log(SupabaseLogLevel.Warning,
                "Realtime channel recovery stopped for " + channel.Topic + ": " +
                SupabaseHttp.Redact(reason));
        }

        private static bool Contains(string value, string expected)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void OnMessage(string text)
        {
            try
            {
                var message = JArray.Parse(text);
                if (message.Count < 5) return;
                var joinReference = message[0].Type == JTokenType.Null ? null : (string)message[0];
                var messageReference = message[1].Type == JTokenType.Null ? null : (string)message[1];
                var topic = (string)message[2];
                var eventName = (string)message[3];
                var payload = message[4] as JObject ?? new JObject();
                if (eventName == "phx_reply" && !string.IsNullOrEmpty(messageReference))
                {
                    TaskCompletionSource<SupabaseResult<JObject>> completion = null;
                    lock (gate)
                    {
                        if (pending.TryGetValue(messageReference, out completion)) pending.Remove(messageReference);
                    }
                    if (completion != null)
                    {
                        var status = (string)payload["status"];
                        var response = payload["response"] as JObject ?? payload;
                        if (status == "ok") completion.TrySetResult(SupabaseResult<JObject>.Success(response));
                        else completion.TrySetResult(SupabaseResult<JObject>.Failure(SupabaseError.Create(
                            SupabaseService.Realtime, SupabaseErrorKind.Protocol,
                            (string)response["reason"] ?? "Realtime rejected the message.",
                            rawResponse: payload.ToString(Newtonsoft.Json.Formatting.None))));
                    }
                    return;
                }
                RealtimeChannel channel;
                lock (gate) channels.TryGetValue(topic, out channel);
                if (channel != null)
                    SupabaseRuntimeHost.Post(delegate { channel.Dispatch(eventName, payload, joinReference); });
            }
            catch (Exception exception) { OnError(exception); }
        }

        private void OnOpened()
        {
            var handler = Connected;
            if (handler != null) SupabaseRuntimeHost.Post(handler);
        }

        private void OnClosed(int code, string reason)
        {
            FailPendingPushes(SupabaseError.Create(SupabaseService.Realtime,
                SupabaseErrorKind.Transport,
                "Realtime disconnected before acknowledging the message.",
                details: reason, retryable: !manualClose));
            RealtimeChannel[] snapshot;
            lock (gate)
            {
                snapshot = new RealtimeChannel[channels.Count];
                channels.Values.CopyTo(snapshot, 0);
            }
            foreach (var channel in snapshot) channel.NotifySocketClosed();
            var handler = Disconnected;
            if (handler != null) SupabaseRuntimeHost.Post(delegate { handler(code, reason); });
            if (!manualClose && !disposed) ReconnectLoopAsync().Forget(OnError);
        }

        private void FailPendingPushes(SupabaseError error)
        {
            TaskCompletionSource<SupabaseResult<JObject>>[] snapshot;
            lock (gate)
            {
                snapshot = new TaskCompletionSource<SupabaseResult<JObject>>[pending.Count];
                pending.Values.CopyTo(snapshot, 0);
                pending.Clear();
            }
            foreach (var completion in snapshot)
                completion.TrySetResult(SupabaseResult<JObject>.Failure(error));
        }

        private void OnError(Exception exception)
        {
            options.Logger.Log(SupabaseLogLevel.Error, "Supabase Realtime transport error.", exception);
            var handler = Error;
            if (handler != null) SupabaseRuntimeHost.Post(delegate { handler(exception); });
        }

        private async Task ReconnectLoopAsync()
        {
            lock (gate)
            {
                if (reconnecting) return;
                reconnecting = true;
            }
            try
            {
                var delays = new[] { 1, 2, 5, 10, 20 };
                for (var attempt = 0; !manualClose && !disposed; attempt++)
                {
                    await SupabaseRuntimeHost.Delay(TimeSpan.FromSeconds(delays[Math.Min(attempt, delays.Length - 1)]),
                        CancellationToken.None);
                    var result = await ConnectAsync(CancellationToken.None);
                    if (!result.IsSuccess) continue;
                    RealtimeChannel[] snapshot;
                    lock (gate)
                    {
                        snapshot = new RealtimeChannel[channels.Count];
                        channels.Values.CopyTo(snapshot, 0);
                    }
                    var restored = true;
                    foreach (var channel in snapshot)
                    {
                        var subscribed = await channel.ResubscribeAsync();
                        if (!subscribed.IsSuccess) restored = false;
                    }
                    if (restored && IsConnected) return;
                }
            }
            finally { lock (gate) reconnecting = false; }
        }

        private void StartHeartbeat(CancellationToken cancellationToken)
        {
            HeartbeatLoopAsync(cancellationToken).Forget(OnError);
        }

        private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await SupabaseRuntimeHost.Delay(options.RealtimeHeartbeatInterval, cancellationToken);
                if (!IsConnected) continue;
                var heartbeat = await PushAsync("phoenix", "heartbeat", new JObject(),
                    TimeSpan.FromSeconds(10), cancellationToken);
                if (!heartbeat.IsSuccess && socket != null)
                    await socket.CloseAsync(1001, "Heartbeat acknowledgement timed out", cancellationToken);
            }
        }

        private IWebSocketTransport CreateTransport()
        {
            if (options.WebSocketTransportFactory != null) return options.WebSocketTransportFactory();
#if UNITY_WEBGL && !UNITY_EDITOR
            return new WebGlWebSocketTransport();
#else
            return new NativeWebSocketTransport();
#endif
        }

        private Uri BuildSocketUri()
        {
            var root = endpoint.AbsoluteUri.TrimEnd('/');
            if (!root.EndsWith("/websocket", StringComparison.OrdinalIgnoreCase)) root += "/websocket";
            return SupabaseHttp.Combine(new Uri(root), string.Empty, new[]
            {
                new KeyValuePair<string, string>("apikey", options.PublishableKey),
                new KeyValuePair<string, string>("vsn", "1.0.0")
            });
        }

        private IReadOnlyDictionary<string, string> BuildHeaders()
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            SupabaseHttp.ApplyClientHeaders(headers, options, null);
            // Realtime authenticates users in the Phoenix join payload. Keeping JWTs out of the
            // WebSocket handshake matches browser behavior and minimizes credential exposure.
            headers.Remove("Authorization");
            return headers;
        }

        private void DetachAndDispose(IWebSocketTransport value)
        {
            value.Opened -= OnOpened;
            value.MessageReceived -= OnMessage;
            value.Closed -= OnClosed;
            value.Error -= OnError;
            value.Dispose();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            manualClose = true;
            if (lifetime != null) lifetime.Cancel();
            FailPendingPushes(SupabaseError.Create(SupabaseService.Realtime,
                SupabaseErrorKind.Transport,
                "Realtime was disposed before acknowledging the message."));
            RealtimeChannel[] snapshot;
            lock (gate)
            {
                snapshot = new RealtimeChannel[channels.Count];
                channels.Values.CopyTo(snapshot, 0);
            }
            foreach (var channel in snapshot) channel.NotifySocketClosed();
            if (socket != null) DetachAndDispose(socket);
            if (lifetime != null) lifetime.Dispose();
            sendGate.Dispose();
            connectionGate.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException("RealtimeClient");
        }
    }
}
