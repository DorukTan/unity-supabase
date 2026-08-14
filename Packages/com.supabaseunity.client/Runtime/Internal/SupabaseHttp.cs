using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Supabase.Unity
{
    internal static class SupabaseHttp
    {
        internal const string ClientInfo = "supabase-unity/0.2.0-beta.4";
        private static readonly Regex JwtPattern = new Regex(
            @"\beyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b",
            RegexOptions.CultureInvariant);
        private static readonly Regex BearerPattern = new Regex(
            @"Bearer\s+[^\s,\""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static SupabaseHttpRequest CreateJsonRequest(
            SupabaseClientOptions options,
            Uri uri,
            SupabaseHttpMethod method,
            object body,
            string accessToken = null)
        {
            var request = new SupabaseHttpRequest
            {
                Uri = uri,
                Method = method,
                Timeout = options.HttpTimeout,
                ContentType = "application/json"
            };
            if (body != null)
                request.Body = Encoding.UTF8.GetBytes(SupabaseJson.Serialize(body));
            ApplyClientHeaders(request.Headers, options, accessToken);
            return request;
        }

        internal static void ApplyClientHeaders(
            IDictionary<string, string> headers,
            SupabaseClientOptions options,
            string accessToken)
        {
            ValidateAdditionalHeaders(options.Headers, options.PublishableKey);
            foreach (var header in options.Headers)
                headers[header.Key] = header.Value;

            headers["apikey"] = options.PublishableKey;
            headers.Remove("Authorization");
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                SupabaseKeyValidator.RejectElevatedKey(accessToken, "an Authorization token");
                headers["Authorization"] = "Bearer " + accessToken;
            }
            else if (!SupabaseKeyValidator.IsPublishableKey(options.PublishableKey))
            {
                // Legacy anon keys are JWTs. New sb_publishable keys are not and must never be sent as Bearer tokens.
                headers["Authorization"] = "Bearer " + options.PublishableKey;
            }
            headers["X-Client-Info"] = ClientInfo;
        }

        internal static void ValidateAdditionalHeaders(
            IEnumerable<KeyValuePair<string, string>> headers,
            string configuredPublishableKey)
        {
            if (headers == null)
                return;
            foreach (var header in headers)
            {
                SupabaseKeyValidator.RejectElevatedKey(header.Value, "a request header");
                if (string.Equals(header.Key, "apikey", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(header.Value, configuredPublishableKey, StringComparison.Ordinal))
                        throw new SupabaseConfigurationException(
                            "The per-request apikey header cannot override the configured publishable key.");
                    continue;
                }

                if (!string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                    continue;
                var credential = header.Value == null ? string.Empty : header.Value.Trim();
                if (credential.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    credential = credential.Substring("Bearer ".Length).Trim();
                SupabaseKeyValidator.RejectElevatedKey(credential, "the Authorization header");
                if (SupabaseKeyValidator.IsPublishableKey(credential))
                    throw new SupabaseConfigurationException(
                        "A publishable API key is not a JWT and cannot be used in the Authorization header.");
            }
        }

        internal static async Task<SupabaseHttpResponse> SendAsync(
            IHttpTransport transport,
            SupabaseHttpRequest request,
            CancellationToken cancellationToken)
        {
            return await transport.SendAsync(request, cancellationToken);
        }

        internal static SupabaseResponseMetadata Metadata(SupabaseHttpResponse response)
        {
            long parsedCount;
            long? count = null;
            string contentRange;
            if (TryGetHeader(response.Headers, "Content-Range", out contentRange))
            {
                var slash = contentRange.LastIndexOf('/');
                if (slash >= 0 && long.TryParse(contentRange.Substring(slash + 1), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out parsedCount))
                    count = parsedCount;
            }

            return new SupabaseResponseMetadata
            {
                StatusCode = response.StatusCode,
                Headers = response.Headers,
                Count = count
            };
        }

        internal static SupabaseError Error(SupabaseService service, SupabaseHttpResponse response)
        {
            if (response.TimedOut)
                return SupabaseError.Create(service, SupabaseErrorKind.Timeout, "The request timed out.",
                    statusCode: response.StatusCode, retryable: true);
            if (!string.IsNullOrWhiteSpace(response.TransportError))
                return SupabaseError.Create(service, SupabaseErrorKind.Transport, response.TransportError,
                    statusCode: response.StatusCode, retryable: true);

            var message = string.IsNullOrWhiteSpace(response.Text)
                ? "Supabase returned HTTP " + response.StatusCode + "."
                : response.Text;
            string code = null;
            string details = null;
            string hint = null;
            try
            {
                var json = JObject.Parse(response.Text);
                code = FirstString(json, "code", "error_code", "error");
                message = Redact(FirstString(json, "message", "msg", "error_description") ?? message);
                details = Redact(FirstString(json, "details"));
                hint = Redact(FirstString(json, "hint"));
            }
            catch
            {
                // Non-JSON responses remain useful as the public error message.
            }

            var retryable = response.StatusCode == 408 || response.StatusCode == 429 || response.StatusCode >= 500;
            return SupabaseError.Create(service, SupabaseErrorKind.Http, message, code, response.StatusCode,
                details, hint, Redact(response.Text), retryable);
        }

        internal static Uri Combine(Uri root, string relativePath, IEnumerable<KeyValuePair<string, string>> query = null)
        {
            var baseText = root.AbsoluteUri.TrimEnd('/');
            var path = string.IsNullOrEmpty(relativePath) ? string.Empty : "/" + relativePath.TrimStart('/');
            var builder = new StringBuilder(baseText + path);
            if (query != null)
            {
                var first = true;
                foreach (var pair in query)
                {
                    if (pair.Value == null)
                        continue;
                    builder.Append(first ? '?' : '&');
                    first = false;
                    builder.Append(Uri.EscapeDataString(pair.Key));
                    builder.Append('=');
                    builder.Append(Uri.EscapeDataString(pair.Value));
                }
            }
            return new Uri(builder.ToString());
        }

        internal static bool TryGetHeader(
            IReadOnlyDictionary<string, string> headers,
            string key,
            out string value)
        {
            if (headers != null)
            {
                foreach (var header in headers)
                {
                    if (string.Equals(header.Key, key, StringComparison.OrdinalIgnoreCase))
                    {
                        value = header.Value;
                        return true;
                    }
                }
            }
            value = null;
            return false;
        }

        internal static string Redact(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;
            try
            {
                var json = JToken.Parse(value);
                RedactJson(json);
                value = json.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch
            {
                // Preserve non-JSON response text and still remove recognizable secrets below.
            }
            value = BearerPattern.Replace(value, "Bearer [REDACTED]");
            value = JwtPattern.Replace(value, "[REDACTED_JWT]");
            const string marker = "sb_secret_";
            var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                var end = index + marker.Length;
                while (end < value.Length && !char.IsWhiteSpace(value[end]) && value[end] != '"')
                    end++;
                value = value.Substring(0, index) + "[REDACTED]" + value.Substring(end);
                index = value.IndexOf(marker, index + "[REDACTED]".Length,
                    StringComparison.OrdinalIgnoreCase);
            }
            return value;
        }

        private static void RedactJson(JToken token)
        {
            var json = token as JObject;
            if (json != null)
            {
                foreach (var property in json.Properties())
                {
                    if (IsSensitiveKey(property.Name))
                        property.Value = "[REDACTED]";
                    else
                        RedactJson(property.Value);
                }
                return;
            }
            var array = token as JArray;
            if (array != null)
                foreach (var item in array) RedactJson(item);
        }

        private static bool IsSensitiveKey(string key)
        {
            return string.Equals(key, "access_token", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "refresh_token", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "provider_token", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "provider_refresh_token", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "id_token", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "password", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "authorization", StringComparison.OrdinalIgnoreCase);
        }

        internal static string EscapePath(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            var segments = value.Replace('\\', '/').Split('/');
            for (var i = 0; i < segments.Length; i++)
                segments[i] = Uri.EscapeDataString(segments[i]);
            return string.Join("/", segments);
        }

        private static string FirstString(JObject json, params string[] keys)
        {
            foreach (var key in keys)
            {
                var token = json[key];
                if (token != null && token.Type != JTokenType.Null)
                    return token.Type == JTokenType.String ? (string)token : token.ToString();
            }
            return null;
        }
    }
}
