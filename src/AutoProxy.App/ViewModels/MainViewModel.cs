using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Monitoring;
using AutoProxy.Core.Models;
using AutoProxy.Core.Services;

namespace AutoProxy.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly MonitoringService _monitoring;
    private readonly AppStateService _state;
    private readonly IProxyRepository _proxyRepo;
    private readonly INetworkMonitor _networkMonitor;
    private readonly IWindowsProxyManager _proxyManager;
    private readonly ILogService _log;
    private readonly CancellationTokenSource _cts = new();

    public MainViewModel(
        MonitoringService monitoring,
        AppStateService state,
        IProxyRepository proxyRepo,
        INetworkMonitor networkMonitor,
        IWindowsProxyManager proxyManager,
        ILogService log,
        DashboardViewModel dashboard,
        ProxiesViewModel proxies,
        NetworksViewModel networks,
        LogsViewModel logs,
        SettingsViewModel settings)
    {
        _monitoring = monitoring;
        _state = state;
        _proxyRepo = proxyRepo;
        _networkMonitor = networkMonitor;
        _proxyManager = proxyManager;
        _log = log;

        Dashboard = dashboard;
        Proxies = proxies;
        Networks = networks;
        Logs = logs;
        Settings = settings;

        CurrentPage = Dashboard;

        Dashboard.NavigateAction = key =>
        {
            switch (key)
            {
                case "Proxies": GoProxies(); break;
                case "Networks": GoNetworks(); break;
                case "Logs": GoLogs(); break;
                case "Settings": GoSettings(); break;
                default: GoDashboard(); break;
            }
        };

        _state.Changed += _ => Application.Current?.Dispatcher.InvokeAsync(OnStateChanged);
        _ = RefreshManualProxiesAsync();
    }

    partial void OnSelectedModeChanged(AppMode value)
    {
        if (value == AppMode.ManualProxy)
            _ = RefreshManualProxiesAsync();
    }

    public DashboardViewModel Dashboard { get; }
    public ProxiesViewModel Proxies { get; }
    public NetworksViewModel Networks { get; }
    public LogsViewModel Logs { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    private object? currentPage;

    [ObservableProperty]
    private string currentPageKey = "Dashboard";

    public AppStateService State => _state;

    public string CurrentNetworkName =>
        _state.LastResult?.Network?.DisplayName ?? "No network";

    public string StatusText => _state.StatusText;

    public string LatencyText =>
        _state.LastResult is { LatencyMs: > 0 } r ? $"{r.LatencyMs} ms" : "—";

    public string LastCheckText =>
        _state.LastCheck == DateTime.MinValue ? "—" : _state.LastCheck.ToString("HH:mm:ss");

    public string ModeText => _state.Mode switch
    {
        AppMode.Automatic => "Automatic",
        AppMode.ManualDirect => "Manual — Direct",
        AppMode.ManualProxy => "Manual — Proxy",
        _ => "Manual — Disabled",
    };

    public IReadOnlyList<AppMode> Modes { get; } =
    [
        AppMode.Automatic,
        AppMode.ManualDirect,
        AppMode.ManualProxy,
        AppMode.ManualDisable,
    ];

    [ObservableProperty]
    private AppMode selectedMode = AppMode.Automatic;

    [ObservableProperty]
    private IReadOnlyList<ProxyProfile> manualProxies = Array.Empty<ProxyProfile>();

    [ObservableProperty]
    private ProxyProfile? selectedManualProxy;

    [RelayCommand]
    private void GoDashboard() => SetPage("Dashboard", Dashboard);

    [RelayCommand]
    private void GoProxies()
    {
        SetPage("Proxies", Proxies);
        _ = Proxies.LoadAsync();
    }

    [RelayCommand]
    private void GoNetworks()
    {
        SetPage("Networks", Networks);
        _ = Networks.LoadAsync();
    }

    [RelayCommand]
    private void GoLogs()
    {
        SetPage("Logs", Logs);
        Logs.RefreshAll();
    }

    [RelayCommand]
    private void GoSettings()
    {
        SetPage("Settings", Settings);
        _ = Settings.LoadAsync();
    }

    private void SetPage(string key, object page)
    {
        CurrentPageKey = key;
        CurrentPage = page;
    }

    [RelayCommand]
    private async Task ApplyModeAsync()
    {
        var mode = SelectedMode;
        ProxyProfile? proxy = mode == AppMode.ManualProxy ? SelectedManualProxy : null;

        if (mode == AppMode.ManualProxy && proxy is null)
        {
            _log.Warning("Manual", "Select a proxy before enabling manual proxy mode.");
            return;
        }

        await _monitoring.SetModeAsync(mode, proxy, _cts.Token);
        OnStateChanged();
    }

    [RelayCommand]
    private async Task RefreshManualProxiesAsync()
    {
        ManualProxies = await _proxyRepo.GetEnabledAsync(_cts.Token);
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        OnPropertyChanged(nameof(StatusText));
        await _monitoring.ManualTestNowAsync(_cts.Token);
        OnStateChanged();
        NotifyAllDashboardItems();
    }

    [RelayCommand]
    private void RestorePreviousProxySettings()
    {
        if (_proxyManager.RestorePreviousSettings())
        {
            _log.Info("Proxy", "Previous proxy settings restored from the dashboard.");
        }
    }

    public void NotifyAllDashboardItems()
    {
        OnPropertyChanged(nameof(CurrentNetworkName));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LatencyText));
        OnPropertyChanged(nameof(LastCheckText));
        OnPropertyChanged(nameof(ModeText));
    }

    public void OnStateChanged()
    {
        NotifyAllDashboardItems();
        if (_state.Mode != SelectedMode)
        {
            SelectedMode = _state.Mode;
        }
    }

    public void ShowManualModes()
    {
        _ = RefreshManualProxiesAsync();
    }

    public CancellationToken ApplicationToken => _cts.Token;

    public void Shutdown()
    {
        _cts.Cancel();
    }
}