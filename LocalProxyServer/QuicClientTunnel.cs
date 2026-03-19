using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;

namespace LocalProxyServer;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
public static class QuicClientTunnel
{
    private static QuicConnection? _sharedConnection;
    private static readonly SemaphoreSlim _connectionLock = new(1, 1);

    public static async Task RunAsync(string serverEndpointStr, string? listenEndpointStr = null)
    {
        IPEndPoint? serverEndpoint = ParseEndpoint(serverEndpointStr, IPAddress.Loopback);
        if (serverEndpoint == null) return;

        if (string.IsNullOrEmpty(listenEndpointStr))
        {
            // Legacy Stdin/Stdout mapping Mode (creates a new connection each time)
            await RunStdinStdoutModeAsync(serverEndpoint);
        }
        else
        {
            // Multiplexing TCP-listener Mode (Connection Caching)
            IPEndPoint? listenEndpoint = ParseEndpoint(listenEndpointStr, IPAddress.Loopback);
            if (listenEndpoint == null) return;

            await RunTcpListenerModeAsync(serverEndpoint, listenEndpoint);
        }
    }

    private static IPEndPoint? ParseEndpoint(string endpointStr, IPAddress defaultIp)
    {
        int lastColon = endpointStr.LastIndexOf(':');
        if (lastColon == -1)
        {
            if (int.TryParse(endpointStr, out int p))
                return new IPEndPoint(defaultIp, p);
            Console.Error.WriteLine($"Invalid endpoint format: {endpointStr}. Use [ip:]port");
            return null;
        }

        string hostPart = endpointStr.Substring(0, lastColon);
        string portPart = endpointStr.Substring(lastColon + 1);

        if (!int.TryParse(portPart, out int port))
        {
            Console.Error.WriteLine($"Invalid port in endpoint: {endpointStr}. Use [ip:]port");
            return null;
        }

        if (string.IsNullOrEmpty(hostPart) || hostPart == "*")
            return new IPEndPoint(defaultIp, port);

        if (hostPart.StartsWith("[") && hostPart.EndsWith("]"))
            hostPart = hostPart.Substring(1, hostPart.Length - 2);

        var ips = Dns.GetHostAddresses(hostPart);
        if (ips.Length == 0)
        {
            Console.Error.WriteLine($"Could not resolve host: {hostPart}");
            return null;
        }
        return new IPEndPoint(ips.First(), port);
    }

    private static async Task<QuicConnection> GetOrCreateConnectionAsync(IPEndPoint endpoint)
    {
        await _connectionLock.WaitAsync();
        try
        {
            if (_sharedConnection != null)
            {
                // Simple health check: if it's already faulted, it will throw on Open stream anyway,
                // but we can preemptively recreate it if it's cleanly closed or disposed. 
                // Unfortunately QUIC API doesn't have an "IsConnected" property. We try to use it and catch later.
                return _sharedConnection;
            }

            Console.WriteLine($"[QUIC] Connecting to {endpoint}...");
            var newConnection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 0,
                MaxInboundUnidirectionalStreams = 0,
                MaxInboundBidirectionalStreams = 10,
                RemoteEndPoint = endpoint,
                IdleTimeout = TimeSpan.FromSeconds(60),
                KeepAliveInterval = TimeSpan.FromSeconds(15), // Shorter keepalive for weak networks
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = new List<SslApplicationProtocol> { new SslApplicationProtocol("tunnel") },
                    RemoteCertificateValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
                }
            });

            Console.WriteLine("[QUIC] Connected successfully. Connection cached.");
            _sharedConnection = newConnection;
            return _sharedConnection;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private static async Task RunTcpListenerModeAsync(IPEndPoint serverEndpoint, IPEndPoint listenEndpoint)
    {
        using var listener = new System.Net.Sockets.TcpListener(listenEndpoint);
        listener.Start();
        Console.WriteLine($"[Daemon] Listening on TCP {listener.LocalEndpoint}. Forwarding securely over QUIC to {serverEndpoint}");
        Console.WriteLine($"[Daemon] Update your ProxyCommand/SOCKS proxy to map to {listener.LocalEndpoint} instead of standard IO.");

        // Pre-warm the connection
        try { await GetOrCreateConnectionAsync(serverEndpoint); } catch { }

        while (true)
        {
            var tcpClient = await listener.AcceptTcpClientAsync();
            _ = Task.Run(async () =>
            {
                try
                {
                    Console.WriteLine($"[TCP] Client connected from {tcpClient.Client.RemoteEndPoint}");
                    await using var networkStream = tcpClient.GetStream();

                    // Attempt to multiplex over the existing connection
                    QuicStream? quicStream = null;
                    int retries = 0;
                    while (retries < 2 && quicStream == null)
                    {
                        var conn = await GetOrCreateConnectionAsync(serverEndpoint);
                        try
                        {
                            quicStream = await conn.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
                        }
                        catch (Exception)
                        {
                            // Stale or dead connection, remove it from cache and try one more time
                            Console.WriteLine("[QUIC] Cached connection dead. Reconnecting...");
                            await _connectionLock.WaitAsync();
                            if (object.ReferenceEquals(_sharedConnection, conn))
                            {
                                await _sharedConnection.DisposeAsync();
                                _sharedConnection = null;
                            }
                            _connectionLock.Release();
                            retries++;
                        }
                    }

                    if (quicStream == null)
                    {
                        Console.Error.WriteLine("[TCP] Failed to establish QUIC reliable stream after retries.");
                        return;
                    }

                    Console.WriteLine("[QUIC] Opened 0-RTT Multiplexed Stream for TCP client.");

                    var copyToQuicTask = networkStream.CopyToAsync(quicStream);
                    var copyFromQuicTask = quicStream.CopyToAsync(networkStream);

                    await Task.WhenAny(copyToQuicTask, copyFromQuicTask);
                    Console.WriteLine($"[TCP] Session closed for {tcpClient.Client.RemoteEndPoint}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[TCP Proxy Error] {ex.Message}");
                }
                finally
                {
                    tcpClient.Dispose();
                }
            });
        }
    }

    private static async Task RunStdinStdoutModeAsync(IPEndPoint endpoint)
    {
        int retryDelay = 1000;
        while (true)
        {
            try
            {
                // In Stdin mode, we just get/create connection and use it once
                var connection = await GetOrCreateConnectionAsync(endpoint);

                Console.Error.WriteLine("[Legacy] Connected. Opening tunnel stream via Standard IO...");
                await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
                retryDelay = 1000; // Reset retry delay on success

                using var stdin = Console.OpenStandardInput();
                using var stdout = Console.OpenStandardOutput();

                var copyToQuicTask = stdin.CopyToAsync(stream);
                var copyFromQuicTask = stream.CopyToAsync(stdout);

                await Task.WhenAny(copyToQuicTask, copyFromQuicTask);
                Console.Error.WriteLine("[Legacy] Tunnel stream closed or connection lost.");
            }
            catch (QuicException ex)
            {
                Console.Error.WriteLine($"[QUIC Error] {ex.Message}");
                // Invalidate shared connection
                await _connectionLock.WaitAsync();
                _sharedConnection = null;
                _connectionLock.Release();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Error] {ex.Message}");
                await _connectionLock.WaitAsync();
                _sharedConnection = null;
                _connectionLock.Release();
            }

            Console.Error.WriteLine($"Retrying in {retryDelay / 1000}s...");
            await Task.Delay(retryDelay);
            retryDelay = Math.Min(retryDelay * 2, 30000); // Exponential backoff up to 30s
        }
    }
}
