using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Models;
using AutoProxy.Core.Monitoring;
using AutoProxy.Core.Services;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace AutoProxy.App.Services;

/// <summary>
/// Owns the system-tray icon, its dynamic context menu, state-driven icon/balloon
/// updates and the manual-override commands. Runs headless (no host window).
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly AppStateService _state;
    private readonly MonitoringService _monitoring;
    private readonly IProxyRepository _proxyRepo;
    private readonly ILogService _log;
    private readonly CancellationTokenSource _cts = new();

    private TaskbarIcon? _icon;
    private IReadOnlyList<ProxyProfile> _proxies = Array.Empty<ProxyProfile>();
    private ConnectionState _lastState = ConnectionState.Detecting;
    private bool _initialized;
    private bool _disposed;

    public TrayService(
        AppStateService state,
        MonitoringService monitoring,
        IProxyRepository proxyRepo,
        ILogService log)
    {
        _state = state;
        _monitoring = monitoring;
        _proxyRepo = proxyRepo;
        _log = log;
    }

    /// <summary>Raised when the user asks to bring the main window to the front.</summary>
    public event EventHandler? ShowRequested;

    /// <summary>Raised when the user chooses Quit from the tray menu.</summary>
    public event EventHandler? ExitRequested;

    public void Initialize(CancellationToken appToken)
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            _icon = new TaskbarIcon
            {
                ToolTipText = "AutoProxy",
                NoLeftClickDelay = true,
            };

            _icon.TrayLeftMouseUp += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
            _icon.TrayMouseDoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

            ApplyIcon(_state.State);
            RebuildMenu();
            _icon.ForceCreate();

            _state.Changed += OnStateChanged;
            _ = RefreshProxiesAsync();
            _ = appToken; // app lifetime is managed by the owner service

            _log.Info("Tray", "System-tray icon created.");
        }
        catch (Exception ex)
        {
            _log.Error("Tray", "Failed to create the system-tray icon.", ex.Message);
        }
    }

    private async Task RefreshProxiesAsync()
    {
        try
        {
            _proxies = await _proxyRepo.GetEnabledAsync(_cts.Token);
            await Application.Current.Dispatcher.InvokeAsync(RebuildMenu);
        }
        catch (Exception ex)
        {
            _log.Warning("Tray", "Could not refresh proxy menu.", ex.Message);
        }
    }

    private void OnStateChanged(AppStateService state)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            ApplyIcon(state.State);
            RebuildMenu();
            NotifyTransition(state.State);

            if (_icon is not null)
                _icon.ToolTipText = $"AutoProxy — {state.StatusText}";
        });
    }

    private void ApplyIcon(ConnectionState state)
    {
        if (_icon is null) return;

        var file = state switch
        {
            ConnectionState.Direct => "tray-direct.ico",
            ConnectionState.Proxy => "tray-proxy.ico",
            ConnectionState.Offline => "tray-offline.ico",
            ConnectionState.CaptivePortal => "tray-error.ico",
            ConnectionState.Error => "tray-error.ico",
            _ => "tray-detecting.ico",
        };

        var source = LoadIcon(file);
        if (source is not null)
            _icon.IconSource = source;
    }

    private static BitmapImage? LoadIcon(string fileName)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "tray", fileName);
            if (!File.Exists(path)) return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void RebuildMenu()
    {
        if (_icon is null) return;

        var menu = new ContextMenu();

        var header = new MenuItem
        {
            Header = _state.StatusText,
            IsEnabled = false,
        };
        menu.Items.Add(header);
        menu.Items.Add(new Separator());

        menu.Items.Add(Checkable("Automatic", AppMode.Automatic, _state.Mode == AppMode.Automatic,
            () => { _ = SetModeAsync(AppMode.Automatic, null); }));
        menu.Items.Add(Checkable("Direct (no proxy)", AppMode.ManualDirect,
            _state.Mode == AppMode.ManualDirect,
            () => { _ = SetModeAsync(AppMode.ManualDirect, null); }));

        var proxyMenu = new MenuItem
        {
            Header = "Use proxy",
            IsChecked = _state.Mode == AppMode.ManualProxy,
            IsCheckable = false,
        };
        if (_proxies.Count == 0)
        {
            proxyMenu.Items.Add(new MenuItem { Header = "(no enabled proxies)", IsEnabled = false });
        }
        else
        {
            foreach (var proxy in _proxies)
            {
                var captured = proxy;
                var item = new MenuItem
                {
                    Header = $"{proxy.Name}  ({proxy.DisplayAddress})",
                    IsCheckable = true,
                    IsChecked = _state.Mode == AppMode.ManualProxy &&
                                _state.ActiveProxy?.Id == proxy.Id,
                };
                item.Click += (_, _) => _ = SetModeAsync(AppMode.ManualProxy, captured);
                proxyMenu.Items.Add(item);
            }
        }

        menu.Items.Add(proxyMenu);
        menu.Items.Add(Checkable("Disable proxy (manual)", AppMode.ManualDisable,
            _state.Mode == AppMode.ManualDisable,
            () => { _ = SetModeAsync(AppMode.ManualDisable, null); }));

        menu.Items.Add(new Separator());

        var test = new MenuItem { Header = "Test connection now" };
        test.Click += (_, _) => _ = TestNowAsync();
        menu.Items.Add(test);

        var dashboard = new MenuItem { Header = "Open dashboard" };
        dashboard.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(dashboard);

        var exit = new MenuItem { Header = "Quit AutoProxy" };
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(exit);

        _icon.ContextMenu = menu;
    }

    private MenuItem Checkable(string header, AppMode mode, bool isChecked, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            IsCheckable = true,
            IsChecked = isChecked,
        };
        item.Click += (_, _) => action();
        return item;
    }

    private async Task SetModeAsync(AppMode mode, ProxyProfile? proxy)
    {
        try
        {
            await _monitoring.SetModeAsync(mode, proxy, _cts.Token);
        }
        catch (Exception ex)
        {
            _log.Error("Tray", $"Could not switch to {mode}.", ex.Message);
        }
    }

    private async Task TestNowAsync()
    {
        try
        {
            var result = await _monitoring.ManualTestNowAsync(_cts.Token);
            var message = result.State switch
            {
                ConnectionState.Direct => $"Direct connection works ({result.LatencyMs} ms).",
                ConnectionState.Proxy => $"Proxy works: {result.SelectedProxy?.Name}.",
                ConnectionState.Offline => "No internet connectivity detected.",
                ConnectionState.CaptivePortal => "A captive portal is blocking the connection.",
                _ => result.Detail,
            };

            Notify("Connection test", message, IconFor(result.State));
        }
        catch (Exception ex)
        {
            _log.Error("Tray", "Manual connection test failed.", ex.Message);
        }
    }

    private void NotifyTransition(ConnectionState state)
    {
        if (state == _lastState) return;
        var previous = _lastState;
        _lastState = state;

        // Only surface meaningful, user-visible transitions.
        var notify = state switch
        {
            ConnectionState.Proxy => "Proxy connection active.",
            ConnectionState.Offline => "Internet connection lost.",
            ConnectionState.CaptivePortal => "Captive portal detected — open your browser to sign in.",
            _ => null,
        };

        if (notify is null) return;
        if (previous == ConnectionState.Offline && state == ConnectionState.Proxy)
            notify = "Connection restored — proxy active.";

        Notify("AutoProxy", notify, IconFor(state));
    }

    private void Notify(string title, string message, NotificationIcon icon)
    {
        try
        {
            _icon?.ShowNotification(title, message, icon, timeout: TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _log.Verbose("Tray", "Could not show tray notification.", ex.Message);
        }
    }

    private static NotificationIcon IconFor(ConnectionState state) => state switch
    {
        ConnectionState.Proxy or ConnectionState.Direct => NotificationIcon.Info,
        ConnectionState.Offline or ConnectionState.CaptivePortal => NotificationIcon.Warning,
        ConnectionState.Error => NotificationIcon.Error,
        _ => NotificationIcon.None,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _state.Changed -= OnStateChanged;
        _cts.Cancel();

        try
        {
            _icon?.Dispose();
        }
        catch
        {
            // ignored
        }

        _cts.Dispose();
    }
}