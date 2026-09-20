using System.Net.Http;
using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Apply;
using AutoProxy.Core.Detection;
using AutoProxy.Core.Monitoring;
using AutoProxy.Core.Network;
using AutoProxy.Core.Services;
using AutoProxy.Data;
using AutoProxy.Windows;
using AutoProxy.App.Services;
using AutoProxy.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AutoProxy.App;

public static class CompositionRoot
{
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        // --- Data / repository layer ---
        services.AddSingleton<DatabaseContext>();
        services.AddSingleton<ILogRepository, LogRepository>();
        services.AddSingleton<IProxyRepository, ProxyRepository>();
        services.AddSingleton<INetworkRepository, NetworkRepository>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();
        services.AddSingleton<IDetectionHistoryRepository, DetectionHistoryRepository>();

        // --- Services ---
        services.AddSingleton<ILogService, LogService>();
        services.AddSingleton<ICredentialManager, CredentialManager>();
        services.AddSingleton<IWindowsProxyManager, WindowsProxyManager>();
        services.AddSingleton<AppStateService>();

        // --- Networking ---
        services.AddSingleton<INetworkMonitor, NativeNetworkMonitor>();
        RegisterHttpClient(services);

        services.AddSingleton<IConnectivityTester, ConnectivityTester>();
        services.AddSingleton<IProxyTester, ProxyTester>();

        // --- Apply ---
        services.AddSingleton<ApplyEngine>();

        // --- Detection ---
        services.AddSingleton<DecisionEngine>();
        services.AddSingleton<DetectionEngine>();
        services.AddSingleton<MonitoringService>();

        // --- ViewModels ---
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<ProxiesViewModel>();
        services.AddSingleton<NetworksViewModel>();
        services.AddSingleton<LogsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<TrayService>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The direct-connectivity client must bypass the Windows system proxy,
    /// otherwise "direct" tests could silently route through a proxy.
    /// </summary>
    private static void RegisterHttpClient(IServiceCollection services)
    {
        services.AddSingleton<HttpMessageHandler>(_ => new SocketsHttpHandler
        {
            UseProxy = false,
            Proxy = null,
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        });

        services.AddSingleton(sp =>
            new HttpClient(sp.GetRequiredService<HttpMessageHandler>(), disposeHandler: false));
    }
}