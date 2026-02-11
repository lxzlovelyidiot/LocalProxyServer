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
```

## Configuration Files

### appsettings.json (Default Configuration)
Main configuration file, currently set to HTTPS mode.

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

### Firefox
1. Settings → Network Settings → Manual proxy configuration
2. HTTP proxy: `localhost:8080`
3. HTTPS proxy: `localhost:8443` (also accepts HTTP)

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

# HTTPS proxy (need to skip certificate verification)
curl -x https://localhost:8443 --proxy-insecure https://www.google.com

# HTTP over HTTPS-enabled port
curl -x http://localhost:8443 https://www.google.com

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

### Issue 4: Upstream Process Failed to Start

**Check logs:**
```
[Error] Failed to start upstream process
[Error] Cannot start upstream process: FileName is not configured
```

**Solution:**
1. Check if `FileName` path is correct
2. Check if environment variables expand correctly
3. Test command manually
4. View detailed error logs

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

- [Process Management and Auto-restart](PROCESS_CLEANUP.md)
- [Auto-restart Feature](AUTO_RESTART.md)
- [Windows Job Objects (Process Cleanup)](JOB_OBJECTS.md)
- [Configuration Examples](appsettings.example.json)

