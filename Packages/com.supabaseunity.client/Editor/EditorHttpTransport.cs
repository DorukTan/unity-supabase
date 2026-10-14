using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Supabase.Unity.Editor
{
    internal sealed class EditorHttpTransport : IHttpTransport
    {
        private readonly int mainThreadId = Thread.CurrentThread.ManagedThreadId;
        private readonly List<PendingRequest> requests = new List<PendingRequest>();
        private readonly Queue<Action> notifications = new Queue<Action>();
        private bool disposed;

        internal int ActiveRequestCount { get { return requests.Count; } }

        internal EditorHttpTransport()
        {
            EditorApplication.update += Update;
        }

        public Task<SupabaseHttpResponse> SendAsync(SupabaseHttpRequest request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            RequireMainThread();
            if (disposed) throw new ObjectDisposedException("EditorHttpTransport");
            if (request == null) throw new ArgumentNullException("request");
            if (request.Uri == null) throw new ArgumentException("The HTTP request URI is required.", "request");
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<SupabaseHttpResponse>(cancellationToken);

            UnityWebRequest unityRequest = null;
            try
            {
                unityRequest = UnityWebRequestTransport.CreateRequest(request);
                unityRequest.SendWebRequest();
                var pending = new PendingRequest(this, request, unityRequest, cancellationToken);
                requests.Add(pending);
                return pending.Completion.Task;
            }
            catch (Exception exception)
            {
                if (unityRequest != null) unityRequest.Dispose();
                return Task.FromResult(Failure(exception));
            }
        }

        internal void Post(Action action)
        {
            lock (notifications)
            {
                if (disposed) return;
                if (Thread.CurrentThread.ManagedThreadId != mainThreadId)
                {
                    notifications.Enqueue(action);
                    return;
                }
            }
            action();
        }

        private void Update()
        {
            Action[] pendingNotifications;
            lock (notifications)
            {
                pendingNotifications = notifications.ToArray();
                notifications.Clear();
            }
            foreach (var notification in pendingNotifications)
            {
                if (disposed) return;
                try { notification(); }
                catch (Exception exception) { Debug.LogException(exception); }
            }
            foreach (var request in requests.ToArray())
                request.Tick();
        }

        public void Dispose()
        {
            RequireMainThread();
            lock (notifications)
            {
                if (disposed) return;
                disposed = true;
                notifications.Clear();
            }
            EditorApplication.update -= Update;
            foreach (var request in requests.ToArray())
                request.Finish(null, true);
        }

        private void RequireMainThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId)
                throw new InvalidOperationException("Editor client operations must run on Unity's main thread.");
        }

        private static SupabaseHttpResponse Failure(Exception exception)
        {
            return new SupabaseHttpResponse { TransportError = SupabaseHttp.Redact(exception.Message) };
        }

        private sealed class PendingRequest
        {
            private readonly EditorHttpTransport owner;
            private readonly SupabaseHttpRequest descriptor;
            private readonly UnityWebRequest request;
            private readonly CancellationToken cancellationToken;
            private bool finished;

            internal readonly TaskCompletionSource<SupabaseHttpResponse> Completion
                = new TaskCompletionSource<SupabaseHttpResponse>();

            internal PendingRequest(EditorHttpTransport owner, SupabaseHttpRequest descriptor,
                UnityWebRequest request, CancellationToken cancellationToken)
            {
                this.owner = owner;
                this.descriptor = descriptor;
                this.request = request;
                this.cancellationToken = cancellationToken;
            }

            internal void Tick()
            {
                if (finished) return;
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Finish(null, true);
                        return;
                    }
                    if (request.isDone)
                    {
                        Finish(UnityWebRequestTransport.ReadResponse(request), false);
                        return;
                    }
                    if (descriptor.UploadProgress != null)
                        descriptor.UploadProgress.Report(request.uploadProgress);
                    // Progress callbacks may dispose their owning window/client.
                    if (finished) return;
                    if (descriptor.DownloadProgress != null)
                        descriptor.DownloadProgress.Report(request.downloadProgress);
                }
                catch (Exception exception)
                {
                    Finish(Failure(exception), false);
                }
            }

            internal void Finish(SupabaseHttpResponse response, bool cancelled)
            {
                if (finished) return;
                finished = true;
                owner.requests.Remove(this);
                try
                {
                    // Also abort if progress/reporting failed before the response finished.
                    if (!request.isDone) request.Abort();
                    request.Dispose();
                    if (cancelled)
                        Completion.TrySetCanceled();
                    else
                    {
                        if (descriptor.UploadProgress != null) descriptor.UploadProgress.Report(1f);
                        if (descriptor.DownloadProgress != null) descriptor.DownloadProgress.Report(1f);
                        Completion.TrySetResult(response);
                    }
                }
                catch (Exception exception)
                {
                    request.Dispose();
                    if (cancelled) Completion.TrySetCanceled();
                    else Completion.TrySetResult(Failure(exception));
                }
            }
        }
    }
}
