namespace AutoProxy.Core.Models;

public class AppSettings
{
    public string[] ProbeUrls { get; set; } =
    {
        "https://connectivitycheck.gstatic.com/generate_204",
        "https://cp.cloudflare.com/generate_204",
    };

    public string CustomProbeUrl { get; set; } = string.Empty;
    public int DirectTestTimeoutMs { get; set; } = 8000;
    public int ProxyTestTimeoutMs { get; set; } = 8000;
    public int HealthCheckIntervalSeconds { get; set; } = 60;
    public int FailureThreshold { get; set; } = 3;
    public int CooldownSeconds { get; set; } = 30;
    public bool IgnoreCertificateErrors { get; set; }
    public bool VerifyProxyAfterApply { get; set; } = true;
    public bool StartMonitoringOnLaunch { get; set; } = true;
    public bool CloseToTray { get; set; } = true;

    public IReadOnlyList<string> EffectiveProbeUrls()
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in ProbeUrls)
        {
            if (!string.IsNullOrWhiteSpace(url) && seen.Add(url))
                list.Add(url.Trim());
        }

        if (!string.IsNullOrWhiteSpace(CustomProbeUrl) && seen.Add(CustomProbeUrl.Trim()))
            list.Add(CustomProbeUrl.Trim());

        return list;
    }
}