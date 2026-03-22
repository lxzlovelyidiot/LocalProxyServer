using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;

namespace LocalProxyServer
{
    public class DnsServer
    {
        private readonly DnsConfiguration _config;
        private readonly ILogger _logger;
        private readonly DnsCache? _cache;
        private readonly IReadOnlyList<DnsOverHttpsClient> _upstreamDohClients;
        private readonly DnsOverHttpsClient? _defaultDohClient;
        private readonly CancellationTokenSource _cts = new();
        private Socket? _udpSocket;
        private TcpListener? _tcpListener;

        public DnsServer(DnsConfiguration config, ILogger logger)
        {
            _config = config;
            _logger = logger;
            _cache = config.Cache.Enabled ? new DnsCache(config.Cache) : null;

            var socksClients = BuildSocks5Clients(config, logger);
            var upstreamEndpoints = BuildEndpoints(config).ToList();
            _upstreamDohClients = upstreamEndpoints
                .Select(endpoint => new DnsOverHttpsClient(endpoint, socksClients, config.TimeoutMs, logger, config.GetPreferredAddressFamily()))
                .ToList();

            if (!string.IsNullOrEmpty(config.DefaultDohEndpoint) && Uri.TryCreate(config.DefaultDohEndpoint, UriKind.Absolute, out var defaultUri))
            {
                // Default DoH goes direct, not through SOCKS5 upstream (assumes direct connection for non-pattern queries)
                _defaultDohClient = new DnsOverHttpsClient(defaultUri, Array.Empty<Socks5Client>(), config.TimeoutMs, logger, config.GetPreferredAddressFamily());
            }
        }

        public Task StartAsync()
        {
            StartUdpListener();
            StartTcpListener();
            return Task.CompletedTask;
        }

        public void Stop()
        {
            _cts.Cancel();
            _udpSocket?.Close();
            _tcpListener?.Stop();
        }

        private void StartUdpListener()
        {
            var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp)
            {
                DualMode = true
            };
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, _config.Port));
            _udpSocket = socket;

            _logger.LogInformation("DNS UDP server started on port {Port} (IPv4/IPv6)", _config.Port);
            _ = Task.Run(() => UdpLoopAsync(socket, _cts.Token));
        }

        private void StartTcpListener()
        {
            var listener = new TcpListener(IPAddress.IPv6Any, _config.Port);
            listener.Server.DualMode = true;
            listener.Start();
            _tcpListener = listener;

            _logger.LogInformation("DNS TCP server started on port {Port} (IPv4/IPv6)", _config.Port);
            _ = Task.Run(() => TcpLoopAsync(listener, _cts.Token));
        }

        private async Task UdpLoopAsync(Socket socket, CancellationToken token)
        {
            var buffer = new byte[4096];
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.IPv6Any, 0), token);
                    var length = result.ReceivedBytes;
                    if (length <= 0)
                    {
                        continue;
                    }

                    var request = new byte[length];
                    Buffer.BlockCopy(buffer, 0, request, 0, length);
                    _ = Task.Run(async () =>
                    {
                        var response = await ResolveAsync(request, token);
                        if (response.Length > 0)
                        {
                            await socket.SendToAsync(response, SocketFlags.None, result.RemoteEndPoint, token);
                        }
                    }, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DNS UDP loop error");
                }
            }
        }

        private async Task TcpLoopAsync(TcpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await listener.AcceptTcpClientAsync(token);
                    _ = Task.Run(() => HandleTcpClientAsync(client, token), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    client?.Dispose();
                    _logger.LogWarning(ex, "DNS TCP accept error");
                }
            }
        }

        private async Task HandleTcpClientAsync(TcpClient client, CancellationToken token)
        {
            using var tcpClient = client;
            using var stream = tcpClient.GetStream();

            try
            {
                var lengthPrefix = new byte[2];
                await ReadExactlyAsync(stream, lengthPrefix, token);
                int length = (lengthPrefix[0] << 8) | lengthPrefix[1];
                if (length <= 0 || length > 65535)
                {
                    return;
                }

                var request = new byte[length];
                await ReadExactlyAsync(stream, request, token);

                var response = await ResolveAsync(request, token);
                if (response.Length == 0)
                {
                    return;
                }

                var responsePrefix = new byte[2];
                responsePrefix[0] = (byte)(response.Length >> 8);
                responsePrefix[1] = (byte)(response.Length & 0xFF);
                await stream.WriteAsync(responsePrefix, token);
                await stream.WriteAsync(response, token);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DNS TCP client error");
            }
        }

        private async Task<byte[]> ResolveAsync(byte[] request, CancellationToken token)
        {
            if (request.Length < 12)
            {
                return Array.Empty<byte>();
            }

            string cacheKey = BuildCacheKey(request);
            if (_cache != null && _cache.TryGet(cacheKey, out var cached))
            {
                var response = (byte[])cached.Clone();
                response[0] = request[0];
                response[1] = request[1];
                return response;
            }

            var queryName = DnsMessageParser.GetQueryName(request);
            byte[] responseBytes = Array.Empty<byte>();

            if (DnsPatternMatcher.IsMatch(queryName, _config.DohEndpointsMatchs))
            {
                _logger.LogDebug("Query {QueryName} matches pattern, using upstream DoH", queryName);
                responseBytes = await QueryAnyAsync(_upstreamDohClients, request, token);
            }
            else
            {
                _logger.LogDebug("Query {QueryName} does not match pattern, using default DoH/System DNS", queryName);
                if (_defaultDohClient != null)
                {
                    try
                    {
                        responseBytes = await _defaultDohClient.QueryAsync(request, token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Default DoH query failed for {QueryName}, falling back to System DNS", queryName);
                    }
                }

                if (responseBytes.Length == 0)
                {
                    responseBytes = await QuerySystemDnsAsync(request, token);
                }
            }

            if (responseBytes.Length == 0)
            {
                return Array.Empty<byte>();
            }

            if (responseBytes.Length >= 2)
            {
                responseBytes[0] = request[0];
                responseBytes[1] = request[1];
            }

            if (_cache != null && DnsMessageParser.TryGetMinimumTtl(responseBytes, out var ttl))
            {
                _cache.Set(cacheKey, responseBytes, ttl);
            }

            return responseBytes;
        }

        private async Task<byte[]> QuerySystemDnsAsync(byte[] request, CancellationToken token)
        {
            if (request.Length < 12) return Array.Empty<byte>();

            // Only support simple A (1) and AAAA (28) queries for system fallback via Dns.GetHostAddressesAsync
            int offset = 12;
            if (!DnsMessageParser.TryParseName(request, ref offset, out var name) || offset + 4 > request.Length)
            {
                return Array.Empty<byte>();
            }

            ushort qtype = (ushort)((request[offset] << 8) | request[offset + 1]);
            // ushort qclass = (ushort)((request[offset + 2] << 8) | request[offset + 3]);

            if (qtype != 1 && qtype != 28) // A or AAAA
            {
                _logger.LogDebug("System DNS fallback only supports A/AAAA, ignoring type {Type} for {Name}", qtype, name);
                return Array.Empty<byte>();
            }

            try
            {
                _logger.LogDebug("Falling back to system DNS for {Name} (Type {Type})", name, qtype);
                var addresses = await Dns.GetHostAddressesAsync(name, token);
                var filtered = addresses.Where(a => (qtype == 1 && a.AddressFamily == AddressFamily.InterNetwork) ||
                                                    (qtype == 28 && a.AddressFamily == AddressFamily.InterNetworkV6)).ToList();

                if (filtered.Count == 0)
                {
                    return Array.Empty<byte>();
                }

                // Construct a very basic DNS response
                // Header (12 bytes) + Question (original) + Answer Records
                using var ms = new MemoryStream();
                ms.Write(request, 0, offset + 4); // Original header + question

                // Header update: QR=1, AA=0, TC=0, RD=1, RA=1, Z=0, RCODE=0 (0x8180)
                ms.Position = 2;
                ms.WriteByte(0x81);
                ms.WriteByte(0x80);

                // ANCOUNT
                ms.Position = 6;
                ms.WriteByte((byte)(filtered.Count >> 8));
                ms.WriteByte((byte)(filtered.Count & 0xFF));

                ms.Position = ms.Length;

                foreach (var addr in filtered)
                {
                    // Name (Pointer to offset 12)
                    ms.WriteByte(0xC0);
                    ms.WriteByte(0x0C);

                    // Type
                    ms.WriteByte((byte)(qtype >> 8));
                    ms.WriteByte((byte)(qtype & 0xFF));

                    // Class (IN = 1)
                    ms.WriteByte(0x00);
                    ms.WriteByte(0x01);

                    // TTL (e.g., 60s)
                    ms.WriteByte(0x00);
                    ms.WriteByte(0x00);
                    ms.WriteByte(0x00);
                    ms.WriteByte(0x3C);

                    // Data Length
                    var ipBytes = addr.GetAddressBytes();
                    ms.WriteByte((byte)(ipBytes.Length >> 8));
                    ms.WriteByte((byte)(ipBytes.Length & 0xFF));

                    // Data
                    ms.Write(ipBytes, 0, ipBytes.Length);
                }

                return ms.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "System DNS resolution failed for {Name}", name);
                return Array.Empty<byte>();
            }
        }

        private async Task<byte[]> QueryAnyAsync(IReadOnlyList<DnsOverHttpsClient> clients, byte[] request, CancellationToken token)
        {
            if (clients.Count == 0)
            {
                return Array.Empty<byte>();
            }

            if (clients.Count == 1)
            {
                try
                {
                    return await clients[0].QueryAsync(request, token);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DoH query failed");
                    return Array.Empty<byte>();
                }
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var tasks = clients
                .Select(client => client.QueryAsync(request, cts.Token))
                .ToList();

            while (tasks.Count > 0)
            {
                var finished = await Task.WhenAny(tasks);
                tasks.Remove(finished);
                try
                {
                    var result = await finished;
                    cts.Cancel();
                    return result;
                }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException && cts.IsCancellationRequested)
                    {
                        // Intentional cancellation because another endpoint succeeded
                        _logger.LogDebug("DoH endpoint query canceled because another endpoint succeeded");
                    }
                    else if (ex is OperationCanceledException)
                    {
                        // Likely a timeout (token from QueryAnyAsync expired)
                        _logger.LogDebug("DoH endpoint query timed out");
                    }
                    else
                    {
                        _logger.LogDebug(ex, $"DoH endpoint failed, {ex.Message}");
                    }
                }
            }

            var queryName = DnsMessageParser.GetQueryName(request);
            _logger.LogWarning("All DoH endpoints failed for {QueryName}", queryName);
            return Array.Empty<byte>();
        }

        private static string BuildCacheKey(byte[] request)
        {
            return DnsMessageParser.GetCacheKey(request);
        }

        private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken token)
        {
            int readTotal = 0;
            while (readTotal < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(readTotal, buffer.Length - readTotal), token);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                readTotal += read;
            }
        }

        private static IEnumerable<Uri> BuildEndpoints(DnsConfiguration config)
        {
            if (config.DohEndpoints != null && config.DohEndpoints.Count > 0)
            {
                foreach (var endpoint in config.DohEndpoints)
                {
                    if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
                    {
                        yield return uri;
                    }
                }

                yield break;
            }

            // Fall back to the single DohEndpoint when no DohEndpoints list is configured.
            if (Uri.TryCreate(config.DohEndpoint, UriKind.Absolute, out var single))
            {
                yield return single;
            }
        }

        private static IReadOnlyList<Socks5Client> BuildSocks5Clients(DnsConfiguration config, ILogger logger)
        {
            var upstreams = config.Upstreams?.Where(u => u.Enabled && u.Type.Equals("socks5", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(u.Host))
                .ToList();

            if (upstreams == null || upstreams.Count == 0)
            {
                return Array.Empty<Socks5Client>();
            }

            return upstreams
                .Select(u => new Socks5Client(u.Host!, u.Port, logger, config.GetPreferredAddressFamily()))
                .ToList();
        }
    }
}
