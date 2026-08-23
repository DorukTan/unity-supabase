using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Supabase.Unity.Tests
{
    internal sealed class RecordingHttpTransport : IHttpTransport
    {
        private readonly Queue<Func<SupabaseHttpRequest, SupabaseHttpResponse>> queued
            = new Queue<Func<SupabaseHttpRequest, SupabaseHttpResponse>>();

        internal readonly List<SupabaseHttpRequest> Requests = new List<SupabaseHttpRequest>();

        internal SupabaseHttpRequest LastRequest
        {
            get { return Requests.Count == 0 ? null : Requests[Requests.Count - 1]; }
        }

        internal Func<SupabaseHttpRequest, SupabaseHttpResponse> Response = delegate
        {
            return new SupabaseHttpResponse
            {
                StatusCode = 200,
                Body = System.Text.Encoding.UTF8.GetBytes("[]")
            };
        };

        /// <summary>Queues one response. Queued responses are consumed before <see cref="Response"/>.</summary>
        internal RecordingHttpTransport Enqueue(int statusCode, string body)
        {
            queued.Enqueue(delegate
            {
                return new SupabaseHttpResponse
                {
                    StatusCode = statusCode,
                    Body = System.Text.Encoding.UTF8.GetBytes(body ?? string.Empty)
                };
            });
            return this;
        }

        internal RecordingHttpTransport Enqueue(Func<SupabaseHttpRequest, SupabaseHttpResponse> responder)
        {
            queued.Enqueue(responder);
            return this;
        }

        public Task<SupabaseHttpResponse> SendAsync(SupabaseHttpRequest request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var responder = queued.Count > 0 ? queued.Dequeue() : Response;
            return Task.FromResult(responder(request));
        }

        public void Dispose() { }
    }

    internal sealed class ThrowingHttpTransport : IHttpTransport
    {
        private readonly Exception exception;

        internal ThrowingHttpTransport(Exception exception)
        {
            this.exception = exception ?? throw new ArgumentNullException("exception");
        }

        public Task<SupabaseHttpResponse> SendAsync(SupabaseHttpRequest request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw exception;
        }

        public void Dispose() { }
    }

    /// <summary>
    /// Serves queued responses immediately, then holds every later request open until
    /// <see cref="Release"/> is called. <see cref="RecordingHttpTransport"/> always completes
    /// synchronously, so a call made against it is finished before the next one starts; this
    /// transport is what makes a genuinely still-in-flight request observable in a test.
    /// </summary>
    internal sealed class GatedHttpTransport : IHttpTransport
    {
        private readonly Queue<SupabaseHttpResponse> queued = new Queue<SupabaseHttpResponse>();

        private readonly TaskCompletionSource<SupabaseHttpResponse> gate
            = new TaskCompletionSource<SupabaseHttpResponse>();

        internal readonly List<SupabaseHttpRequest> Requests = new List<SupabaseHttpRequest>();

        internal int RequestCount { get { return Requests.Count; } }

        internal SupabaseHttpRequest LastRequest
        {
            get { return Requests.Count == 0 ? null : Requests[Requests.Count - 1]; }
        }

        /// <summary>Queues one response that is served without touching the gate.</summary>
        internal GatedHttpTransport Enqueue(int statusCode, string body)
        {
            queued.Enqueue(NewResponse(statusCode, body));
            return this;
        }

        public Task<SupabaseHttpResponse> SendAsync(SupabaseHttpRequest request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (queued.Count > 0)
                return Task.FromResult(queued.Dequeue());
            if (cancellationToken.CanBeCanceled)
                cancellationToken.Register(delegate { gate.TrySetCanceled(); });
            return gate.Task;
        }

        /// <summary>Completes every gated request with the same response.</summary>
        internal void Release(int statusCode, string body)
        {
            gate.TrySetResult(NewResponse(statusCode, body));
        }

        private static SupabaseHttpResponse NewResponse(int statusCode, string body)
        {
            return new SupabaseHttpResponse
            {
                StatusCode = statusCode,
                Body = System.Text.Encoding.UTF8.GetBytes(body ?? string.Empty)
            };
        }

        public void Dispose() { }
    }

    /// <summary>
    /// Serves ordinary requests immediately while holding refresh-token requests open. This
    /// allows tests to complete a newer sign-in or sign-out before the older refresh returns.
    /// </summary>
    internal sealed class RefreshGatedHttpTransport : IHttpTransport
    {
        private readonly Queue<SupabaseHttpResponse> immediate = new Queue<SupabaseHttpResponse>();
        private readonly TaskCompletionSource<SupabaseHttpResponse> refreshGate
            = new TaskCompletionSource<SupabaseHttpResponse>();

        internal readonly List<SupabaseHttpRequest> Requests = new List<SupabaseHttpRequest>();

        internal RefreshGatedHttpTransport Enqueue(int statusCode, string body)
        {
            immediate.Enqueue(NewResponse(statusCode, body));
            return this;
        }

        public Task<SupabaseHttpResponse> SendAsync(SupabaseHttpRequest request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (request.Uri.Query.IndexOf("grant_type=refresh_token", StringComparison.Ordinal) >= 0)
                return refreshGate.Task;
            if (immediate.Count == 0)
                throw new InvalidOperationException("No immediate HTTP response was queued.");
            return Task.FromResult(immediate.Dequeue());
        }

        internal void ReleaseRefresh(int statusCode, string body)
        {
            refreshGate.TrySetResult(NewResponse(statusCode, body));
        }

        private static SupabaseHttpResponse NewResponse(int statusCode, string body)
        {
            return new SupabaseHttpResponse
            {
                StatusCode = statusCode,
                Body = System.Text.Encoding.UTF8.GetBytes(body ?? string.Empty)
            };
        }

        public void Dispose() { }
    }

    /// <summary>
    /// Holds the first request whose URI contains a selected fragment while serving every other
    /// request from an immediate queue. This makes out-of-order Auth responses deterministic.
    /// </summary>
    internal sealed class OneRequestGatedHttpTransport : IHttpTransport
    {
        private readonly string gatedUriFragment;
        private readonly Queue<SupabaseHttpResponse> immediate = new Queue<SupabaseHttpResponse>();
        private readonly TaskCompletionSource<SupabaseHttpResponse> gate
            = new TaskCompletionSource<SupabaseHttpResponse>();
        private bool requestGated;

        internal readonly List<SupabaseHttpRequest> Requests = new List<SupabaseHttpRequest>();

        internal OneRequestGatedHttpTransport(string gatedUriFragment)
        {
            this.gatedUriFragment = gatedUriFragment;
        }

        internal OneRequestGatedHttpTransport Enqueue(int statusCode, string body)
        {
            immediate.Enqueue(NewResponse(statusCode, body));
            return this;
        }

        public Task<SupabaseHttpResponse> SendAsync(SupabaseHttpRequest request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (!requestGated && request.Uri.AbsoluteUri.IndexOf(gatedUriFragment,
                StringComparison.Ordinal) >= 0)
            {
                requestGated = true;
                return gate.Task;
            }
            if (immediate.Count == 0)
                throw new InvalidOperationException("No immediate HTTP response was queued.");
            return Task.FromResult(immediate.Dequeue());
        }

        internal void Release(int statusCode, string body)
        {
            gate.TrySetResult(NewResponse(statusCode, body));
        }

        private static SupabaseHttpResponse NewResponse(int statusCode, string body)
        {
            return new SupabaseHttpResponse
            {
                StatusCode = statusCode,
                Body = System.Text.Encoding.UTF8.GetBytes(body ?? string.Empty)
            };
        }

        public void Dispose() { }
    }

    /// <summary>
    /// In-memory session storage that can hold one read or mutation open. It captures reads when
    /// they start and applies mutations when they finish, matching the ordering hazards of real
    /// asynchronous platform storage without relying on timing.
    /// </summary>
    internal sealed class GatedSessionStore : ISessionStore
    {
        private readonly object sync = new object();
        private readonly Dictionary<string, string> values = new Dictionary<string, string>();
        private TaskCompletionSource<bool> readGate;
        private TaskCompletionSource<bool> mutationGate;
        private bool gateNextRead;
        private bool gateNextMutation;
        private bool gatedMutationStarted;

        internal string StoredValue
        {
            get
            {
                lock (sync)
                {
                    foreach (var value in values.Values)
                        return value;
                    return null;
                }
            }
        }

        internal bool GatedMutationStarted
        {
            get
            {
                lock (sync) return gatedMutationStarted;
            }
        }

        internal void GateNextGet()
        {
            lock (sync)
            {
                if (gateNextRead)
                    throw new InvalidOperationException("A session-store read is already gated.");
                readGate = new TaskCompletionSource<bool>();
                gateNextRead = true;
            }
        }

        internal void GateNextMutation()
        {
            lock (sync)
            {
                if (gateNextMutation)
                    throw new InvalidOperationException("A session-store mutation is already gated.");
                mutationGate = new TaskCompletionSource<bool>();
                gateNextMutation = true;
                gatedMutationStarted = false;
            }
        }

        internal void ReleaseGet()
        {
            TaskCompletionSource<bool> pending;
            lock (sync) pending = readGate;
            if (pending == null)
                throw new InvalidOperationException("No session-store read is gated.");
            pending.TrySetResult(true);
        }

        internal void ReleaseMutation()
        {
            TaskCompletionSource<bool> pending;
            lock (sync) pending = mutationGate;
            if (pending == null)
                throw new InvalidOperationException("No session-store mutation is gated.");
            pending.TrySetResult(true);
        }

        public async Task<string> GetAsync(
            string key,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string value;
            Task wait = null;
            lock (sync)
            {
                values.TryGetValue(key, out value);
                if (gateNextRead)
                {
                    gateNextRead = false;
                    wait = readGate.Task;
                }
            }
            if (wait != null)
                await WaitWithCancellationAsync(wait, cancellationToken);
            return value;
        }

        public async Task SetAsync(
            string key,
            string value,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            await WaitForMutationAsync(cancellationToken);
            lock (sync) values[key] = value;
        }

        public async Task RemoveAsync(
            string key,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            await WaitForMutationAsync(cancellationToken);
            lock (sync) values.Remove(key);
        }

        private async Task WaitForMutationAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task wait = null;
            lock (sync)
            {
                if (gateNextMutation)
                {
                    gateNextMutation = false;
                    gatedMutationStarted = true;
                    wait = mutationGate.Task;
                }
            }
            if (wait != null)
                await WaitWithCancellationAsync(wait, cancellationToken);
        }

        private static async Task WaitWithCancellationAsync(
            Task task,
            CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled)
            {
                await task;
                return;
            }
            var canceled = new TaskCompletionSource<bool>();
            using (cancellationToken.Register(delegate { canceled.TrySetResult(true); }))
            {
                if (task != await Task.WhenAny(task, canceled.Task))
                    throw new OperationCanceledException(cancellationToken);
            }
            await task;
        }
    }

    internal sealed class FakeCallbackProvider : IAuthCallbackProvider, IAuthCallbackSanitizer
    {
        public event Action<Uri> CallbackReceived;
        internal readonly List<Uri> Opened = new List<Uri>();
        internal readonly List<Uri> Sanitized = new List<Uri>();

        public Uri InitialCallback { get; internal set; }

        public void Open(Uri authorizationUri) { Opened.Add(authorizationUri); }

        public void ClearSensitiveCallback(Uri callbackUri)
        {
            Sanitized.Add(callbackUri);
            if (InitialCallback == callbackUri)
                InitialCallback = null;
        }

        internal void Raise(Uri uri)
        {
            var handler = CallbackReceived;
            if (handler != null) handler(uri);
        }
    }

    internal sealed class RecordingWebSocketTransport : IWebSocketTransport
    {
        internal Uri ConnectedUri;
        internal IReadOnlyDictionary<string, string> ConnectedHeaders;
        internal readonly List<string> Sent = new List<string>();
        internal JObject JoinResponse = new JObject();
        internal bool AcknowledgeBroadcast;
        public SupabaseWebSocketState State { get; private set; } = SupabaseWebSocketState.Closed;
        public event Action Opened;
        public event Action<string> MessageReceived;
        public event Action<int, string> Closed;
        public event Action<Exception> Error;

        public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ConnectedUri = uri;
            ConnectedHeaders = headers == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(headers);
            State = SupabaseWebSocketState.Open;
            var opened = Opened; if (opened != null) opened();
            return Task.CompletedTask;
        }

        public Task SendTextAsync(string message,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            Sent.Add(message);
            var outbound = JArray.Parse(message);
            var eventName = (string)outbound[3];
            if (eventName == "phx_join" || eventName == "phx_leave" ||
                (eventName == "broadcast" && AcknowledgeBroadcast))
            {
                var reply = new JArray(null, outbound[1], outbound[2], "phx_reply", new JObject
                {
                    ["status"] = "ok",
                    ["response"] = eventName == "phx_join" ? JoinResponse : new JObject()
                });
                var received = MessageReceived;
                if (received != null) received(reply.ToString(Newtonsoft.Json.Formatting.None));
            }
            return Task.CompletedTask;
        }

        public Task CloseAsync(int code = 1000, string reason = "Normal closure",
            CancellationToken cancellationToken = default(CancellationToken))
        {
            State = SupabaseWebSocketState.Closed;
            var closed = Closed; if (closed != null) closed(code, reason);
            return Task.CompletedTask;
        }

        internal void RaiseError(Exception exception) { var handler = Error; if (handler != null) handler(exception); }

        internal void RaiseChannelEvent(string joinReference, string topic, string eventName,
            JObject payload = null)
        {
            var handler = MessageReceived;
            if (handler == null) return;
            handler(new JArray(joinReference, null, topic, eventName, payload ?? new JObject())
                .ToString(Newtonsoft.Json.Formatting.None));
        }

        public void Dispose() { State = SupabaseWebSocketState.Closed; }
    }
}
