using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Supabase.Unity.Tests
{
    internal sealed class ContractHttpRequest
    {
        internal string Method;
        internal string Target;
        internal Dictionary<string, string> Headers;
        internal byte[] Body;

        internal string Text
        {
            get { return Body == null || Body.Length == 0 ? string.Empty : Encoding.UTF8.GetString(Body); }
        }
    }

    internal sealed class ContractHttpResponse
    {
        internal int StatusCode = 200;
        internal string ContentType = "application/json";
        internal byte[] Body = new byte[0];
        internal readonly Dictionary<string, string> Headers
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static ContractHttpResponse Json(int statusCode, string json)
        {
            return new ContractHttpResponse
            {
                StatusCode = statusCode,
                Body = Encoding.UTF8.GetBytes(json ?? string.Empty)
            };
        }
    }

    /// <summary>
    /// A loopback HTTP boundary for contract tests. It scripts responses without attempting
    /// to reproduce Supabase service behavior.
    /// </summary>
    internal sealed class LoopbackSupabaseFixture : IDisposable
    {
        private readonly TcpListener listener;
        private readonly Func<ContractHttpRequest, ContractHttpResponse> responder;
        private readonly List<ContractHttpRequest> requests = new List<ContractHttpRequest>();
        private readonly object gate = new object();
        private readonly Task worker;
        private volatile bool stopping;
        private Exception failure;

        internal Uri ProjectUrl { get; private set; }

        internal IReadOnlyList<ContractHttpRequest> Requests
        {
            get
            {
                lock (gate)
                    return requests.ToArray();
            }
        }

        internal LoopbackSupabaseFixture(Func<ContractHttpRequest, ContractHttpResponse> responder)
        {
            this.responder = responder ?? throw new ArgumentNullException("responder");
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            ProjectUrl = new Uri("http://127.0.0.1:" +
                endpoint.Port.ToString(CultureInfo.InvariantCulture));
            worker = Task.Run((Action)ListenLoop);
        }

        internal void ThrowIfFaulted()
        {
            Exception captured;
            lock (gate) captured = failure;
            if (captured != null)
                throw new InvalidOperationException("The local Supabase contract fixture failed.", captured);
        }

        private void ListenLoop()
        {
            while (!stopping)
            {
                TcpClient client = null;
                try
                {
                    client = listener.AcceptTcpClient();
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    using (client)
                    using (var stream = client.GetStream())
                    {
                        var raw = RawHttp.Read(stream);
                        var request = new ContractHttpRequest
                        {
                            Method = RawHttp.Method(raw.FirstLine),
                            Target = RawHttp.Target(raw.FirstLine),
                            Headers = raw.Headers,
                            Body = raw.Body
                        };
                        lock (gate) requests.Add(request);

                        ContractHttpResponse response;
                        try
                        {
                            response = responder(request) ?? ContractHttpResponse.Json(500,
                                "{\"message\":\"Contract responder returned no response.\"}");
                        }
                        catch (Exception exception)
                        {
                            RecordFailure(exception);
                            response = ContractHttpResponse.Json(500,
                                "{\"message\":\"Contract responder failed.\"}");
                        }
                        RawHttp.WriteResponse(stream, response);
                    }
                }
                catch (ObjectDisposedException)
                {
                    if (!stopping) RecordFailure(new IOException("The contract listener was disposed."));
                }
                catch (SocketException exception)
                {
                    if (!stopping) RecordFailure(exception);
                }
                catch (Exception exception)
                {
                    if (!stopping) RecordFailure(exception);
                }
            }
        }

        private void RecordFailure(Exception exception)
        {
            lock (gate)
                if (failure == null) failure = exception;
        }

        public void Dispose()
        {
            if (stopping) return;
            stopping = true;
            listener.Stop();
            if (!worker.Wait(TimeSpan.FromSeconds(2)))
                RecordFailure(new TimeoutException("The contract listener did not stop."));
        }
    }

    /// <summary>Test-only transport that crosses the loopback HTTP boundary.</summary>
    internal sealed class LoopbackHttpTransport : IHttpTransport
    {
        public Task<SupabaseHttpResponse> SendAsync(SupabaseHttpRequest request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException("request");
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var outbound = (HttpWebRequest)WebRequest.Create(request.Uri);
                outbound.Method = request.Method.ToString().ToUpperInvariant();
                outbound.Timeout = (int)Math.Max(1,
                    Math.Min(int.MaxValue, request.Timeout.TotalMilliseconds));
                outbound.ReadWriteTimeout = outbound.Timeout;
                outbound.ServicePoint.Expect100Continue = false;
                foreach (var header in request.Headers)
                    outbound.Headers[header.Key] = header.Value;
                if (!string.IsNullOrWhiteSpace(request.ContentType))
                    outbound.ContentType = request.ContentType;
                var body = request.Body ?? new byte[0];
                if (body.Length > 0)
                {
                    outbound.ContentLength = body.Length;
                    using (var requestStream = outbound.GetRequestStream())
                        requestStream.Write(body, 0, body.Length);
                }
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using (var response = (HttpWebResponse)outbound.GetResponse())
                        return Task.FromResult(ToResponse(response));
                }
                catch (WebException exception)
                {
                    var response = exception.Response as HttpWebResponse;
                    if (response != null)
                    {
                        using (response)
                            return Task.FromResult(ToResponse(response));
                    }
                    throw;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return Task.FromResult(new SupabaseHttpResponse
                {
                    TransportError = exception.Message
                });
            }
        }

        public void Dispose() { }

        private static SupabaseHttpResponse ToResponse(HttpWebResponse response)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in response.Headers.AllKeys)
                headers[key] = response.Headers[key];
            using (var body = new MemoryStream())
            using (var stream = response.GetResponseStream())
            {
                if (stream != null) stream.CopyTo(body);
                return new SupabaseHttpResponse
                {
                    StatusCode = (int)response.StatusCode,
                    Headers = headers,
                    Body = body.ToArray()
                };
            }
        }
    }

    internal static class RawHttp
    {
        internal sealed class Message
        {
            internal string FirstLine;
            internal Dictionary<string, string> Headers;
            internal byte[] Body;
        }

        internal static Message Read(Stream stream)
        {
            var headerBytes = new MemoryStream();
            var delimiter = new byte[] { 13, 10, 13, 10 };
            var matched = 0;
            while (matched < delimiter.Length)
            {
                var value = stream.ReadByte();
                if (value < 0) throw new EndOfStreamException("HTTP headers ended unexpectedly.");
                headerBytes.WriteByte((byte)value);
                if (headerBytes.Length > 64 * 1024)
                    throw new InvalidDataException("HTTP headers exceeded 64 KiB.");
                if (value == delimiter[matched])
                    matched++;
                else
                    matched = value == delimiter[0] ? 1 : 0;
            }

            var bytes = headerBytes.ToArray();
            var text = Encoding.ASCII.GetString(bytes, 0, bytes.Length - delimiter.Length);
            var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
                throw new InvalidDataException("HTTP message had no start line.");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 1; index < lines.Length; index++)
            {
                var separator = lines[index].IndexOf(':');
                if (separator <= 0) continue;
                headers[lines[index].Substring(0, separator).Trim()] =
                    lines[index].Substring(separator + 1).Trim();
            }

            var contentLength = 0;
            string lengthText;
            if (headers.TryGetValue("Content-Length", out lengthText) &&
                (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture,
                    out contentLength) || contentLength < 0 || contentLength > 16 * 1024 * 1024))
                throw new InvalidDataException("HTTP Content-Length was invalid.");
            var body = new byte[contentLength];
            var offset = 0;
            while (offset < body.Length)
            {
                var read = stream.Read(body, offset, body.Length - offset);
                if (read <= 0) throw new EndOfStreamException("HTTP body ended unexpectedly.");
                offset += read;
            }
            return new Message { FirstLine = lines[0], Headers = headers, Body = body };
        }

        internal static string Method(string requestLine)
        {
            return Part(requestLine, 0, "request method");
        }

        internal static string Target(string requestLine)
        {
            return Part(requestLine, 1, "request target");
        }

        internal static void WriteResponse(Stream stream, ContractHttpResponse response)
        {
            var body = response.Body ?? new byte[0];
            var builder = new StringBuilder();
            builder.Append("HTTP/1.1 ").Append(response.StatusCode.ToString(CultureInfo.InvariantCulture))
                .Append(' ').Append(Reason(response.StatusCode)).Append("\r\n")
                .Append("Connection: close\r\n")
                .Append("Content-Type: ").Append(response.ContentType ?? "application/octet-stream")
                .Append("\r\n");
            foreach (var header in response.Headers)
                builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            builder.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture))
                .Append("\r\n\r\n");
            Write(stream, builder.ToString(), body);
        }

        private static void Write(Stream stream, string headers, byte[] body)
        {
            var headerBytes = Encoding.ASCII.GetBytes(headers);
            stream.Write(headerBytes, 0, headerBytes.Length);
            if (body.Length > 0) stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static string Part(string line, int index, string name)
        {
            var parts = line.Split(' ');
            if (parts.Length <= index || string.IsNullOrWhiteSpace(parts[index]))
                throw new InvalidDataException("HTTP " + name + " was missing.");
            return parts[index];
        }

        private static string Reason(int statusCode)
        {
            switch (statusCode)
            {
                case 200: return "OK";
                case 201: return "Created";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 404: return "Not Found";
                case 429: return "Too Many Requests";
                case 500: return "Internal Server Error";
                default: return "Response";
            }
        }
    }
}
