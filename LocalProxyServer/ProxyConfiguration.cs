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
        /// Upstream proxy configuration.
        /// </summary>
        public UpstreamConfiguration? Upstream { get; set; }
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
}
