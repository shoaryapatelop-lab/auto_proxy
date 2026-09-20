namespace AutoProxy.Core.Models;

public class ProxyProfile
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public bool AuthenticationRequired { get; set; }
    public string? CredentialTarget { get; set; }
    public bool Enabled { get; set; } = true;

    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public long? AverageLatencyMs { get; set; }
    public DateTime? LastSuccessfulTest { get; set; }
    public DateTime? LastFailedTest { get; set; }
    public string? LastError { get; set; }

    public double SuccessRate =>
        SuccessCount + FailureCount == 0
            ? 0
            : (double)SuccessCount / (SuccessCount + FailureCount);

    public string DisplayAddress => AutoProxy.Core.Network.ProxyValidator.FormatAddress(Host, Port);

    public ProxyProfile Clone() => (ProxyProfile)MemberwiseClone();
}