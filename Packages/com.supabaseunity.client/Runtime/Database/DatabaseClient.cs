using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Supabase.Unity
{
    public enum PostgrestFilterOperator
    {
        Equal, NotEqual, GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual,
        Like, ILike, Is, In, Contains, ContainedBy, Overlaps, TextSearch, Match,
        IMatch, StrictlyLeft, StrictlyRight, NotExtendRight, NotExtendLeft,
        Adjacent
    }

    public enum PostgrestCount { None, Exact, Planned, Estimated }
    public enum PostgrestReturn { Minimal, Representation }

    public sealed class PostgrestWriteOptions
    {
        public PostgrestReturn Returning { get; set; } = PostgrestReturn.Representation;
        public PostgrestCount Count { get; set; } = PostgrestCount.None;
        public string OnConflict { get; set; }
        public bool IgnoreDuplicates { get; set; }
        public bool DefaultToNull { get; set; } = true;
    }

    public sealed class DatabaseClient
    {
        private readonly SupabaseClientOptions options;
        private readonly Uri endpoint;
        private readonly IHttpTransport transport;
        private readonly Func<string> accessToken;

        internal DatabaseClient(SupabaseClientOptions options, Uri endpoint, IHttpTransport transport,
            Func<string> accessToken)
        {
            this.options = options;
            this.endpoint = endpoint;
            this.transport = transport;
            this.accessToken = accessToken;
        }

        public PostgrestQuery<T> From<T>(string table = null)
        {
            if (string.IsNullOrWhiteSpace(table))
            {
                var attribute = typeof(T).GetCustomAttribute<SupabaseTableAttribute>(true);
                table = attribute == null ? typeof(T).Name : attribute.Name;
            }
            return new PostgrestQuery<T>(options, endpoint, transport, accessToken, table,
                options.DefaultSchema);
        }

        public async Task<SupabaseResult<T>> RpcAsync<T>(string function, object parameters = null,
            PostgrestCount count = PostgrestCount.None,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(function))
                throw new ArgumentException("RPC function name cannot be empty.", "function");
            var uri = SupabaseHttp.Combine(endpoint, "rpc/" + Uri.EscapeDataString(function));
            var request = SupabaseHttp.CreateJsonRequest(options, uri, SupabaseHttpMethod.Post,
                parameters ?? new JObject(), accessToken());
            request.Headers["Content-Profile"] = options.DefaultSchema;
            request.Headers["Accept-Profile"] = options.DefaultSchema;
            ApplyCount(request.Headers, count);
            try
            {
                var response = await transport.SendAsync(request, cancellationToken);
                var metadata = SupabaseHttp.Metadata(response);
                if (!response.IsSuccessStatusCode)
                    return SupabaseResult<T>.Failure(SupabaseHttp.Error(SupabaseService.Database, response), metadata);
                var data = string.IsNullOrWhiteSpace(response.Text) ? default(T) : SupabaseJson.Deserialize<T>(response.Text);
                return SupabaseResult<T>.Success(data, metadata);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                return SupabaseResult<T>.Failure(SupabaseError.Create(SupabaseService.Database,
                    SupabaseErrorKind.Serialization, "The RPC response could not be processed.",
                    details: exception.Message));
            }
        }

        internal static void ApplyCount(IDictionary<string, string> headers, PostgrestCount count)
        {
            if (count != PostgrestCount.None)
                AppendPrefer(headers, "count=" + count.ToString().ToLowerInvariant());
        }

        internal static void AppendPrefer(IDictionary<string, string> headers, string value)
        {
            string existing;
            headers.TryGetValue("Prefer", out existing);
            headers["Prefer"] = string.IsNullOrEmpty(existing) ? value : existing + "," + value;
        }
    }

    public sealed class PostgrestQuery<T>
    {
        private readonly SupabaseClientOptions options;
        private readonly Uri endpoint;
        private readonly IHttpTransport transport;
        private readonly Func<string> accessToken;
        private readonly string table;
        private readonly string schema;
        private readonly List<KeyValuePair<string, string>> parameters = new List<KeyValuePair<string, string>>();
        private string columns = "*";
        private PostgrestCount count;

        internal PostgrestQuery(SupabaseClientOptions options, Uri endpoint, IHttpTransport transport,
            Func<string> accessToken, string table, string schema)
        {
            this.options = options;
            this.endpoint = endpoint;
            this.transport = transport;
            this.accessToken = accessToken;
            this.table = table;
            this.schema = schema;
        }

        public PostgrestQuery<T> Select(string selectedColumns = "*")
        {
            columns = string.IsNullOrWhiteSpace(selectedColumns) ? "*" : selectedColumns;
            return this;
        }

        public PostgrestQuery<T> Count(PostgrestCount value = PostgrestCount.Exact)
        {
            count = value;
            return this;
        }

        public PostgrestQuery<T> Filter(string column, PostgrestFilterOperator op, object value)
        {
            RequireColumn(column);
            parameters.Add(new KeyValuePair<string, string>(column, Operator(op) + "." + Format(value, op)));
            return this;
        }

        public PostgrestQuery<T> Not(string column, PostgrestFilterOperator op, object value)
        {
            RequireColumn(column);
            parameters.Add(new KeyValuePair<string, string>(column, "not." + Operator(op) + "." + Format(value, op)));
            return this;
        }

        public PostgrestQuery<T> Eq(string column, object value) { return Filter(column, PostgrestFilterOperator.Equal, value); }
        public PostgrestQuery<T> Neq(string column, object value) { return Filter(column, PostgrestFilterOperator.NotEqual, value); }
        public PostgrestQuery<T> Gt(string column, object value) { return Filter(column, PostgrestFilterOperator.GreaterThan, value); }
        public PostgrestQuery<T> Gte(string column, object value) { return Filter(column, PostgrestFilterOperator.GreaterThanOrEqual, value); }
        public PostgrestQuery<T> Lt(string column, object value) { return Filter(column, PostgrestFilterOperator.LessThan, value); }
        public PostgrestQuery<T> Lte(string column, object value) { return Filter(column, PostgrestFilterOperator.LessThanOrEqual, value); }
        public PostgrestQuery<T> Like(string column, string pattern) { return Filter(column, PostgrestFilterOperator.Like, pattern); }
        public PostgrestQuery<T> ILike(string column, string pattern) { return Filter(column, PostgrestFilterOperator.ILike, pattern); }
        public PostgrestQuery<T> Is(string column, object value) { return Filter(column, PostgrestFilterOperator.Is, value); }
        public PostgrestQuery<T> In(string column, object values) { return Filter(column, PostgrestFilterOperator.In, values); }
        public PostgrestQuery<T> Contains(string column, object value) { return Filter(column, PostgrestFilterOperator.Contains, value); }
        public PostgrestQuery<T> ContainedBy(string column, object value) { return Filter(column, PostgrestFilterOperator.ContainedBy, value); }
        public PostgrestQuery<T> Overlaps(string column, object value) { return Filter(column, PostgrestFilterOperator.Overlaps, value); }

        public PostgrestQuery<T> Or(string expression, string referencedTable = null)
        {
            if (string.IsNullOrWhiteSpace(expression))
                throw new ArgumentException("OR expression cannot be empty.", "expression");
            parameters.Add(new KeyValuePair<string, string>(string.IsNullOrWhiteSpace(referencedTable)
                ? "or" : referencedTable + ".or", "(" + expression.Trim('(', ')') + ")"));
            return this;
        }

        public PostgrestQuery<T> Match(IDictionary<string, object> values)
        {
            if (values == null)
                throw new ArgumentNullException("values");
            foreach (var value in values)
                Eq(value.Key, value.Value);
            return this;
        }

        public PostgrestQuery<T> TextSearch(string column, string query, string config = null,
            string type = null)
        {
            var op = string.IsNullOrWhiteSpace(type) ? "fts" : type.Trim().ToLowerInvariant();
            var value = string.IsNullOrWhiteSpace(config) ? query : "(" + config + ")." + query;
            parameters.Add(new KeyValuePair<string, string>(column, op + "." + value));
            return this;
        }

        public PostgrestQuery<T> Order(string column, bool ascending = true, bool nullsFirst = false,
            string referencedTable = null)
        {
            RequireColumn(column);
            var key = string.IsNullOrWhiteSpace(referencedTable) ? "order" : referencedTable + ".order";
            var suffix = ascending ? ".asc" : ".desc";
            suffix += nullsFirst ? ".nullsfirst" : ".nullslast";
            AddOrAppend(key, column + suffix);
            return this;
        }

        public PostgrestQuery<T> Limit(int value, string referencedTable = null)
        {
            if (value < 0) throw new ArgumentOutOfRangeException("value");
            parameters.Add(new KeyValuePair<string, string>(string.IsNullOrWhiteSpace(referencedTable)
                ? "limit" : referencedTable + ".limit", value.ToString(CultureInfo.InvariantCulture)));
            return this;
        }

        public PostgrestQuery<T> Offset(int value, string referencedTable = null)
        {
            if (value < 0) throw new ArgumentOutOfRangeException("value");
            parameters.Add(new KeyValuePair<string, string>(string.IsNullOrWhiteSpace(referencedTable)
                ? "offset" : referencedTable + ".offset", value.ToString(CultureInfo.InvariantCulture)));
            return this;
        }

        public PostgrestQuery<T> Range(int from, int to, string referencedTable = null)
        {
            if (from < 0 || to < from) throw new ArgumentOutOfRangeException("to");
            Limit(to - from + 1, referencedTable);
            Offset(from, referencedTable);
            return this;
        }

        public Task<SupabaseResult<IReadOnlyList<T>>> GetAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return ReadListAsync(false, cancellationToken);
        }

        public async Task<SupabaseResult<string>> GetCsvAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var request = SupabaseHttp.CreateJsonRequest(options,
                SupabaseHttp.Combine(endpoint, SupabaseHttp.EscapePath(table), BuildParameters(true)),
                SupabaseHttpMethod.Get, null, accessToken());
            request.Headers["Accept-Profile"] = schema;
            request.Headers["Accept"] = "text/csv";
            DatabaseClient.ApplyCount(request.Headers, count);
            try
            {
                var response = await transport.SendAsync(request, cancellationToken);
                var metadata = SupabaseHttp.Metadata(response);
                if (!response.IsSuccessStatusCode)
                    return SupabaseResult<string>.Failure(
                        SupabaseHttp.Error(SupabaseService.Database, response), metadata);
                return SupabaseResult<string>.Success(response.Text, metadata);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { return SerializationFailure<string>(exception); }
        }

        public async Task<SupabaseResult<T>> SingleAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return await ReadSingleAsync(false, cancellationToken);
        }

        public async Task<SupabaseResult<T>> MaybeSingleAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return await ReadSingleAsync(true, cancellationToken);
        }

        public Task<SupabaseResult<IReadOnlyList<T>>> InsertAsync(T value,
            PostgrestWriteOptions writeOptions = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return WriteAsync(SupabaseHttpMethod.Post, value, writeOptions, false, cancellationToken);
        }

        public Task<SupabaseResult<IReadOnlyList<T>>> InsertAsync(IEnumerable<T> values,
            PostgrestWriteOptions writeOptions = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return WriteAsync(SupabaseHttpMethod.Post, values, writeOptions, false, cancellationToken);
        }

        public Task<SupabaseResult<IReadOnlyList<T>>> UpsertAsync(object values,
            PostgrestWriteOptions writeOptions = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return WriteAsync(SupabaseHttpMethod.Post, values, writeOptions, true, cancellationToken);
        }

        public Task<SupabaseResult<IReadOnlyList<T>>> UpdateAsync(object values,
            PostgrestWriteOptions writeOptions = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return WriteAsync(SupabaseHttpMethod.Patch, values, writeOptions, false, cancellationToken);
        }

        public Task<SupabaseResult<IReadOnlyList<T>>> DeleteAsync(
            PostgrestWriteOptions writeOptions = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return WriteAsync(SupabaseHttpMethod.Delete, null, writeOptions, false, cancellationToken);
        }

        private async Task<SupabaseResult<IReadOnlyList<T>>> ReadListAsync(bool head,
            CancellationToken cancellationToken)
        {
            var query = BuildParameters(true);
            var request = SupabaseHttp.CreateJsonRequest(options,
                SupabaseHttp.Combine(endpoint, SupabaseHttp.EscapePath(table), query),
                head ? SupabaseHttpMethod.Head : SupabaseHttpMethod.Get, null, accessToken());
            ApplyReadHeaders(request);
            try
            {
                var response = await transport.SendAsync(request, cancellationToken);
                var metadata = SupabaseHttp.Metadata(response);
                if (!response.IsSuccessStatusCode)
                    return SupabaseResult<IReadOnlyList<T>>.Failure(
                        SupabaseHttp.Error(SupabaseService.Database, response), metadata);
                var list = string.IsNullOrWhiteSpace(response.Text)
                    ? new List<T>() : SupabaseJson.Deserialize<List<T>>(response.Text);
                return SupabaseResult<IReadOnlyList<T>>.Success(list, metadata);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { return SerializationFailure<IReadOnlyList<T>>(exception); }
        }

        private async Task<SupabaseResult<T>> ReadSingleAsync(bool maybe, CancellationToken cancellationToken)
        {
            if (maybe)
            {
                var list = await ReadListAsync(false, cancellationToken);
                if (!list.IsSuccess)
                    return SupabaseResult<T>.Failure(list.Error, list.Metadata);
                if (list.Data.Count == 0)
                    return SupabaseResult<T>.Success(default(T), list.Metadata);
                if (list.Data.Count == 1)
                    return SupabaseResult<T>.Success(list.Data[0], list.Metadata);
                return SupabaseResult<T>.Failure(SupabaseError.Create(SupabaseService.Database,
                    SupabaseErrorKind.Protocol, "MaybeSingleAsync received more than one row.",
                    code: "multiple_rows"), list.Metadata);
            }
            var request = SupabaseHttp.CreateJsonRequest(options,
                SupabaseHttp.Combine(endpoint, SupabaseHttp.EscapePath(table), BuildParameters(true)),
                SupabaseHttpMethod.Get, null, accessToken());
            ApplyReadHeaders(request);
            request.Headers["Accept"] = "application/vnd.pgrst.object+json";
            try
            {
                var response = await transport.SendAsync(request, cancellationToken);
                var metadata = SupabaseHttp.Metadata(response);
                if (!response.IsSuccessStatusCode)
                    return SupabaseResult<T>.Failure(SupabaseHttp.Error(SupabaseService.Database, response), metadata);
                return SupabaseResult<T>.Success(string.IsNullOrWhiteSpace(response.Text)
                    ? default(T) : SupabaseJson.Deserialize<T>(response.Text), metadata);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { return SerializationFailure<T>(exception); }
        }

        private async Task<SupabaseResult<IReadOnlyList<T>>> WriteAsync(SupabaseHttpMethod method,
            object body, PostgrestWriteOptions writeOptions, bool upsert, CancellationToken cancellationToken)
        {
            writeOptions = writeOptions ?? new PostgrestWriteOptions();
            var query = BuildParameters(writeOptions.Returning == PostgrestReturn.Representation);
            if (!string.IsNullOrWhiteSpace(writeOptions.OnConflict))
                query.Add(new KeyValuePair<string, string>("on_conflict", writeOptions.OnConflict));
            var request = SupabaseHttp.CreateJsonRequest(options,
                SupabaseHttp.Combine(endpoint, SupabaseHttp.EscapePath(table), query), method, body, accessToken());
            request.Headers["Content-Profile"] = schema;
            request.Headers["Accept-Profile"] = schema;
            DatabaseClient.AppendPrefer(request.Headers, "return=" +
                (writeOptions.Returning == PostgrestReturn.Minimal ? "minimal" : "representation"));
            DatabaseClient.ApplyCount(request.Headers, writeOptions.Count);
            if (upsert)
                DatabaseClient.AppendPrefer(request.Headers, "resolution=" +
                    (writeOptions.IgnoreDuplicates ? "ignore-duplicates" : "merge-duplicates"));
            if (!writeOptions.DefaultToNull)
                DatabaseClient.AppendPrefer(request.Headers, "missing=default");
            try
            {
                var response = await transport.SendAsync(request, cancellationToken);
                var metadata = SupabaseHttp.Metadata(response);
                if (!response.IsSuccessStatusCode)
                    return SupabaseResult<IReadOnlyList<T>>.Failure(
                        SupabaseHttp.Error(SupabaseService.Database, response), metadata);
                var values = string.IsNullOrWhiteSpace(response.Text)
                    ? new List<T>() : SupabaseJson.Deserialize<List<T>>(response.Text);
                return SupabaseResult<IReadOnlyList<T>>.Success(values, metadata);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { return SerializationFailure<IReadOnlyList<T>>(exception); }
        }

        private List<KeyValuePair<string, string>> BuildParameters(bool includeSelect)
        {
            var query = new List<KeyValuePair<string, string>>();
            if (includeSelect)
                query.Add(new KeyValuePair<string, string>("select", columns));
            foreach (var pair in parameters)
                if (pair.Key != "__accept") query.Add(pair);
            return query;
        }

        private void ApplyReadHeaders(SupabaseHttpRequest request)
        {
            request.Headers["Accept-Profile"] = schema;
            DatabaseClient.ApplyCount(request.Headers, count);
            foreach (var pair in parameters)
                if (pair.Key == "__accept") request.Headers["Accept"] = pair.Value;
        }

        private void AddOrAppend(string key, string value)
        {
            for (var index = 0; index < parameters.Count; index++)
            {
                if (parameters[index].Key != key) continue;
                parameters[index] = new KeyValuePair<string, string>(key, parameters[index].Value + "," + value);
                return;
            }
            parameters.Add(new KeyValuePair<string, string>(key, value));
        }

        private static string Operator(PostgrestFilterOperator value)
        {
            switch (value)
            {
                case PostgrestFilterOperator.Equal: return "eq";
                case PostgrestFilterOperator.NotEqual: return "neq";
                case PostgrestFilterOperator.GreaterThan: return "gt";
                case PostgrestFilterOperator.GreaterThanOrEqual: return "gte";
                case PostgrestFilterOperator.LessThan: return "lt";
                case PostgrestFilterOperator.LessThanOrEqual: return "lte";
                case PostgrestFilterOperator.Like: return "like";
                case PostgrestFilterOperator.ILike: return "ilike";
                case PostgrestFilterOperator.Is: return "is";
                case PostgrestFilterOperator.In: return "in";
                case PostgrestFilterOperator.Contains: return "cs";
                case PostgrestFilterOperator.ContainedBy: return "cd";
                case PostgrestFilterOperator.Overlaps: return "ov";
                case PostgrestFilterOperator.TextSearch: return "fts";
                case PostgrestFilterOperator.Match: return "match";
                case PostgrestFilterOperator.IMatch: return "imatch";
                case PostgrestFilterOperator.StrictlyLeft: return "sl";
                case PostgrestFilterOperator.StrictlyRight: return "sr";
                case PostgrestFilterOperator.NotExtendRight: return "nxr";
                case PostgrestFilterOperator.NotExtendLeft: return "nxl";
                case PostgrestFilterOperator.Adjacent: return "adj";
                default: throw new ArgumentOutOfRangeException("value");
            }
        }

        private static string Format(object value, PostgrestFilterOperator op)
        {
            if (value == null) return "null";
            if (op == PostgrestFilterOperator.In)
            {
                var token = value as JArray ?? JArray.FromObject(value, JsonSerializer.Create(SupabaseJson.Settings));
                var items = new List<string>();
                foreach (var item in token) items.Add(Scalar(item.ToObject<object>()));
                return "(" + string.Join(",", items.ToArray()) + ")";
            }
            if (value is System.Collections.IEnumerable && !(value is string))
                return SupabaseJson.Serialize(value);
            return Scalar(value);
        }

        private static string Scalar(object value)
        {
            if (value == null) return "null";
            if (value is bool) return (bool)value ? "true" : "false";
            if (value is DateTimeOffset) return ((DateTimeOffset)value).ToString("o", CultureInfo.InvariantCulture);
            if (value is DateTime) return ((DateTime)value).ToString("o", CultureInfo.InvariantCulture);
            if (value is IFormattable) return ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
            var text = value.ToString();
            return text.IndexOfAny(new[] { ',', '(', ')', '"' }) >= 0
                ? "\"" + text.Replace("\"", "\\\"") + "\"" : text;
        }

        private static void RequireColumn(string column)
        {
            if (string.IsNullOrWhiteSpace(column))
                throw new ArgumentException("Column cannot be empty.", "column");
        }

        private static SupabaseResult<TValue> SerializationFailure<TValue>(Exception exception)
        {
            return SupabaseResult<TValue>.Failure(SupabaseError.Create(SupabaseService.Database,
                SupabaseErrorKind.Serialization, "The database response could not be processed.",
                details: exception.Message));
        }
    }
}
