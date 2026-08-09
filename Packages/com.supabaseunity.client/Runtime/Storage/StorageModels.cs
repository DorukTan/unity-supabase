using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Converters;

namespace Supabase.Unity
{
    public sealed class StorageBucket
    {
        [JsonProperty("id")] public string Id { get; internal set; }
        [JsonProperty("name")] public string Name { get; internal set; }
        [JsonProperty("owner")] public string Owner { get; internal set; }
        [JsonProperty("public")] public bool IsPublic { get; internal set; }
        [JsonProperty("file_size_limit")] public long? FileSizeLimit { get; internal set; }
        [JsonProperty("allowed_mime_types")] public List<string> AllowedMimeTypes { get; internal set; }
        [JsonProperty("created_at")] public DateTimeOffset? CreatedAt { get; internal set; }
        [JsonProperty("updated_at")] public DateTimeOffset? UpdatedAt { get; internal set; }
    }

    public sealed class StorageBucketOptions
    {
        [JsonProperty("public")] public bool IsPublic { get; set; }
        [JsonProperty("file_size_limit")] public long? FileSizeLimit { get; set; }
        [JsonProperty("allowed_mime_types")] public IReadOnlyList<string> AllowedMimeTypes { get; set; }
    }

    public sealed class StorageObject
    {
        [JsonProperty("id")] public string Id { get; internal set; }
        [JsonProperty("name")] public string Name { get; internal set; }
        [JsonProperty("bucket_id")] public string BucketId { get; internal set; }
        [JsonProperty("owner")] public string Owner { get; internal set; }
        [JsonProperty("owner_id")] public string OwnerId { get; internal set; }
        [JsonProperty("version")] public string Version { get; internal set; }
        [JsonProperty("metadata")] public JObject Metadata { get; internal set; }
        [JsonProperty("user_metadata")] public JObject UserMetadata { get; internal set; }
        [JsonProperty("created_at")] public DateTimeOffset? CreatedAt { get; internal set; }
        [JsonProperty("updated_at")] public DateTimeOffset? UpdatedAt { get; internal set; }
        [JsonProperty("last_accessed_at")] public DateTimeOffset? LastAccessedAt { get; internal set; }
    }

    public sealed class StorageUploadOptions
    {
        public string ContentType { get; set; } = "application/octet-stream";
        public string CacheControl { get; set; } = "3600";
        public bool Upsert { get; set; }
        public JObject Metadata { get; set; }
        public Dictionary<string, string> Headers { get; private set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public IProgress<float> Progress { get; set; }
    }

    public sealed class StorageListOptions
    {
        [JsonProperty("limit")] public int? Limit { get; set; }
        [JsonProperty("offset")] public int? Offset { get; set; }
        [JsonProperty("search")] public string Search { get; set; }
        [JsonProperty("sortBy")] public StorageSortOptions SortBy { get; set; }
    }

    public sealed class StorageSortOptions
    {
        [JsonProperty("column")] public string Column { get; set; } = "name";
        [JsonProperty("order")] public string Order { get; set; } = "asc";
    }

    public enum StorageResizeMode { Cover, Contain, Fill }

    public sealed class StorageTransformOptions
    {
        [JsonProperty("width")] public int? Width { get; set; }
        [JsonProperty("height")] public int? Height { get; set; }
        [JsonProperty("quality")] public int? Quality { get; set; }
        [JsonProperty("resize"), JsonConverter(typeof(StringEnumConverter))]
        public StorageResizeMode? Resize { get; set; }
        [JsonProperty("format")] public string Format { get; set; }

        internal IEnumerable<KeyValuePair<string, string>> ToQuery()
        {
            if (Width.HasValue) yield return Pair("width", Width.Value.ToString());
            if (Height.HasValue) yield return Pair("height", Height.Value.ToString());
            if (Quality.HasValue) yield return Pair("quality", Quality.Value.ToString());
            if (Resize.HasValue) yield return Pair("resize", Resize.Value.ToString().ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(Format)) yield return Pair("format", Format);
        }

        private static KeyValuePair<string, string> Pair(string key, string value)
        {
            return new KeyValuePair<string, string>(key, value);
        }
    }

    public sealed class StorageFileResult
    {
        [JsonProperty("id")] public string Id { get; internal set; }
        [JsonProperty("path")] public string Path { get; internal set; }
        [JsonProperty("fullPath")] public string FullPath { get; internal set; }
    }

    public sealed class StorageSignedUrl
    {
        [JsonProperty("path")] public string Path { get; internal set; }
        [JsonProperty("signedURL")] public string SignedUrl { get; internal set; }
        [JsonProperty("signedUrl")] private string SignedUrlAlternative { set { if (SignedUrl == null) SignedUrl = value; } }
        [JsonProperty("token")] public string Token { get; internal set; }
    }
}
