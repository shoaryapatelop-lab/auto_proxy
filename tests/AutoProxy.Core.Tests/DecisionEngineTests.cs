using AutoProxy.Core.Detection;
using AutoProxy.Core.Models;
using Xunit;

namespace AutoProxy.Core.Tests;

public class DecisionEngineTests
{
    private readonly DecisionEngine _engine = new();

    private static EndpointResult Ok(string url, long latency = 20) =>
        new() { Url = url, Success = true, HttpStatus = 204, LatencyMs = latency };

    private static EndpointResult Portal(string url) =>
        new() { Url = url, Success = false, HttpStatus = 200, RedirectUrl = "http://portal/", LooksLikeCaptivePortal = true };

    private static EndpointResult Failed(string url, string error = "timeout") =>
        new() { Url = url, Success = false, Error = error };

    private static DirectTestResult Direct(params EndpointResult[] endpoints) =>
        new() { Endpoints = endpoints };

    private static ProxyTestResult ProxyOk(string name, long latency = 80) =>
        new()
        {
            Proxy = new ProxyProfile { Id = 1, Name = name, Host = "p", Port = 1 },
            Outcome = ProxyTestOutcome.Success,
            LatencyMs = latency,
        };

    private static ProxyTestResult ProxyFail(string name) =>
        new()
        {
            Proxy = new ProxyProfile { Id = 2, Name = name, Host = "p", Port = 2 },
            Outcome = ProxyTestOutcome.ProxyUnreachable,
            Error = "connection refused",
        };

    [Fact]
    public void No_endpoints_is_error()
    {
        var decision = _engine.DetermineMode(Direct(), Array.Empty<ProxyTestResult>());

        Assert.Equal(ConnectionState.Error, decision.State);
    }

    [Fact]
    public void Captive_portal_takes_priority_over_internet()
    {
        var direct = Direct(Ok("a"), Portal("b"));

        var decision = _engine.DetermineMode(direct, Array.Empty<ProxyTestResult>());

        Assert.Equal(ConnectionState.CaptivePortal, decision.State);
    }

    [Fact]
    public void Internet_available_is_direct_even_when_proxies_exist()
    {
        var decision = _engine.DetermineMode(
            Direct(Ok("a")), new[] { ProxyOk("corp") });

        Assert.Equal(ConnectionState.Direct, decision.State);
        Assert.Null(decision.SelectedProxy);
    }

    [Fact]
    public void Direct_failure_with_working_proxy_selects_proxy()
    {
        var decision = _engine.DetermineMode(
            Direct(Failed("a")), new[] { ProxyOk("corp", 42) });

        Assert.Equal(ConnectionState.Proxy, decision.State);
        Assert.NotNull(decision.SelectedProxy);
        Assert.Equal("corp", decision.SelectedProxy!.Name);
    }

    [Fact]
    public void Direct_failure_with_no_proxies_is_offline()
    {
        var decision = _engine.DetermineMode(
            Direct(Failed("a")), Array.Empty<ProxyTestResult>());

        Assert.Equal(ConnectionState.Offline, decision.State);
    }

    [Fact]
    public void Direct_failure_with_only_failing_proxies_is_offline()
    {
        var decision = _engine.DetermineMode(
            Direct(Failed("a")), new[] { ProxyFail("x"), ProxyFail("y") });

        Assert.Equal(ConnectionState.Offline, decision.State);
        Assert.Null(decision.SelectedProxy);
    }

    [Fact]
    public void First_successful_proxy_is_selected()
    {
        var decision = _engine.DetermineMode(
            Direct(Failed("a")),
            new[] { ProxyFail("x"), ProxyOk("y", 10), ProxyOk("z", 5) });

        Assert.Equal(ConnectionState.Proxy, decision.State);
        Assert.Equal("y", decision.SelectedProxy!.Name);
    }

    [Fact]
    public void Proxy_reason_mentions_direct_failure_cause_and_proxy()
    {
        var decision = _engine.DetermineMode(
            Direct(Classified(EndpointClassification.DnsFailure, success: false)),
            new[] { ProxyOk("corp", 42) });

        Assert.Equal(ConnectionState.Proxy, decision.State);
        Assert.Contains("DNS", decision.Reason);
        Assert.Contains("corp", decision.Reason);
        Assert.Contains("42", decision.Reason);
    }

    [Fact]
    public void Offline_reason_includes_failure_cause_and_proxy_summary()
    {
        var decision = _engine.DetermineMode(
            Direct(Classified(EndpointClassification.Timeout, success: false)),
            new[] { ProxyFail("x"), ProxyFail("y") });

        Assert.Equal(ConnectionState.Offline, decision.State);
        Assert.Contains("timed out", decision.Reason);
        Assert.Contains("x", decision.Reason);
        Assert.Contains("y", decision.Reason);
    }

    [Fact]
    public void Direct_reason_lists_endpoint_count()
    {
        var decision = _engine.DetermineMode(
            Direct(Ok("a", 10), Ok("b", 20)), Array.Empty<ProxyTestResult>());

        Assert.Equal(ConnectionState.Direct, decision.State);
        Assert.Contains("2 endpoints", decision.Reason);
    }

    private static EndpointResult Classified(EndpointClassification c, bool success) =>
        new()
        {
            Url = "https://probe/x",
            Success = success,
            HttpStatus = success ? 204 : 0,
            Classification = c,
        };
}
