using LocalProxyServer;

using Microsoft.Extensions.Logging;

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace LocalProxyServer
{
    public class Program
    {
        private static ILogger<Program>? _programLogger;
        private static CancellationTokenSource? _shutdownCts;
        private static readonly List<IDisposable> _posixSignalRegistrations = new();
        private static int _cleanupStarted;

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        public static async Task Main(string[] args)
        {
            if (args.Contains("--install-ca"))
            {
                Console.WriteLine("Attempting to install LocalProxyServer Root CA...");
                bool forceRegen = args.Contains("--force-regenerate");
                CertificateManager.InstallRootCa(forceRegenerate: forceRegen);
                return;
            }

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
            if (args.Length >= 6 && args[0] == "server" && args[1] == "tunnel" && args[2] == "--listen" && args[4] == "--forward")
            {
                await QuicServerTunnel.RunAsync(args[3], args[5]);
                return;
            }

            var builder = WebApplication.CreateBuilder(args);

            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();
            builder.Logging.AddProvider(new WebSocketLoggerProvider());

            var app = builder.Build();
            var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
            _programLogger = loggerFactory.CreateLogger<Program>();

            ServiceManager.Initialize(builder.Configuration, loggerFactory);

            bool enableWebUI = ServiceManager.Instance.WebUIConfig?.Enabled ?? false;
            if (args.Contains("--webui")) enableWebUI = true;
            if (args.Contains("--no-webui")) enableWebUI = false;

            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            Console.CancelKeyPress += OnCancelKeyPress;
            RegisterLinuxSignals();

            _shutdownCts = new CancellationTokenSource();
            var ct = _shutdownCts.Token;

            _programLogger.LogInformation("Starting LocalProxyServer services...");

            bool isDnsMode = args.Length >= 2 && 
                             args[0].Equals("dns", StringComparison.OrdinalIgnoreCase) && 
                             args[1].Equals("server", StringComparison.OrdinalIgnoreCase);

            if (isDnsMode)
            {
                await ServiceManager.Instance.StartDnsAsync(ct);
            }
            else
            {
                await ServiceManager.Instance.StartProxyAsync(ct);
            }

            if (enableWebUI)
            {
                _programLogger.LogInformation("Starting WebUI Server...");
                _ = WebUIServer.StartAsync(Array.Empty<string>(), ServiceManager.Instance.WebUIConfig ?? new WebUIConfiguration());
            }

            _programLogger.LogInformation("LocalProxyServer is running. Press Ctrl+C to stop.");

            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }

            Cleanup();
            
            if (enableWebUI)
            {
                await WebUIServer.StopAsync();
            }
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
            if (Interlocked.Exchange(ref _cleanupStarted, 1) == 1) return;

            _programLogger?.LogInformation("Stopping servers");

            try
            {
                ServiceManager.Instance.StopAll();
            }
            catch (Exception ex)
            {
                _programLogger?.LogError(ex, "Error during cleanup");
            }

            foreach (var registration in _posixSignalRegistrations)
            {
                registration.Dispose();
            }
            _posixSignalRegistrations.Clear();

            _programLogger?.LogInformation("LocalProxyServer stopped");
        }

        private static void RegisterLinuxSignals()
        {
            if (!OperatingSystem.IsLinux()) return;
            if (_posixSignalRegistrations.Count > 0) return;

            _posixSignalRegistrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                _programLogger?.LogInformation("Shutdown requested (SIGTERM)");
                _shutdownCts?.Cancel();
                context.Cancel = true;
            }));
            _posixSignalRegistrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { _shutdownCts?.Cancel(); context.Cancel = true; }));
            _posixSignalRegistrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGQUIT, context => { _shutdownCts?.Cancel(); context.Cancel = true; }));
            _posixSignalRegistrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGHUP, context => { _shutdownCts?.Cancel(); context.Cancel = true; }));
        }
    }
}
