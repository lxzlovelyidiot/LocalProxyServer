using LocalProxyServer;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace LocalProxyServer
{
    public class Program
    {
        private static List<UpstreamProcessManager> _upstreamProcesses = new();
        private static ProxyServer? _proxy;
        private static CrlServer? _crlServer;
        private static ILogger<Program>? _programLogger;

        public static async Task Main(string[] args)
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
            var cts = new CancellationTokenSource();

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

            foreach (var upstream in allUpstreams)
            {
                if (upstream.Enabled)
                {
                    activeUpstreams.Add(upstream);
                    _programLogger.LogInformation("Upstream {Type} proxy enabled: {Host}:{Port}",
                        upstream.Type.ToUpperInvariant(), upstream.Host, upstream.Port);

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

        private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            _programLogger?.LogInformation("Shutdown requested (Ctrl+C)");
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
