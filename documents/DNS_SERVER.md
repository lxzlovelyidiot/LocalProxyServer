# DNS Server (DoH + SOCKS5 Upstream)

This document describes the built-in DNS server mode, which accepts IPv4/IPv6 DNS queries and forwards them to DoH (DNS over HTTPS) via SOCKS5 upstreams with caching.

## How To Start

DNS mode is **mutually exclusive** with proxy mode. Use the `dns server` subcommand to start DNS only:

```bash
LocalProxyServer.exe dns server

# or
dotnet run -- dns server
```

Proxy mode (default) does not start DNS:

```bash
LocalProxyServer.exe
```

## Configuration

DNS configuration lives under the `Dns` section in `appsettings.json` (and environment-specific variants).

```json
{
  "Dns": {
    "Enabled": false,
    "Port": 53,
    "DohEndpoints": [
      "https://dns.google/dns-query",
      "https://cloudflare-dns.com/dns-query"
    ],
    "TimeoutMs": 5000,
    "PreferredAddressFamily": "auto",
    "Cache": {
      "Enabled": true,
      "MaxEntries": 10000,
      "MinTtlSeconds": 30,
      "MaxTtlSeconds": 3600
    },
    "Upstreams": [
      {
        "Enabled": true,
        "Type": "socks5",
        "Host": "localhost",
        "Port": 1081,
        "Process": {
          "AutoStart": true,
          "FileName": "ssh.exe",
          "Arguments": "root@proxy.host -D 1081 -i private.key",
          "WorkingDirectory": "%OneDrive%\\TOOLS\\SSHKEY",
          "StartupDelayMs": 1000,
          "RedirectOutput": false,
          "AutoRestart": true,
          "MaxRestartAttempts": 10,
          "RestartDelayMs": 3000
        }
      }
    ]
  }
}
```

Notes:
- `Enabled` is ignored in DNS-only mode. DNS starts only when `dns server` is provided.
- `DohEndpoints` are queried **in parallel**; the first successful response wins.
- `Upstreams` uses the same schema and process lifecycle management as `Proxy:Upstreams`.
- `PreferredAddressFamily`: `auto` (default), `ipv4`, or `ipv6`.

## Caching Semantics

The cache TTL is derived from the **minimum TTL** among all records in the DoH response.  
After parsing the response:

1. Take the minimum TTL across all records (Answer/Authority/Additional).
2. Clamp it to `[MinTtlSeconds, MaxTtlSeconds]`.
3. Use the result as the cache TTL for that response.

If parsing fails or TTL is `0`, the response is not cached.

## Upstream Lifecycle

DNS `Upstreams` are handled by the same process manager used by the proxy:
- Auto-start, auto-restart, and health checks behave identically.
- Environment variable expansion is supported in `Process` fields.

Refer to the general upstream documentation for field details.

## Quick Test

```bash
nslookup example.com 127.0.0.1
nslookup example.com ::1
```

On Windows, binding to port `53` may require administrator privileges.
