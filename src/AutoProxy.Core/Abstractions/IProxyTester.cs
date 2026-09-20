using AutoProxy.Core.Models;

namespace AutoProxy.Core.Abstractions;

public interface IProxyTester
{
    Task<ProxyTestResult> TestAsync(ProxyProfile proxy, AppSettings settings, CancellationToken cancellationToken);
    Task<(bool IsServing, long LatencyMs)> GetProxyHealthAsync(ProxyProfile proxy, AppSettings settings, CancellationToken cancellationToken);
}