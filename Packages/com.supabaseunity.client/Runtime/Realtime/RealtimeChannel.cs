using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Supabase.Unity
{
    public sealed class RealtimeChannel
    {
        private sealed class PostgresBinding
        {
            internal RealtimePostgresChangeFilter Filter;
            internal Action<RealtimePostgresChange> Callback;
            internal long? Id;
        }

        private readonly RealtimeClient client;
        private readonly RealtimeChannelConfig config;
        private readonly List<PostgresBinding> postgresBindings = new List<PostgresBinding>();
        private readonly Dictionary<string, List<Action<JToken>>> broadcastBindings
            = new Dictionary<string, List<Action<JToken>>>(StringComparer.Ordinal);
        private readonly SemaphoreSlim subscriptionGate = new SemaphoreSlim(1, 1);
        private readonly object recoveryGate = new object();
        private CancellationTokenSource recoveryCancellation;
        private bool recoveryRunning;
        private RealtimeChannelRecoveryMode pendingRecoveryMode;
        private bool subscribedBeforeDisconnect;

        public string Topic { get; private set; }
        internal string JoinReference { get; private set; }
        public RealtimeChannelState State { get; private set; } = RealtimeChannelState.Closed;
        public JObject PresenceState { get; private set; } = new JObject();
        public event Action<JObject> PresenceSynchronized;
        public event Action<RealtimePresenceChange> PresenceChanged;
        public event Action<RealtimeSystemMessage> SystemMessageReceived;
        public event Action<RealtimeChannelState> StateChanged;

        internal RealtimeChannel(RealtimeClient client, string topic, RealtimeChannelConfig config)
        {
            this.client = client;
            Topic = topic;
            this.config = config;
        }

        public RealtimeChannel OnPostgresChanges(RealtimePostgresChangeFilter filter,
            Action<RealtimePostgresChange> callback)
        {
            if (filter == null) throw new ArgumentNullException("filter");
            if (callback == null) throw new ArgumentNullException("callback");
            if (State == RealtimeChannelState.Joined)
                throw new InvalidOperationException("Postgres bindings must be configured before subscribing.");
            postgresBindings.Add(new PostgresBinding { Filter = filter, Callback = callback });
            return this;
        }

        public RealtimeChannel OnBroadcast(string eventName, Action<JToken> callback)
        {
            if (string.IsNullOrWhiteSpace(eventName)) throw new ArgumentException("Event cannot be empty.", "eventName");
            if (callback == null) throw new ArgumentNullException("callback");
            List<Action<JToken>> callbacks;
            if (!broadcastBindings.TryGetValue(eventName, out callbacks))
            {
                callbacks = new List<Action<JToken>>();
                broadcastBindings[eventName] = callbacks;
            }
            callbacks.Add(callback);
            return this;
        }

        public async Task<SupabaseResult> SubscribeAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            CancelRecovery();
            return await SubscribeCoreAsync(cancellationToken, true, false);
        }

        public async Task<SupabaseResult> UnsubscribeAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            CancelRecovery();
            subscribedBeforeDisconnect = false;
            await subscriptionGate.WaitAsync(cancellationToken);
            try
            {
                if (State == RealtimeChannelState.Closed) return SupabaseResult.Success();
                SetState(RealtimeChannelState.Leaving);
                if (!client.IsConnected)
                {
                    JoinReference = null;
                    SetState(RealtimeChannelState.Closed);
                    return SupabaseResult.Success();
                }
                var response = await client.PushAsync(Topic, "phx_leave", new JObject(),
                    TimeSpan.FromSeconds(10), cancellationToken, JoinReference);
                JoinReference = null;
                SetState(response.IsSuccess ? RealtimeChannelState.Closed : RealtimeChannelState.Errored);
                return response.IsSuccess ? SupabaseResult.Success(response.Metadata) :
                    SupabaseResult.Failure(response.Error, response.Metadata);
            }
            finally
            {
                subscriptionGate.Release();
            }
        }

        public async Task<SupabaseResult> SendBroadcastAsync(string eventName, object payload,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (State != RealtimeChannelState.Joined)
                return SupabaseResult.Failure(SupabaseError.Create(SupabaseService.Realtime,
                    SupabaseErrorKind.Protocol, "Subscribe to the channel before broadcasting."));
            var body = new JObject
            {
                ["type"] = "broadcast",
                ["event"] = eventName,
                ["payload"] = payload == null ? JValue.CreateNull() : JToken.FromObject(payload)
            };
            if (!config.BroadcastAcknowledge)
                return await client.SendEventAsync(Topic, "broadcast", body, cancellationToken, JoinReference);
            var acknowledged = await client.PushAsync(Topic, "broadcast", body,
                TimeSpan.FromSeconds(10), cancellationToken, JoinReference);
            return acknowledged.IsSuccess
                ? SupabaseResult.Success(acknowledged.Metadata)
                : SupabaseResult.Failure(acknowledged.Error, acknowledged.Metadata);
        }

        public Task<SupabaseResult> TrackAsync(object state,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return client.SendEventAsync(Topic, "presence", new JObject
            {
                ["type"] = "presence", ["event"] = "track",
                ["payload"] = state == null ? new JObject() : JObject.FromObject(state)
            }, cancellationToken, JoinReference);
        }

        public Task<SupabaseResult> UntrackAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return client.SendEventAsync(Topic, "presence", new JObject
            {
                ["type"] = "presence", ["event"] = "untrack"
            }, cancellationToken, JoinReference);
        }

        internal async Task<SupabaseResult> ResubscribeAsync()
        {
            if (!subscribedBeforeDisconnect) return SupabaseResult.Success();
            CancelRecovery();
            State = RealtimeChannelState.Closed;
            JoinReference = null;
            return await SubscribeCoreAsync(CancellationToken.None, false, true);
        }

        internal void NotifySocketClosed()
        {
            CancelRecovery();
            if (State == RealtimeChannelState.Joined || State == RealtimeChannelState.Joining)
                SetState(RealtimeChannelState.Errored);
        }

        internal async Task<SupabaseResult> RejoinAfterChannelFailureAsync(
            CancellationToken cancellationToken)
        {
            JoinReference = null;
            SetState(RealtimeChannelState.Closed);
            return await SubscribeCoreAsync(cancellationToken, false, true);
        }

        internal void Dispatch(string eventName, JObject payload, string joinReference = null)
        {
            if ((eventName == "system" || eventName == "phx_error" || eventName == "phx_close") &&
                !string.IsNullOrEmpty(joinReference) &&
                !string.Equals(joinReference, JoinReference, StringComparison.Ordinal))
                return;
            if (eventName == "postgres_changes")
            {
                DispatchPostgres(payload);
                return;
            }
            if (eventName == "broadcast")
            {
                var name = (string)payload["event"];
                List<Action<JToken>> callbacks;
                if (name != null && broadcastBindings.TryGetValue(name, out callbacks))
                    foreach (var callback in callbacks.ToArray()) callback(payload["payload"]);
                return;
            }
            if (eventName == "presence_state")
            {
                PresenceState = payload;
                var synchronized = PresenceSynchronized;
                if (synchronized != null) synchronized(PresenceState);
                return;
            }
            if (eventName == "presence_diff")
            {
                var change = new RealtimePresenceChange
                {
                    Joins = payload["joins"] as JObject ?? new JObject(),
                    Leaves = payload["leaves"] as JObject ?? new JObject(),
                    RawPayload = payload
                };
                ApplyPresenceDiff(change);
                var changed = PresenceChanged;
                if (changed != null) changed(change);
                var synchronized = PresenceSynchronized;
                if (synchronized != null) synchronized(PresenceState);
                return;
            }
            if (eventName == "system")
            {
                DispatchSystemMessage(payload);
                return;
            }
            if (eventName == "phx_error")
            {
                HandleChannelFailure(RealtimeChannelState.Errored, false);
                return;
            }
            if (eventName == "phx_close")
                HandleChannelFailure(RealtimeChannelState.Closed, true);
        }

        private async Task<SupabaseResult> SubscribeCoreAsync(
            CancellationToken cancellationToken, bool rememberSubscription, bool requireSubscription)
        {
            await subscriptionGate.WaitAsync(cancellationToken);
            try
            {
                if (requireSubscription && !subscribedBeforeDisconnect)
                    return SupabaseResult.Success();
                if (State == RealtimeChannelState.Joined)
                    return SupabaseResult.Success();

                SetState(RealtimeChannelState.Joining);
                var payload = BuildJoinPayload();
                var joinReference = client.NextReference();
                var response = await client.PushAsync(Topic, "phx_join", payload,
                    TimeSpan.FromSeconds(10), cancellationToken, null, joinReference);
                if (!response.IsSuccess)
                {
                    SetState(RealtimeChannelState.Errored);
                    return SupabaseResult.Failure(response.Error, response.Metadata);
                }

                JoinReference = joinReference;
                ApplyPostgresBindingIds(response.Data);
                pendingRecoveryMode = RealtimeChannelRecoveryMode.None;
                SetState(RealtimeChannelState.Joined);
                if (rememberSubscription)
                    subscribedBeforeDisconnect = true;
                return SupabaseResult.Success(response.Metadata);
            }
            finally
            {
                subscriptionGate.Release();
            }
        }

        private void DispatchSystemMessage(JObject payload)
        {
            var message = new RealtimeSystemMessage
            {
                Message = (string)payload["message"],
                Status = (string)payload["status"],
                Extension = (string)payload["extension"],
                Channel = (string)payload["channel"] ?? Topic,
                RawPayload = payload
            };
            var handler = SystemMessageReceived;
            if (handler != null) handler(message);

            if (!message.IsError ||
                !string.Equals(message.Extension, "system", StringComparison.OrdinalIgnoreCase))
                return;
            pendingRecoveryMode = ClassifySystemFailure(message.Message);
        }

        private void HandleChannelFailure(RealtimeChannelState state, bool clearPendingMode)
        {
            JoinReference = null;
            SetState(state);
            var mode = pendingRecoveryMode == RealtimeChannelRecoveryMode.None
                ? RealtimeChannelRecoveryMode.Transient
                : pendingRecoveryMode;
            if (clearPendingMode)
                pendingRecoveryMode = RealtimeChannelRecoveryMode.None;
            RequestRecovery(mode);
        }

        private void RequestRecovery(RealtimeChannelRecoveryMode mode)
        {
            CancellationTokenSource cancellation;
            lock (recoveryGate)
            {
                if (!subscribedBeforeDisconnect || recoveryRunning ||
                    mode == RealtimeChannelRecoveryMode.None || mode == RealtimeChannelRecoveryMode.Manual)
                    return;
                recoveryRunning = true;
                recoveryCancellation = new CancellationTokenSource();
                cancellation = recoveryCancellation;
            }
            RunRecoveryAsync(mode, cancellation).Forget(client.ReportRecoveryError);
        }

        private async Task RunRecoveryAsync(RealtimeChannelRecoveryMode mode,
            CancellationTokenSource cancellation)
        {
            try
            {
                await client.RecoverChannelAsync(this, mode, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                // Unsubscribe, disposal, or a socket-wide reconnect superseded this recovery.
            }
            finally
            {
                lock (recoveryGate)
                {
                    if (recoveryCancellation == cancellation)
                    {
                        recoveryCancellation.Dispose();
                        recoveryCancellation = null;
                        recoveryRunning = false;
                    }
                }
            }
        }

        private void CancelRecovery()
        {
            lock (recoveryGate)
            {
                if (recoveryCancellation != null)
                    recoveryCancellation.Cancel();
            }
        }

        private static RealtimeChannelRecoveryMode ClassifySystemFailure(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return RealtimeChannelRecoveryMode.Manual;
            var normalized = message.ToLowerInvariant();
            if (normalized.Contains("token has expired"))
                return RealtimeChannelRecoveryMode.RefreshToken;
            if (normalized.Contains("too many messages") || normalized.Contains("rate limit"))
                return RealtimeChannelRecoveryMode.RateLimited;
            if (normalized.Contains("server requested disconnect") ||
                normalized.Contains("replication connection timeout"))
                return RealtimeChannelRecoveryMode.Transient;
            return RealtimeChannelRecoveryMode.Manual;
        }

        private JObject BuildJoinPayload()
        {
            var changes = new JArray();
            foreach (var binding in postgresBindings) changes.Add(binding.Filter.ToJson());
            var payload = new JObject
            {
                ["config"] = new JObject
                {
                    ["broadcast"] = new JObject
                    {
                        ["ack"] = config.BroadcastAcknowledge,
                        ["self"] = config.BroadcastSelf
                    },
                    ["presence"] = new JObject { ["key"] = config.PresenceKey ?? string.Empty },
                    ["postgres_changes"] = changes,
                    ["private"] = config.Private
                }
            };
            var token = client.CurrentAccessToken;
            if (!string.IsNullOrWhiteSpace(token)) payload["access_token"] = token;
            return payload;
        }

        private void DispatchPostgres(JObject payload)
        {
            var data = payload["data"] as JObject ?? payload;
            var matchingIds = payload["ids"] as JArray;
            var eventText = ((string)data["type"] ?? (string)data["eventType"] ?? "*").ToUpperInvariant();
            RealtimePostgresEvent eventType;
            if (!Enum.TryParse(eventText, true, out eventType)) eventType = RealtimePostgresEvent.All;
            DateTimeOffset timestamp;
            DateTimeOffset? parsedTimestamp = DateTimeOffset.TryParse((string)data["commit_timestamp"], out timestamp)
                ? timestamp : (DateTimeOffset?)null;
            var change = new RealtimePostgresChange
            {
                Event = eventType,
                Schema = (string)data["schema"],
                Table = (string)data["table"],
                CommitTimestamp = parsedTimestamp,
                NewRecord = data["record"] as JObject ?? data["new"] as JObject,
                OldRecord = data["old_record"] as JObject ?? data["old"] as JObject,
                Errors = data["errors"],
                RawPayload = payload
            };
            foreach (var binding in postgresBindings.ToArray())
            {
                if (matchingIds != null && binding.Id.HasValue &&
                    !ContainsId(matchingIds, binding.Id.Value)) continue;
                if (binding.Filter.Event != RealtimePostgresEvent.All && binding.Filter.Event != change.Event) continue;
                if (!string.IsNullOrWhiteSpace(binding.Filter.Schema) && binding.Filter.Schema != change.Schema) continue;
                if (!string.IsNullOrWhiteSpace(binding.Filter.Table) && binding.Filter.Table != change.Table) continue;
                binding.Callback(change);
            }
        }

        private void ApplyPresenceDiff(RealtimePresenceChange change)
        {
            foreach (var join in change.Joins)
            {
                var incoming = join.Value as JObject;
                var existing = PresenceState[join.Key] as JObject;
                if (incoming == null || existing == null)
                {
                    PresenceState[join.Key] = join.Value.DeepClone();
                    continue;
                }
                MergePresenceMetas(existing, incoming);
            }

            foreach (var leave in change.Leaves)
            {
                var existing = PresenceState[leave.Key] as JObject;
                var leaving = leave.Value as JObject;
                if (existing == null || leaving == null)
                {
                    PresenceState.Remove(leave.Key);
                    continue;
                }
                RemovePresenceMetas(existing, leaving);
                var remaining = existing["metas"] as JArray;
                if (remaining == null || remaining.Count == 0)
                    PresenceState.Remove(leave.Key);
            }
        }

        private void ApplyPostgresBindingIds(JObject response)
        {
            foreach (var binding in postgresBindings) binding.Id = null;
            var serverBindings = response == null ? null : response["postgres_changes"] as JArray;
            if (serverBindings == null) return;
            foreach (var serverToken in serverBindings)
            {
                var server = serverToken as JObject;
                var id = (long?)server?["id"];
                if (!id.HasValue) continue;
                foreach (var binding in postgresBindings)
                {
                    if (binding.Id.HasValue || !MatchesServerBinding(binding.Filter, server)) continue;
                    binding.Id = id;
                    break;
                }
            }
        }

        private static bool MatchesServerBinding(RealtimePostgresChangeFilter filter, JObject server)
        {
            var expected = filter.ToJson();
            return string.Equals((string)expected["event"], (string)server["event"], StringComparison.OrdinalIgnoreCase) &&
                   string.Equals((string)expected["schema"], (string)server["schema"], StringComparison.Ordinal) &&
                   string.Equals((string)expected["table"] ?? string.Empty,
                       (string)server["table"] ?? string.Empty, StringComparison.Ordinal) &&
                   string.Equals((string)expected["filter"] ?? string.Empty,
                       (string)server["filter"] ?? string.Empty, StringComparison.Ordinal);
        }

        private static bool ContainsId(JArray ids, long id)
        {
            foreach (var token in ids)
                if ((long?)token == id) return true;
            return false;
        }

        private static void MergePresenceMetas(JObject existing, JObject incoming)
        {
            var currentMetas = existing["metas"] as JArray;
            var joinedMetas = incoming["metas"] as JArray;
            if (currentMetas == null || joinedMetas == null)
            {
                existing.Replace(incoming.DeepClone());
                return;
            }
            foreach (var joined in joinedMetas)
            {
                var reference = PresenceReference(joined);
                var duplicate = false;
                foreach (var current in currentMetas)
                    if (!string.IsNullOrEmpty(reference) && PresenceReference(current) == reference) duplicate = true;
                if (!duplicate) currentMetas.Add(joined.DeepClone());
            }
        }

        private static void RemovePresenceMetas(JObject existing, JObject leaving)
        {
            var currentMetas = existing["metas"] as JArray;
            var leavingMetas = leaving["metas"] as JArray;
            if (currentMetas == null || leavingMetas == null)
            {
                currentMetas?.RemoveAll();
                return;
            }
            for (var index = currentMetas.Count - 1; index >= 0; index--)
            {
                var currentReference = PresenceReference(currentMetas[index]);
                foreach (var leavingMeta in leavingMetas)
                {
                    if (!string.IsNullOrEmpty(currentReference) &&
                        currentReference == PresenceReference(leavingMeta))
                    {
                        currentMetas.RemoveAt(index);
                        break;
                    }
                }
            }
        }

        private static string PresenceReference(JToken value)
        {
            return (string)value?["phx_ref"] ?? (string)value?["phx_ref_prev"];
        }

        private void SetState(RealtimeChannelState value)
        {
            State = value;
            var handler = StateChanged;
            if (handler != null) SupabaseRuntimeHost.Post(delegate { handler(value); });
        }
    }
}
