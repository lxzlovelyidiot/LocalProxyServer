using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalProxyServer
{
    public enum ServiceStatus { Stopped, Starting, Running, Error }

    public class UpstreamStatus
    {
        public int Index { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public bool Enabled { get; set; }
        public bool ProcessRunning { get; set; }
        public int? ProcessId { get; set; }
        public int RestartCount { get; set; }
        public bool HealthCheckEnabled { get; set; }
        public bool? LastHealthCheckResult { get; set; }
    }

    public class CertificateInfo
    {
        public string? Subject { get; set; }
        public string? Issuer { get; set; }
        public string? Thumbprint { get; set; }
        public DateTime? NotBefore { get; set; }
        public DateTime? NotAfter { get; set; }
        public bool IsInstalled { get; set; }
    }

    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Level { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public class ServiceManager
    {
        private static ServiceManager? _instance;
        public static ServiceManager Instance => _instance ?? throw new InvalidOperationException("ServiceManager not initialized.");

        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<ServiceManager> _logger;
        public IConfiguration Configuration { get; private set; }

        public ProxyConfiguration? ProxyConfig { get; private set; }
        public DnsConfiguration? DnsConfig { get; private set; }
        public WebUIConfiguration? WebUIConfig { get; private set; }

        private ProxyServer? _proxy;
        private CrlServer? _crlServer;
        private DnsServer? _dnsServer;
        private readonly List<UpstreamProcessManager> _upstreamProcesses = new();

        public ServiceStatus ProxyStatus { get; private set; } = ServiceStatus.Stopped;
        public ServiceStatus DnsStatus { get; private set; } = ServiceStatus.Stopped;

        public event Action<LogEntry>? OnLogEntry;

        public static void Initialize(IConfiguration configuration, ILoggerFactory loggerFactory)
        {
            _instance = new ServiceManager(configuration, loggerFactory);
        }

        private ServiceManager(IConfiguration configuration, ILoggerFactory loggerFactory)
        {
            Configuration = configuration;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<ServiceManager>();
            LoadConfiguration();
        }

        private void LoadConfiguration()
        {
            ProxyConfig = Configuration.GetSection("Proxy").Get<ProxyConfiguration>() ?? new ProxyConfiguration();
            DnsConfig = Configuration.GetSection("Dns").Get<DnsConfiguration>() ?? new DnsConfiguration();
            WebUIConfig = Configuration.GetSection("WebUI").Get<WebUIConfiguration>() ?? new WebUIConfiguration();
        }

        public async Task StartProxyAsync(CancellationToken ct)
        {
            if (ProxyStatus != ServiceStatus.Stopped) return;
            ProxyStatus = ServiceStatus.Starting;
            try
            {
                var activeUpstreams = new List<UpstreamConfiguration>();
                var allUpstreams = new List<UpstreamConfiguration>();
                if (ProxyConfig?.Upstream != null) allUpstreams.Add(ProxyConfig.Upstream);
                if (ProxyConfig?.Upstreams != null) allUpstreams.AddRange(ProxyConfig.Upstreams);

                Environment.SetEnvironmentVariable("LSP_PATH", Environment.ProcessPath);
                Environment.SetEnvironmentVariable("LSP_PWD", Environment.CurrentDirectory);

                foreach (var upstream in allUpstreams)
                {
                    if (!upstream.Enabled) continue;

                    if (upstream.Type.Equals("socks5", StringComparison.OrdinalIgnoreCase) || 
                        upstream.Type.Equals("http", StringComparison.OrdinalIgnoreCase))
                    {
                        activeUpstreams.Add(upstream);
                    }

                    if (upstream.Process != null)
                    {
                        var processLogger = _loggerFactory.CreateLogger<UpstreamProcessManager>();
                        var processManager = new UpstreamProcessManager(upstream, processLogger);
                        _upstreamProcesses.Add(processManager);

                        var started = await processManager.StartAsync(ct);
                        if (!started)
                        {
                            _logger.LogError("Failed to start upstream process for {Host}:{Port}", upstream.Host, upstream.Port);
                        }
                    }
                }

                X509Certificate2? cert = null;
                if (ProxyConfig != null && ProxyConfig.UseHttps)
                {
                    string? crlDistributionUrl = ProxyConfig.CrlPort > 0 ? $"http://127.0.0.1:{ProxyConfig.CrlPort}/crl.der" : null;
                    cert = CertificateManager.GetOrCreateServerCertificate(crlDistributionUrl);

                    if (ProxyConfig.CrlPort > 0)
                    {
                        var rootCa = CertificateManager.GetRootCa();
                        if (rootCa != null)
                        {
                            byte[] crlDer = CertificateManager.BuildEmptyCrl(rootCa);
                            _crlServer = new CrlServer(ProxyConfig.CrlPort, crlDer, "/crl.der");
                            _crlServer.Start();
                        }
                    }
                }

                if (ProxyConfig != null)
                {
                    var proxyLogger = _loggerFactory.CreateLogger<ProxyServer>();
                    _proxy = new ProxyServer(ProxyConfig.Port, activeUpstreams, ProxyConfig.LoadBalancingStrategy, cert, proxyLogger);
                    _ = _proxy.StartAsync(); // Start in background
                }
                ProxyStatus = ServiceStatus.Running;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start proxy");
                ProxyStatus = ServiceStatus.Error;
            }
        }

        public Task StopProxyAsync()
        {
            if (ProxyStatus == ServiceStatus.Stopped) return Task.CompletedTask;
            
            _proxy?.Stop();
            _proxy = null;

            _crlServer?.Stop();
            _crlServer = null;

            foreach (var process in _upstreamProcesses)
            {
                try { process.Stop(); } catch { }
            }
            _upstreamProcesses.Clear();

            ProxyStatus = ServiceStatus.Stopped;
            return Task.CompletedTask;
        }

        public async Task RestartProxyAsync(CancellationToken ct)
        {
            await StopProxyAsync();
            await StartProxyAsync(ct);
        }

        public async Task StartDnsAsync(CancellationToken ct)
        {
            if (DnsStatus != ServiceStatus.Stopped) return;
            DnsStatus = ServiceStatus.Starting;
            try
            {
                if (DnsConfig != null)
                {
                    var allUpstreams = new List<UpstreamConfiguration>();
                    if (DnsConfig.Upstreams != null) allUpstreams.AddRange(DnsConfig.Upstreams);

                    foreach (var upstream in allUpstreams)
                    {
                        if (!upstream.Enabled) continue;
                        if (upstream.Process != null)
                        {
                            var processLogger = _loggerFactory.CreateLogger<UpstreamProcessManager>();
                            var processManager = new UpstreamProcessManager(upstream, processLogger);
                            _upstreamProcesses.Add(processManager);
                            await processManager.StartAsync(ct);
                        }
                    }

                    var dnsLogger = _loggerFactory.CreateLogger<DnsServer>();
                    _dnsServer = new DnsServer(DnsConfig, dnsLogger);
                    await _dnsServer.StartAsync();
                }
                DnsStatus = ServiceStatus.Running;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start DNS");
                DnsStatus = ServiceStatus.Error;
            }
        }

        public Task StopDnsAsync()
        {
            if (DnsStatus == ServiceStatus.Stopped) return Task.CompletedTask;
            
            _dnsServer?.Stop();
            _dnsServer = null;

            // Notice: If proxy and dns share upstreams, we might over-stop here.
            // But in original code, ProxyMain and DnsMain were separate. Let's just stop all.
            foreach (var process in _upstreamProcesses)
            {
                try { process.Stop(); } catch { }
            }
            _upstreamProcesses.Clear();

            DnsStatus = ServiceStatus.Stopped;
            return Task.CompletedTask;
        }

        public void StopAll()
        {
            _proxy?.Stop();
            _crlServer?.Stop();
            _dnsServer?.Stop();
            foreach (var process in _upstreamProcesses)
            {
                try { process.Stop(); } catch { }
            }
            _upstreamProcesses.Clear();
            ProxyStatus = ServiceStatus.Stopped;
            DnsStatus = ServiceStatus.Stopped;
        }

        public IReadOnlyList<UpstreamStatus> UpstreamStatuses
        {
            get
            {
                var list = new List<UpstreamStatus>();
                var allConfigs = new List<UpstreamConfiguration>();
                if (ProxyConfig?.Upstream != null) allConfigs.Add(ProxyConfig.Upstream);
                if (ProxyConfig?.Upstreams != null) allConfigs.AddRange(ProxyConfig.Upstreams);
                
                for (int i = 0; i < allConfigs.Count; i++)
                {
                    var config = allConfigs[i];
                    var manager = _upstreamProcesses.FirstOrDefault(m => m.Configuration == config);
                    list.Add(new UpstreamStatus
                    {
                        Index = i,
                        Type = config.Type,
                        Host = config.Host,
                        Port = config.Port,
                        Enabled = config.Enabled,
                        ProcessRunning = manager?.ProcessRunning ?? false,
                        ProcessId = manager?.ProcessId,
                        RestartCount = manager?.RestartCount ?? 0,
                        HealthCheckEnabled = config.HealthCheck?.Enabled ?? false,
                        LastHealthCheckResult = manager?.LastHealthCheckResult
                    });
                }
                return list;
            }
        }

        public Task ReloadConfigurationAsync()
        {
            // For now, updating appsettings requires restart or hot reload. 
            // In ASP.NET, IConfiguration auto-reloads if configured.
            LoadConfiguration();
            return Task.CompletedTask;
        }

        public async Task UpdateProxyConfigAsync(ProxyConfiguration newConfig)
        {
            UpdateJsonConfig("Proxy", newConfig);
            await ReloadConfigurationAsync();
        }

        public async Task UpdateDnsConfigAsync(DnsConfiguration newConfig)
        {
            UpdateJsonConfig("Dns", newConfig);
            await ReloadConfigurationAsync();
        }

        private void UpdateJsonConfig<T>(string sectionName, T newConfig)
        {
            var filePath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(filePath)) filePath = "appsettings.json";

            var json = File.ReadAllText(filePath);
            var jsonObj = JsonNode.Parse(json) as JsonObject;
            if (jsonObj != null)
            {
                jsonObj[sectionName] = JsonSerializer.SerializeToNode(newConfig, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, jsonObj.ToString());
            }
        }

        public CertificateInfo GetCertificateInfo()
        {
            var rootCa = CertificateManager.GetRootCa();
            if (rootCa == null)
            {
                return new CertificateInfo { IsInstalled = false };
            }

            bool isInstalled = false;
            try
            {
                using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadOnly);
                var certs = store.Certificates.Find(X509FindType.FindByThumbprint, rootCa.Thumbprint, false);
                isInstalled = certs.Count > 0;
            }
            catch { }

            return new CertificateInfo
            {
                Subject = rootCa.Subject,
                Issuer = rootCa.Issuer,
                Thumbprint = rootCa.Thumbprint,
                NotBefore = rootCa.NotBefore,
                NotAfter = rootCa.NotAfter,
                IsInstalled = isInstalled
            };
        }

        public Task RegenerateCertificateAsync()
        {
            try
            {
                _logger.LogInformation("Regenerating Root CA via WebUI request (requesting elevation)...");
                string? crlDistributionUrl = ProxyConfig?.CrlPort > 0 ? $"http://127.0.0.1:{ProxyConfig.CrlPort}/crl.der" : null;
                CertificateManager.InstallRootCa(crlDistributionUrl, forceRegenerate: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to regenerate sequence");
            }

            return Task.CompletedTask;
        }
    }
}
