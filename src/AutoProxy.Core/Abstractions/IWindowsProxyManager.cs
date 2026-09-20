namespace AutoProxy.Core.Abstractions;

public class ProxySettings
{
    public bool Enabled { get; set; }
    public string? Server { get; set; }
    public string? BypassList { get; set; }
    /// <summary>The AutoConfigURL (PAC script) value. Non-null when a PAC/WPAD source governs proxying.</summary>
    public string? AutoConfigUrl { get; set; }

    public static ProxySettings Disabled() => new() { Enabled = false };

    public static ProxySettings FromProxy(string host, int port) =>
        new() { Enabled = true, Server = AutoProxy.Core.Network.ProxyValidator.FormatAddress(host, port) };

    public ProxySettings Clone() =>
        new() { Enabled = Enabled, Server = Server, BypassList = BypassList, AutoConfigUrl = AutoConfigUrl };

    public override string ToString() =>
        Enabled ? $"enabled ({Server})" : "disabled";
}

/// <summary>
/// Manages the Windows system proxy (WinINet registry + binary blobs) and
/// the separate WinHTTP configuration (via netsh winhttp).
/// </summary>
public interface IWindowsProxyManager
{
    ProxySettings GetCurrentSettings();

    /// <summary>What AutoProxy last applied (persisted across the process lifetime).</summary>
    ProxySettings? GetExpectedSettings();

    bool EnableProxy(string host, int port, string? bypassList = null);
    bool DisableProxy();

    /// <summary>Restore the settings that were in place before AutoProxy changed them.</summary>
    bool RestorePreviousSettings();

    /// <summary>Capture the pre-change snapshot unless one already exists.</summary>
    void SaveSnapshot(bool force);

    /// <summary>Undo any proxy left behind by a previous crashed session.</summary>
    void RestorePreviousSettingsOnStartup();

    event EventHandler? WindowsProxyChanged;

    string DescribeScope();
}