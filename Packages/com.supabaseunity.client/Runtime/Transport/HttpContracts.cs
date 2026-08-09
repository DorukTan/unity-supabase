using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Supabase.Unity
{
    public enum SupabaseHttpMethod
    {
        Get,
        Post,
        Put,
        Patch,
        Delete,
        Head
    }

    public sealed class SupabaseHttpRequest
    {
        public Uri Uri { get; set; }
        public SupabaseHttpMethod Method { get; set; }
        public Dictionary<string, string> Headers { get; private set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body { get; set; }
        public string ContentType { get; set; }
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
        public IProgress<float> UploadProgress { get; set; }
        public IProgress<float> DownloadProgress { get; set; }
        public Action<byte[]> DownloadChunk { get; set; }
    }

    public sealed class SupabaseHttpResponse
    {
        public int StatusCode { get; internal set; }
        public byte[] Body { get; internal set; } = new byte[0];
        public Dictionary<string, string> Headers { get; internal set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string TransportError { get; internal set; }
        public bool WasCancelled { get; internal set; }
        public bool TimedOut { get; internal set; }

        public bool IsSuccessStatusCode { get { return StatusCode >= 200 && StatusCode <= 299; } }
        public string Text { get { return Body == null || Body.Length == 0 ? string.Empty : Encoding.UTF8.GetString(Body); } }
    }

    public interface IHttpTransport : IDisposable
    {
        Task<SupabaseHttpResponse> SendAsync(
            SupabaseHttpRequest request,
            CancellationToken cancellationToken = default(CancellationToken));
    }
}
