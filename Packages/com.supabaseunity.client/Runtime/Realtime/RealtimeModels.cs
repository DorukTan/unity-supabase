using System;
using Newtonsoft.Json.Linq;

namespace Supabase.Unity
{
    public enum RealtimeChannelState { Closed, Joining, Joined, Leaving, Errored }
    public enum RealtimePostgresEvent { All, Insert, Update, Delete }

    public sealed class RealtimeChannelConfig
    {
        public bool BroadcastAcknowledge { get; set; }
        public bool BroadcastSelf { get; set; }
        public string PresenceKey { get; set; }
        public bool Private { get; set; }
    }

    public sealed class RealtimePostgresChangeFilter
    {
        public RealtimePostgresEvent Event { get; set; } = RealtimePostgresEvent.All;
        public string Schema { get; set; } = "public";
        public string Table { get; set; }
        public string Filter { get; set; }

        internal JObject ToJson()
        {
            var result = new JObject
            {
                ["event"] = Event == RealtimePostgresEvent.All ? "*" : Event.ToString().ToUpperInvariant(),
                ["schema"] = string.IsNullOrWhiteSpace(Schema) ? "public" : Schema
            };
            if (!string.IsNullOrWhiteSpace(Table)) result["table"] = Table;
            if (!string.IsNullOrWhiteSpace(Filter)) result["filter"] = Filter;
            return result;
        }
    }

    public sealed class RealtimePostgresChange
    {
        public RealtimePostgresEvent Event { get; internal set; }
        public string Schema { get; internal set; }
        public string Table { get; internal set; }
        public DateTimeOffset? CommitTimestamp { get; internal set; }
        public JObject NewRecord { get; internal set; }
        public JObject OldRecord { get; internal set; }
        public JToken Errors { get; internal set; }
        public JObject RawPayload { get; internal set; }

        public T New<T>() { return NewRecord == null ? default(T) : NewRecord.ToObject<T>(); }
        public T Old<T>() { return OldRecord == null ? default(T) : OldRecord.ToObject<T>(); }
    }

    public sealed class RealtimePresenceChange
    {
        public JObject Joins { get; internal set; }
        public JObject Leaves { get; internal set; }
        public JObject RawPayload { get; internal set; }
    }

    /// <summary>A server notice about a Realtime channel or one of its extensions.</summary>
    public sealed class RealtimeSystemMessage
    {
        public string Message { get; internal set; }
        public string Status { get; internal set; }
        public string Extension { get; internal set; }
        public string Channel { get; internal set; }
        public JObject RawPayload { get; internal set; }

        public bool IsError
        {
            get
            {
                return string.Equals(Status, "error", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(Status, "timeout", StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    internal enum RealtimeChannelRecoveryMode
    {
        None,
        Transient,
        RefreshToken,
        RateLimited,
        Manual
    }
}
