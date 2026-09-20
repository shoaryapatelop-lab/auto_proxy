namespace AutoProxy.Core.Models;

/// <summary>
/// Classifies what a single endpoint probe actually saw, so the decision layer
/// can distinguish "network offline" from "captive portal" from "host down".
/// </summary>
public enum EndpointClassification
{
    Healthy,
    RedirectedToLogin,
    LoginPageContent,
    ContentMismatch,
    HttpError,
    Timeout,
    DnsFailure,
    TlsError,
    ConnectionFailed,
    Cancelled,
}

/// <summary>
/// High-level reason why direct connectivity failed. Used to produce
/// human-readable explanations in the DecisionEngine and DiagnosticSummary.
/// </summary>
public enum DirectFailureReason
{
    None,
    Offline,
    ContentBlocked,
    CaptivePortal,
    DnsFailure,
    TlsFailure,
    Timeout,
    ConnectionFailed,
    TestCancelled,
}

public class EndpointResult
{
    public string Url { get; init; } = string.Empty;
    public bool Success { get; set; }
    public int HttpStatus { get; set; }
    public long LatencyMs { get; set; }
    public string? RedirectUrl { get; set; }
    public bool LooksLikeCaptivePortal { get; set; }
    public string? Error { get; set; }

    public EndpointClassification Classification { get; set; }
}

public class DirectTestResult
{
    /// <summary>Optional label so notifications can say which zone failed (e.g. REST / DNS / CDN).</summary>
    public string? TestName { get; init; }

    public IReadOnlyList<EndpointResult> Endpoints { get; init; } =
        Array.Empty<EndpointResult>();

    public int SuccessCount => Endpoints.Count(e => e.Success);
    public int FailCount => Endpoints.Count(e => !e.Success);
    public bool InternetAvailable => SuccessCount > 0;
    public long BestLatencyMs =>
        Endpoints.Where(e => e.Success).Select(e => e.LatencyMs).DefaultIfEmpty(0).Min();
    public bool CaptivePortalHint => Endpoints.Any(e => e.LooksLikeCaptivePortal);
    public string? FirstError => Endpoints.FirstOrDefault(e => !string.IsNullOrEmpty(e.Error))?.Error;

    public bool HasContentMismatch => Endpoints.Any(e =>
        e.Classification == EndpointClassification.ContentMismatch);

    public bool HasLoginContent => Endpoints.Any(e =>
        e.Classification is EndpointClassification.LoginPageContent or
            EndpointClassification.RedirectedToLogin);

    public bool RedirectedToLogin => Endpoints.Any(e =>
        e.Classification == EndpointClassification.RedirectedToLogin);

    public bool AnyTimeout => Endpoints.Any(e =>
        e.Classification is EndpointClassification.Timeout);

    public bool AnyDnsFailure => Endpoints.Any(e =>
        e.Classification is EndpointClassification.DnsFailure);

    public bool AnyTlsFailure => Endpoints.Any(e =>
        e.Classification is EndpointClassification.TlsError);

    public bool AnyConnectionFailure => Endpoints.Any(e =>
        e.Classification is EndpointClassification.ConnectionFailed);

    /// <summary>
    /// Highest-confidence explanation for why the direct test failed.
    /// Voting order: content mismatch (blocked) &gt; redirect (portal) &gt;
    /// DNS &gt; TLS &gt; timeout &gt; TCP &gt; generic offline.
    /// </summary>
    public DirectFailureReason FailureReason
    {
        get
        {
            if (InternetAvailable || Endpoints.Count == 0)
                return DirectFailureReason.None;

            if (RedirectedToLogin || HasLoginContent || CaptivePortalHint)
                return DirectFailureReason.CaptivePortal;
            if (HasContentMismatch)
                return DirectFailureReason.ContentBlocked;
            if (AnyDnsFailure) return DirectFailureReason.DnsFailure;
            if (AnyTlsFailure) return DirectFailureReason.TlsFailure;
            if (AnyTimeout) return DirectFailureReason.Timeout;
            if (AnyConnectionFailure) return DirectFailureReason.ConnectionFailed;
            if (Endpoints.All(e => e.Classification == EndpointClassification.Cancelled))
                return DirectFailureReason.TestCancelled;
            return DirectFailureReason.Offline;
        }
    }

    /// <summary>A single-line, human-readable summary of what the direct test observed.</summary>
    public string DiagnosticSummary
    {
        get
        {
            if (InternetAvailable)
                return $"verified via {SuccessCount} endpoint(s), best {BestLatencyMs} ms";

            return FailureReason switch
            {
                DirectFailureReason.CaptivePortal =>
                    "network requires browser authentication",
                DirectFailureReason.ContentBlocked =>
                    $"network reached but content was blocked/mismatched{ScopeSuffix}",
                DirectFailureReason.DnsFailure =>
                    $"DNS resolution failed{ScopeSuffix}",
                DirectFailureReason.TlsFailure =>
                    $"TLS handshake failed{ScopeSuffix}",
                DirectFailureReason.Timeout =>
                    $"connectivity timed out{ScopeSuffix}",
                DirectFailureReason.ConnectionFailed =>
                    $"connection failed{ScopeSuffix}",
                DirectFailureReason.TestCancelled =>
                    "direct connectivity test was cancelled",
                _ => "no direct connectivity",
            };
        }
    }

    private string ScopeSuffix => string.IsNullOrEmpty(TestName) ? string.Empty : $" on {TestName}";

    /// <summary>Endpoint classification counting, used by tests and the DiagnosticSummary builder.</summary>
    public IReadOnlyDictionary<EndpointClassification, int> ClassificationCounts =>
        Endpoints.GroupBy(e => e.Classification)
            .ToDictionary(g => g.Key, g => g.Count());
}