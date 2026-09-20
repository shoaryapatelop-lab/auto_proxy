namespace AutoProxy.Core.Models;

public class CurrentNetwork
{
    public NetworkIdentity Identity { get; set; } = new();
    public bool HasActiveRoute { get; set; }
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
}