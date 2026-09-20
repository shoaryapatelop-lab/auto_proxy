using System.Windows;
using AutoProxy.App.Services;
using AutoProxy.App.ViewModels;
using AutoProxy.App.Views;
using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Monitoring;
using Microsoft.Extensions.DependencyInjection;

namespace AutoProxy.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private TrayService? _tray;
    private MainWindow? _mainWindow;
    private MonitoringService? _monitoring;
    private readonly CancellationTokenSource _appCts = new();
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            _services = CompositionRoot.Build();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"AutoProxy failed to start.\n\n{ex.Message}",
                "AutoProxy", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _tray = _services.GetRequiredService<TrayService>();
        _tray.ShowRequested += (_, _) => ShowMainWindow();
        _tray.ExitRequested += async (_, _) => await ExitAsync();
        _tray.Initialize(_appCts.Token);

        var mainViewModel = _services.GetRequiredService<MainViewModel>();
        _mainWindow = new MainWindow(mainViewModel);
        _mainWindow.Show();

        _monitoring = _services.GetRequiredService<MonitoringService>();

        // --no-monitor: open the UI without touching the system proxy settings.
        var safeStart = e.Args.Any(a =>
            string.Equals(a, "--no-monitor", StringComparison.OrdinalIgnoreCase));

        if (safeStart)
        {
            _services.GetRequiredService<ILogService>()
                .Info("App", "Started with monitoring disabled (--no-monitor).");
        }
        else
        {
            _ = StartMonitoringAsync();
        }
    }

    private async Task StartMonitoringAsync()
    {
        try
        {
            await _monitoring!.StartAsync(_appCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Background monitoring could not be started.\n\n{ex.Message}",
                "AutoProxy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null) return;

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;

        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;

        try
        {
            if (_monitoring is not null)
                await _monitoring.StopAsync();
        }
        catch (Exception)
        {
            // Settings restoration is best-effort; proceed with exit.
        }

        _mainWindow?.AllowClose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_exiting)
        {
            try
            {
                _monitoring?.StopAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // ignored
            }
        }

        _tray?.Dispose();
        _appCts.Dispose();
        _services?.Dispose();
        base.OnExit(e);
    }
}