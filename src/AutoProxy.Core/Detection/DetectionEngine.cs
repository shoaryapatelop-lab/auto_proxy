using System.Diagnostics;
using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Models;

namespace AutoProxy.Core.Detection;

public class DetectionEngine
{
    private readonly INetworkMonitor _monitor;
    private readonly IConnectivityTester _connectivity;
    private readonly IProxyTester _proxyTester;
    private readonly IProxyRepository _proxyRepo;
    private readonly INetworkRepository _networkRepo;
    private readonly ISettingsRepository _settingsRepo;
    private readonly IDetectionHistoryRepository _history;
    private readonly ILogService _log;
    private readonly DecisionEngine _decision;

    public DetectionEngine(
        INetworkMonitor monitor,
        IConnectivityTester connectivity,
        IProxyTester proxyTester,
        IProxyRepository proxyRepo,
        INetworkRepository networkRepo,
        ISettingsRepository settingsRepo,
        IDetectionHistoryRepository history,
        ILogService log,
        DecisionEngine decision)
    {
        _monitor = monitor;
        _connectivity = connectivity;
        _proxyTester = proxyTester;
        _proxyRepo = proxyRepo;
        _networkRepo = networkRepo;
        _settingsRepo = settingsRepo;
        _history = history;
        _log = log;
        _decision = decision;
    }

    public async Task<DetectionResult> DetectAsync(CancellationToken cancellationToken)
    {
        var settings = await _settingsRepo.LoadAsync(cancellationToken);
        _log.Info("Detect", "Starting connection detection…");
        var stopwatch = Stopwatch.StartNew();

        var current = await _monitor.GetCurrentNetworkAsync(cancellationToken);
        if (current is null)
        {
            _log.Warning("Detect", "No active network interface detected.");
            return FinishStopwatch(
                new DetectionResult
                {
                    State = ConnectionState.Offline,
                    Detail = "No active network interface detected.",
                },
                stopwatch);
        }

        if (!current.HasActiveRoute)
        {
            _log.Warning("Detect", "Network interface exists but has no route to the internet.");
            return FinishStopwatch(
                new DetectionResult
                {
                    State = ConnectionState.Offline,
                    Network = current.Identity,
                    Detail = "Network unavailable — no default route.",
                },
                stopwatch);
        }

        var signatureBefore = _monitor.GetQuickSignature();
        var identity = current.Identity;

        var remembered = await EnsureNetworkRememberedAsync(identity, cancellationToken);
        long? networkId = remembered?.Id;

        _log.Info("Network", $"Connected to network: {identity.DisplayName}",
            $"Network ID: {identity.Identifier}", networkId);

        _log.Info("Direct", "Testing direct internet connection…",
            $"Endpoints: {string.Join(", ", settings.EffectiveProbeUrls())}", networkId);
        var direct = await _connectivity.TestDirectAsync(settings, cancellationToken);
        LogDirectResult(direct, networkId);

        var proxyResults = new List<ProxyTestResult>();
        if (!direct.InternetAvailable && !direct.CaptivePortalHint)
        {
            proxyResults = await TestSavedProxiesAsync(remembered, settings, networkId, cancellationToken);
        }

        var decision = _decision.DetermineMode(direct, proxyResults);
        stopwatch.Stop();

        await UpdateProxyStatsAsync(proxyResults, cancellationToken);
        await SaveDetectionHistoryAsync(
            identity, decision, direct, stopwatch.ElapsedMilliseconds, cancellationToken);

        var result = new DetectionResult
        {
            State = decision.State,
            SelectedProxy = decision.SelectedProxy,
            Network = identity,
            LatencyMs = stopwatch.ElapsedMilliseconds,
            Detail = decision.Reason,
            Timestamp = DateTime.UtcNow,
            DirectTest = direct,
            ProxyTests = proxyResults,
        };

        if (remembered is not null)
        {
            await RememberOutcomeAsync(remembered, result, cancellationToken);
        }

        var signatureAfter = _monitor.GetQuickSignature();
        result.NetworkChangedDuringTest = !string.Equals(signatureBefore, signatureAfter);

        _log.Info("Detect",
            $"Detection complete: {decision.State} ({result.LatencyMs} ms)",
            decision.Reason, networkId, decision.SelectedProxy?.Id);

        if (result.NetworkChangedDuringTest)
        {
            _log.Warning("Detect",
                "Network changed while the connection test was running — run again to confirm.",
                null, networkId);
        }

        return result;
    }

    private async Task<NetworkProfile?> EnsureNetworkRememberedAsync(
        NetworkIdentity identity, CancellationToken cancellationToken)
    {
        var existing = await _networkRepo.GetByIdentifierAsync(identity.Identifier, cancellationToken);
        if (existing is null)
        {
            existing = new NetworkProfile
            {
                NetworkIdentifier = identity.Identifier,
                Ssid = identity.Ssid,
                Gateway = identity.Gateway,
                InterfaceType = identity.InterfaceType,
                PreferredMode = "Detecting",
                CreatedAt = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow,
            };
            await _networkRepo.SaveAsync(existing, cancellationToken);
            _log.Info("Network", "New network remembered.",
                $"{identity.DisplayName} — {identity.Identifier}");

            // Re-read so the freshly assigned primary key is available to callers.
            existing = await _networkRepo.GetByIdentifierAsync(
                identity.Identifier, cancellationToken) ?? existing;
        }

        return existing;
    }

    private async Task<List<ProxyTestResult>> TestSavedProxiesAsync(
        NetworkProfile? remembered,
        AppSettings settings,
        long? networkId,
        CancellationToken cancellationToken)
    {
        var proxies = await _proxyRepo.GetEnabledAsync(cancellationToken);
        if (proxies.Count == 0)
        {
            _log.Info("Proxy", "Direct connection failed; no proxies are configured to test.",
                null, networkId);
            return new List<ProxyTestResult>();
        }

        var candidates = ProxyRanker.Rank(proxies, remembered?.PreferredProxyId);
        _log.Info("Proxy",
            $"Direct connection failed; testing {candidates.Count} saved proxies…",
            null, networkId);

        var results = new List<ProxyTestResult>(candidates.Count);
        foreach (var proxy in candidates)
        {
            _log.Info("Proxy",
                $"Testing saved proxy: {proxy.Name} ({proxy.DisplayAddress})",
                null, networkId, proxy.Id);

            var test = await _proxyTester.TestAsync(proxy, settings, cancellationToken);
            _log.Info("Proxy",
                test.Success
                    ? $"Proxy connection successful: {proxy.Name} ({test.LatencyMs} ms)"
                    : $"Proxy failed: {proxy.Name} → {DescribeOutcome(test)}",
                test.Error, networkId, proxy.Id);

            results.Add(test);
            if (test.Success) break;
        }

        return results;
    }

    private async Task UpdateProxyStatsAsync(
        IReadOnlyList<ProxyTestResult> results, CancellationToken cancellationToken)
    {
        foreach (var r in results)
        {
            var profile = await _proxyRepo.GetByIdAsync(r.Proxy.Id, cancellationToken);
            if (profile is null) continue;

            if (r.Success)
            {
                profile.SuccessCount++;
                profile.AverageLatencyMs = profile.AverageLatencyMs is null
                    ? r.LatencyMs
                    : (profile.AverageLatencyMs.Value * (profile.SuccessCount - 1) + r.LatencyMs) /
                      profile.SuccessCount;
                profile.LastSuccessfulTest = DateTime.UtcNow;
                profile.LastError = null;
            }
            else
            {
                profile.FailureCount++;
                profile.LastFailedTest = DateTime.UtcNow;
                profile.LastError = r.Error ?? $"{r.Outcome}";
            }

            await _proxyRepo.UpdateAsync(profile, cancellationToken);
        }
    }

    private async Task RememberOutcomeAsync(
        NetworkProfile profile, DetectionResult result, CancellationToken cancellationToken)
    {
        profile.LastSeen = DateTime.UtcNow;

        if (result.State == ConnectionState.Direct)
        {
            profile.PreferredMode = "Direct";
        }
        else if (result.State == ConnectionState.Proxy && result.SelectedProxy is not null)
        {
            profile.PreferredMode = "Proxy";
            profile.PreferredProxyId = result.SelectedProxy.Id;
        }

        await _networkRepo.SaveAsync(profile, cancellationToken);
    }

    private async Task SaveDetectionHistoryAsync(
        NetworkIdentity identity,
        ConnectionDecision decision,
        DirectTestResult direct,
        long latencyMs,
        CancellationToken cancellationToken)
    {
        try
        {
            var record = new DetectionResult
            {
                State = decision.State,
                SelectedProxy = decision.SelectedProxy,
                Network = identity,
                LatencyMs = latencyMs,
                Detail = $"{decision.Reason} Direct endpoints OK: " +
                         $"{direct.SuccessCount}/{direct.Endpoints.Count}",
            };
            await _history.AddAsync(record, cancellationToken);
        }
        catch (Exception ex)
        {
            _log.Warning("Detect", "Could not save detection history.", ex.Message);
        }
    }

    private void LogDirectResult(DirectTestResult direct, long? networkId)
    {
        var detail = direct.Endpoints.Count == 0
            ? "No endpoints configured."
            : string.Join(
                "; ",
                direct.Endpoints.Select(e =>
                    $"{e.Url} → {(e.Success ? $"{e.HttpStatus} ({e.LatencyMs} ms)" : e.Error)}"));

        if (direct.InternetAvailable)
            _log.Info("Direct", "Direct connection works — no proxy needed.", detail, networkId);
        else if (direct.CaptivePortalHint)
            _log.Warning("Direct", "Captive portal / login page detected.", detail, networkId);
        else
            _log.Warning("Direct", "Direct connection failed.", detail, networkId);
    }

    private static string DescribeOutcome(ProxyTestResult test) =>
        test.Outcome?.ToString() ?? "Unknown";

    private static DetectionResult FinishStopwatch(DetectionResult result, Stopwatch sw)
    {
        sw.Stop();
        result.LatencyMs = sw.ElapsedMilliseconds;
        result.Timestamp = DateTime.UtcNow;
        return result;
    }
}