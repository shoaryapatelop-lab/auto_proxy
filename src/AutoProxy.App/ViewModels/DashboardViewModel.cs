using System.Windows;
using AutoProxy.Core.Models;
using AutoProxy.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoProxy.App.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly AppStateService _state;

    public DashboardViewModel(AppStateService state)
    {
        _state = state;
        _state.Changed += OnAppStateChanged;
    }

    public Action<string>? NavigateAction { get; set; }

    public string CurrentNetworkName =>
        _state.LastResult?.Network?.DisplayName ?? "No network";

    public string StatusText => _state.StatusText;

    public string? CurrentProxy =>
        _state.ActiveProxy?.Name;

    public string LatencyText =>
        _state.LastResult is { LatencyMs: > 0 } r ? $"{r.LatencyMs} ms" : "—";

    public string ModeText => _state.Mode switch
    {
        AppMode.Automatic => "ON",
        _ => "OFF",
    };

    public string LastCheckText =>
        _state.LastCheck == DateTime.MinValue ? "—" : _state.LastCheck.ToString("HH:mm:ss");

    public string DetailText => _state.LastResult?.Detail ?? "Waiting for the first detection…";

    private void OnAppStateChanged(AppStateService state)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(CurrentNetworkName));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(CurrentProxy));
            OnPropertyChanged(nameof(LatencyText));
            OnPropertyChanged(nameof(ModeText));
            OnPropertyChanged(nameof(LastCheckText));
            OnPropertyChanged(nameof(DetailText));
        });
    }

    [RelayCommand]
    private void ManageProxies() => NavigateAction?.Invoke("Proxies");

    [RelayCommand]
    private void NetworkProfiles() => NavigateAction?.Invoke("Networks");

    [RelayCommand]
    private void ViewLogs() => NavigateAction?.Invoke("Logs");

    [RelayCommand]
    private void OpenSettings() => NavigateAction?.Invoke("Settings");
}