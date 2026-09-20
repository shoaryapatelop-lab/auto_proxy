namespace AutoProxy.Core.Models;

public class NetworkProfile
{
    public long Id { get; set; }
    public string NetworkIdentifier { get; set; } = string.Empty;
    public string? Ssid { get; set; }
    public string? Gateway { get; set; }
    public string? InterfaceType { get; set; }
    public string PreferredMode { get; set; } = "Detecting";
    public long? PreferredProxyId { get; set; }
    public DateTime LastSeen { get; set; }
    public DateTime CreatedAt { get; set; }
}