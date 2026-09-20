namespace AutoProxy.Core.Models;

public enum ProxyTestOutcome
{
    Success,
    Timeout,
    ConnectFailure,
    DnsFailure,
    AuthenticationFailed,
    TlsError,
    ProtocolError,
    ProxyUnreachable,
    Cancelled,
}

public class ProxyTestResult
{
    public ProxyProfile Proxy { get; init; } = new();
    public bool Success => Outcome == ProxyTestOutcome.Success;
    public ProxyTestOutcome? Outcome { get; set; }
    public int HttpStatus { get; set; }
    public long LatencyMs { get; set; }
    public string? Error { get; set; }
    public bool AuthChallenge { get; set; }

    public static ProxyTestResult CancelledResult(ProxyProfile proxy) =>
        new() { Proxy = proxy, Outcome = ProxyTestOutcome.Cancelled, Error = "Test was cancelled." };

    public static ProxyTestResult Failure(ProxyProfile proxy, ProxyTestOutcome outcome, string? error = null) =>
        new() { Proxy = proxy, Outcome = outcome, Error = error };
}