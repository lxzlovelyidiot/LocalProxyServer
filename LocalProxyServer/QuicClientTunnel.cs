using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;

namespace LocalProxyServer;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
public static class QuicClientTunnel
{
    public static async Task RunAsync(string serverEndpointStr)
    {
        IPEndPoint endpoint;
        int lastColon = serverEndpointStr.LastIndexOf(':');
        if (lastColon == -1)
        {
            if (int.TryParse(serverEndpointStr, out int port))
            {
                endpoint = new IPEndPoint(IPAddress.Loopback, port);
            }
            else
            {
                Console.Error.WriteLine("Invalid server endpoint format. Use [ip:]port");
                return;
            }
        }
        else
        {
            string hostPart = serverEndpointStr.Substring(0, lastColon);
            string portPart = serverEndpointStr.Substring(lastColon + 1);

            if (int.TryParse(portPart, out int port))
            {
                if (string.IsNullOrEmpty(hostPart) || hostPart == "*")
                {
                    endpoint = new IPEndPoint(IPAddress.Loopback, port);
                }
                else
                {
                    if (hostPart.StartsWith("[") && hostPart.EndsWith("]"))
                        hostPart = hostPart.Substring(1, hostPart.Length - 2);

                    var ips = await Dns.GetHostAddressesAsync(hostPart);
                    endpoint = new IPEndPoint(ips.First(), port);
                }
            }
            else
            {
                Console.Error.WriteLine("Invalid server endpoint format. Use [ip:]port");
                return;
            }
        }

        await using var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
        {
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            MaxInboundUnidirectionalStreams = 0,
            MaxInboundBidirectionalStreams = 10,
            RemoteEndPoint = endpoint,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = new List<SslApplicationProtocol> { new SslApplicationProtocol("tunnel") },
                RemoteCertificateValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            }
        });

        // Open bidirectional stream
        await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);

        // Standard input/output streams
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();

        // Copy input to QUIC stream, and QUIC stream to output concurrently
        var copyToQuicTask = stdin.CopyToAsync(stream);
        var copyFromQuicTask = stream.CopyToAsync(stdout);

        await Task.WhenAny(copyToQuicTask, copyFromQuicTask);
    }
}
