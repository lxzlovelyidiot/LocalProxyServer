using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace LocalProxyServer
{
    public class WebSocketLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentDictionary<string, WebSocketLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);

        public ILogger CreateLogger(string categoryName)
        {
            return _loggers.GetOrAdd(categoryName, name => new WebSocketLogger(name));
        }

        public void Dispose()
        {
            _loggers.Clear();
        }
    }

    public class WebSocketLogger : ILogger
    {
        private readonly string _categoryName;

        public WebSocketLogger(string categoryName)
        {
            _categoryName = categoryName;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = logLevel.ToString(),
                Category = _categoryName,
                Message = message
            };

            WebSocketLogBroadcaster.BroadcastLog(entry);
        }
    }

    public static class WebSocketLogBroadcaster
    {
        private static readonly ConcurrentDictionary<Guid, (WebSocket Socket, LogLevel MinLevel)> _clients = new();
        private static readonly Channel<LogEntry> _logChannel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });

        static WebSocketLogBroadcaster()
        {
            _ = Task.Run(ProcessLogsAsync);
        }

        private static async Task ProcessLogsAsync()
        {
            await foreach (var entry in _logChannel.Reader.ReadAllAsync())
            {
                if (_clients.IsEmpty) continue;
                if (!Enum.TryParse<LogLevel>(entry.Level, out var entryLevel)) continue;

                var messageBytes = JsonSerializer.SerializeToUtf8Bytes(entry, WebUIJsonContext.Default.LogEntry);

                var deadClients = new List<Guid>();
                foreach (var kvp in _clients)
                {
                    var id = kvp.Key;
                    var client = kvp.Value;

                    if (client.Socket.State != WebSocketState.Open || entryLevel < client.MinLevel)
                    {
                        if (client.Socket.State is WebSocketState.Closed or WebSocketState.Aborted)
                        {
                            deadClients.Add(id);
                        }
                        continue;
                    }

                    try
                    {
                        var sendTask = client.Socket.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                        // Optional: Timeout or fire-forget
                    }
                    catch
                    {
                        deadClients.Add(id);
                    }
                }

                foreach (var dead in deadClients)
                {
                    _clients.TryRemove(dead, out _);
                }
            }
        }

        public static async Task HandleWebSocketAsync(WebSocket webSocket, LogLevel minLevel)
        {
            var id = Guid.NewGuid();
            _clients.TryAdd(id, (webSocket, minLevel));

            var buffer = new byte[1024 * 4];
            try
            {
                // Keep the connection open until client disconnects
                while (webSocket.State == WebSocketState.Open)
                {
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    }
                }
            }
            catch
            {
                // Ignore exceptions on disconnect
            }
            finally
            {
                _clients.TryRemove(id, out _);
            }
        }

        public static void BroadcastLog(LogEntry entry)
        {
            if (_clients.IsEmpty) return;
            _logChannel.Writer.TryWrite(entry);
        }
    }
}
