using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace Supabase.Unity
{
    public sealed class UnityWebRequestTransport : IHttpTransport
    {
        private bool disposed;

        public Task<SupabaseHttpResponse> SendAsync(
            SupabaseHttpRequest request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (disposed)
                throw new ObjectDisposedException(GetType().Name);
            if (request == null)
                throw new ArgumentNullException("request");
            if (request.Uri == null)
                throw new ArgumentException("The HTTP request URI is required.", "request");

            var completion = new TaskCompletionSource<SupabaseHttpResponse>();
            SupabaseRuntimeHost.Run(SendRoutine(request, cancellationToken, completion));
            return completion.Task;
        }

        private static IEnumerator SendRoutine(
            SupabaseHttpRequest request,
            CancellationToken cancellationToken,
            TaskCompletionSource<SupabaseHttpResponse> completion)
        {
            var routine = SendRoutineCore(request, cancellationToken, completion);
            while (true)
            {
                bool hasNext;
                object current = null;
                try
                {
                    hasNext = routine.MoveNext();
                    if (hasNext) current = routine.Current;
                }
                catch (Exception exception)
                {
                    var disposable = routine as IDisposable;
                    if (disposable != null) disposable.Dispose();
                    completion.TrySetResult(new SupabaseHttpResponse
                    {
                        StatusCode = 0,
                        Body = new byte[0],
                        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                        TransportError = SupabaseHttp.Redact(exception.Message)
                    });
                    yield break;
                }

                if (!hasNext)
                    yield break;
                yield return current;
            }
        }

        private static IEnumerator SendRoutineCore(
            SupabaseHttpRequest request,
            CancellationToken cancellationToken,
            TaskCompletionSource<SupabaseHttpResponse> completion)
        {
            using (var unityRequest = CreateRequest(request))
            {
                var operation = unityRequest.SendWebRequest();
                while (!operation.isDone)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        unityRequest.Abort();
                        completion.TrySetCanceled();
                        yield break;
                    }

                    if (request.UploadProgress != null)
                        request.UploadProgress.Report(unityRequest.uploadProgress);
                    if (request.DownloadProgress != null)
                        request.DownloadProgress.Report(unityRequest.downloadProgress);
                    yield return null;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled();
                    yield break;
                }

                var response = new SupabaseHttpResponse
                {
                    StatusCode = (int)unityRequest.responseCode,
                    Headers = unityRequest.GetResponseHeaders()
                        ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    TransportError = HasTransportError(unityRequest) ? unityRequest.error : null,
                    TimedOut = string.Equals(unityRequest.error, "Request timeout", StringComparison.OrdinalIgnoreCase)
                };

                var chunks = unityRequest.downloadHandler as ChunkDownloadHandler;
                response.Body = chunks != null
                    ? chunks.Bytes
                    : (unityRequest.downloadHandler == null ? new byte[0] : unityRequest.downloadHandler.data);

                if (request.UploadProgress != null)
                    request.UploadProgress.Report(1f);
                if (request.DownloadProgress != null)
                    request.DownloadProgress.Report(1f);
                completion.TrySetResult(response);
            }
        }

        private static UnityWebRequest CreateRequest(SupabaseHttpRequest request)
        {
            var method = ToUnityMethod(request.Method);
            var unityRequest = new UnityWebRequest(request.Uri, method);
            unityRequest.timeout = Math.Max(1, (int)Math.Ceiling(request.Timeout.TotalSeconds));
            // Supabase API endpoints should respond directly. Following a cross-origin redirect could
            // forward apikey/Authorization headers to a host the caller did not configure.
            unityRequest.redirectLimit = 0;

            if (request.Body != null)
                unityRequest.uploadHandler = new UploadHandlerRaw(request.Body);

            unityRequest.downloadHandler = request.DownloadChunk == null
                ? (DownloadHandler)new DownloadHandlerBuffer()
                : new ChunkDownloadHandler(request.DownloadChunk);

            if (!string.IsNullOrWhiteSpace(request.ContentType))
                unityRequest.SetRequestHeader("Content-Type", request.ContentType);

            foreach (var header in request.Headers)
            {
                if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    unityRequest.SetRequestHeader("Content-Type", header.Value);
                else
                    unityRequest.SetRequestHeader(header.Key, header.Value);
            }

            return unityRequest;
        }

        private static string ToUnityMethod(SupabaseHttpMethod method)
        {
            switch (method)
            {
                case SupabaseHttpMethod.Get: return UnityWebRequest.kHttpVerbGET;
                case SupabaseHttpMethod.Post: return UnityWebRequest.kHttpVerbPOST;
                case SupabaseHttpMethod.Put: return UnityWebRequest.kHttpVerbPUT;
                case SupabaseHttpMethod.Patch: return "PATCH";
                case SupabaseHttpMethod.Delete: return UnityWebRequest.kHttpVerbDELETE;
                case SupabaseHttpMethod.Head: return UnityWebRequest.kHttpVerbHEAD;
                default: throw new ArgumentOutOfRangeException("method", method, null);
            }
        }

        private static bool HasTransportError(UnityWebRequest request)
        {
#if UNITY_2020_2_OR_NEWER
            return request.result == UnityWebRequest.Result.ConnectionError ||
                   request.result == UnityWebRequest.Result.DataProcessingError;
#else
            return request.isNetworkError;
#endif
        }

        public void Dispose()
        {
            disposed = true;
        }

        private sealed class ChunkDownloadHandler : DownloadHandlerScript
        {
            private readonly MemoryStream stream = new MemoryStream();
            private readonly Action<byte[]> callback;

            internal byte[] Bytes { get { return stream.ToArray(); } }

            internal ChunkDownloadHandler(Action<byte[]> callback)
                : base(new byte[32 * 1024])
            {
                this.callback = callback;
            }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength <= 0)
                    return false;
                stream.Write(data, 0, dataLength);
                var chunk = new byte[dataLength];
                Buffer.BlockCopy(data, 0, chunk, 0, dataLength);
                callback(chunk);
                return true;
            }

        }
    }
}
