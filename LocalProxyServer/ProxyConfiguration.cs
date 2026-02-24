namespace LocalProxyServer
{
    /// <summary>
    /// Configuration for the local proxy server.
    /// </summary>
    public class ProxyConfiguration
    {
        /// <summary>
        /// The port on which the proxy server listens.
        /// </summary>
        public int Port { get; set; } = 8080;

        /// <summary>
        /// Whether to use HTTPS for client connections.
        /// </summary>
        public bool UseHttps { get; set; } = true;

        /// <summary>
        /// CRL distribution point port (for certificate revocation).
        /// </summary>
        public int CrlPort { get; set; } = 8081;

        /// <summary>
        /// Upstream proxy configuration (legacy, use Upstreams instead).
        /// </summary>
        public UpstreamConfiguration? Upstream { get; set; }

        /// <summary>
        /// List of upstream proxy configurations.
        /// </summary>
        public List<UpstreamConfiguration>? Upstreams { get; set; }

        /// <summary>
        /// Load balancing strategy when multiple upstreams are configured ("failover" or "roundRobin").
        /// </summary>
        public string LoadBalancingStrategy { get; set; } = "failover";
    }

    /// <summary>
    /// Configuration for upstream proxy.
    /// </summary>
    public class UpstreamConfiguration
    {
        /// <summary>
        /// Type of upstream proxy: "socks5", "http", or "direct".
        /// </summary>
        public string Type { get; set; } = "direct";

        /// <summary>
        /// Upstream proxy host.
        /// </summary>
        public string? Host { get; set; }

        /// <summary>
        /// Upstream proxy port.
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// Whether upstream is enabled.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Command line configuration for starting upstream process.
        /// </summary>
        public UpstreamProcessConfiguration? Process { get; set; }

        /// <summary>
        /// Active health check configuration for this upstream.
        /// </summary>
        public HealthCheckConfiguration? HealthCheck { get; set; }
    }

    /// <summary>
    /// Configuration for upstream process that will be started automatically.
    /// All path and argument fields support environment variable expansion (e.g., %OneDrive%, %USERPROFILE%).
    /// </summary>
    public class UpstreamProcessConfiguration
    {
        /// <summary>
        /// Whether to start the upstream process automatically.
        /// </summary>
        public bool AutoStart { get; set; }

        /// <summary>
        /// Path to the executable. Supports environment variables (e.g., %ProgramFiles%\tool.exe).
        /// </summary>
        public string? FileName { get; set; }

        /// <summary>
        /// Command line arguments. Supports environment variables (e.g., -config %APPDATA%\config.json).
        /// </summary>
        public string? Arguments { get; set; }

        /// <summary>
        /// Working directory for the process. Supports environment variables (e.g., %USERPROFILE%\tools).
        /// </summary>
        public string? WorkingDirectory { get; set; }

        /// <summary>
        /// Wait time in milliseconds after starting process before using it.
        /// </summary>
        public int StartupDelayMs { get; set; } = 1000;

        /// <summary>
        /// Whether to redirect standard output/error.
        /// </summary>
        public bool RedirectOutput { get; set; } = true;

        /// <summary>
        /// Whether to automatically restart the process if it exits unexpectedly.
        /// </summary>
        public bool AutoRestart { get; set; } = true;

        /// <summary>
        /// Maximum number of restart attempts before giving up. 0 = unlimited.
        /// </summary>
        public int MaxRestartAttempts { get; set; } = 5;

        /// <summary>
        /// Delay in milliseconds before attempting to restart after a crash.
        /// </summary>
        public int RestartDelayMs { get; set; } = 3000;
    }

    /// <summary>
    /// Active health check configuration for an upstream proxy.
    /// A TCP connection attempt is made periodically; consecutive failures above the threshold trigger a restart.
    /// </summary>
    public class HealthCheckConfiguration
    {
        /// <summary>
        /// Whether active health checking is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Interval in milliseconds between health check probes.
        /// </summary>
        public int IntervalMs { get; set; } = 30_000;

        /// <summary>
        /// Timeout in milliseconds for each TCP connection probe.
        /// </summary>
        public int TimeoutMs { get; set; } = 5_000;

        /// <summary>
        /// Number of consecutive probe failures before the upstream process is restarted.
        /// </summary>
        public int FailureThreshold { get; set; } = 3;
    }
}
