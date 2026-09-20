using AutoProxy.Core.Models;

namespace AutoProxy.Core.Abstractions;

public interface ILogService
{
    event EventHandler<LogEntry>? EntryAdded;
    IReadOnlyList<LogEntry> GetRecentlyAdded(int limit);
    void Verbose(string category, string message, string? detail = null);
    void Info(string category, string message, string? detail = null, long? networkId = null, long? proxyId = null);
    void Warning(string category, string message, string? detail = null, long? networkId = null, long? proxyId = null);
    void Error(string category, string message, string? detail = null, long? networkId = null, long? proxyId = null);
}