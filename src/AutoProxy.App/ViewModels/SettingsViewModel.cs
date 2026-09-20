using System.Windows;
using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Models;
using AutoProxy.Core.Monitoring;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoProxy.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsRepository _repo;
    private readonly IWindowsProxyManager _proxyManager;
    private readonly MonitoringService _monitoring;
    private readonly ILogService _log;
    private readonly CancellationTokenSource _cts = new();

    public SettingsViewModel(
        ISettingsRepository repo,
        IWindowsProxyManager proxyManager,
        MonitoringService monitoring,
        ILogService log)
    {
        _repo = repo;
        _proxyManager = proxyManager;
        _monitoring = monitoring;
        _log = log;
    }

    // Endpoints
    [ObservableProperty]
    private string probeUrls = string.Empty;

    [ObservableProperty]
    private string customProbeUrl = string.Empty;

    // Timing
    [ObservableProperty]
    private string directTimeoutText = "8";

    [ObservableProperty]
    private string proxyTimeoutText = "8";

    [ObservableProperty]
    private string healthCheckIntervalText = "60";

    [ObservableProperty]
    private string failureThresholdText = "3";

    [ObservableProperty]
    private string cooldownText = "30";

    // Behaviour
    [ObservableProperty]
    private bool ignoreCertificateErrors;

    [ObservableProperty]
    private bool startMonitoringOnLaunch = true;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private string? proxyNow;

    public async Task LoadAsync()
    {
        var settings = await _repo.LoadAsync(_cts.Token);
        ProbeUrls = string.Join(Environment.NewLine, settings.ProbeUrls);
        CustomProbeUrl = settings.CustomProbeUrl;
        DirectTimeoutText = (settings.DirectTestTimeoutMs / 1000).ToString();
        ProxyTimeoutText = (settings.ProxyTestTimeoutMs / 1000).ToString();
        HealthCheckIntervalText = settings.HealthCheckIntervalSeconds.ToString();
        FailureThresholdText = settings.FailureThreshold.ToString();
        CooldownText = settings.CooldownSeconds.ToString();
        IgnoreCertificateErrors = settings.IgnoreCertificateErrors;
        StartMonitoringOnLaunch = settings.StartMonitoringOnLaunch;
        ProxyNow = Describe(_proxyManager.GetCurrentSettings());
    }

    [RelayCommand]
    private async Task SaveAsync() => await SaveCoreAsync();

    [RelayCommand]
    private async Task SaveAndRestartDetectionAsync()
    {
        if (await SaveCoreAsync())
        {
            await _monitoring.TriggerDetectionAsync(
                force: true, reason: "settings saved", _cts.Token);
        }
    }

    private async Task<bool> SaveCoreAsync()
    {
        var settings = new AppSettings
        {
            ProbeUrls = (ProbeUrls ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(u => u.Trim())
                .Where(u => !string.IsNullOrEmpty(u))
                .ToArray(),
            CustomProbeUrl = (CustomProbeUrl ?? string.Empty).Trim(),
            DirectTestTimeoutMs = ParseSeconds(DirectTimeoutText, 8),
            ProxyTestTimeoutMs = ParseSeconds(ProxyTimeoutText, 8),
            HealthCheckIntervalSeconds = ParseSeconds(HealthCheckIntervalText, 60, min: 10),
            FailureThreshold = ParseSeconds(FailureThresholdText, 3, min: 1, max: 10),
            CooldownSeconds = ParseSeconds(CooldownText, 30),
            IgnoreCertificateErrors = IgnoreCertificateErrors,
            StartMonitoringOnLaunch = StartMonitoringOnLaunch,
        };

        // Validation: at least one probe URL and reasonable timeouts.
        if (settings.EffectiveProbeUrls().Count == 0)
        {
            StatusMessage = "At least one connectivity check endpoint is required.";
            return false;
        }

        if (settings.DirectTestTimeoutMs < 2000 || settings.ProxyTestTimeoutMs < 2000)
        {
            StatusMessage = "Timeouts must be at least 2 seconds.";
            return false;
        }

        await _repo.SaveAsync(settings, _cts.Token);
        _log.Info("Settings", "Settings saved.",
            $"{settings.EffectiveProbeUrls().Count} probe endpoints; " +
            $"health check every {settings.HealthCheckIntervalSeconds}s; " +
            $"failure threshold {settings.FailureThreshold}.");
        StatusMessage = "Settings saved.";
        return true;
    }

    [RelayCommand]
    private void RestorePreviousProxySettings()
    {
        if (_proxyManager.RestorePreviousSettings())
        {
            StatusMessage = "Previous proxy settings restored.";
            _log.Info("Proxy", "Previous proxy settings restored from Settings.");
            ProxyNow = Describe(_proxyManager.GetCurrentSettings());
        }
        else
        {
            StatusMessage = "No previous settings to restore.";
        }
    }

    private static int ParseSeconds(string text, int fallback, int min = 1, int max = 3600)
    {
        if (!int.TryParse(text, out var value))
            return fallback;
        return Math.Clamp(value, min, max);
    }

    private static string Describe(ProxySettings settings)
    {
        return settings.Enabled
            ? $"Enabled — {settings.Server}"
            : "Disabled (direct access)";
    }
}