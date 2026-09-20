using System.Collections.ObjectModel;
using System.Windows;
using AutoProxy.App.Views;
using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Models;
using AutoProxy.Core.Network;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoProxy.App.ViewModels;

public class ProxyListItem
{
    public ProxyProfile Model { get; init; } = new();
    public string Name => Model.Name;
    public string Host => Model.Host;
    public string PortText => Model.Port.ToString();
    public string Address => Model.DisplayAddress;
    public string EnabledText => Model.Enabled ? "Enabled" : "Disabled";

    public string Status
    {
        get
        {
            if (!string.IsNullOrEmpty(Model.LastError)) return "Unavailable";
            return Model.LastSuccessfulTest is not null ? "Healthy" : "Never tested";
        }
    }

    public string LatencyText =>
        Model.AverageLatencyMs is > 0 ? $"{Model.AverageLatencyMs} ms" : "—";

    public string LastTestedText => (Model.LastSuccessfulTest ?? Model.LastFailedTest)
        ?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
}

public partial class ProxiesViewModel : ObservableObject
{
    private readonly IProxyRepository _repo;
    private readonly INetworkRepository _networks;
    private readonly IProxyTester _tester;
    private readonly ISettingsRepository _settings;
    private readonly ICredentialManager _credentials;
    private readonly INetworkMonitor _monitor;
    private readonly ILogService _log;
    private readonly CancellationTokenSource _cts = new();

    public ProxiesViewModel(
        IProxyRepository repo,
        INetworkRepository networks,
        IProxyTester tester,
        ISettingsRepository settings,
        ICredentialManager credentials,
        INetworkMonitor monitor,
        ILogService log)
    {
        _repo = repo;
        _networks = networks;
        _tester = tester;
        _settings = settings;
        _credentials = credentials;
        _monitor = monitor;
        _log = log;
    }

    public ObservableCollection<ProxyListItem> Proxies { get; } = new();

    [ObservableProperty]
    private ProxyListItem? selected;

    [ObservableProperty]
    private bool isLoading;

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var all = await _repo.GetAllAsync(_cts.Token);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Proxies.Clear();
                foreach (var proxy in all)
                {
                    Proxies.Add(new ProxyListItem { Model = proxy });
                }
            });
        }
        catch (Exception ex)
        {
            _log.Error("Proxies", "Could not load proxies.", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private Task<ProxyProfile?> ShowEditorAsync(ProxyProfile? original)
    {
        var dialog = new AddEditProxyDialog(original, _credentials, _log)
        {
            Owner = Application.Current.MainWindow,
        };
        return Task.FromResult(dialog.ShowDialogAndCapture());
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var result = await ShowEditorAsync(null);
        if (result is null) return;


        await _repo.AddAsync(result, _cts.Token);
        _log.Info("Proxies", $"Proxy added: {result.Name} ({result.DisplayAddress})",
            string.IsNullOrEmpty(result.CredentialTarget) ? null : "Credentials saved securely.");
        await LoadAsync();
    }

    [RelayCommand]
    private async Task EditAsync()
    {
        var item = Selected;
        if (item is null) return;

        var result = await ShowEditorAsync(item.Model);
        if (result is null) return;

        result.Id = item.Model.Id;
        result.SuccessCount = item.Model.SuccessCount;
        result.FailureCount = item.Model.FailureCount;
        result.AverageLatencyMs = item.Model.AverageLatencyMs;
        result.LastSuccessfulTest = item.Model.LastSuccessfulTest;
        result.LastFailedTest = item.Model.LastFailedTest;
        result.LastError = item.Model.LastError;

        await _repo.UpdateAsync(result, _cts.Token);
        _log.Info("Proxies", $"Proxy updated: {result.Name}");
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        var item = Selected;
        if (item is null) return;

        var confirm = MessageBox.Show(
            $"Delete proxy \"{item.Model.Name}\"?",
            "Delete proxy",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        if (!string.IsNullOrEmpty(item.Model.CredentialTarget))
            _credentials.DeleteCredential(item.Model.CredentialTarget);

        await _repo.DeleteAsync(item.Model.Id, _cts.Token);
        _log.Info("Proxies", $"Proxy deleted: {item.Model.Name}");
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ToggleAsync()
    {
        var item = Selected;
        if (item is null) return;

        item.Model.Enabled = !item.Model.Enabled;
        await _repo.UpdateAsync(item.Model, _cts.Token);
        _log.Info("Proxies",
            $"Proxy {(item.Model.Enabled ? "enabled" : "disabled")}: {item.Model.Name}");
        await LoadAsync();
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        var item = Selected;
        if (item is null) return;

        IsLoading = true;
        try
        {
            var settings = await _settings.LoadAsync(_cts.Token);
            var result = await _tester.TestAsync(item.Model, settings, _cts.Token);

            var model = await _repo.GetByIdAsync(item.Model.Id, _cts.Token);
            if (model is null) return;

            if (result.Success)
            {
                model.SuccessCount++;
                model.AverageLatencyMs = model.AverageLatencyMs is null
                    ? result.LatencyMs
                    : (model.AverageLatencyMs.Value * (model.SuccessCount - 1) + result.LatencyMs) /
                      model.SuccessCount;
                model.LastSuccessfulTest = DateTime.UtcNow;
                model.LastError = null;
                _log.Info("Proxies",
                    $"Proxy test succeeded: {model.Name} ({result.LatencyMs} ms)",
                    result.HttpStatus > 0 ? $"HTTP {result.HttpStatus}" : null);
            }
            else
            {
                model.FailureCount++;
                model.LastFailedTest = DateTime.UtcNow;
                model.LastError = result.Error ?? $"{result.Outcome}";
                _log.Warning("Proxies", $"Proxy test failed: {model.Name}",
                    $"{result.Outcome}: {result.Error}");
            }

            await _repo.UpdateAsync(model, _cts.Token);
        }
        catch (Exception ex)
        {
            _log.Error("Proxies", "Proxy test failed unexpectedly.", ex.Message);
        }
        finally
        {
            IsLoading = false;
            await LoadAsync();
        }
    }

    [RelayCommand]
    private async Task SetPreferredAsync()
    {
        var item = Selected;
        if (item is null)
        {
            _log.Warning("Proxies", "Select a proxy to set as preferred.");
            return;
        }

        var current = await _monitor.GetCurrentNetworkAsync(_cts.Token);
        if (current is null)
        {
            _log.Warning("Proxies", "No active network to associate a preferred proxy with.");
            return;
        }

        await _networks.SetPreferredModeAsync(
            current.Identity.Identifier, "Proxy", item.Model.Id, _cts.Token);
        _log.Info("Proxies",
            $"Preferred proxy set: {item.Model.Name} for {current.Identity.DisplayName}");
    }
}

/// <summary>Backing model for the Add/Edit proxy dialog window.</summary>
public class ProxyEditorModel
{
    private readonly ProxyProfile? _original;
    private readonly ICredentialManager _credentials;
    private readonly ILogService _log;

    public ProxyEditorModel(
        ProxyProfile? original, ICredentialManager credentials, ILogService log)
    {
        _original = original;
        _credentials = credentials;
        _log = log;

        Name = original?.Name ?? string.Empty;
        Host = original?.Host ?? string.Empty;
        PortText = (original?.Port ?? 8080).ToString();
        AuthenticationRequired = original?.AuthenticationRequired ?? false;
        Enabled = original?.Enabled ?? true;

        if (original is not null && original.AuthenticationRequired &&
            !string.IsNullOrEmpty(original.CredentialTarget))
        {
            var creds = credentials.ReadCredential(original.CredentialTarget);
            Username = creds?.Username ?? string.Empty;
        }
    }

    public bool IsEditing => _original is not null;
    public string Title => IsEditing ? "Edit Proxy" : "Add Proxy";

    // Bound fields (set by the window before validation)
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string PortText { get; set; } = "8080";
    public bool AuthenticationRequired { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    /// <summary>Human-readable reason the last <see cref="Validate"/> call failed.</summary>
    public string? ValidationError { get; private set; }

    /// <summary>Validates the input; returns the profile or null if invalid.</summary>
    public ProxyProfile? Validate()
    {
        var profile = new ProxyProfile
        {
            Name = Name?.Trim() ?? string.Empty,
            Host = Host?.Trim() ?? string.Empty,
            Port = int.TryParse(PortText, out var port) ? port : 0,
            AuthenticationRequired = AuthenticationRequired,
            Enabled = Enabled,
        };

        var error = ProxyValidator.Validate(profile);
        if (AuthenticationRequired && string.IsNullOrWhiteSpace(Username))
            error ??= "Username is required when authentication is enabled.";

        if (AuthenticationRequired && _original is null && string.IsNullOrWhiteSpace(Password))
            error ??= "Password is required when authentication is enabled.";

        ValidationError = error;
        return error is null ? profile : null;
    }

    /// <summary>
    /// Persists credentials to the secured store and attaches the credential
    /// target to the profile. Returns false if credential storage failed.
    /// </summary>
    public bool SaveCredentials(ProxyProfile profile)
    {
        if (AuthenticationRequired)
        {
            var target = _original?.CredentialTarget ?? $"AutoProxy:Proxy:{Guid.NewGuid():N}";
            var password = Password ?? string.Empty;

            // Editing without re-typing the password must not wipe the stored
            // secret: reuse the existing credential's password (and username).
            if (string.IsNullOrEmpty(password) && _original is not null)
            {
                var existing = _credentials.ReadCredential(target);
                if (existing is not null)
                {
                    password = existing.Value.Password;
                    if (string.IsNullOrWhiteSpace(Username))
                        Username = existing.Value.Username;
                }
            }

            if (!_credentials.SaveCredential(target, Username, password))
            {
                _log.Warning("Proxies",
                    "Could not save proxy credentials to the secure store.");
                return false;
            }

            profile.CredentialTarget = target;
        }
        else if (_original?.CredentialTarget is not null)
        {
            _credentials.DeleteCredential(_original.CredentialTarget);
        }

        return true;
    }

    public ProxyProfile ToProfile(long id)
    {
        var profile = Validate()!;
        profile.Id = id;
        return profile;
    }
}