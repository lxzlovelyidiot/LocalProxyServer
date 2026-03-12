using LocalProxyServer;

using Microsoft.Extensions.Logging;

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace LocalProxyServer
{
    public class Program
    {
        private static List<UpstreamProcessManager> _upstreamProcesses = new();
        private static ProxyServer? _proxy;
        private static CrlServer? _crlServer;
        private static DnsServer? _dnsServer;
        private static ILogger<Program>? _programLogger;
        private static CancellationTokenSource? _shutdownCts;

        private static async Task DnsMain(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            var configuration = builder.Configuration;

            // Configure logging
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();

            var app = builder.Build();
            var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
            _programLogger = loggerFactory.CreateLogger<Program>();

            var dnsConfig = configuration.GetSection("Dns").Get<DnsConfiguration>()
                ?? new DnsConfiguration();

            _programLogger.LogInformation("Starting LocalProxyServer (DNS mode)");
            _programLogger.LogInformation("DNS server enabled on port {Port}", dnsConfig.Port);

            // Register cleanup handlers for all exit scenarios
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            Console.CancelKeyPress += OnCancelKeyPress;

            // Handle graceful shutdown
            _shutdownCts = new CancellationTokenSource();
            var cts = _shutdownCts;

            // Setup upstreams for DNS
            var allUpstreams = new List<UpstreamConfiguration>();
            if (dnsConfig.Upstreams != null)
            {
                allUpstreams.AddRange(dnsConfig.Upstreams);
            }

            Environment.SetEnvironmentVariable("LSP_PATH", Environment.ProcessPath);
            Environment.SetEnvironmentVariable("LSP_PWD", Environment.CurrentDirectory);
            foreach (var upstream in allUpstreams)
            {
                if (!upstream.Enabled)
                {
                    continue;
                }

                _programLogger.LogInformation("DNS upstream {Type} proxy enabled: {Host}:{Port}",
                    upstream.Type.ToUpperInvariant(), upstream.Host, upstream.Port);

                if (upstream.Process != null)
                {
                    var processLogger = loggerFactory.CreateLogger<UpstreamProcessManager>();
                    var processManager = new UpstreamProcessManager(upstream, processLogger);
                    _upstreamProcesses.Add(processManager);

                    var started = await processManager.StartAsync(cts.Token);
                    if (!started)
                    {
                        _programLogger.LogError("Failed to start upstream process. DNS to {Host}:{Port} may not work correctly", upstream.Host, upstream.Port);
                    }
                }
            }

            var dnsLogger = loggerFactory.CreateLogger<DnsServer>();
            _dnsServer = new DnsServer(dnsConfig, dnsLogger);
            await _dnsServer.StartAsync();

            _programLogger.LogInformation("DNS server is running. Press Ctrl+C to stop");

            try
            {
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
            }

            Cleanup();
        }

        private static async Task ProxyMain(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            var configuration = builder.Configuration;

            // Configure logging
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();

            var app = builder.Build();
            var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger<ProxyServer>();
            _programLogger = loggerFactory.CreateLogger<Program>();

            // Get configuration using the new model
            var proxyConfig = configuration.GetSection("Proxy").Get<ProxyConfiguration>()
                ?? new ProxyConfiguration();

            _programLogger.LogInformation("Starting LocalProxyServer");
            _programLogger.LogInformation("Configuration: Port={Port}, HTTPS={UseHttps}, CRL Port={CrlPort}",
                proxyConfig.Port, proxyConfig.UseHttps, proxyConfig.CrlPort);

            // Register cleanup handlers for all exit scenarios
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            Console.CancelKeyPress += OnCancelKeyPress;

            // Handle graceful shutdown
            _shutdownCts = new CancellationTokenSource();
            var cts = _shutdownCts;

            // Setup upstreams
            var activeUpstreams = new List<UpstreamConfiguration>();

            // Merge legacy Upstream and new Upstreams list
            var allUpstreams = new List<UpstreamConfiguration>();
            if (proxyConfig.Upstream != null)
            {
                allUpstreams.Add(proxyConfig.Upstream);
            }
            if (proxyConfig.Upstreams != null)
            {
                allUpstreams.AddRange(proxyConfig.Upstreams);
            }

            Environment.SetEnvironmentVariable("LSP_PATH", Environment.ProcessPath);
            Environment.SetEnvironmentVariable("LSP_PWD", Environment.CurrentDirectory);
            foreach (var upstream in allUpstreams)
            {
                if (upstream.Enabled)
                {
                    if (upstream.Type.Equals("socks5", StringComparison.OrdinalIgnoreCase) || 
                        upstream.Type.Equals("http", StringComparison.OrdinalIgnoreCase))
                    {
                        activeUpstreams.Add(upstream);
                        _programLogger.LogInformation("Upstream {Type} proxy enabled: {Host}:{Port}",
                            upstream.Type.ToUpperInvariant(), upstream.Host, upstream.Port);
                    }
                    else
                    {
                        _programLogger.LogInformation("Background daemon process enabled: {Type} {Host}:{Port}",
                            upstream.Type, upstream.Host, upstream.Port);
                    }

                    if (upstream.Process != null)
                    {
                        var processLogger = loggerFactory.CreateLogger<UpstreamProcessManager>();
                        var processManager = new UpstreamProcessManager(upstream, processLogger);
                        _upstreamProcesses.Add(processManager);

                        var started = await processManager.StartAsync(cts.Token);
                        if (!started)
                        {
                            _programLogger.LogError("Failed to start upstream process. Proxy to {Host}:{Port} may not work correctly", upstream.Host, upstream.Port);
                        }
                    }
                }
            }

            if (activeUpstreams.Count == 0)
            {
                _programLogger.LogInformation("No upstream proxy configured, using direct connection");
            }

            // Setup certificates if HTTPS is enabled
            X509Certificate2? cert = null;

            if (proxyConfig.UseHttps)
            {
                string? crlDistributionUrl = proxyConfig.CrlPort > 0
                    ? $"http://127.0.0.1:{proxyConfig.CrlPort}/crl.der"
                    : null;

                _programLogger.LogInformation("Generating server certificate with CRL distribution point: {CrlUrl}",
                    crlDistributionUrl);

                cert = CertificateManager.GetOrCreateServerCertificate(crlDistributionUrl);
                _programLogger.LogInformation("Server certificate ready: {Subject}", cert.Subject);

                if (proxyConfig.CrlPort > 0)
                {
                    var rootCa = CertificateManager.GetRootCa();
                    if (rootCa != null)
                    {
                        byte[] crlDer = CertificateManager.BuildEmptyCrl(rootCa);
                        _crlServer = new CrlServer(proxyConfig.CrlPort, crlDer, "/crl.der");
                        _crlServer.Start();
                        _programLogger.LogInformation("CRL server started on port {Port}", proxyConfig.CrlPort);
                    }
                }
            }

            // Start proxy server
            _proxy = new ProxyServer(proxyConfig.Port, activeUpstreams, proxyConfig.LoadBalancingStrategy, cert, logger);

            var proxyTask = _proxy.StartAsync();

            _programLogger.LogInformation("Proxy server is running. Press Ctrl+C to stop");

            try
            {
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
            }

            Cleanup();
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        public static async Task Main(string[] args)
        {
            if (args.Length >= 4 && args[0] == "client" && args[1] == "tunnel" && args[2] == "--server")
            {
                string? listenEndpoint = null;
                if (args.Length >= 6 && args[4] == "--listen")
                {
                    listenEndpoint = args[5];
                }
                await QuicClientTunnel.RunAsync(args[3], listenEndpoint);
                return;
            }
            // dotnet publish -r linux-musl-x64 -c Release /p:PublishAot=true /p:SelfContained=true
            if (args.Length >= 6 && args[0] == "server" && args[1] == "tunnel" && args[2] == "--listen" && args[4] == "--forward")
            {
                await QuicServerTunnel.RunAsync(args[3], args[5]);
                return;
            }

            if (args.Length >= 2 &&
                args[0].Equals("dns", StringComparison.OrdinalIgnoreCase) &&
                args[1].Equals("server", StringComparison.OrdinalIgnoreCase))
            {
                await DnsMain(args);
                return;
            }

            await ProxyMain(args);
            return;
        }

        private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            _programLogger?.LogInformation("Shutdown requested (Ctrl+C)");
            _shutdownCts?.Cancel();
        }

        private static void OnProcessExit(object? sender, EventArgs e)
        {
            _programLogger?.LogInformation("Process exit detected, cleaning up");
            Cleanup();
        }

        private static void Cleanup()
        {
            _programLogger?.LogInformation("Stopping servers");

            try
            {
                _proxy?.Stop();
            }
            catch (Exception ex)
            {
                _programLogger?.LogError(ex, "Error stopping proxy server");
            }

            try
            {
                _crlServer?.Stop();
            }
            catch (Exception ex)
            {
                _programLogger?.LogError(ex, "Error stopping CRL server");
            }

            try
            {
                _dnsServer?.Stop();
            }
            catch (Exception ex)
            {
                _programLogger?.LogError(ex, "Error stopping DNS server");
            }

            foreach (var process in _upstreamProcesses)
            {
                try
                {
                    process.Stop();
                }
                catch (Exception ex)
                {
                    _programLogger?.LogError(ex, "Error stopping upstream process");
                }
            }

            _programLogger?.LogInformation("LocalProxyServer stopped");
        }
    }
}
