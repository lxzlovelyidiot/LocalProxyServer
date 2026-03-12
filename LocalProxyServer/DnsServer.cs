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
        private readonly IReadOnlyList<DnsOverHttpsClient> _dohClients;
        private readonly CancellationTokenSource _cts = new();
        private Socket? _udpSocket;
        private TcpListener? _tcpListener;

        public DnsServer(DnsConfiguration config, ILogger logger)
        {
            _config = config;
            _logger = logger;
            _cache = config.Cache.Enabled ? new DnsCache(config.Cache) : null;

            var socksClients = BuildSocks5Clients(config, logger);
            var endpoints = BuildEndpoints(config).ToList();
            _dohClients = endpoints
                .Select(endpoint => new DnsOverHttpsClient(endpoint, socksClients, config.TimeoutMs, logger, config.GetPreferredAddressFamily()))
                .ToList();
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

            var responseBytes = await QueryAnyAsync(request, token);
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

        private async Task<byte[]> QueryAnyAsync(byte[] request, CancellationToken token)
        {
            if (_dohClients.Count == 0)
            {
                _logger.LogWarning("No DoH endpoints configured");
                return Array.Empty<byte>();
            }

            if (_dohClients.Count == 1)
            {
                try
                {
                    return await _dohClients[0].QueryAsync(request, token);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DoH query failed");
                    return Array.Empty<byte>();
                }
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var tasks = _dohClients
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
                    _logger.LogDebug(ex, "DoH endpoint failed");
                }
            }

            _logger.LogWarning("All DoH endpoints failed");
            return Array.Empty<byte>();
        }

        private static string BuildCacheKey(byte[] request)
        {
            var keyBytes = (byte[])request.Clone();
            if (keyBytes.Length >= 2)
            {
                keyBytes[0] = 0;
                keyBytes[1] = 0;
            }

            return Convert.ToBase64String(keyBytes);
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
