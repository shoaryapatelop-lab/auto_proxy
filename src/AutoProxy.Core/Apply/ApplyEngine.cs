using System.Diagnostics;
using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Models;

namespace AutoProxy.Core.Apply;

/// <summary>
/// Describes the result of applying a desired system-proxy state, including
/// whether the change was read back correctly, connectivity was verified
/// (for proxies), and whether a rollback was necessary.
/// </summary>
public record ApplyOutcome
{
    public bool Success { get; init; }
    public ConnectionState DesiredState { get; init; }
    public ProxyProfile? Proxy { get; init; }
    public ProxySettings DesiredSettings { get; init; } = ProxySettings.Disabled();
    public ProxySettings? ReadBackSettings { get; init; }
    public bool ConnectivityVerified { get; init; }
    public long ApplyLatencyMs { get; init; }
    public string? Error { get; init; }
    public bool RolledBack { get; init; }

    public string Describe()
    {
        if (Success)
        {
            var verified = DesiredState == ConnectionState.Proxy
                ? ConnectivityVerified
                    ? ", proxy traffic verified"
                    : " (verification skipped)"
                : string.Empty;
            return $"applied {DesiredState} ({DesiredSettings}){verified} in {ApplyLatencyMs} ms";
        }

        var rollbackText = RolledBack
            ? "previous settings restored"
            : "rollback FAILED — restore manually required";
        return $"FAILED to apply {DesiredState}: {Error} ({rollbackText})";
    }
}

/// <summary>
/// Safely applies a desired system-proxy state that a detection decision
/// produced. Sequence: snapshot → apply → read-back verify → (proxy only)
/// connectivity verify → commit. Any failure restores the previous snapshot.
/// Disabling (Direct / Offline / CaptivePortal) is read-back verified only —
/// connectivity checks are not run to avoid state flapping.
/// </summary>
public class ApplyEngine
{
    private readonly IWindowsProxyManager _proxyManager;
    private readonly IProxyTester _proxyTester;
    private readonly ILogService _log;

    public ApplyEngine(
        IWindowsProxyManager proxyManager,
        IProxyTester proxyTester,
        ILogService log)
    {
        _proxyManager = proxyManager;
        _proxyTester = proxyTester;
        _log = log;
    }

    public async Task<ApplyOutcome> ApplyDesiredAsync(
        ConnectionState desiredState,
        ProxyProfile? proxy,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var desired = BuildDesiredSettings(desiredState, proxy);
        if (desired is null)
        {
            var error = $"Cannot apply {desiredState}: no proxy profile supplied.";
            _log.Error("Apply", error);
            return new ApplyOutcome { Success = false, DesiredState = desiredState, Error = error };
        }

        if (SettingsEqual(_proxyManager.GetCurrentSettings(), desired))
        {
            _log.Info("Apply", $"Already in desired state: {desiredState} ({desired}).");
            return new ApplyOutcome
            {
                Success = true,
                DesiredState = desiredState,
                Proxy = proxy,
                DesiredSettings = desired,
                ReadBackSettings = desired,
            };
        }

        _proxyManager.SaveSnapshot(force: false);
        var stopwatch = Stopwatch.StartNew();

        bool applyOk = desiredState == ConnectionState.Proxy
            ? _proxyManager.EnableProxy(proxy!.Host, proxy.Port)
            : _proxyManager.DisableProxy();

        if (!applyOk)
        {
            stopwatch.Stop();
            var failure = new ApplyOutcome
            {
                Success = false,
                DesiredState = desiredState,
                Proxy = proxy,
                DesiredSettings = desired,
                Error = "Windows proxy manager failed to apply the change.",
            };

            if (_proxyManager.RestorePreviousSettings())
            {
                return failure with { RolledBack = true, ApplyLatencyMs = stopwatch.ElapsedMilliseconds };
            }

            _log.Error("Apply",
                "Apply failed AND rollback failed — the system proxy needs manual attention.",
                $"Previous configuration was: {_proxyManager.GetExpectedSettings()?.ToString() ?? "unknown"}.",
                null, proxy?.Id);
            return failure with { ApplyLatencyMs = stopwatch.ElapsedMilliseconds };
        }

        // Allow the registry update and system notification to settle.
        try
        {
            await Task.Delay(300, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return await RollbackAsync(stopwatch, desiredState, proxy, desired,
                "Apply cancelled before verification.");
        }

        var actual = _proxyManager.GetCurrentSettings();
        if (!SettingsEqual(desired, actual))
        {
            _log.Error("Apply",
                "Read-back verification failed — system proxy did not take the expected value.",
                $"Expected: ({desired.Enabled}, {desired.Server}). Actual: ({actual.Enabled}, {actual.Server}).",
                null, proxy?.Id);
            return await RollbackAsync(stopwatch, desiredState, proxy, desired,
                $"read-back mismatch: expected ({desired.Enabled}, {desired.Server}), " +
                $"got ({actual.Enabled}, {actual.Server})");
        }

        bool connectivityVerified = false;
        if (desiredState == ConnectionState.Proxy)
        {
            if (settings.VerifyProxyAfterApply)
            {
                var (serving, latencyMs) = await _proxyTester.GetProxyHealthAsync(
                    proxy!, settings, cancellationToken);
                if (!serving)
                {
                    var message = "Proxy applied but traffic verification through it failed.";
                    _log.Error("Apply", message,
                        "The proxy is configured but not currently serving traffic.", null, proxy?.Id);
                    return await RollbackAsync(stopwatch, desiredState, proxy, desired, message);
                }

                connectivityVerified = true;
                _log.Info("Apply",
                    $"Proxy traffic verified through {proxy!.Name} ({latencyMs} ms).",
                    null, null, proxy?.Id);
            }
            else
            {
                _log.Verbose("Apply", "Proxy traffic verification disabled by settings.");
            }
        }

        stopwatch.Stop();
        _log.Info("Apply", $"Verified: system proxy matches {desiredState}.",
            $"{desired} read-back OK; applied in {stopwatch.ElapsedMilliseconds} ms.", null, proxy?.Id);
        return new ApplyOutcome
        {
            Success = true,
            DesiredState = desiredState,
            Proxy = proxy,
            DesiredSettings = desired,
            ReadBackSettings = actual,
            ConnectivityVerified = connectivityVerified,
            ApplyLatencyMs = stopwatch.ElapsedMilliseconds,
        };
    }

    private async Task<ApplyOutcome> RollbackAsync(
        Stopwatch stopwatch,
        ConnectionState desiredState,
        ProxyProfile? proxy,
        ProxySettings desired,
        string error)
    {
        stopwatch.Stop();
        bool rolledBack = false;
        try
        {
            rolledBack = _proxyManager.RestorePreviousSettings();
        }
        catch (Exception ex)
        {
            _log.Error("Apply", "Rollback threw an exception.", ex.ToString());
        }

        if (!rolledBack)
        {
            _log.Error("Apply",
                "Rollback FAILED after a failed apply — restore the system proxy manually.",
                $"Last attempted state: {desiredState} ({desired}).");
        }

        return new ApplyOutcome
        {
            Success = false,
            DesiredState = desiredState,
            Proxy = proxy,
            DesiredSettings = desired,
            ReadBackSettings = _proxyManager.GetCurrentSettings(),
            Error = error,
            RolledBack = rolledBack,
            ApplyLatencyMs = stopwatch.ElapsedMilliseconds,
        };
    }

    private static ProxySettings? BuildDesiredSettings(ConnectionState state, ProxyProfile? proxy) =>
        state switch
        {
            ConnectionState.Direct or ConnectionState.Offline or ConnectionState.CaptivePortal =>
                ProxySettings.Disabled(),
            ConnectionState.Proxy when proxy is not null =>
                ProxySettings.FromProxy(proxy.Host, proxy.Port),
            _ => null,
        };

    private static bool SettingsEqual(ProxySettings? a, ProxySettings? b)
    {
        if (a is null || b is null) return false;
        return a.Enabled == b.Enabled &&
               string.Equals(a.Server, b.Server, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(a.AutoConfigUrl, b.AutoConfigUrl, StringComparison.OrdinalIgnoreCase);
    }
}