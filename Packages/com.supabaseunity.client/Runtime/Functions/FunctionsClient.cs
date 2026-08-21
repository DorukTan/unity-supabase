using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Supabase.Unity
{
    public sealed class FunctionInvokeOptions
    {
        public object Body { get; set; }
        public byte[] RawBody { get; set; }
        public string ContentType { get; set; } = "application/json";
        public SupabaseHttpMethod Method { get; set; } = SupabaseHttpMethod.Post;
        public TimeSpan? Timeout { get; set; }
        public Dictionary<string, string> Headers { get; private set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class FunctionResponse
    {
        public byte[] Body { get; internal set; }
        public string Text { get { return Body == null ? string.Empty : Encoding.UTF8.GetString(Body); } }
        public int StatusCode { get; internal set; }
        public IReadOnlyDictionary<string, string> Headers { get; internal set; }
    }

    public sealed class FunctionsClient
    {
        private readonly SupabaseClientOptions options;
        private readonly Uri endpoint;
        private readonly IHttpTransport transport;
        private readonly Func<string> accessToken;

        internal FunctionsClient(SupabaseClientOptions options, Uri endpoint, IHttpTransport transport,
            Func<string> accessToken)
        {
            this.options = options;
            this.endpoint = endpoint;
            this.transport = transport;
            this.accessToken = accessToken;
        }

        public async Task<SupabaseResult<T>> InvokeAsync<T>(string functionName,
            FunctionInvokeOptions invokeOptions = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var raw = await InvokeAsync(functionName, invokeOptions, cancellationToken);
            if (!raw.IsSuccess)
                return SupabaseResult<T>.Failure(raw.Error, raw.Metadata);
            try
            {
                var value = string.IsNullOrWhiteSpace(raw.Data.Text)
                    ? default(T) : SupabaseJson.Deserialize<T>(raw.Data.Text);
                return SupabaseResult<T>.Success(value, raw.Metadata);
            }
            catch (Exception exception)
            {
                return SupabaseResult<T>.Failure(SupabaseError.Create(SupabaseService.Functions,
                    SupabaseErrorKind.Serialization, "The Edge Function response could not be deserialized.",
                    details: exception.Message), raw.Metadata);
            }
        }

        public async Task<SupabaseResult<FunctionResponse>> InvokeAsync(string functionName,
            FunctionInvokeOptions invokeOptions = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(functionName))
                throw new ArgumentException("Function name cannot be empty.", "functionName");
            invokeOptions = invokeOptions ?? new FunctionInvokeOptions();
            var request = SupabaseHttp.CreateJsonRequest(options,
                SupabaseHttp.Combine(endpoint, SupabaseHttp.EscapePath(functionName)),
                invokeOptions.Method, invokeOptions.Body, accessToken());
            request.ContentType = invokeOptions.ContentType;
            if (invokeOptions.RawBody != null)
                request.Body = invokeOptions.RawBody;
            if (invokeOptions.Timeout.HasValue)
                request.Timeout = invokeOptions.Timeout.Value;
            SupabaseHttp.ValidateAdditionalHeaders(invokeOptions.Headers, options.PublishableKey);
            foreach (var header in invokeOptions.Headers)
                request.Headers[header.Key] = header.Value;
            try
            {
                var response = await SupabaseHttp.SendAsync(transport, request, cancellationToken);
                var metadata = SupabaseHttp.Metadata(response);
                if (!response.IsSuccessStatusCode)
                    return SupabaseResult<FunctionResponse>.Failure(
                        SupabaseHttp.Error(SupabaseService.Functions, response), metadata);
                return SupabaseResult<FunctionResponse>.Success(new FunctionResponse
                {
                    Body = response.Body,
                    StatusCode = response.StatusCode,
                    Headers = response.Headers
                }, metadata);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                return SupabaseResult<FunctionResponse>.Failure(SupabaseError.Create(
                    SupabaseService.Functions, SupabaseErrorKind.Transport,
                    "The Edge Function could not be invoked.", details: exception.Message, retryable: true));
            }
        }
    }
}
