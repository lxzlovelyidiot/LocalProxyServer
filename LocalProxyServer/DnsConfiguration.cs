using System.Net.Sockets;

namespace LocalProxyServer
{
    public class DnsConfiguration
    {
        public bool Enabled { get; set; } = false;
        public int Port { get; set; } = 53;
        /// <summary>
        /// Fallback single DoH endpoint for pattern-matched queries when <see cref="DohEndpoints"/> is not configured.
        /// </summary>
        public string DohEndpoint { get; set; } = "https://dns.google/dns-query";

        /// <summary>
        /// Multiple DoH endpoints used for pattern-matched queries (raced in parallel via QueryAnyAsync).
        /// When set, overrides <see cref="DohEndpoint"/>.
        /// </summary>
        public List<string>? DohEndpoints { get; set; }

        /// <summary>
        /// Default DoH endpoint for queries that do NOT match any pattern in <see cref="DohEndpointsMatchs"/>.
        /// Independent of <see cref="DohEndpoints"/>. Falls back to system DNS when omitted.
        /// </summary>
        public string? DefaultDohEndpoint { get; set; }

        /// <summary>
        /// Glob-style patterns; matching queries are routed to <see cref="DohEndpoints"/> (or <see cref="DohEndpoint"/>).
        /// Non-matching queries use <see cref="DefaultDohEndpoint"/> or system DNS.
        /// </summary>
        public List<string>? DohEndpointsMatchs { get; set; }
        public int TimeoutMs { get; set; } = 5000;
        public DnsCacheConfiguration Cache { get; set; } = new();
        public List<UpstreamConfiguration>? Upstreams { get; set; }
        public string PreferredAddressFamily { get; set; } = "auto";

        public AddressFamily? GetPreferredAddressFamily()
        {
            return PreferredAddressFamily.ToLowerInvariant() switch
            {
                "ipv4" => AddressFamily.InterNetwork,
                "ipv6" => AddressFamily.InterNetworkV6,
                _ => null
            };
        }
    }

    public class DnsCacheConfiguration
    {
        public bool Enabled { get; set; } = true;
        public int MaxEntries { get; set; } = 10000;
        public int MinTtlSeconds { get; set; } = 30;
        public int MaxTtlSeconds { get; set; } = 3600;
    }

}
