using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Apply;
using AutoProxy.Core.Detection;
using AutoProxy.Core.Models;
using AutoProxy.Core.Services;

namespace AutoProxy.Core.Monitoring;

public class MonitoringService : IAsyncDisposable
{
    private readonly INetworkMonitor _monitor;
    private readonly DetectionEngine _detector;
    private readonly IConnectivityTester _connectivity;
    private readonly IProxyTester _proxyTester;
    private readonly IWindowsProxyManager _proxyManager;
    private readonly ApplyEngine _apply;
    private readonly ISettingsRepository _settingsRepo;
    private readonly ILogService _log;
    private readonly AppStateService _state;
    private readonly SemaphoreSlim _detectLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _stopped;
    private string _lastAppliedPreset = "none";
    private RetryPolicy? _signalFailures;
    private long _lastKnownLatency;
    private DateTime _lastDetectionAt = DateTime.MinValue;

    private int _rerunPending;
    private CancellationTokenSource? _activeDetectionCts;
    private Task? _runningDetection;

    public MonitoringService(
        INetworkMonitor monitor,
        DetectionEngine detector,
        IConnectivityTester connectivity,
        IProxyTester proxyTester,
        IWindowsProxyManager proxyManager,
        ApplyEngine apply,
        ISettingsRepository settingsRepo,
        ILogService log,
        AppStateService state)
    {
        _monitor = monitor;
        _detector = detector;
        _connectivity = connectivity;
        _proxyTester = proxyTester;
        _proxyManager = proxyManager;
        _apply = apply;
        _settingsRepo = settingsRepo;
        _log = log;
        _state = state;
    }

    public async Task StartAsync(CancellationToken appStopping)
    {
        if (_stopped) throw new InvalidOperationException("MonitoringService was already stopped.");

        _proxyManager.RestorePreviousSettingsOnStartup();
        await _monitor.StartAsync(appStopping);

        _monitor.NetworkChanged += OnNetworkChanged;
        _proxyManager.WindowsProxyChanged += OnWindowsProxyChanged;
        _state.Changed += OnStateChanged;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(appStopping);
        _loop = Task.Run(() => MonitorLoopAsync(_cts.Token));

        _log.Info("Monitor", "Monitoring started. Automatic mode enabled.");

        var settings = await _settingsRepo.LoadAsync(appStopping);
        if (settings.StartMonitoringOnLaunch)
        {
            await TriggerDetectionAsync(force: true, reason: "Startup", appStopping);
        }
    }

    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;

        _state.Changed -= OnStateChanged;
        _monitor.NetworkChanged -= OnNetworkChanged;
        _proxyManager.WindowsProxyChanged -= OnWindowsProxyChanged;

        _cts?.Cancel();
        _activeDetectionCts?.Cancel();
        await _monitor.StopAsync();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }

        // Wait for any in-flight detection to finish. This guarantees a
        // detection cannot re-apply Windows proxy settings AFTER we restore
        // the previous configuration below.
        await _detectLock.WaitAsync();
        _detectLock.Release();

        _log.Info("Monitor", "Restoring previous Windows proxy settings before exit…");
        try
        {
            await Task.Run(_proxyManager.RestorePreviousSettings);
        }
        catch (Exception ex)
        {
            _log.Error("Monitor", "Failed to restore previous proxy settings.", ex.Message);
        }

        _cts?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _detectLock.Dispose();
    }

    /// <summary>Schedules a detection run. Manual mode blocks auto runs unless forced.</summary>
    public async Task TriggerDetectionAsync(
        bool force, string reason, CancellationToken cancellationToken)
    {
        if (_stopped) return;

        if (_state.Mode != AppMode.Automatic && !force)
        {
            _log.Info("Monitor",
                $"Manual mode active — ignoring automatic detection ({reason}).");
            return;
        }

        if (!force)
        {
            var cooldown = await GetCooldownAsync(cancellationToken);
            var elapsed = DateTime.UtcNow - _lastDetectionAt;
            if (cooldown > TimeSpan.Zero && elapsed < cooldown)
            {
                _log.Info("Monitor",
                    $"Within cooldown ({(int)(cooldown - elapsed).TotalSeconds}s remaining) " +
                    $"— skipping detection ({reason}).");
                return;
            }
        }

        if (!await _detectLock.WaitAsync(0, cancellationToken))
        {
            // A detection is already running. Mark a re-run and cancel the
            // in-flight detection so the newest trigger wins (stale-result
            // protection). The runner re-checks as soon as it releases the lock.
            Interlocked.Exchange(ref _rerunPending, 1);
            _activeDetectionCts?.Cancel();
            _log.Verbose("Monitor",
                $"Detection already running; queued re-run ({reason}).");
            return;
        }

        try
        {
            _runningDetection = RunDetectionLoopAsync(async token =>
            {
                _state.BeginDetecting();
                _log.Info("Monitor", $"Detection triggered: {reason}.");
                var result = await _detector.DetectAsync(token);
                await ApplyDecisionAsync(result, token);
            }, cancellationToken);
            await _runningDetection;
        }
        finally
        {
            _runningDetection = null;
            _detectLock.Release();
        }
    }

    /// <summary>Test the connection right now without changing mode.</summary>
    public async Task<DetectionResult> ManualTestNowAsync(CancellationToken cancellationToken)
    {
        if (_stopped)
        {
            return new DetectionResult
            {
                State = ConnectionState.Error,
                Detail = "Monitoring is stopped.",
            };
        }

        await _detectLock.WaitAsync(cancellationToken);
        try
        {
            DetectionResult? latest = null;
            _runningDetection = RunDetectionLoopAsync(async token =>
            {
                _state.BeginDetecting();
                latest = await _detector.DetectAsync(token);

                if (latest.NetworkChangedDuringTest)
                {
                    _log.Warning("Monitor",
                        "Network changed during the manual test — result discarded, re-running.");
                    Interlocked.Exchange(ref _rerunPending, 1);
                    latest = null;
                }
                else
                {
                    _state.CommitResult(latest);
                }
            }, cancellationToken);
            await _runningDetection;
            return latest ?? new DetectionResult
            {
                State = ConnectionState.Error,
                Detail = "Detection did not complete.",
            };
        }
        finally
        {
            _runningDetection = null;
            _detectLock.Release();
        }
    }

    /// <summary>
    /// Runs a single detection iteration and re-runs it when a newer trigger
    /// arrived while it was in flight (the newer trigger cancels the active
    /// run). This guarantees the newest detection wins and a stale result can
    /// never be the last word on the system state.
    /// </summary>
    private async Task RunDetectionLoopAsync(
        Func<CancellationToken, Task> iteration, CancellationToken callerToken)
    {
        while (true)
        {
            if (callerToken.IsCancellationRequested) return;

            Interlocked.Exchange(ref _rerunPending, 0);
            var runCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            var previous = Interlocked.Exchange(ref _activeDetectionCts, runCts);
            previous?.Dispose();

            try
            {
                await iteration(runCts.Token);
            }
            catch (OperationCanceledException)
            {
                _log.Warning("Monitor",
                    "Detection cancelled/superseded — re-running for the newest state.");
            }
            catch (Exception ex)
            {
                _log.Error("Detect", "Detection failed unexpectedly.", ex.ToString());
                _state.ApplyError($"Detection failed: {ex.Message}");
            }
            finally
            {
                Interlocked.CompareExchange(ref _activeDetectionCts, null, runCts);
                runCts.Dispose();
            }

            if (Volatile.Read(ref _rerunPending) == 0) return;
        }
    }

    /// <summary>
    /// Switches between Automatic and the Manual override modes. Manual modes
    /// suspend automated switching until Automatic is selected again.
    /// </summary>
    public async Task SetModeAsync(AppMode mode, ProxyProfile? manualProxy, CancellationToken cancellationToken)
    {
        _log.Info("Monitor", $"Switching mode to {mode}.",
            manualProxy is null ? null : $"Proxy: {manualProxy.Name}");

        switch (mode)
        {
            case AppMode.Automatic:
                _state.SetMode(AppMode.Automatic);
                await TriggerDetectionAsync(force: true, reason: "automatic mode enabled", cancellationToken);
                break;

            case AppMode.ManualDirect:
                _state.SetMode(AppMode.ManualDirect);
                _log.Info("Proxy", "Manual mode — forcing Direct (no proxy).");
                _proxyManager.DisableProxy();
                _state.CommitResult(new DetectionResult
                {
                    State = ConnectionState.Direct,
                    Detail = "Manual Direct mode — Windows proxy disabled.",
                    Timestamp = DateTime.UtcNow,
                });
                break;

            case AppMode.ManualProxy when manualProxy is not null:
                _state.SetMode(AppMode.ManualProxy, manualProxy);
                _log.Info("Proxy",
                    $"Manual mode — applying proxy {manualProxy.Name} ({manualProxy.DisplayAddress}).");
                _proxyManager.EnableProxy(manualProxy.Host, manualProxy.Port);
                _state.CommitResult(new DetectionResult
                {
                    State = ConnectionState.Proxy,
                    SelectedProxy = manualProxy,
                    Detail = $"Manual Proxy mode — {manualProxy.DisplayAddress} applied.",
                    Timestamp = DateTime.UtcNow,
                });
                break;

            case AppMode.ManualProxy:
                _log.Warning("Manual", "Manual proxy mode requires a selected proxy.");
                break;

            case AppMode.ManualDisable:
            default:
                _state.SetMode(AppMode.ManualDisable);
                _log.Info("Proxy", "Manual mode — disabling the Windows proxy.");
                _proxyManager.DisableProxy();
                _state.CommitResult(new DetectionResult
                {
                    State = ConnectionState.Direct,
                    Detail = "Manual mode — Windows proxy disabled.",
                    Timestamp = DateTime.UtcNow,
                });
                break;
        }
    }

    private async Task ApplyDecisionAsync(DetectionResult result, CancellationToken cancellationToken)
    {
        if (result.NetworkChangedDuringTest)
        {
            _log.Warning("Monitor",
                "Network changed while detection was running — discarding the stale result.");
            Interlocked.Exchange(ref _rerunPending, 1);
            return;
        }

        if (_state.Mode != AppMode.Automatic)
        {
            _log.Info("Monitor",
                "Manual mode — detection result recorded but not applied to the system.");
            _state.CommitResult(result);
            return;
        }

        // Detecting / Error carry no actionable system-proxy change.
        if (result.State is ConnectionState.Detecting or ConnectionState.Error)
        {
            _log.Warning("Monitor",
                $"Detection finished in {result.State} — leaving Windows proxy settings unchanged.");
            _state.CommitResult(result);
            _lastDetectionAt = DateTime.UtcNow;
            return;
        }

        var settings = await _settingsRepo.LoadAsync(cancellationToken);
        var outcome = await _apply.ApplyDesiredAsync(
            result.State, result.SelectedProxy, settings, cancellationToken);

        if (!outcome.Success)
        {
            var error = $"Failed to apply {result.State}: {outcome.Error}";
            if (outcome.RolledBack) error += " (previous settings restored).";
            else error += " (ROLLBACK FAILED — restore the system proxy manually).";
            _log.Error("Monitor", error);
            _state.ApplyError(error);
            _lastDetectionAt = DateTime.UtcNow;
            return;
        }

        _state.CommitResult(result);
        _lastDetectionAt = DateTime.UtcNow;
        _lastAppliedPreset = result.State.ToString();
        _log.Info("Verify", "Connection verified.",
            $"Final state: {result.State}. Windows proxy: {DescribeProxySettings()}");
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var settings = await _settingsRepo.LoadAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(settings.HealthCheckIntervalSeconds), cancellationToken);
                await HealthCheckAsync(settings, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A transient database/settings error must not silently kill
                // the monitoring loop.
                _log.Warning("Monitor", "Health loop iteration failed.", ex.Message);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    internal async Task HealthCheckAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (_state.Mode != AppMode.Automatic)
            return;

        var current = _state.LastResult;
        if (current is null)
            return;

        if (current.State is ConnectionState.Offline or ConnectionState.CaptivePortal or
            ConnectionState.Error or ConnectionState.Detecting)
        {
            // Wait for a network change event rather than probing from a broken state.
            return;
        }

        bool healthy;
        long latency = _lastKnownLatency;
        if (current.State == ConnectionState.Direct)
        {
            var direct = await _connectivity.TestDirectAsync(settings, cancellationToken);
            healthy = direct.InternetAvailable;
            latency = direct.BestLatencyMs;
        }
        else if (current.State == ConnectionState.Proxy && current.SelectedProxy is not null)
        {
            var (serving, ms) = await _proxyTester.GetProxyHealthAsync(
                current.SelectedProxy, settings, cancellationToken);
            healthy = serving;
            latency = ms;
        }
        else
        {
            return;
        }

        if (healthy)
        {
            if (_signalFailures is not null) _signalFailures.Reset();
            _lastKnownLatency = latency;
            return;
        }

        _signalFailures ??= new RetryPolicy(settings.FailureThreshold);
        var escalated = _signalFailures.RecordFailure();
        _log.Warning("Health",
            $"Health check failed ({current.State}, " +
            $"{_signalFailures.ConsecutiveFailures}/{_signalFailures.FailureThreshold}).");

        if (escalated)
        {
            _signalFailures.Reset();
            _log.Info("Health", "Failure threshold reached — running full re-detection.");
            await TriggerDetectionAsync(force: true, reason: "health threshold exceeded", cancellationToken);
            return;
        }

        // Slow signal: also surface any external proxy setting drift.
        VerifyExternalProxyDrift(current);
    }

    private void VerifyExternalProxyDrift(DetectionResult current)
    {
        try
        {
            var actual = _proxyManager.GetCurrentSettings();
            var expected = current.State switch
            {
                ConnectionState.Proxy => ProxySettings.FromProxy(
                    current.SelectedProxy!.Host, current.SelectedProxy.Port),
                ConnectionState.Direct => ProxySettings.Disabled(),
                _ => null,
            };

            if (expected is null) return;
            if (SettingsEqual(actual, expected)) return;

            _log.Warning("Proxy",
                "Windows proxy settings were changed externally — restoring expected state.",
                $"Expected: ({expected.Enabled}, {expected.Server}). Actual: " +
                $"({actual.Enabled}, {actual.Server}).");

            if (current.State == ConnectionState.Proxy)
                _proxyManager.EnableProxy(current.SelectedProxy!.Host, current.SelectedProxy.Port);
            else
                _proxyManager.DisableProxy();
        }
        catch (Exception ex)
        {
            _log.Warning("Proxy", "Could not verify Windows proxy settings.", ex.Message);
        }
    }

    private string DescribeProxySettings()
    {
        var s = _proxyManager.GetCurrentSettings();
        return s.Enabled ? $"proxy {s.Server}" : "direct (no proxy)";
    }

    private async Task<TimeSpan> GetCooldownAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _settingsRepo.LoadAsync(cancellationToken);
            return TimeSpan.FromSeconds(Math.Max(0, settings.CooldownSeconds));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    private static bool SettingsEqual(ProxySettings? a, ProxySettings? b)
    {
        if (a is null || b is null) return false;
        return a.Enabled == b.Enabled &&
               string.Equals(a.Server, b.Server, StringComparison.OrdinalIgnoreCase);
    }

    private async void OnNetworkChanged(object? sender, NetworkChangedEventArgs e)
    {
        _log.Info("Monitor", "Network change detected — re-running detection.");
        try
        {
            await TriggerDetectionAsync(
                force: true, reason: "network change", CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.Error("Monitor", "Network-change handling failed.", ex.Message);
        }
    }

    private async void OnWindowsProxyChanged(object? sender, EventArgs e)
    {
        _log.Info("Proxy", "Windows proxy configuration changed outside AutoProxy.");
        if (_state.Mode == AppMode.Automatic)
        {
            try
            {
                await TriggerDetectionAsync(
                    force: false, reason: "external proxy change", CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.Error("Monitor", "External proxy-change handling failed.", ex.Message);
            }
        }
    }

    private void OnStateChanged(AppStateService state)
    {
        if (state.Mode != AppMode.Automatic)
        {
            _log.Info("Monitor", $"Mode set to {state.Mode}. Automatic switching is suspended.");
        }
    }
}