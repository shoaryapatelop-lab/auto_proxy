namespace AutoProxy.Core.Models;

public class NetworkIdentity
{
    public string Identifier { get; set; } = string.Empty;
    public string DisplayName { get; set; } = "Unknown";
    public string? Ssid { get; set; }
    public string? Gateway { get; set; }
    public string Subnet { get; set; } = string.Empty;
    public string InterfaceType { get; set; } = "Unknown";

    public override string ToString() => DisplayName;
}