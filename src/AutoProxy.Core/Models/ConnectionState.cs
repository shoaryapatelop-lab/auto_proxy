namespace AutoProxy.Core.Models;

public enum ConnectionState
{
    Detecting,
    Direct,
    Proxy,
    Offline,
    CaptivePortal,
    Error,
}