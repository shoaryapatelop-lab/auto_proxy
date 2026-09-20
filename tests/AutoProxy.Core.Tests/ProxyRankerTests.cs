using AutoProxy.Core.Detection;
using AutoProxy.Core.Models;
using Xunit;

namespace AutoProxy.Core.Tests;

public class ProxyRankerTests
{
    private static readonly DateTime Now = new(2026, 9, 18, 10, 0, 0, DateTimeKind.Utc);

    private static ProxyProfile Proxy(
        long id,
        double successRate = 0,
        int failures = 0,
        long? latency = null,
        DateTime? lastSuccess = null,
        DateTime? lastFailed = null) =>
        new()
        {
            Id = id,
            Name = $"p{id}",
            Host = "10.0.0.1",
            Port = 3128,
            SuccessCount = (int)(successRate * 10),
            FailureCount = failures,
            AverageLatencyMs = latency,
            LastSuccessfulTest = lastSuccess,
            LastFailedTest = lastFailed,
        };

    [Fact]
    public void Preferred_proxy_is_ranked_first_despite_worse_stats()
    {
        var preferred = Proxy(1, successRate: 0.1, latency: 900, lastSuccess: Now.AddHours(-10));
        var other = Proxy(2, successRate: 0.9, latency: 30, lastSuccess: Now.AddHours(-2));

        var ranked = ProxyRanker.Rank(new[] { other, preferred }, preferredProxyId: 1, now: Now);

        Assert.Equal(1, ranked[0].Id);
        Assert.Equal(2, ranked[1].Id);
    }

    [Fact]
    public void Recent_success_outweighs_older_faster_proxy()
    {
        var older = Proxy(1, successRate: 0.9, latency: 20, lastSuccess: Now.AddHours(-2));
        var recent = Proxy(2, successRate: 0.8, latency: 800, lastSuccess: Now.AddMinutes(-10));

        var ranked = ProxyRanker.Rank(new[] { older, recent }, preferredProxyId: null, now: Now);

        Assert.Equal(2, ranked[0].Id);
        Assert.Equal(1, ranked[1].Id);
    }

    [Fact]
    public void Recent_failure_plus_slowness_sinks_a_recent_success()
    {
        var shaky = Proxy(1, successRate: 0.5, failures: 8, latency: 900,
            lastSuccess: Now.AddMinutes(-30), lastFailed: Now.AddMinutes(-5));
        var steady = Proxy(2, successRate: 0.9, latency: 40, lastSuccess: Now.AddHours(-20));

        var ranked = ProxyRanker.Rank(new[] { shaky, steady }, preferredProxyId: null, now: Now);

        Assert.Equal(2, ranked[0].Id);
        Assert.Equal(1, ranked[1].Id);
    }

    [Fact]
    public void Ties_resolve_lowest_id_first_for_determinism()
    {
        var a = Proxy(7, successRate: 0.5, latency: 100, lastSuccess: Now.AddHours(-3));
        var b = Proxy(3, successRate: 0.5, latency: 100, lastSuccess: Now.AddHours(-3));

        var ranked = ProxyRanker.Rank(new[] { a, b }, preferredProxyId: null, now: Now);

        Assert.Equal(3, ranked[0].Id);
        Assert.Equal(7, ranked[1].Id);
    }

    [Fact]
    public void Rank_keeps_all_candidates_and_is_stable_across_calls()
    {
        var candidates = new[]
        {
            Proxy(4, successRate: 0.4, latency: 200, lastSuccess: Now.AddHours(-26)),
            Proxy(5, successRate: 0.7, latency: 60, lastSuccess: Now.AddDays(-2)),
            Proxy(6, successRate: 0.2, latency: 500),
        };

        var first = ProxyRanker.Rank(candidates, preferredProxyId: null, now: Now);
        var second = ProxyRanker.Rank(candidates, preferredProxyId: null, now: Now);

        Assert.Equal(3, first.Count);
        Assert.Equal(first.Select(p => p.Id), second.Select(p => p.Id));
    }

    [Fact]
    public void Preferred_match_ignored_when_id_is_zero()
    {
        var zero = Proxy(0, successRate: 0.0, latency: 1200);
        var normal = Proxy(9, successRate: 0.6, latency: 300, lastSuccess: Now.AddHours(-5));

        var ranked = ProxyRanker.Rank(new[] { zero, normal }, preferredProxyId: 0, now: Now);

        // Zero never matches a stored proxy id, so the normal candidate wins.
        Assert.Equal(9, ranked[0].Id);
    }
}