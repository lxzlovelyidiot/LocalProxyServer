using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System.Text.Json.Serialization;

namespace LocalProxyServer
{
    public record ProxyStatusDto(string Status, int? Port, bool? UseHttps, int UpstreamCount, string? LoadBalancingStrategy);
    public record DnsStatusDto(string Status, int? Port);
    public record StatusResponse(ProxyStatusDto Proxy, DnsStatusDto Dns, IReadOnlyList<UpstreamStatus> Upstreams, CertificateInfo Certificate);
    public record ConfigUpdateResponse<T>(T Config, bool RequiresRestart);
    public record SuccessResponse(bool Success, bool RequiresRestart = false, string? Message = null);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(StatusResponse))]
    [JsonSerializable(typeof(ProxyConfiguration))]
    [JsonSerializable(typeof(ConfigUpdateResponse<ProxyConfiguration>))]
    [JsonSerializable(typeof(IReadOnlyList<UpstreamStatus>))]
    [JsonSerializable(typeof(UpstreamConfiguration))]
    [JsonSerializable(typeof(SuccessResponse))]
    [JsonSerializable(typeof(DnsConfiguration))]
    [JsonSerializable(typeof(ConfigUpdateResponse<DnsConfiguration>))]
    [JsonSerializable(typeof(CertificateInfo))]
    [JsonSerializable(typeof(LogEntry))]
    public partial class WebUIJsonContext : JsonSerializerContext
    {
    }

    public static class WebUIServer
    {
        private static WebApplication? _app;

        public static async Task StartAsync(string[] args, WebUIConfiguration config)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
            });

            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.TypeInfoResolverChain.Insert(0, WebUIJsonContext.Default);
            });

            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenLocalhost(config.Port, listenOptions =>
                {
                    var cert = CertificateManager.GetOrCreateServerCertificate(null);
                    if (cert != null)
                    {
                        listenOptions.UseHttps(cert);
                    }
                });
            });

            var app = builder.Build();
            app.UseCors();

            var embeddedProvider = new EmbeddedFileProvider(typeof(WebUIServer).Assembly, "LocalProxyServer.WebUI");
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = embeddedProvider,
                RequestPath = ""
            });

            app.UseWebSockets();
            app.Map("/ws/logs", async context =>
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    var webSocket = await context.WebSockets.AcceptWebSocketAsync();

                    var minLevelString = context.Request.Query["level"].ToString();
                    var minLevel = Microsoft.Extensions.Logging.LogLevel.Information;
                    if (Enum.TryParse<Microsoft.Extensions.Logging.LogLevel>(minLevelString, true, out var parsedLevel))
                    {
                        minLevel = parsedLevel;
                    }

                    await WebSocketLogBroadcaster.HandleWebSocketAsync(webSocket, minLevel);
                }
                else
                {
                    context.Response.StatusCode = 400;
                }
            });

            app.MapGet("/api/status", () => 
            {
                var proxyStatus = new ProxyStatusDto(
                    ServiceManager.Instance.ProxyStatus.ToString(),
                    ServiceManager.Instance.ProxyConfig?.Port,
                    ServiceManager.Instance.ProxyConfig?.UseHttps,
                    ServiceManager.Instance.ProxyConfig?.Upstream != null ? 1 : (ServiceManager.Instance.ProxyConfig?.Upstreams?.Count ?? 0),
                    ServiceManager.Instance.ProxyConfig?.LoadBalancingStrategy
                );
                var dnsStatus = new DnsStatusDto(
                    ServiceManager.Instance.DnsStatus.ToString(),
                    ServiceManager.Instance.DnsConfig?.Port
                );
                var upstreams = ServiceManager.Instance.UpstreamStatuses;
                var cert = ServiceManager.Instance.GetCertificateInfo();

                return Results.Ok(new StatusResponse(proxyStatus, dnsStatus, upstreams, cert));
            });

            app.MapGet("/api/config/proxy", () => Results.Ok(ServiceManager.Instance.ProxyConfig));
            app.MapPut("/api/config/proxy", async (ProxyConfiguration newConfig) => 
            {
                var current = ServiceManager.Instance.ProxyConfig;
                if (current != null)
                {
                    newConfig.Upstream = current.Upstream;
                    newConfig.Upstreams = current.Upstreams;
                }
                await ServiceManager.Instance.UpdateProxyConfigAsync(newConfig);
                return Results.Ok(new ConfigUpdateResponse<ProxyConfiguration>(ServiceManager.Instance.ProxyConfig!, true));
            });

            app.MapGet("/api/config/proxy/upstreams", () => Results.Ok(ServiceManager.Instance.UpstreamStatuses));

            app.MapGet("/api/config/proxy/upstreams/{index}", (int index) => 
            {
                var upstreams = ServiceManager.Instance.UpstreamStatuses;
                if (index < 0 || index >= upstreams.Count) return Results.NotFound();

                var configList = new List<UpstreamConfiguration>();
                if (ServiceManager.Instance.ProxyConfig?.Upstream != null) configList.Add(ServiceManager.Instance.ProxyConfig.Upstream);
                if (ServiceManager.Instance.ProxyConfig?.Upstreams != null) configList.AddRange(ServiceManager.Instance.ProxyConfig.Upstreams);

                return configList.Count > index ? Results.Ok(configList[index]) : Results.NotFound();
            });

            app.MapPost("/api/config/proxy/upstreams", async (UpstreamConfiguration newUpstream) => 
            {
                var config = ServiceManager.Instance.ProxyConfig ?? new ProxyConfiguration();
                config.Upstreams ??= new List<UpstreamConfiguration>();
                config.Upstreams.Add(newUpstream);
                await ServiceManager.Instance.UpdateProxyConfigAsync(config);
                return Results.Ok(new SuccessResponse(true, true, null));
            });

            app.MapPut("/api/config/proxy/upstreams/{index}", async (int index, UpstreamConfiguration updatedUpstream) => 
            {
                var config = ServiceManager.Instance.ProxyConfig;
                if (config == null) return Results.NotFound();

                if (index == 0 && config.Upstream != null)
                {
                    config.Upstream = updatedUpstream;
                }
                else
                {
                    var offsetIndex = config.Upstream != null ? index - 1 : index;
                    if (config.Upstreams == null || offsetIndex < 0 || offsetIndex >= config.Upstreams.Count) return Results.NotFound();
                    config.Upstreams[offsetIndex] = updatedUpstream;
                }
                await ServiceManager.Instance.UpdateProxyConfigAsync(config);
                return Results.Ok(new SuccessResponse(true, true, null));
            });

            app.MapDelete("/api/config/proxy/upstreams/{index}", async (int index) => 
            {
                var config = ServiceManager.Instance.ProxyConfig;
                if (config == null) return Results.NotFound();

                if (index == 0 && config.Upstream != null)
                {
                    config.Upstream = null;
                }
                else
                {
                    var offsetIndex = config.Upstream != null ? index - 1 : index;
                    if (config.Upstreams == null || offsetIndex < 0 || offsetIndex >= config.Upstreams.Count) return Results.NotFound();
                    config.Upstreams.RemoveAt(offsetIndex);
                }
                await ServiceManager.Instance.UpdateProxyConfigAsync(config);
                return Results.Ok(new SuccessResponse(true, true, null));
            });

            app.MapGet("/api/config/dns", () => Results.Ok(ServiceManager.Instance.DnsConfig));
            app.MapPut("/api/config/dns", async (DnsConfiguration newConfig) => 
            {
                await ServiceManager.Instance.UpdateDnsConfigAsync(newConfig);
                return Results.Ok(new ConfigUpdateResponse<DnsConfiguration>(ServiceManager.Instance.DnsConfig!, true));
            });

            app.MapPost("/api/proxy/start", async (CancellationToken ct) => 
            {
                await ServiceManager.Instance.StartProxyAsync(ct);
                return Results.Ok(new SuccessResponse(true, false, "Proxy server started"));
            });

            app.MapPost("/api/proxy/stop", async () => 
            {
                await ServiceManager.Instance.StopProxyAsync();
                return Results.Ok(new SuccessResponse(true, false, "Proxy server stopped"));
            });

            app.MapPost("/api/proxy/restart", async (CancellationToken ct) => 
            {
                await ServiceManager.Instance.RestartProxyAsync(ct);
                return Results.Ok(new SuccessResponse(true, false, "Proxy server restarted"));
            });

            app.MapPost("/api/dns/start", async (CancellationToken ct) => 
            {
                await ServiceManager.Instance.StartDnsAsync(ct);
                return Results.Ok(new SuccessResponse(true, false, "DNS server started"));
            });

            app.MapPost("/api/dns/stop", async () => 
            {
                await ServiceManager.Instance.StopDnsAsync();
                return Results.Ok(new SuccessResponse(true, false, "DNS server stopped"));
            });

            app.MapGet("/api/certificate", () => Results.Ok(ServiceManager.Instance.GetCertificateInfo()));
            app.MapPost("/api/certificate/regenerate", async () => 
            {
                await ServiceManager.Instance.RegenerateCertificateAsync();
                return Results.Ok(new SuccessResponse(true, false, "Certificate regenerated"));
            });

            app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = embeddedProvider });

            _app = app;
            await _app.StartAsync();
        }

        public static async Task StopAsync()
        {
            if (_app != null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
                _app = null;
            }
        }
    }
}
