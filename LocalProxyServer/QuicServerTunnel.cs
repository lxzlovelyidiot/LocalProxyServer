using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace LocalProxyServer;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
public static class QuicServerTunnel
{
    public static async Task RunAsync(string listenEndpointStr, string forwardEndpointStr)
    {
        IPEndPoint listenEndpoint;
        int listenLastColon = listenEndpointStr.LastIndexOf(':');
        if (listenLastColon == -1)
        {
            if (int.TryParse(listenEndpointStr, out int port))
            {
                listenEndpoint = new IPEndPoint(IPAddress.Any, port);
            }
            else
            {
                Console.Error.WriteLine("Invalid listen endpoint format. Use [ip:]port");
                return;
            }
        }
        else
        {
            string hostPart = listenEndpointStr.Substring(0, listenLastColon);
            string portPart = listenEndpointStr.Substring(listenLastColon + 1);

            if (int.TryParse(portPart, out int port))
            {
                if (string.IsNullOrEmpty(hostPart) || hostPart == "*")
                {
                    listenEndpoint = new IPEndPoint(IPAddress.Any, port);
                }
                else
                {
                    if (hostPart.StartsWith("[") && hostPart.EndsWith("]"))
                        hostPart = hostPart.Substring(1, hostPart.Length - 2);

                    var ips = await Dns.GetHostAddressesAsync(hostPart);
                    listenEndpoint = new IPEndPoint(ips.First(), port);
                }
            }
            else
            {
                Console.Error.WriteLine("Invalid listen endpoint format. Use [ip:]port");
                return;
            }
        }

        IPEndPoint forwardEndpoint;
        int forwardLastColon = forwardEndpointStr.LastIndexOf(':');
        if (forwardLastColon == -1)
        {
            if (int.TryParse(forwardEndpointStr, out int port))
            {
                forwardEndpoint = new IPEndPoint(IPAddress.Loopback, port);
            }
            else
            {
                Console.Error.WriteLine("Invalid forward endpoint format. Use [ip:]port");
                return;
            }
        }
        else
        {
            string hostPart = forwardEndpointStr.Substring(0, forwardLastColon);
            string portPart = forwardEndpointStr.Substring(forwardLastColon + 1);

            if (int.TryParse(portPart, out int port))
            {
                if (string.IsNullOrEmpty(hostPart) || hostPart == "*")
                {
                    forwardEndpoint = new IPEndPoint(IPAddress.Loopback, port);
                }
                else
                {
                    if (hostPart.StartsWith("[") && hostPart.EndsWith("]"))
                        hostPart = hostPart.Substring(1, hostPart.Length - 2);

                    var ips = await Dns.GetHostAddressesAsync(hostPart);
                    forwardEndpoint = new IPEndPoint(ips.First(), port);
                }
            }
            else
            {
                Console.Error.WriteLine("Invalid forward endpoint format. Use [ip:]port");
                return;
            }
        }

        var cert = CertificateManager.GetOrCreateServerCertificate(null);

        await using var listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = listenEndpoint,
            ApplicationProtocols = new List<SslApplicationProtocol> { new SslApplicationProtocol("tunnel") },
            ConnectionOptionsCallback = (connectionInfo, sslClientHelloInfo, cancellationToken) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 0,
                MaxInboundBidirectionalStreams = 100,
                MaxInboundUnidirectionalStreams = 0,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ApplicationProtocols = new List<SslApplicationProtocol> { new SslApplicationProtocol("tunnel") },
                    ServerCertificate = cert
                }
            })
        });

        Console.WriteLine($"Server listening on {listener.LocalEndPoint}, forwarding to {forwardEndpoint}");

        try
        {
            while (true)
            {
                var connection = await listener.AcceptConnectionAsync();
                _ = HandleConnectionAsync(connection, forwardEndpoint);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Listener accept loop error: {ex}");
            throw;
        }
    }

    private static async Task HandleConnectionAsync(QuicConnection connection, IPEndPoint forwardEndpoint)
    {
        try
        {
            await using (connection)
            {
                while (true)
                {
                    var stream = await connection.AcceptInboundStreamAsync();
                    _ = HandleStreamAsync(stream, forwardEndpoint);
                }
            }
        }
        catch (QuicException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Connection error: {ex.Message}");
        }
    }

    private static async Task HandleStreamAsync(QuicStream stream, IPEndPoint forwardEndpoint)
    {
        try
        {
            await using (stream)
            {
                using var tcpClient = new TcpClient();
                await tcpClient.ConnectAsync(forwardEndpoint);
                await using var tcpStream = tcpClient.GetStream();

                var copyToQuicTask = stream.CopyToAsync(tcpStream);
                var copyFromQuicTask = tcpStream.CopyToAsync(stream);

                await Task.WhenAny(copyToQuicTask, copyFromQuicTask);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Stream error: {ex.Message}");
        }
    }
}
