using System.Collections.ObjectModel;
using System.Windows;
using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoProxy.App.ViewModels;

public class NetworkListItem
{
    public NetworkProfile Model { get; init; } = new();

    public string DisplayName =>
        Model.Ssid ?? (Model.InterfaceType ?? "Unknown") + (string.IsNullOrEmpty(Model.Gateway)
            ? ""
            : $" ({Model.Gateway})");

    public string InterfaceType => Model.InterfaceType ?? "Unknown";
    public string Mode => Model.PreferredMode;
    public string? PreferredProxyName { get; set; }
    public string LastSeenText => Model.LastSeen.ToString("yyyy-MM-dd HH:mm");
    public string NetworkId => Model.NetworkIdentifier;
}

public partial class NetworksViewModel : ObservableObject
{
    private readonly INetworkRepository _repo;
    private readonly IProxyRepository _proxies;
    private readonly ILogService _log;
    private readonly CancellationTokenSource _cts = new();

    public NetworksViewModel(
        INetworkRepository repo,
        IProxyRepository proxies,
        ILogService log)
    {
        _repo = repo;
        _proxies = proxies;
        _log = log;
    }

    public ObservableCollection<NetworkListItem> Networks { get; } = new();

    [ObservableProperty]
    private NetworkListItem? selected;

    [ObservableProperty]
    private IReadOnlyList<ProxyProfile> allProxies = Array.Empty<ProxyProfile>();

    [ObservableProperty]
    private ProxyProfile? preferredProxyChoice;

    public async Task LoadAsync()
    {
        try
        {
            var proxyList = await _proxies.GetAllAsync(_cts.Token);
            var networkList = await _repo.GetAllAsync(_cts.Token);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                AllProxies = proxyList;
                Networks.Clear();
                foreach (var network in networkList)
                {
                    var item = new NetworkListItem { Model = network };
                    item.PreferredProxyName = proxyList
                        .FirstOrDefault(p => p.Id == network.PreferredProxyId)?.Name;
                    Networks.Add(item);
                }
            });
        }
        catch (Exception ex)
        {
            _log.Error("Networks", "Could not load network profiles.", ex.Message);
        }
    }

    [RelayCommand]
    private async Task SetDirectPreferredAsync()
    {
        var item = Selected;
        if (item is null) return;

        await _repo.SetPreferredModeAsync(
            item.NetworkId, "Direct", null, _cts.Token);
        _log.Info("Networks",
            $"Remembered: {item.DisplayName} → Direct mode");
        await LoadAsync();
    }

    [RelayCommand]
    private async Task SetProxyPreferredAsync()
    {
        var item = Selected;
        var proxy = PreferredProxyChoice;
        if (item is null)
        {
            _log.Warning("Networks", "Select a network profile first.");
            return;
        }

        if (proxy is null)
        {
            _log.Warning("Networks", "Select a proxy to associate with this network.");
            return;
        }

        await _repo.SetPreferredModeAsync(item.NetworkId, "Proxy", proxy.Id, _cts.Token);
        _log.Info("Networks",
            $"Remembered: {item.DisplayName} → Proxy ({proxy.Name})");
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ForgetAsync()
    {
        var item = Selected;
        if (item is null) return;

        var confirm = MessageBox.Show(
            $"Forget this network profile?\n\n{item.DisplayName}",
            "Forget network",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        await _repo.DeleteByIdentifierAsync(item.NetworkId, _cts.Token);
        _log.Info("Networks", $"Network profile forgotten: {item.DisplayName}");
        await LoadAsync();
    }
}