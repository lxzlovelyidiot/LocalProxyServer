using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace LocalProxyServer
{
    /// <summary>
    /// Client for connecting through HTTP/HTTPS proxy servers.
    /// </summary>
    public class HttpProxyClient
    {
        private readonly string _proxyHost;
        private readonly int _proxyPort;
        private readonly ILogger? _logger;

        public HttpProxyClient(string proxyHost, int proxyPort, ILogger? logger = null)
        {
            _proxyHost = proxyHost;
            _proxyPort = proxyPort;
            _logger = logger;
        }

        /// <summary>
        /// Connect to target host through HTTP proxy using CONNECT method.
        /// </summary>
        public async Task<TcpClient> ConnectAsync(string targetHost, int targetPort)
        {
            _logger?.LogDebug("Connecting to HTTP proxy {ProxyHost}:{ProxyPort}", _proxyHost, _proxyPort);

            var client = new TcpClient();
            await client.ConnectAsync(_proxyHost, _proxyPort);
            var stream = client.GetStream();

            try
            {
                // Send CONNECT request
                var connectRequest = $"CONNECT {targetHost}:{targetPort} HTTP/1.1\r\n" +
                                   $"Host: {targetHost}:{targetPort}\r\n" +
                                   "Proxy-Connection: Keep-Alive\r\n" +
                                   "\r\n";

                var requestBytes = Encoding.ASCII.GetBytes(connectRequest);
                await stream.WriteAsync(requestBytes);

                _logger?.LogDebug("Sent CONNECT request to HTTP proxy for {Target}:{Port}", targetHost, targetPort);

                // Read response
                var responseBuilder = new StringBuilder();
                var buffer = new byte[1];
                var headerComplete = false;

                while (!headerComplete)
                {
                    var bytesRead = await stream.ReadAsync(buffer, 0, 1);
                    if (bytesRead == 0)
                    {
                        throw new Exception("HTTP proxy closed connection while reading response");
                    }

                    responseBuilder.Append((char)buffer[0]);
                    var response = responseBuilder.ToString();

                    if (response.EndsWith("\r\n\r\n"))
                    {
                        headerComplete = true;
                    }
                }

                var responseText = responseBuilder.ToString();
                _logger?.LogDebug("Received HTTP proxy response: {Response}", 
                    responseText.Split('\r')[0]);

                // Parse status line
                var lines = responseText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0)
                {
                    throw new Exception("Invalid HTTP proxy response");
                }

                var statusLine = lines[0];
                var parts = statusLine.Split(' ', 3);
                
                if (parts.Length < 2)
                {
                    throw new Exception($"Invalid HTTP proxy status line: {statusLine}");
                }

                if (!int.TryParse(parts[1], out var statusCode))
                {
                    throw new Exception($"Invalid HTTP proxy status code: {parts[1]}");
                }

                if (statusCode != 200)
                {
                    var statusMessage = parts.Length > 2 ? parts[2] : "Unknown error";
                    _logger?.LogError("HTTP proxy connection failed with status {StatusCode}: {Message}", 
                        statusCode, statusMessage);
                    throw new Exception($"HTTP proxy connection failed: {statusCode} {statusMessage}");
                }

                _logger?.LogInformation("HTTP proxy connection established to {Target}:{Port} via {ProxyHost}:{ProxyPort}", 
                    targetHost, targetPort, _proxyHost, _proxyPort);

                return client;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to connect through HTTP proxy to {Target}:{Port}", 
                    targetHost, targetPort);
                client.Dispose();
                throw;
            }
        }
    }
}
