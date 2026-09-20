using AutoProxy.Core.Models;

namespace AutoProxy.Core.Abstractions;

public class NetworkChangedEventArgs : EventArgs
{
    public string? PreviousSignature { get; init; }
    public string? NewSignature { get; init; }
    public bool IsAvailabilityChange { get; init; }
}

public interface INetworkMonitor
{
    event EventHandler<NetworkChangedEventArgs>? NetworkChanged;
    event EventHandler? NetworkUnavailable;

    Task<CurrentNetwork?> GetCurrentNetworkAsync(CancellationToken cancellationToken);
    string GetQuickSignature();
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
}