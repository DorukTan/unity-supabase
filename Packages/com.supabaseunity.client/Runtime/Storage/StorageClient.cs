using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Supabase.Unity
{
    public sealed class StorageClient
    {
        private readonly SupabaseClientOptions options;
        private readonly Uri endpoint;
        private readonly IHttpTransport transport;
        private readonly Func<string> accessToken;

        internal StorageClient(SupabaseClientOptions options, Uri endpoint, IHttpTransport transport,
            Func<string> accessToken)
        {
            this.options = options;
            this.endpoint = endpoint;
            this.transport = transport;
            this.accessToken = accessToken;
        }

        public StorageBucketClient From(string bucketId)
        {
            if (string.IsNullOrWhiteSpace(bucketId))
                throw new ArgumentException("Bucket id cannot be empty.", "bucketId");
            return new StorageBucketClient(options, endpoint, transport, accessToken, bucketId);
        }

        public Task<SupabaseResult<IReadOnlyList<StorageBucket>>> ListBucketsAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return SendAsync<IReadOnlyList<StorageBucket>>(SupabaseHttpMethod.Get, "bucket", null,
                cancellationToken);
        }

        public Task<SupabaseResult<StorageBucket>> GetBucketAsync(string bucketId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return SendAsync<StorageBucket>(SupabaseHttpMethod.Get,
                "bucket/" + SupabaseHttp.EscapePath(bucketId), null, cancellationToken);
        }

        public Task<SupabaseResult<StorageBucket>> CreateBucketAsync(string bucketId,
            StorageBucketOptions bucketOptions = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(bucketId)) throw new ArgumentException("Bucket id cannot be empty.", "bucketId");
            var body = JObject.FromObject(bucketOptions ?? new StorageBucketOptions());
            body["id"] = bucketId;
            body["name"] = bucketId;
            return SendAsync<StorageBucket>(SupabaseHttpMethod.Post, "bucket", body, cancellationToken);
        }

        public Task<SupabaseResult<StorageBucket>> UpdateBucketAsync(string bucketId,
            StorageBucketOptions bucketOptions,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (bucketOptions == null) throw new ArgumentNullException("bucketOptions");
            return SendAsync<StorageBucket>(SupabaseHttpMethod.Put,
                "bucket/" + SupabaseHttp.EscapePath(bucketId), bucketOptions, cancellationToken);
        }

        public Task<SupabaseResult> EmptyBucketAsync(string bucketId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return SendEmptyAsync(SupabaseHttpMethod.Post,
                "bucket/" + SupabaseHttp.EscapePath(bucketId) + "/empty", new JObject(), cancellationToken);
        }

        public Task<SupabaseResult> DeleteBucketAsync(string bucketId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return SendEmptyAsync(SupabaseHttpMethod.Delete,
                "bucket/" + SupabaseHttp.EscapePath(bucketId), null, cancellationToken);
        }

        private async Task<SupabaseResult<T>> SendAsync<T>(SupabaseHttpMethod method, string path, object body,
            CancellationToken cancellationToken)
        {
            var request = SupabaseHttp.CreateJsonRequest(options, SupabaseHttp.Combine(endpoint, path), method,
                body, accessToken());
            try
            {
                var response = await transport.SendAsync(request, cancellationToken);
                var metadata = SupabaseHttp.Metadata(response);
                if (!response.IsSuccessStatusCode)
                    return SupabaseResult<T>.Failure(SupabaseHttp.Error(SupabaseService.Storage, response), metadata);
                return SupabaseResult<T>.Success(string.IsNullOrWhiteSpace(response.Text)
                    ? default(T) : SupabaseJson.Deserialize<T>(response.Text), metadata);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { return StorageBucketClient.Failure<T>(exception); }
        }

        private async Task<SupabaseResult> SendEmptyAsync(SupabaseHttpMethod method, string path, object body,
            CancellationToken cancellationToken)
        {
            var result = await SendAsync<JObject>(method, path, body, cancellationToken);
            return result.IsSuccess ? SupabaseResult.Success(result.Metadata) : SupabaseResult.Failure(result.Error, result.Metadata);
        }
    }

    public sealed class StorageBucketClient
    {
        private readonly SupabaseClientOptions options;
        private readonly Uri endpoint;
        private readonly IHttpTransport transport;
        private readonly Func<string> accessToken;
        public string BucketId { get; private set; }

        internal StorageBucketClient(SupabaseClientOptions options, Uri endpoint, IHttpTransport transport,
            Func<string> accessToken, string bucketId)
        {
            this.options = options;
            this.endpoint = endpoint;
            this.transport = transport;
            this.accessToken = accessToken;
            BucketId = bucketId;
        }

        public Task<SupabaseResult<StorageFileResult>> UploadAsync(string path, byte[] data,
            StorageUploadOptions uploadOptions = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return UploadCoreAsync(SupabaseHttpMethod.Post, "object/" + ObjectPath(path), null, data,
                uploadOptions, cancellationToken);
        }

        public Task<SupabaseResult<StorageFileResult>> UpdateAsync(string path, byte[] data,
            StorageUploadOptions uploadOptions = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return UploadCoreAsync(SupabaseHttpMethod.Put, "object/" + ObjectPath(path), null, data,
                uploadOptions, cancellationToken);
        }

        public async Task<SupabaseResult<byte[]>> DownloadAsync(string path,
            StorageTransformOptions transform = null, IProgress<float> progress = null,
            Action<byte[]> chunk = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var relative = transform == null ? "object/" : "render/image/authenticated/";
            var uri = SupabaseHttp.Combine(endpoint, relative + ObjectPath(path),
                transform == null ? null : transform.ToQuery());
            var request = SupabaseHttp.CreateJsonRequest(options, uri, SupabaseHttpMethod.Get, null, accessToken());
            request.DownloadProgress = progress;
            request.DownloadChunk = chunk;
            try
            {
                var response = await transport.SendAsync(request, cancellationToken);
                var metadata = SupabaseHttp.Metadata(response);
                if (!response.IsSuccessStatusCode)
                    return SupabaseResult<byte[]>.Failure(SupabaseHttp.Error(SupabaseService.Storage, response), metadata);
                return SupabaseResult<byte[]>.Success(response.Body, metadata);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { return Failure<byte[]>(exception); }
        }

        public Task<SupabaseResult<StorageObject>> InfoAsync(string path,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return JsonAsync<StorageObject>(SupabaseHttpMethod.Get, "object/info/" + ObjectPath(path), null,
                cancellationToken);
        }

        public Task<SupabaseResult<IReadOnlyList<StorageObject>>> ListAsync(string prefix = null,
            StorageListOptions listOptions = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var body = JObject.FromObject(listOptions ?? new StorageListOptions());
            body["prefix"] = prefix ?? string.Empty;
            return JsonAsync<IReadOnlyList<StorageObject>>(SupabaseHttpMethod.Post,
                "object/list/" + SupabaseHttp.EscapePath(BucketId), body, cancellationToken);
        }

        public Task<SupabaseResult<StorageFileResult>> MoveAsync(string fromPath, string toPath,
            string destinationBucket = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return TransferAsync("object/move", fromPath, toPath, destinationBucket, false, cancellationToken);
        }

        public Task<SupabaseResult<StorageFileResult>> CopyAsync(string fromPath, string toPath,
            string destinationBucket = null, bool upsert = false,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return TransferAsync("object/copy", fromPath, toPath, destinationBucket, upsert, cancellationToken);
        }

        public Task<SupabaseResult<IReadOnlyList<StorageObject>>> RemoveAsync(IEnumerable<string> paths,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (paths == null) throw new ArgumentNullException("paths");
            return JsonAsync<IReadOnlyList<StorageObject>>(SupabaseHttpMethod.Delete,
                "object/" + SupabaseHttp.EscapePath(BucketId), new { prefixes = paths }, cancellationToken);
        }

        public async Task<SupabaseResult<StorageSignedUrl>> CreateSignedUrlAsync(string path, int expiresInSeconds,
            StorageTransformOptions transform = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (expiresInSeconds <= 0) throw new ArgumentOutOfRangeException("expiresInSeconds");
            var body = new JObject { ["expiresIn"] = expiresInSeconds };
            if (transform != null) body["transform"] = JObject.FromObject(transform);
            var result = await JsonAsync<StorageSignedUrl>(SupabaseHttpMethod.Post,
                "object/sign/" + ObjectPath(path), body, cancellationToken);
            if (result.IsSuccess) NormalizeSignedUrl(result.Data);
            return result;
        }

        public async Task<SupabaseResult<IReadOnlyList<StorageSignedUrl>>> CreateSignedUrlsAsync(
            IEnumerable<string> paths, int expiresInSeconds,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (paths == null) throw new ArgumentNullException("paths");
            var result = await JsonAsync<IReadOnlyList<StorageSignedUrl>>(SupabaseHttpMethod.Post,
                "object/sign/" + SupabaseHttp.EscapePath(BucketId),
                new { expiresIn = expiresInSeconds, paths = paths }, cancellationToken);
            if (result.IsSuccess && result.Data != null)
                foreach (var signedUrl in result.Data) NormalizeSignedUrl(signedUrl);
            return result;
        }

        public Task<SupabaseResult<StorageSignedUrl>> CreateSignedUploadUrlAsync(string path,
            bool upsert = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            var headers = new Dictionary<string, string>();
            if (upsert) headers["x-upsert"] = "true";
            return JsonAsync<StorageSignedUrl>(SupabaseHttpMethod.Post,
                "object/upload/sign/" + ObjectPath(path), new JObject(), cancellationToken, headers);
        }

        public Task<SupabaseResult<StorageFileResult>> UploadToSignedUrlAsync(string path, string token,
            byte[] data, StorageUploadOptions uploadOptions = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Token cannot be empty.", "token");
            return UploadCoreAsync(SupabaseHttpMethod.Put, "object/upload/sign/" + ObjectPath(path),
                new[] { new KeyValuePair<string, string>("token", token) }, data, uploadOptions, cancellationToken);
        }

        public Uri GetPublicUrl(string path, StorageTransformOptions transform = null,
            bool download = false, string downloadFileName = null)
        {
            var query = new List<KeyValuePair<string, string>>();
            if (transform != null) query.AddRange(transform.ToQuery());
            if (download) query.Add(new KeyValuePair<string, string>("download",
                string.IsNullOrWhiteSpace(downloadFileName) ? "" : downloadFileName));
            var relative = transform == null ? "object/public/" : "render/image/public/";
            return SupabaseHttp.Combine(endpoint, relative + ObjectPath(path), query);
        }

        private async Task<SupabaseResult<StorageFileResult>> UploadCoreAsync(SupabaseHttpMethod method,
            string relativePath, IEnumerable<KeyValuePair<string, string>> query, byte[] data,
            StorageUploadOptions uploadOptions, CancellationToken cancellationToken)
        {
            if (data == null) throw new ArgumentNullException("data");
            uploadOptions = uploadOptions ?? new StorageUploadOptions();
            var request = new SupabaseHttpRequest
            {
                Uri = SupabaseHttp.Combine(endpoint, relativePath, query), Method = method, Body = data,
                ContentType = uploadOptions.ContentType, Timeout = options.HttpTimeout,
                UploadProgress = uploadOptions.Progress
            };
            SupabaseHttp.ApplyClientHeaders(request.Headers, options, accessToken());
            request.Headers["cache-control"] = "max-age=" + uploadOptions.CacheControl;
            request.Headers["x-upsert"] = uploadOptions.Upsert ? "true" : "false";
            if (uploadOptions.Metadata != null)
                request.Headers["x-metadata"] = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(uploadOptions.Metadata.ToString(Newtonsoft.Json.Formatting.None)));
            SupabaseHttp.ValidateAdditionalHeaders(uploadOptions.Headers, options.PublishableKey);
            foreach (var header in uploadOptions.Headers) request.Headers[header.Key] = header.Value;
            try
            {
                var response = await transport.SendAsync(request, cancellationToken);
                var metadata = SupabaseHttp.Metadata(response);
                if (!response.IsSuccessStatusCode)
                    return SupabaseResult<StorageFileResult>.Failure(
                        SupabaseHttp.Error(SupabaseService.Storage, response), metadata);
                var value = string.IsNullOrWhiteSpace(response.Text)
                    ? new StorageFileResult() : SupabaseJson.Deserialize<StorageFileResult>(response.Text);
                return SupabaseResult<StorageFileResult>.Success(value, metadata);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { return Failure<StorageFileResult>(exception); }
        }

        private Task<SupabaseResult<StorageFileResult>> TransferAsync(string route, string fromPath,
            string toPath, string destinationBucket, bool upsert, CancellationToken cancellationToken)
        {
            var body = new JObject
            {
                ["bucketId"] = BucketId,
                ["sourceKey"] = fromPath,
                ["destinationKey"] = toPath
            };
            if (!string.IsNullOrWhiteSpace(destinationBucket)) body["destinationBucket"] = destinationBucket;
            if (upsert) body["upsert"] = true;
            return JsonAsync<StorageFileResult>(SupabaseHttpMethod.Post, route, body, cancellationToken);
        }

        private async Task<SupabaseResult<T>> JsonAsync<T>(SupabaseHttpMethod method, string path, object body,
            CancellationToken cancellationToken, IDictionary<string, string> headers = null)
        {
            var request = SupabaseHttp.CreateJsonRequest(options, SupabaseHttp.Combine(endpoint, path), method,
                body, accessToken());
            if (headers != null) foreach (var header in headers) request.Headers[header.Key] = header.Value;
            try
            {
                var response = await transport.SendAsync(request, cancellationToken);
                var metadata = SupabaseHttp.Metadata(response);
                if (!response.IsSuccessStatusCode)
                    return SupabaseResult<T>.Failure(SupabaseHttp.Error(SupabaseService.Storage, response), metadata);
                var data = string.IsNullOrWhiteSpace(response.Text) ? default(T) : SupabaseJson.Deserialize<T>(response.Text);
                return SupabaseResult<T>.Success(data, metadata);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { return Failure<T>(exception); }
        }

        private string ObjectPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Object path cannot be empty.", "path");
            return SupabaseHttp.EscapePath(BucketId) + "/" + SupabaseHttp.EscapePath(path);
        }

        private void NormalizeSignedUrl(StorageSignedUrl value)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.SignedUrl)) return;
            Uri absolute;
            if (Uri.TryCreate(value.SignedUrl, UriKind.Absolute, out absolute)) return;
            value.SignedUrl = endpoint.AbsoluteUri.TrimEnd('/') + "/" + value.SignedUrl.TrimStart('/');
        }

        internal static SupabaseResult<T> Failure<T>(Exception exception)
        {
            return SupabaseResult<T>.Failure(SupabaseError.Create(SupabaseService.Storage,
                SupabaseErrorKind.Serialization, "The Storage response could not be processed.",
                details: exception.Message));
        }
    }
}
