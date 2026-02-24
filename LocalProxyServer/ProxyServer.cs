using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;

namespace LocalProxyServer
{
    public class ProxyServer
    {
        private readonly int _port;
        private readonly IReadOnlyList<UpstreamConfiguration> _upstreams;
        private readonly string _loadBalancingStrategy;
        private readonly X509Certificate2? _serverCertificate;
        private int _roundRobinIndex = 0;
        private readonly ILogger<ProxyServer> _logger;
        private TcpListener? _listener;
        private bool _isRunning;

        public ProxyServer(
            int port, 
            IEnumerable<UpstreamConfiguration>? upstreams = null, 
            string loadBalancingStrategy = "failover",
            X509Certificate2? serverCertificate = null,
            ILogger<ProxyServer>? logger = null)
        {
            _port = port;
            var upstreamList = upstreams?.ToList() ?? new List<UpstreamConfiguration>();
            _upstreams = upstreamList.Where(u => u.Enabled).ToList();
            _loadBalancingStrategy = loadBalancingStrategy.ToLowerInvariant();
            _serverCertificate = serverCertificate;
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ProxyServer>.Instance;
        }

        public async Task StartAsync()
        {
            if (Socket.OSSupportsIPv6)
            {
                _listener = new TcpListener(IPAddress.IPv6Any, _port);
                _listener.Server.DualMode = true;
                _logger.LogInformation("IPv6 dual-mode listener enabled");
            }
            else
            {
                _listener = new TcpListener(IPAddress.Any, _port);
            }

            _listener.Start();
            _isRunning = true;
            _logger.LogInformation("Proxy server started on port {Port}", _port);
            
            if (_serverCertificate != null)
            {
                _logger.LogInformation("HTTPS proxy support enabled (TLS to Proxy)");
            }
            if (_upstreams.Count > 0)
            {
                _logger.LogInformation("Using {Count} upstream proxies with strategy '{Strategy}'", 
                    _upstreams.Count, _loadBalancingStrategy);
            }

            while (_isRunning)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = HandleClientAsync(client);
                }
                catch (Exception ex)
                {
                    if (_isRunning) 
                        _logger.LogError(ex, "Error accepting client");
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            var remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
            var clientAddressFamily = remoteEndPoint?.AddressFamily;
            var clientEndpoint = remoteEndPoint?.ToString() ?? "unknown";
            _logger.LogInformation("New client connection from {Endpoint}", clientEndpoint);
            
            Stream? finalStream = null;
            try
            {
                var networkStream = client.GetStream();
                var (prefixedStream, isTls) = await PrepareClientStreamAsync(networkStream, clientEndpoint);
                if (prefixedStream == null)
                {
                    return;
                }

                if (isTls)
                {
                    if (_serverCertificate == null)
                    {
                        _logger.LogWarning("TLS request received from {Endpoint} but HTTPS is not enabled.", clientEndpoint);
                        return;
                    }

                    _logger.LogDebug("Establishing TLS connection with client {Endpoint}", clientEndpoint);
                    var sslStream = new SslStream(prefixedStream, false);
                    try
                    {
                        await sslStream.AuthenticateAsServerAsync(_serverCertificate, false, System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13, false);
                    }
                    catch (System.Security.Authentication.AuthenticationException ex)
                    {
                        _logger.LogWarning(ex, "TLS handshake failed with client {Endpoint}. Client may be using HTTP to connect to an HTTPS proxy.", clientEndpoint);
                        return;
                    }

                    finalStream = sslStream;
                    _logger.LogDebug("TLS connection established with client {Endpoint}", clientEndpoint);
                }
                else
                {
                    finalStream = prefixedStream;
                    _logger.LogDebug("Handling HTTP proxy request from {Endpoint}", clientEndpoint);
                }

                // Read request line
                var reader = new StreamReader(finalStream, Encoding.ASCII, leaveOpen: true);
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(line))
                {
                    _logger.LogWarning("Empty request from client {Endpoint}", clientEndpoint);
                    return;
                }

                var parts = line.Split(' ');
                if (parts.Length < 3)
                {
                    _logger.LogWarning("Invalid request line from client {Endpoint}: {Line}", clientEndpoint, line);
                    return;
                }

                var method = parts[0];
                var url = parts[1];

                _logger.LogInformation("Request from {Endpoint}: {Method} {Url}", clientEndpoint, method, url);

                if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleConnectAsync(client, finalStream, url, clientEndpoint, clientAddressFamily);
                }
                else
                {
                    await HandleHttpAsync(client, finalStream, line, reader, clientEndpoint, clientAddressFamily);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling client {Endpoint}", clientEndpoint);
            }
            finally
            {
                finalStream?.Dispose();
                client.Dispose();
                _logger.LogInformation("Client connection closed: {Endpoint}", clientEndpoint);
            }
        }

        private static bool LooksLikeTls(ReadOnlySpan<byte> buffer)
        {
            return buffer.Length >= 3 &&
                   buffer[0] == 0x16 &&
                   buffer[1] == 0x03 &&
                   buffer[2] >= 0x01 &&
                   buffer[2] <= 0x04;
        }

        private async Task<(Stream? Stream, bool IsTls)> PrepareClientStreamAsync(NetworkStream networkStream, string clientEndpoint)
        {
            var prefixBuffer = new byte[5];
            int bytesRead;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            try
            {
                bytesRead = await networkStream.ReadAsync(prefixBuffer, 0, prefixBuffer.Length, cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Timed out waiting for initial data from {Endpoint}", clientEndpoint);
                return (null, false);
            }

            if (bytesRead == 0)
            {
                _logger.LogWarning("Client {Endpoint} closed connection before sending data", clientEndpoint);
                return (null, false);
            }

            var prefix = new ReadOnlyMemory<byte>(prefixBuffer, 0, bytesRead);
            var prefixedStream = new PrefixedStream(networkStream, prefix);
            var isTls = LooksLikeTls(prefix.Span);
            return (prefixedStream, isTls);
        }

        private sealed class PrefixedStream : Stream
        {
            private readonly Stream _inner;
            private ReadOnlyMemory<byte> _prefix;

            public PrefixedStream(Stream inner, ReadOnlyMemory<byte> prefix)
            {
                _inner = inner;
                _prefix = prefix;
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() => _inner.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = ReadFromPrefix(buffer.AsSpan(offset, count));
                return read > 0 ? read : _inner.Read(buffer, offset, count);
            }

            public override int Read(Span<byte> buffer)
            {
                var read = ReadFromPrefix(buffer);
                return read > 0 ? read : _inner.Read(buffer);
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                var read = ReadFromPrefix(buffer.AsSpan(offset, count));
                return read > 0 ? Task.FromResult(read) : _inner.ReadAsync(buffer, offset, count, cancellationToken);
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var read = ReadFromPrefix(buffer.Span);
                return read > 0 ? ValueTask.FromResult(read) : _inner.ReadAsync(buffer, cancellationToken);
            }

            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
            public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => _inner.WriteAsync(buffer, offset, count, cancellationToken);

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
                => _inner.WriteAsync(buffer, cancellationToken);

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }

                base.Dispose(disposing);
            }

            private int ReadFromPrefix(Span<byte> destination)
            {
                if (_prefix.IsEmpty)
                {
                    return 0;
                }

                var toCopy = Math.Min(destination.Length, _prefix.Length);
                _prefix.Span[..toCopy].CopyTo(destination);
                _prefix = _prefix.Slice(toCopy);
                return toCopy;
            }
        }

        private async Task HandleConnectAsync(TcpClient client, Stream clientStream, string target, string clientEndpoint, AddressFamily? preferredAddressFamily)
        {
            if (!TryParseHostAndPort(target, 443, out var host, out var port))
            {
                _logger.LogWarning("Invalid CONNECT target from {Client}: {Target}", clientEndpoint, target);
                return;
            }

            _logger.LogInformation("CONNECT request from {Client} to {Host}:{Port}", clientEndpoint, host, port);

            TcpClient? upstreamClient = null;
            try
            {
                upstreamClient = await ConnectToUpstreamAsync(host, port, preferredAddressFamily);

                _logger.LogInformation("Tunnel established: {Client} <-> {Host}:{Port}", clientEndpoint, host, port);

                using (upstreamClient)
                using (var upstreamStream = upstreamClient.GetStream())
                {
                    // Send 200 Connection Established to client
                    var response = "HTTP/1.1 200 Connection Established\r\n\r\n";
                    await clientStream.WriteAsync(Encoding.ASCII.GetBytes(response));

                    // Track data transfer
                    var clientToServer = CopyWithLoggingAsync(clientStream, upstreamStream, 
                        $"{clientEndpoint} -> {host}:{port}");
                    var serverToClient = CopyWithLoggingAsync(upstreamStream, clientStream, 
                        $"{host}:{port} -> {clientEndpoint}");

                    await Task.WhenAll(clientToServer, serverToClient);
                }
                
                _logger.LogInformation("Tunnel closed: {Client} <-> {Host}:{Port}", clientEndpoint, host, port);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling CONNECT to {Host}:{Port} from {Client}", host, port, clientEndpoint);
                throw;
            }
            finally
            {
                upstreamClient?.Dispose();
            }
        }

        private async Task HandleHttpAsync(TcpClient client, Stream clientStream, string firstLine, StreamReader reader, string clientEndpoint, AddressFamily? preferredAddressFamily)
        {
            // Simple HTTP proxying
            var parts = firstLine.Split(' ');
            var url = parts[1];
            
            string host;
            int port;
            string pathAndQuery;

            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(url);
                host = uri.Host;
                port = uri.Port;
                pathAndQuery = uri.PathAndQuery;
            }
            else
            {
                // If it's a relative path, we must find the Host header
                pathAndQuery = url;
                host = ""; // Will find in headers
                port = 80;
            }

            // Need to read headers to find Host if not in URL, and to forward them
            var headers = new List<string>();
            string? header;
            while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync()))
            {
                headers.Add(header);
                if (string.IsNullOrEmpty(host) && header.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                {
                    var hostValue = header.Substring(5).Trim();
                    if (!TryParseHostAndPort(hostValue, port, out host, out port))
                    {
                        _logger.LogWarning("Invalid Host header from {Client}: {Host}", clientEndpoint, hostValue);
                        return;
                    }
                }
            }

            if (string.IsNullOrEmpty(host))
            {
                _logger.LogWarning("Could not determine host from HTTP request from {Client}", clientEndpoint);
                return;
            }

            _logger.LogInformation("HTTP request from {Client} to {Host}:{Port} {Path}", 
                clientEndpoint, host, port, pathAndQuery);

            TcpClient? upstreamClient = null;
            try
            {
                upstreamClient = await ConnectToUpstreamAsync(host, port, preferredAddressFamily);

                using (upstreamClient)
                using (var upstreamStream = upstreamClient.GetStream())
                {
                    // Forward the first line but with relative path
                    var protocol = parts.Length > 2 ? parts[2] : "HTTP/1.1";
                    var newFirstLine = $"{parts[0]} {pathAndQuery} {protocol}\r\n";
                    await upstreamStream.WriteAsync(Encoding.ASCII.GetBytes(newFirstLine));

                    // Forward remaining headers
                    foreach (var h in headers)
                    {
                        await upstreamStream.WriteAsync(Encoding.ASCII.GetBytes(h + "\r\n"));
                    }
                    await upstreamStream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"));

                    _logger.LogInformation("HTTP proxy established: {Client} <-> {Host}:{Port}", 
                        clientEndpoint, host, port);

                    // Tunnel remaining traffic (body and response)
                    await Task.WhenAll(
                        clientStream.CopyToAsync(upstreamStream),
                        upstreamStream.CopyToAsync(clientStream)
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling HTTP request to {Host}:{Port} from {Client}", 
                    host, port, clientEndpoint);
                throw;
            }
            finally
            {
                upstreamClient?.Dispose();
            }
        }

        private async Task CopyWithLoggingAsync(Stream source, Stream destination, string direction)
        {
            var buffer = new byte[81920]; // 80KB buffer
            long totalBytes = 0;
            int bytesRead;

            try
            {
                while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await destination.WriteAsync(buffer, 0, bytesRead);
                    totalBytes += bytesRead;
                }
                
                _logger.LogDebug("Transfer completed {Direction}: {TotalBytes} bytes", 
                    direction, totalBytes);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Transfer ended {Direction} after {TotalBytes} bytes", 
                    direction, totalBytes);
                throw;
            }
        }

        private async Task<TcpClient> ConnectToUpstreamAsync(string targetHost, int targetPort, AddressFamily? preferredAddressFamily)
        {
            if (_upstreams.Count == 0)
            {
                // Direct connection
                _logger.LogDebug("Connecting directly to {Host}:{Port}", targetHost, targetPort);
                return await TcpClientConnector.ConnectAsync(targetHost, targetPort, preferredAddressFamily);
            }

            IEnumerable<UpstreamConfiguration> selectedUpstreams;

            if (_loadBalancingStrategy == "roundrobin")
            {
                // Subtract 1 so the first call maps to upstream[0].
                // Cast to uint before % to handle int overflow without a branch.
                int skip = (int)((uint)(Interlocked.Increment(ref _roundRobinIndex) - 1) % (uint)_upstreams.Count);
                // Start with the selected one, then failover to others if needed
                selectedUpstreams = _upstreams.Skip(skip).Concat(_upstreams.Take(skip));
            }
            else // failover
            {
                selectedUpstreams = _upstreams;
            }

            List<Exception> exceptions = new();

            foreach (var upstream in selectedUpstreams)
            {
                if (string.IsNullOrEmpty(upstream.Host))
                    continue;

                try
                {
                    return upstream.Type.ToLowerInvariant() switch
                    {
                        "socks5" => await ConnectViaSocks5Async(targetHost, targetPort, upstream, preferredAddressFamily),
                        "http" => await ConnectViaHttpAsync(targetHost, targetPort, upstream, preferredAddressFamily),
                        _ => throw new NotSupportedException($"Upstream type '{upstream.Type}' is not supported")
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to connect to upstream {Type} proxy {Host}:{Port}", upstream.Type, upstream.Host, upstream.Port);
                    exceptions.Add(ex);
                }
            }

            throw new AggregateException($"Failed to connect to any upstream proxy for destination {targetHost}:{targetPort}", exceptions);
        }

        private async Task<TcpClient> ConnectViaSocks5Async(string targetHost, int targetPort, UpstreamConfiguration upstream, AddressFamily? preferredAddressFamily)
        {
            _logger.LogDebug("Connecting to {Host}:{Port} via SOCKS5 {UpstreamHost}:{UpstreamPort}",
                targetHost, targetPort, upstream.Host, upstream.Port);
            
            var socks = new Socks5Client(upstream.Host!, upstream.Port, _logger, preferredAddressFamily);
            return await socks.ConnectAsync(targetHost, targetPort);
        }

        private async Task<TcpClient> ConnectViaHttpAsync(string targetHost, int targetPort, UpstreamConfiguration upstream, AddressFamily? preferredAddressFamily)
        {
            _logger.LogDebug("Connecting to {Host}:{Port} via HTTP proxy {UpstreamHost}:{UpstreamPort}",
                targetHost, targetPort, upstream.Host, upstream.Port);
            
            var httpProxy = new HttpProxyClient(upstream.Host!, upstream.Port, _logger, preferredAddressFamily);
            return await httpProxy.ConnectAsync(targetHost, targetPort);
        }

        private static bool TryParseHostAndPort(string hostValue, int defaultPort, out string host, out int port)
        {
            host = string.Empty;
            port = defaultPort;

            if (string.IsNullOrWhiteSpace(hostValue))
            {
                return false;
            }

            var value = hostValue.Trim();
            if (value.StartsWith('['))
            {
                var closingIndex = value.IndexOf(']');
                if (closingIndex <= 0)
                {
                    return false;
                }

                host = value[1..closingIndex];
                if (closingIndex + 1 < value.Length && value[closingIndex + 1] == ':')
                {
                    var portValue = value[(closingIndex + 2)..];
                    if (!int.TryParse(portValue, out port))
                    {
                        return false;
                    }
                }

                return true;
            }

            var lastColon = value.LastIndexOf(':');
            if (lastColon > 0 && value.IndexOf(':') == lastColon)
            {
                host = value[..lastColon];
                if (!int.TryParse(value[(lastColon + 1)..], out port))
                {
                    return false;
                }

                return true;
            }

            host = value;
            return true;
        }

        public void Stop()
        {
            _isRunning = false;
            _listener?.Stop();
        }
    }
}
