using AutoProxy.Core.Models;

namespace AutoProxy.Core.Detection;

/// <summary>
/// Deterministically ranks proxy candidates for a network so the test order is
/// stable and prioritizes recent health over raw latency, while still honoring
/// the network's preferred proxy. Pure logic — unit-testable with no I/O.
/// </summary>
public static class ProxyRanker
{
    private const int PreferredBias = 2000;
    private const int RecentSuccess1h = 500;
    private const int RecentSuccess24h = 350;
    private const int RecentSuccess7d = 150;
    private const int SuccessRateWeight = 200;
    private const int FailurePenalty = 6;
    private const int RecentFailurePenalty = 80;
    private const int LatencyFast = 90;
    private const int LatencyGood = 45;
    private const int LatencyOk = 15;
    private const long FastLatencyMs = 300;
    private const long GoodLatencyMs = 1000;
    private static readonly TimeSpan Recent1h = TimeSpan.FromHours(1);
    private static readonly TimeSpan Recent24h = TimeSpan.FromHours(24);
    private static readonly TimeSpan Recent7d = TimeSpan.FromDays(7);

    public static IReadOnlyList<ProxyProfile> Rank(
        IEnumerable<ProxyProfile> proxies,
        long? preferredProxyId,
        DateTime? now = null)
    {
        var timestamp = now ?? DateTime.UtcNow;
        return proxies
            .Select(p => new
            {
                Proxy = p,
                Score = Score(p, preferredProxyId, timestamp),
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Proxy.AverageLatencyMs ?? long.MaxValue)
            .ThenBy(x => x.Proxy.Id)
            .Select(x => x.Proxy)
            .ToList();
    }

    internal static int Score(ProxyProfile proxy, long? preferredProxyId, DateTime now)
    {
        int score = 0;

        if (proxy.Id != 0 && proxy.Id == preferredProxyId)
            score += PreferredBias;

        if (proxy.LastSuccessfulTest is not null)
        {
            var age = now - proxy.LastSuccessfulTest.Value;
            if (age <= Recent1h) score += RecentSuccess1h;
            else if (age <= Recent24h) score += RecentSuccess24h;
            else if (age <= Recent7d) score += RecentSuccess7d;
        }

        score += (int)Math.Round(proxy.SuccessRate * SuccessRateWeight, MidpointRounding.AwayFromZero);
        score -= proxy.FailureCount * FailurePenalty;

        if (proxy.LastFailedTest is not null && now - proxy.LastFailedTest.Value <= Recent1h)
            score -= RecentFailurePenalty;

        if (proxy.AverageLatencyMs is { } latency)
        {
            if (latency <= FastLatencyMs) score += LatencyFast;
            else if (latency <= GoodLatencyMs) score += LatencyGood;
            else score += LatencyOk;
        }

        return score;
    }
}