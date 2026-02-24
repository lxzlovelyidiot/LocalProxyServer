# LocalProxyServer - Usage Guide

## Quick Start

### Development Environment (HTTP Proxy - Recommended for Testing)

Start the HTTP proxy server:

```bash
dotnet run --environment Development
```

Configuration file: `appsettings.Development.json`
- Port: 8080
- Protocol: HTTP only (no encryption)
- Suitable for: Local testing, development debugging

**Test Commands:**
```bash
curl -x http://localhost:8080 -v https://www.google.com
curl -x http://localhost:8080 https://api.github.com

# IPv6 loopback
curl -x http://[::1]:8080 -v https://www.google.com
```

### Production Environment (HTTPS Proxy - Secure)

Start the HTTPS proxy server:

```bash
dotnet run --environment Production
```

Configuration file: `appsettings.Production.json`
- Port: 8443
- Protocol: HTTPS enabled (auto-detects HTTP/HTTPS on the same port)
- Suitable for: Production deployment, scenarios requiring encryption

**Test Commands:**
```bash
# Need to add --proxy-insecure to skip certificate verification (local self-signed certificate)
curl -x https://localhost:8443 --proxy-insecure -v https://www.google.com

# HTTP requests are also accepted on the same port
curl -x http://localhost:8443 -v https://www.google.com

# Or after trusting CA certificate
curl -x https://localhost:8443 -v https://www.google.com

# IPv6 loopback
curl -x https://[::1]:8443 --proxy-insecure -v https://www.google.com
```

## Configuration Files

### appsettings.json (Default Configuration)
Main configuration file, currently set to HTTPS mode.

IPv6 is supported automatically. If the client connects via IPv6, the proxy prefers IPv6 when connecting to upstream hosts or proxies.

### appsettings.Development.json (Development Environment)
```json
{
  "Proxy": {
    "Port": 8080,
    "UseHttps": false  // HTTP mode, simple and easy to use
  }
}
```

### appsettings.Production.json (Production Environment)
```json
{
  "Proxy": {
    "Port": 8443,
    "UseHttps": true   // HTTPS enabled, auto-detects HTTP/HTTPS
  }
}
```

## Environment Switching

```bash
# Development environment (HTTP)
dotnet run --environment Development

# Production environment (HTTPS)
dotnet run --environment Production

# Use default configuration
dotnet run
```

## Browser Configuration

### Chrome/Edge
1. Settings → System → Open proxy settings
2. Manual proxy configuration:
   - HTTP: `localhost:8080` (development)
   - HTTPS: `localhost:8443` (production, also accepts HTTP)
   - SOCKS5: `localhost:1080` (upstream)
   - IPv6: `[::1]` with the same ports

### Firefox
1. Settings → Network Settings → Manual proxy configuration
2. HTTP proxy: `localhost:8080`
3. HTTPS proxy: `localhost:8443` (also accepts HTTP)
4. IPv6: `[::1]` with the same ports

### SwitchyOmega (Recommended)
```json
{
  "name": "LocalProxy-HTTP",
  "profileType": "FixedProfile",
  "color": "#99ccee",
  "fallbackProxy": {
    "scheme": "http",
    "host": "localhost",
    "port": 8080
  }
}
```

```json
{
  "name": "LocalProxy-HTTPS",
  "profileType": "FixedProfile",
  "color": "#99ccee",
  "fallbackProxy": {
    "scheme": "https",
    "host": "localhost",
    "port": 8443
  }
}
```

## Common Commands

### Test Connection

```bash
# HTTP proxy
curl -x http://localhost:8080 https://www.google.com
curl -x http://localhost:8080 https://api.github.com
curl -x http://localhost:8080 http://ifconfig.me

# IPv6 HTTP proxy
curl -x http://[::1]:8080 https://www.google.com

# HTTPS proxy (need to skip certificate verification)
curl -x https://localhost:8443 --proxy-insecure https://www.google.com

# HTTP over HTTPS-enabled port
curl -x http://localhost:8443 https://www.google.com

# IPv6 HTTPS proxy
curl -x https://[::1]:8443 --proxy-insecure https://www.google.com

# View proxy information
curl -x http://localhost:8080 -v https://www.google.com 2>&1 | head -20
```

### PowerShell

```powershell
# Test HTTP proxy
Invoke-WebRequest -Uri "https://www.google.com" -Proxy "http://localhost:8080"

# Set session proxy
$proxy = "http://localhost:8080"
Invoke-WebRequest -Uri "https://api.github.com" -Proxy $proxy
```

### Git Configuration

```bash
# HTTP proxy
git config --global http.proxy http://localhost:8080
git config --global https.proxy http://localhost:8080

# Remove proxy
git config --global --unset http.proxy
git config --global --unset https.proxy
```

### NPM/Yarn

```bash
# HTTP proxy
npm config set proxy http://localhost:8080
npm config set https-proxy http://localhost:8080

# Remove proxy
npm config delete proxy
npm config delete https-proxy
```

## Upstream Proxy Configuration

### Overview

Outbound traffic can be routed through one or more upstream proxies (SOCKS5 or HTTP). When no upstream is configured, the proxy connects directly to target hosts.

### Single Upstream

```json
{
  "Proxy": {
    "Upstreams": [
      {
        "Enabled": true,
        "Type": "socks5",
        "Host": "localhost",
        "Port": 1080
      }
    ]
  }
}
```

Supported upstream types:

| `Type`   | Description                             |
|----------|-----------------------------------------|
| `socks5` | SOCKS5 proxy (e.g., SSH dynamic tunnel) |
| `http`   | HTTP CONNECT proxy                      |

### Multiple Upstreams & Load Balancing

When multiple entries are present, the proxy selects among them using the `LoadBalancingStrategy` setting.

```json
{
  "Proxy": {
    "LoadBalancingStrategy": "failover",
    "Upstreams": [
      {
        "Enabled": true,
        "Type": "socks5",
        "Host": "localhost",
        "Port": 1080
      },
      {
        "Enabled": true,
        "Type": "socks5",
        "Host": "localhost",
        "Port": 1081
      },
      {
        "Enabled": false,
        "Type": "http",
        "Host": "proxy.example.com",
        "Port": 3128
      }
    ]
  }
}
```

`Enabled: false` entries are skipped entirely and can be kept in the configuration for future use without removing them.

#### Load Balancing Strategies

| Strategy     | Behavior                                                                                     |
|--------------|----------------------------------------------------------------------------------------------|
| `failover`   | *(default)* Try upstreams in order; automatically fall back to the next one on failure.      |
| `roundRobin` | Distribute connections across upstreams in rotation; falls over to the next one on failure.  |

### Upstream Process Auto-Start

Each upstream entry can include a `Process` block to automatically start a local process (e.g., an SSH tunnel) when the server starts.

```json
{
  "Enabled": true,
  "Type": "socks5",
  "Host": "localhost",
  "Port": 1080,
  "Process": {
    "AutoStart": true,
    "FileName": "ssh.exe",
    "Arguments": "root@proxy.host -D 1080 -i private.key",
    "WorkingDirectory": "%OneDrive%\\TOOLS\\SSHKEY",
    "StartupDelayMs": 1000,
    "RedirectOutput": false,
    "AutoRestart": true,
    "MaxRestartAttempts": 10,
    "RestartDelayMs": 3000
  }
}
```

All path and argument fields support environment variable expansion (e.g., `%USERPROFILE%`, `%OneDrive%`, `%ProgramFiles%`).

#### Process Configuration Reference

| Field                | Type   | Default | Description                                                              |
|----------------------|--------|---------|--------------------------------------------------------------------------|
| `AutoStart`          | bool   | `false` | Start the process automatically on server launch.                        |
| `FileName`           | string | —       | Path to the executable. Supports environment variables.                  |
| `Arguments`          | string | —       | Command-line arguments. Supports environment variables.                  |
| `WorkingDirectory`   | string | —       | Working directory for the process. Supports environment variables.       |
| `StartupDelayMs`     | int    | `1000`  | Milliseconds to wait after process start before using the upstream.      |
| `RedirectOutput`     | bool   | `true`  | Capture and log the process's stdout/stderr.                             |
| `AutoRestart`        | bool   | `true`  | Automatically restart the process if it exits unexpectedly.              |
| `MaxRestartAttempts` | int    | `5`     | Maximum restart attempts. `0` = unlimited (suitable for 24/7 operation). |
| `RestartDelayMs`     | int    | `3000`  | Milliseconds to wait before each restart attempt.                        |

On Windows, all managed upstream processes are added to a [Job Object](JOB_OBJECTS_SUMMARY.md) and are terminated automatically when the proxy server exits.

To complement process monitoring with active port probing, see [Upstream Health Check](#upstream-health-check) below.

### Upstream Health Check

When a `Process` block is present and `AutoStart` is `true`, you can configure an active health check that periodically probes the upstream TCP port and automatically restarts the process if it becomes unresponsive.

> **Requires `Process.AutoStart: true`.**  
> When `AutoStart` is `false`, the `HealthCheck` block is loaded but never activated — no probing occurs and no restart is attempted, because the process lifecycle is managed externally.

```json
{
  "Enabled": true,
  "Type": "socks5",
  "Host": "localhost",
  "Port": 1080,
  "Process": {
    "AutoStart": true,
    "FileName": "ssh.exe",
    "Arguments": "root@proxy.host -D 1080 -i private.key",
    "AutoRestart": true,
    "MaxRestartAttempts": 1000,
    "RestartDelayMs": 3000
  },
  "HealthCheck": {
    "Enabled": true,
    "IntervalMs": 30000,
    "TimeoutMs": 5000,
    "FailureThreshold": 3
  }
}
```

#### How It Works

1. After the process starts, waits one full `IntervalMs` before the first probe (allowing the process to initialize).
2. Opens a TCP connection to `Host:Port` within `TimeoutMs`. Success = healthy; timeout or refused = failure.
3. Counts **consecutive** failures only. A successful probe resets the counter to zero.
4. When `consecutiveFailures >= FailureThreshold`, restarts the process immediately and resets the counter.
5. Runs independently alongside the process-exit monitor (`AutoRestart`). Both can trigger a restart, whichever detects the problem first.

> Health check restarts are **not** counted against `MaxRestartAttempts`. That counter only tracks crash-driven restarts from the process-exit monitor.

#### Health Check Configuration Reference

| Field              | Type | Default  | Description                                                                     |
|--------------------|------|----------|---------------------------------------------------------------------------------|
| `Enabled`          | bool | `true`   | Enable or disable health checking for this upstream.                            |
| `IntervalMs`       | int  | `30000`  | Milliseconds between TCP probes (also the delay before the first probe).        |
| `TimeoutMs`        | int  | `5000`   | Milliseconds before a probe attempt is considered timed out.                    |
| `FailureThreshold` | int  | `3`      | Number of consecutive probe failures before the process is restarted.           |

#### Log Messages

```
# Health check started
[Info] Active health check enabled for localhost:1080 (interval 30000ms, threshold 3)

# Probe failed (not yet at threshold)
[Warn] Upstream health check failed for localhost:1080 (1/3)
[Warn] Upstream health check failed for localhost:1080 (2/3)

# Threshold reached → restart
[Error] Upstream localhost:1080 failed 3 consecutive health checks. Restarting process
[Info]  Upstream process restarted due to health check failure

# Recovered
[Info] Upstream localhost:1080 is healthy again after 2 failure(s)
```

### Troubleshooting: Upstream Process Failed to Start

Check the logs for the following messages:

```
[Error] Failed to start upstream process
[Error] Cannot start upstream process: FileName is not configured
```

Steps to resolve:
1. Verify the `FileName` path is correct.
2. Check that environment variables expand to the expected values.
3. Run the command manually in a terminal to confirm it works.
4. Set `RedirectOutput: true` to capture the process's own error output.

See also: [Auto-restart Feature](AUTO_RESTART.md), [Windows Job Objects](JOB_OBJECTS.md)

## Certificate Management (HTTPS Mode)

### Automatic Installation
CA certificate will be automatically generated and installed on first run:
- Certificate name: `LocalProxyServer-CA`
- Location: `CurrentUser\My` and `LocalMachine\Root`

### Manual Certificate Trust

**Windows:**
```powershell
# View certificate
certmgr.msc

# Location: Trusted Root Certification Authorities -> Certificates
# Find: LocalProxyServer-CA
```

**Export CA Certificate:**
1. Open `certmgr.msc`
2. Find `LocalProxyServer-CA`
3. Right-click → All Tasks → Export
4. Select Base-64 encoded X.509 (.CER)

### Remove Certificate

```powershell
# PowerShell (Administrator)
Get-ChildItem Cert:\CurrentUser\My | Where-Object {$_.Subject -like "*LocalProxyServer*"} | Remove-Item
Get-ChildItem Cert:\LocalMachine\Root | Where-Object {$_.Subject -like "*LocalProxyServer*"} | Remove-Item
```

## Troubleshooting

### Issue 1: "Proxy CONNECT aborted"

**Cause:** HTTPS is disabled (`UseHttps: false`) but the client connects using an HTTPS proxy URL

**Solution:**
```bash
# Option 1: Use HTTP proxy URL when HTTPS is disabled
dotnet run --environment Development
curl -x http://localhost:8080 https://www.google.com

# Option 2: Enable HTTPS support
dotnet run --environment Production
curl -x https://localhost:8443 --proxy-insecure https://www.google.com
```

### Issue 2: "certificate verify failed"

**Cause:** Client doesn't trust self-signed CA certificate

**Solution:**
```bash
# Temporary: Skip certificate verification
curl -x https://localhost:8443 --proxy-insecure https://www.google.com

# Permanent: Trust CA certificate
# 1. Export CA certificate (see "Certificate Management" section above)
# 2. Import to system trust store
```

### Issue 3: "Connection refused"

**Cause:** Proxy server not started or wrong port

**Check:**
```bash
# Check if server is running
netstat -an | findstr "8080"
netstat -an | findstr "8443"

# Check configuration file port
type appsettings.json
```

### Issue 4: Upstream Proxy Unreachable

**Cause:** All configured upstream proxies failed to accept the connection.

**Check:**
- Verify the upstream service is running (e.g., SSH tunnel, SOCKS5 proxy).
- Confirm `Host` and `Port` in the `Upstreams` configuration are correct.
- If using `Process.AutoStart`, see [Upstream Process Auto-Start](#upstream-process-auto-start) for troubleshooting steps.
- Increase log verbosity (`"LocalProxyServer": "Debug"`) to see per-upstream failure details.

## Performance Tuning

### Connection Limit

Windows limits concurrent connections by default, can be modified:

```csharp
// Program.cs
ServicePointManager.DefaultConnectionLimit = 100;
```

### Buffer Size

```csharp
// ProxyServer.cs
private readonly int _bufferSize = 81920; // 80KB
```

### Timeout Settings

```csharp
TcpClient.ReceiveTimeout = 30000;  // 30 seconds
TcpClient.SendTimeout = 30000;     // 30 seconds
```

## Logging Level

Modify `appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",           // General logs
      "LocalProxyServer": "Debug",        // Detailed debug logs
      "Microsoft.AspNetCore": "Warning"   // Reduce ASP.NET noise
    }
  }
}
```

## Recommended Usage Scenarios

| Scenario | Environment | Port | UseHttps | Command |
|----------|-------------|------|----------|---------|
| Local development testing | Development | 8080 | false | `dotnet run --environment Development` |
| Browser proxy | Development | 8080 | false | Set HTTP proxy in browser |
| Production deployment | Production | 8443 | true | `dotnet run --environment Production` |
| Requires encryption | Production | 8443 | true | Need to trust CA certificate |

## Additional Documentation

- [Upstream Proxy Configuration & Multi-Upstream Setup](#upstream-proxy-configuration)
- [Process Management and Auto-restart](PROCESS_CLEANUP.md)
- [Auto-restart Feature](AUTO_RESTART.md)
- [Windows Job Objects (Process Cleanup)](JOB_OBJECTS.md)
- [Configuration Examples](appsettings.example.json)

