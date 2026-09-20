using AutoProxy.Core.Models;

namespace AutoProxy.Core.Abstractions;

public interface IProxyRepository
{
    Task<IReadOnlyList<ProxyProfile>> GetAllAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ProxyProfile>> GetEnabledAsync(CancellationToken cancellationToken);
    Task<ProxyProfile?> GetByIdAsync(long id, CancellationToken cancellationToken);
    Task<long> AddAsync(ProxyProfile profile, CancellationToken cancellationToken);
    Task UpdateAsync(ProxyProfile profile, CancellationToken cancellationToken);
    Task DeleteAsync(long id, CancellationToken cancellationToken);
}

public interface INetworkRepository
{
    Task<NetworkProfile?> GetByIdentifierAsync(string identifier, CancellationToken cancellationToken);
    Task<IReadOnlyList<NetworkProfile>> GetAllAsync(CancellationToken cancellationToken);
    Task SaveAsync(NetworkProfile profile, CancellationToken cancellationToken);
    Task SetPreferredModeAsync(string identifier, string mode, long? preferredProxyId, CancellationToken cancellationToken);
    Task DeleteByIdentifierAsync(string identifier, CancellationToken cancellationToken);
}

public interface ISettingsRepository
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}

public interface IDetectionHistoryRepository
{
    Task AddAsync(DetectionResult result, CancellationToken cancellationToken);
    Task<IReadOnlyList<DetectionResult>> GetRecentAsync(int limit, CancellationToken cancellationToken);
}

public interface ILogRepository
{
    Task AddAsync(LogEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<LogEntry>> GetRecentAsync(int limit, CancellationToken cancellationToken);
    Task AddManyAsync(IEnumerable<LogEntry> entries, CancellationToken cancellationToken);
}