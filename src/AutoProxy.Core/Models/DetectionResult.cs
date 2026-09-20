namespace AutoProxy.Core.Models;

public class DetectionResult
{
    public ConnectionState State { get; set; } = ConnectionState.Detecting;
    public ProxyProfile? SelectedProxy { get; set; }
    public NetworkIdentity? Network { get; set; }
    public long LatencyMs { get; set; }
    public string Detail { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public bool NetworkChangedDuringTest { get; set; }

    /// <summary>Raw direct-connectivity probe data, retained for diagnostics and unit tests.</summary>
    public DirectTestResult? DirectTest { get; set; }

    /// <summary>Results of each proxy candidate probed during this detection cycle.</summary>
    public IReadOnlyList<ProxyTestResult> ProxyTests { get; set; } = Array.Empty<ProxyTestResult>();

    public int ProxiesTested => ProxyTests.Count;
    public int ProxiesPassed => ProxyTests.Count(p => p.Success);
}