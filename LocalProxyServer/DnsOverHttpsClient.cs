using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

using Microsoft.Extensions.Logging;

namespace LocalProxyServer
{
    public class DnsOverHttpsClient
    {
        private readonly Uri _endpoint;
        private readonly IReadOnlyList<Socks5Client> _socks5Clients;
        private readonly int _timeoutMs;
        private readonly ILogger? _logger;
        private readonly AddressFamily? _preferredAddressFamily;
        private int _socksIndex;

        public DnsOverHttpsClient(
            Uri endpoint,
            IReadOnlyList<Socks5Client> socks5Clients,
            int timeoutMs,
            ILogger? logger,
            AddressFamily? preferredAddressFamily)
        {
            _endpoint = endpoint;
            _socks5Clients = socks5Clients;
            _timeoutMs = timeoutMs;
            _logger = logger;
            _preferredAddressFamily = preferredAddressFamily;
        }

        public async Task<byte[]> QueryAsync(byte[] query, CancellationToken cancellationToken)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeoutMs);
            var token = cts.Token;

            using var tcpClient = await ConnectAsync(token);
            using var stream = tcpClient.GetStream();
            using var ssl = new SslStream(stream, false);

            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = _endpoint.Host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, token);

            var request = BuildHttpRequest(query);
            await ssl.WriteAsync(request, token);
            await ssl.FlushAsync(token);

            return await ReadHttpResponseAsync(ssl, token);
        }

        private async Task<TcpClient> ConnectAsync(CancellationToken token)
        {
            int port = _endpoint.Port > 0 ? _endpoint.Port : 443;

            if (_socks5Clients.Count > 0)
            {
                foreach (var socks5 in SelectSocks5Clients())
                {
                    try
                    {
                        _logger?.LogDebug("Connecting to DoH endpoint via SOCKS5 {Host}:{Port}", _endpoint.Host, port);
                        return await socks5.ConnectAsync(_endpoint.Host, port);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "SOCKS5 connect failed for DoH endpoint {Host}:{Port}", _endpoint.Host, port);
                    }
                }
            }

            _logger?.LogDebug("Connecting to DoH endpoint directly {Host}:{Port}", _endpoint.Host, port);
            return await TcpClientConnector.ConnectAsync(_endpoint.Host, port, _preferredAddressFamily);
        }

        private IEnumerable<Socks5Client> SelectSocks5Clients()
        {
            if (_socks5Clients.Count == 1)
            {
                return _socks5Clients;
            }

            int start = (int)((uint)(Interlocked.Increment(ref _socksIndex) - 1) % (uint)_socks5Clients.Count);
            return _socks5Clients.Skip(start).Concat(_socks5Clients.Take(start));
        }

        private byte[] BuildHttpRequest(byte[] query)
        {
            var pathAndQuery = string.IsNullOrEmpty(_endpoint.PathAndQuery) ? "/dns-query" : _endpoint.PathAndQuery;
            var hostHeader = _endpoint.IsDefaultPort ? _endpoint.Host : $"{_endpoint.Host}:{_endpoint.Port}";
            var builder = new StringBuilder();
            builder.Append("POST ").Append(pathAndQuery).Append(" HTTP/1.1\r\n");
            builder.Append("Host: ").Append(hostHeader).Append("\r\n");
            builder.Append("Content-Type: application/dns-message\r\n");
            builder.Append("Accept: application/dns-message\r\n");
            builder.Append("Content-Length: ").Append(query.Length).Append("\r\n");
            builder.Append("Connection: close\r\n\r\n");
            var headerBytes = Encoding.ASCII.GetBytes(builder.ToString());
            var payload = new byte[headerBytes.Length + query.Length];
            Buffer.BlockCopy(headerBytes, 0, payload, 0, headerBytes.Length);
            Buffer.BlockCopy(query, 0, payload, headerBytes.Length, query.Length);
            return payload;
        }

        private static async Task<byte[]> ReadHttpResponseAsync(Stream stream, CancellationToken token)
        {
            var (statusCode, headers, bodyPrefix) = await ReadHeadersAsync(stream, token);
            if (statusCode < 200 || statusCode >= 300)
            {
                throw new IOException($"DoH server returned HTTP {statusCode}");
            }

            if (headers.TryGetValue("transfer-encoding", out var transferEncoding) &&
                transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                return await ReadChunkedBodyAsync(stream, bodyPrefix, token);
            }

            if (!headers.TryGetValue("content-length", out var contentLengthValue) ||
                !int.TryParse(contentLengthValue, out var contentLength) ||
                contentLength < 0)
            {
                throw new IOException("DoH response missing Content-Length");
            }

            var body = new byte[contentLength];
            int copied = 0;
            if (bodyPrefix.Length > 0)
            {
                copied = Math.Min(bodyPrefix.Length, contentLength);
                Buffer.BlockCopy(bodyPrefix, 0, body, 0, copied);
            }

            if (copied < contentLength)
            {
                await ReadExactlyAsync(stream, body, copied, contentLength - copied, token);
            }

            return body;
        }

        private static async Task<(int statusCode, Dictionary<string, string> headers, byte[] bodyPrefix)> ReadHeadersAsync(Stream stream, CancellationToken token)
        {
            var buffer = new byte[8192];
            using var ms = new MemoryStream();
            int headerEnd = -1;

            while (headerEnd < 0)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(), token);
                if (read == 0)
                {
                    break;
                }

                ms.Write(buffer, 0, read);
                headerEnd = FindHeaderTerminator(ms.GetBuffer(), (int)ms.Length);
                if (ms.Length > 64 * 1024)
                {
                    throw new IOException("HTTP response headers too large");
                }
            }

            if (headerEnd < 0)
            {
                throw new IOException("Invalid HTTP response");
            }

            var raw = ms.GetBuffer();
            var headerBytes = raw.AsSpan(0, headerEnd);
            var headerText = Encoding.ASCII.GetString(headerBytes);
            var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                throw new IOException("Missing HTTP status line");
            }

            var statusParts = lines[0].Split(' ');
            if (statusParts.Length < 2 || !int.TryParse(statusParts[1], out var statusCode))
            {
                throw new IOException("Invalid HTTP status line");
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                var name = lines[i].Substring(0, colon).Trim();
                var value = lines[i].Substring(colon + 1).Trim();
                headers[name] = value;
            }

            int bodyStart = headerEnd + 4;
            int bodyLength = (int)ms.Length - bodyStart;
            var bodyPrefix = bodyLength > 0 ? raw.AsSpan(bodyStart, bodyLength).ToArray() : Array.Empty<byte>();

            return (statusCode, headers, bodyPrefix);
        }

        private static async Task<byte[]> ReadChunkedBodyAsync(Stream stream, byte[] prefix, CancellationToken token)
        {
            using var ms = new MemoryStream();
            var buffer = new MemoryStream(prefix, writable: false);

            while (true)
            {
                var line = await ReadLineAsync(stream, buffer, token);
                if (line == null)
                {
                    throw new IOException("Unexpected end of chunked response");
                }

                if (!int.TryParse(line.Split(';')[0], System.Globalization.NumberStyles.HexNumber, null, out var size))
                {
                    throw new IOException("Invalid chunk size");
                }

                if (size == 0)
                {
                    await ReadLineAsync(stream, buffer, token); // Trailing CRLF
                    break;
                }

                await ReadToStreamAsync(stream, buffer, ms, size, token);
                await ReadLineAsync(stream, buffer, token); // Consume CRLF
            }

            return ms.ToArray();
        }

        private static async Task ReadToStreamAsync(Stream stream, MemoryStream prefixStream, MemoryStream destination, int size, CancellationToken token)
        {
            int remaining = size;
            var buffer = new byte[4096];

            while (remaining > 0)
            {
                int read = await ReadFromPrefixOrStreamAsync(stream, prefixStream, buffer, 0, Math.Min(buffer.Length, remaining), token);
                if (read == 0)
                {
                    throw new IOException("Unexpected end of chunked response");
                }

                destination.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        private static async Task<string?> ReadLineAsync(Stream stream, MemoryStream prefixStream, CancellationToken token)
        {
            using var ms = new MemoryStream();
            while (true)
            {
                int b = await ReadByteAsync(stream, prefixStream, token);
                if (b == -1)
                {
                    return null;
                }

                if (b == '\n')
                {
                    var line = Encoding.ASCII.GetString(ms.ToArray()).TrimEnd('\r');
                    return line;
                }

                ms.WriteByte((byte)b);
            }
        }

        private static async Task<int> ReadByteAsync(Stream stream, MemoryStream prefixStream, CancellationToken token)
        {
            var buffer = new byte[1];
            int read = await ReadFromPrefixOrStreamAsync(stream, prefixStream, buffer, 0, 1, token);
            return read == 0 ? -1 : buffer[0];
        }

        private static async Task<int> ReadFromPrefixOrStreamAsync(Stream stream, MemoryStream prefixStream, byte[] buffer, int offset, int count, CancellationToken token)
        {
            if (prefixStream.Position < prefixStream.Length)
            {
                int available = (int)(prefixStream.Length - prefixStream.Position);
                int toRead = Math.Min(count, available);
                int read = prefixStream.Read(buffer, offset, toRead);
                return read;
            }

            return await stream.ReadAsync(buffer.AsMemory(offset, count), token);
        }

        private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken token)
        {
            int readTotal = 0;
            while (readTotal < count)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset + readTotal, count - readTotal), token);
                if (read == 0)
                {
                    throw new IOException("Unexpected end of HTTP response");
                }

                readTotal += read;
            }
        }

        private static int FindHeaderTerminator(byte[] buffer, int length)
        {
            for (int i = 0; i <= length - 4; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' &&
                    buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
