using AutoProxy.Core.Models;

namespace AutoProxy.Core.Abstractions;

public interface IConnectivityTester
{
    Task<DirectTestResult> TestDirectAsync(AppSettings settings, CancellationToken cancellationToken);
}