using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace LocalProxyServer
{
    public class Socks5Client
    {
        private readonly string _socksHost;
        private readonly int _socksPort;
        private readonly ILogger? _logger;

        public Socks5Client(string socksHost, int socksPort, ILogger? logger = null)
        {
            _socksHost = socksHost;
            _socksPort = socksPort;
            _logger = logger;
        }

        public async Task<TcpClient> ConnectAsync(string targetHost, int targetPort)
        {
            _logger?.LogDebug("Connecting to SOCKS5 server {SocksHost}:{SocksPort}", _socksHost, _socksPort);
            
            var client = new TcpClient();
            await client.ConnectAsync(_socksHost, _socksPort);
            var stream = client.GetStream();

            _logger?.LogDebug("SOCKS5 handshake started for target {Target}:{Port}", targetHost, targetPort);

            // 1. Handshake
            // [Version, NumMethods, Method1, ...]
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
            var response = new byte[2];
            await ReadExactlyAsync(stream, response);

            if (response[0] != 0x05 || response[1] != 0x00)
            {
                _logger?.LogError("SOCKS5 handshake failed. Version: {Version}, Method: {Method}", 
                    response[0], response[1]);
                client.Close();
                throw new Exception("SOCKS5 Handshake failed or authentication required.");
            }

            _logger?.LogDebug("SOCKS5 handshake successful, sending connect request");

            // 2. Request
            // [Version, Command, Reserved, AddressType, Address, Port]
            var request = new List<byte> { 0x05, 0x01, 0x00 };

            if (IPAddress.TryParse(targetHost, out var ip))
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    request.Add(0x01); // IPv4
                    request.AddRange(ip.GetAddressBytes());
                    _logger?.LogDebug("Using IPv4 address for SOCKS5 connection");
                }
                else
                {
                    request.Add(0x04); // IPv6
                    request.AddRange(ip.GetAddressBytes());
                    _logger?.LogDebug("Using IPv6 address for SOCKS5 connection");
                }
            }
            else
            {
                request.Add(0x03); // Domain name
                var hostBytes = Encoding.ASCII.GetBytes(targetHost);
                request.Add((byte)hostBytes.Length);
                request.AddRange(hostBytes);
                _logger?.LogDebug("Using domain name for SOCKS5 connection: {Domain}", targetHost);
            }

            request.Add((byte)(targetPort >> 8));
            request.Add((byte)(targetPort & 0xFF));

            await stream.WriteAsync(request.ToArray());

            // 3. Response
            var resHeader = new byte[4];
            await ReadExactlyAsync(stream, resHeader);

            if (resHeader[1] != 0x00)
            {
                var errorMsg = GetSocks5ErrorMessage(resHeader[1]);
                _logger?.LogError("SOCKS5 connect failed with error code: {Code} - {Message}", 
                    resHeader[1], errorMsg);
                client.Close();
                throw new Exception($"SOCKS5 Connect failed with error code: {resHeader[1]} - {errorMsg}");
            }

            // Skip remaining address part of the response
            byte addrType = resHeader[3];
            int skipLen = addrType switch
            {
                0x01 => 4 + 2, // IPv4 + Port
                0x03 => (await ReadByteAsync(stream)) + 2, // Domain + Port
                0x04 => 16 + 2, // IPv6 + Port
                _ => throw new Exception("Unknown address type in SOCKS5 response")
            };

            var skipBuf = new byte[skipLen];
            await ReadExactlyAsync(stream, skipBuf);

            _logger?.LogInformation("SOCKS5 connection established to {Target}:{Port} via {SocksHost}:{SocksPort}", 
                targetHost, targetPort, _socksHost, _socksPort);

            return client;
        }

        private static string GetSocks5ErrorMessage(byte errorCode)
        {
            return errorCode switch
            {
                0x01 => "General SOCKS server failure",
                0x02 => "Connection not allowed by ruleset",
                0x03 => "Network unreachable",
                0x04 => "Host unreachable",
                0x05 => "Connection refused",
                0x06 => "TTL expired",
                0x07 => "Command not supported",
                0x08 => "Address type not supported",
                _ => "Unknown error"
            };
        }

        private async Task ReadExactlyAsync(Stream stream, byte[] buffer)
        {
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer, totalRead, buffer.Length - totalRead);
                if (read == 0) throw new EndOfStreamException();
                totalRead += read;
            }
        }

        private async Task<byte> ReadByteAsync(Stream stream)
        {
            var buf = new byte[1];
            await ReadExactlyAsync(stream, buf);
            return buf[0];
        }
    }
}
