using System.Collections.Concurrent;

namespace LocalProxyServer
{
    public class DnsCache
    {
        private sealed class CacheEntry
        {
            public CacheEntry(byte[] response, DateTimeOffset expiresAt)
            {
                Response = response;
                ExpiresAt = expiresAt;
                CreatedAt = DateTimeOffset.UtcNow;
            }

            public byte[] Response { get; }
            public DateTimeOffset ExpiresAt { get; }
            public DateTimeOffset CreatedAt { get; }
        }

        private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
        private readonly int _maxEntries;
        private readonly int _minTtlSeconds;
        private readonly int _maxTtlSeconds;

        public DnsCache(DnsCacheConfiguration config)
        {
            _maxEntries = Math.Max(1, config.MaxEntries);
            _minTtlSeconds = Math.Max(0, config.MinTtlSeconds);
            _maxTtlSeconds = Math.Max(_minTtlSeconds, config.MaxTtlSeconds);
        }

        public bool TryGet(string key, out byte[] response)
        {
            response = Array.Empty<byte>();
            if (!_entries.TryGetValue(key, out var entry))
            {
                return false;
            }

            if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _entries.TryRemove(key, out _);
                return false;
            }

            response = entry.Response;
            return true;
        }

        public void Set(string key, byte[] response, int ttlSeconds)
        {
            if (ttlSeconds <= 0)
            {
                return;
            }

            var ttl = Math.Clamp(ttlSeconds, _minTtlSeconds, _maxTtlSeconds);
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(ttl);
            _entries[key] = new CacheEntry(response, expiresAt);

            if (_entries.Count > _maxEntries)
            {
                Trim();
            }
        }

        private void Trim()
        {
            var now = DateTimeOffset.UtcNow;

            foreach (var pair in _entries)
            {
                if (pair.Value.ExpiresAt <= now)
                {
                    _entries.TryRemove(pair.Key, out _);
                }
            }

            if (_entries.Count <= _maxEntries)
            {
                return;
            }

            var ordered = _entries
                .OrderBy(kvp => kvp.Value.CreatedAt)
                .Take(Math.Max(1, _entries.Count - _maxEntries))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in ordered)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }
}
