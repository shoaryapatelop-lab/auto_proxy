using AutoProxy.Core.Models;

namespace AutoProxy.Core.Detection;

public class ConnectionDecision
{
    public ConnectionState State { get; init; }
    public ProxyProfile? SelectedProxy { get; init; }
    public required string Reason { get; init; }

    public static ConnectionDecision Direct(string detail) =>
        new() { State = ConnectionState.Direct, Reason = detail };

    public static ConnectionDecision Proxy(ProxyProfile proxy, long latencyMs, string? reason = null) =>
        new()
        {
            State = ConnectionState.Proxy,
            SelectedProxy = proxy,
            Reason = reason ?? $"Working proxy selected: {proxy.Name} ({latencyMs} ms).",
        };

    public static ConnectionDecision CaptivePortal() =>
        new()
        {
            State = ConnectionState.CaptivePortal,
            Reason = "Network requires browser authentication (captive portal detected). " +
                     "Do not assume a proxy is required.",
        };

    public static ConnectionDecision Offline(string reason) =>
        new() { State = ConnectionState.Offline, Reason = reason };

    public static ConnectionDecision Error(string reason) =>
        new() { State = ConnectionState.Error, Reason = reason };
}

public class DecisionEngine
{
    public ConnectionDecision DetermineMode(
        DirectTestResult direct,
        IReadOnlyList<ProxyTestResult> proxyResults)
    {
        if (direct.Endpoints.Count == 0)
        {
            return ConnectionDecision.Error(
                "No connectivity test endpoints are configured. Check Settings.");
        }

        if (direct.CaptivePortalHint)
        {
            return ConnectionDecision.CaptivePortal();
        }

        if (direct.InternetAvailable)
        {
            var detail = direct.SuccessCount == 1
                ? "direct internet connection verified"
                : $"direct internet connection verified via {direct.SuccessCount} endpoints";
            return ConnectionDecision.Direct(detail);
        }

        var failure = direct.FailureReason;
        var failureText = DescribeFailure(failure);
        var success = proxyResults.FirstOrDefault(r => r.Success);
        if (success is not null)
        {
            return ConnectionDecision.Proxy(success.Proxy, success.LatencyMs,
                $"{failureText} — {success.Proxy.Name} verified ({success.LatencyMs} ms).");
        }

        if (proxyResults.Count == 0)
        {
            return ConnectionDecision.Offline(
                $"Direct connectivity unavailable ({failureText}) and no proxies are configured to test.");
        }

        var summary = string.Join("; ",
            proxyResults.Select(r =>
                $"{r.Proxy.Name} → {(string.IsNullOrWhiteSpace(r.Error) ? r.Outcome?.ToString() ?? "Failed" : r.Error)}"));
        return ConnectionDecision.Offline(
            $"Direct connectivity unavailable ({failureText}); all {proxyResults.Count} proxy(ies) failed: {summary}");
    }

    private static string DescribeFailure(DirectFailureReason reason) => reason switch
    {
        DirectFailureReason.CaptivePortal => "captive portal detected",
        DirectFailureReason.ContentBlocked => "network reached but content blocked/mismatched",
        DirectFailureReason.DnsFailure => "DNS resolution failed",
        DirectFailureReason.TlsFailure => "TLS handshake failed",
        DirectFailureReason.Timeout => "connectivity timed out",
        DirectFailureReason.ConnectionFailed => "connection failed",
        DirectFailureReason.TestCancelled => "direct connectivity test cancelled",
        _ => "no direct connectivity",
    };
}