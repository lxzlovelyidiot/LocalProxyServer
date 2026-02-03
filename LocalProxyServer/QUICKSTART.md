# Quick Reference

## Start Server

```bash
# Development environment (HTTP proxy)
dotnet run --environment Development

# Production environment (HTTPS proxy)
dotnet run --environment Production

# Default configuration
dotnet run
```

## Test Commands

### HTTP Proxy (Port 8080)
```bash
# Basic test
curl -x http://localhost:8080 https://www.google.com

# View detailed information
curl -x http://localhost:8080 -v https://www.google.com

# Test API
curl -x http://localhost:8080 https://api.github.com
```

### HTTPS Proxy (Port 8443)
```bash
# Basic test (skip certificate verification)
curl -x https://localhost:8443 --proxy-insecure https://www.google.com

# View detailed information
curl -x https://localhost:8443 --proxy-insecure -v https://www.google.com
```

## Configuration Comparison

| Feature | HTTP (8080) | HTTPS (8443) |
|---------|-------------|--------------|
| Encryption | ❌ None | ✅ TLS |
| Port | 8080 | 8443 |
| Certificate | Not needed | Self-signed CA |
| Testing | Direct use | Needs --proxy-insecure |
| Suitable for | Development testing | Production deployment |

## Browser Settings

### Chrome/Edge
1. Settings → System → Open proxy settings
2. Manual proxy:
   - HTTP: `localhost:8080`
   - HTTPS: `localhost:8443`

### Firefox
1. Settings → Network Settings
2. Manual proxy configuration:
   - HTTP proxy: `localhost:8080`
   - HTTPS proxy: `localhost:8443`

## Environment Variables (PowerShell)

```powershell
# Set HTTP proxy
$env:HTTP_PROXY = "http://localhost:8080"
$env:HTTPS_PROXY = "http://localhost:8080"

# Test
curl https://www.google.com

# Clear
Remove-Item env:HTTP_PROXY
Remove-Item env:HTTPS_PROXY
```

## Common Issues

### ❌ "Proxy CONNECT aborted"
**Cause:** Configuration is HTTPS but using HTTP connection

**Solution:**
```bash
# Use Development environment (HTTP mode)
dotnet run --environment Development
curl -x http://localhost:8080 https://www.google.com
```

### ❌ "Connection refused"
**Cause:** Server not started or wrong port

**Solution:**
```bash
# Check port
netstat -an | findstr "8080"

# Start server
dotnet run --environment Development
```

### ❌ "certificate verify failed"
**Cause:** Self-signed certificate not trusted

**Solution:**
```bash
# Temporary: Skip verification
curl -x https://localhost:8443 --proxy-insecure https://www.google.com

# Permanent: Trust CA certificate
# See README.md Certificate Management section
```

## Configuration Files

| File | Purpose | UseHttps | Port |
|------|---------|----------|------|
| `appsettings.json` | Default | true | 8080 |
| `appsettings.Development.json` | Development | false | 8080 |
| `appsettings.Production.json` | Production | true | 8443 |

## Git/NPM Proxy Settings

### Git
```bash
# Set
git config --global http.proxy http://localhost:8080
git config --global https.proxy http://localhost:8080

# Unset
git config --global --unset http.proxy
git config --global --unset https.proxy
```

### NPM
```bash
# Set
npm config set proxy http://localhost:8080
npm config set https-proxy http://localhost:8080

# Unset
npm config delete proxy
npm config delete https-proxy
```

## More Information

- Complete documentation: [README.md](README.md)
- Process management: [PROCESS_CLEANUP.md](PROCESS_CLEANUP.md)
- Auto-restart: [AUTO_RESTART.md](AUTO_RESTART.md)
