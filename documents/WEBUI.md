# Web Management Interface (WebUI)

LocalProxyServer includes a fully integrated, zero-dependency web management interface. It allows you to monitor the status of your proxies, manage upstream endpoints, inspect real-time logs, and modify configurations on the fly without needing to edit JSON files manually or restart the process.

## How to Enable

The WebUI is disabled by default for security and performance reasons. You can enable it in two ways:

### 1. Command Line Flag (Recommended)

Start the proxy server with the `--webui` flag:

```bash
dotnet run -- --webui
```

Or, if using the compiled executable:

```bash
./LocalProxyServer.exe --webui
```

*Note: You can also use `--no-webui` to explicitly force it off even if it is enabled in your configuration file.*

### 2. Configuration File

You can enable it permanently by modifying your `appsettings.json` (or Environment specific config):

```json
{
  "WebUI": {
    "Enabled": true,
    "Port": 9090
  }
}
```

## Accessing the Dashboard

Once started, open your browser and navigate to:

**[https://localhost:9090/](https://localhost:9090/)**

> **Important Security Note:**  
> The WebUI is served exclusively over HTTPS using the same `LocalProxyServer-CA` root certificate that the proxy uses to intercept traffic. Your browser will show a security warning unless you have installed and trusted the Root CA in your system's certificate store.

## Features Overview

### 1. Dashboard (Overview)
Displays a quick snapshot of the system:
- Real-time running status of the Proxy Server and DNS Server.
- Bound ports and active HTTPS interception status.
- Key Certificate lifecycle information.

### 2. Live Logs
Streams system logs directly to your browser in real-time over WebSockets (`wss://localhost:9090/ws/logs`).
- Adjustable log verbosity (Debug, Info, Warning, Error).
- Non-blocking architecture: heavy traffic logging will not degrade the proxy's network performance.

### 3. Proxy Configuration
Modify the core listening parameters on the fly:
- Port and HTTPS toggle.
- Upstream Load Balancing Strategy (e.g., `failover` or `roundRobin`).
- CRL (Certificate Revocation List) endpoint configuration.

*Modifying these parameters usually requires restarting the proxy service, which can be done directly from the UI.*

### 4. Upstream Management
Manage outbound proxy routes:
- View all active underlying upstream proxies (`socks5`, `http`).
- Add, Edit, or Delete upstream routes dynamically.
- Monitor active **Health Check** status and probe failures for each endpoint.
- Monitor Auto-Start Process status (e.g., automatically launched SSH tunnels).

### 5. DNS Configuration
Toggle the built-in DNS interception and routing engine, and configure its port mapping.

### 6. Certificate Management
Allows you to deeply inspect the currently loaded Root CA. 
- Includes a **Regenerate Root CA** feature.
- **UAC Auto-Elevation:** If you regenerate the certificate from the WebUI, the application will automatically prompt for Windows Administrator privileges (UAC) to cleanly remove the old cached certificate and securely install the new one directly into the OS Trusted Root store.
