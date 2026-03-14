using System.Net.Sockets;

namespace LocalProxyServer
{
    public class DnsConfiguration
    {
        public bool Enabled { get; set; } = false;
        public int Port { get; set; } = 53;
        public string DohEndpoint { get; set; } = "https://dns.google/dns-query";
        public List<string>? DohEndpoints { get; set; }
        public string? DefaultDohEndpoint { get; set; }
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
