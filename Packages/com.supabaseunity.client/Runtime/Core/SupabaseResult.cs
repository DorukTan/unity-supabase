using System;
using System.Collections.Generic;

namespace Supabase.Unity
{
    public enum SupabaseService
    {
        Client,
        Auth,
        Database,
        Realtime,
        Storage,
        Functions
    }

    public enum SupabaseErrorKind
    {
        Configuration,
        Transport,
        Timeout,
        Cancelled,
        Http,
        Protocol,
        Serialization,
        UnsupportedPlatform,
        Unknown
    }

    public sealed class SupabaseResponseMetadata
    {
        public int StatusCode { get; internal set; }
        public long? Count { get; internal set; }
        public IReadOnlyDictionary<string, string> Headers { get; internal set; }
            = new Dictionary<string, string>();
    }

    public sealed class SupabaseError
    {
        public SupabaseErrorKind Kind { get; internal set; }
        public SupabaseService Service { get; internal set; }
        public string Code { get; internal set; }
        public string Message { get; internal set; }
        public string Details { get; internal set; }
        public string Hint { get; internal set; }
        public int? StatusCode { get; internal set; }
        public bool IsRetryable { get; internal set; }
        public string RawResponse { get; internal set; }

        public override string ToString()
        {
            var code = string.IsNullOrEmpty(Code) ? Kind.ToString() : Code;
            return string.Format("[{0}/{1}] {2}", Service, code, Message);
        }

        internal static SupabaseError Create(
            SupabaseService service,
            SupabaseErrorKind kind,
            string message,
            string code = null,
            int? statusCode = null,
            string details = null,
            string hint = null,
            string rawResponse = null,
            bool retryable = false)
        {
            return new SupabaseError
            {
                Service = service,
                Kind = kind,
                Message = SupabaseHttp.Redact(message ?? "Unknown Supabase error."),
                Code = code,
                StatusCode = statusCode,
                Details = SupabaseHttp.Redact(details),
                Hint = SupabaseHttp.Redact(hint),
                RawResponse = SupabaseHttp.Redact(rawResponse),
                IsRetryable = retryable
            };
        }
    }

    public class SupabaseOperationException : Exception
    {
        public SupabaseError Error { get; private set; }

        public SupabaseOperationException(SupabaseError error)
            : base(error == null ? "Supabase operation failed." : error.ToString())
        {
            Error = error;
        }
    }

    public sealed class SupabaseConfigurationException : Exception
    {
        public SupabaseConfigurationException(string message) : base(message)
        {
        }
    }

    public sealed class SupabaseResult<T>
    {
        public bool IsSuccess { get { return Error == null; } }
        public T Data { get; private set; }
        public SupabaseError Error { get; private set; }
        public SupabaseResponseMetadata Metadata { get; private set; }

        private SupabaseResult(T data, SupabaseError error, SupabaseResponseMetadata metadata)
        {
            Data = data;
            Error = error;
            Metadata = metadata ?? new SupabaseResponseMetadata();
        }

        public T GetValueOrThrow()
        {
            if (!IsSuccess)
                throw new SupabaseOperationException(Error);
            return Data;
        }

        public static SupabaseResult<T> Success(T data, SupabaseResponseMetadata metadata = null)
        {
            return new SupabaseResult<T>(data, null, metadata);
        }

        public static SupabaseResult<T> Failure(SupabaseError error, SupabaseResponseMetadata metadata = null)
        {
            if (error == null)
                throw new ArgumentNullException("error");
            return new SupabaseResult<T>(default(T), error, metadata);
        }
    }

    public sealed class SupabaseResult
    {
        public bool IsSuccess { get { return Error == null; } }
        public SupabaseError Error { get; private set; }
        public SupabaseResponseMetadata Metadata { get; private set; }

        private SupabaseResult(SupabaseError error, SupabaseResponseMetadata metadata)
        {
            Error = error;
            Metadata = metadata ?? new SupabaseResponseMetadata();
        }

        public void ThrowIfFailed()
        {
            if (!IsSuccess)
                throw new SupabaseOperationException(Error);
        }

        public static SupabaseResult Success(SupabaseResponseMetadata metadata = null)
        {
            return new SupabaseResult(null, metadata);
        }

        public static SupabaseResult Failure(SupabaseError error, SupabaseResponseMetadata metadata = null)
        {
            if (error == null)
                throw new ArgumentNullException("error");
            return new SupabaseResult(error, metadata);
        }
    }
}
