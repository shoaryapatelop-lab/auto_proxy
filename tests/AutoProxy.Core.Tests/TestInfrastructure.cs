using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Apply;
using AutoProxy.Core.Detection;
using AutoProxy.Core.Models;
using AutoProxy.Core.Monitoring;
using AutoProxy.Core.Services;

namespace AutoProxy.Core.Tests;

public sealed class SilentLog : ILogService
{
    public event EventHandler<LogEntry>? EntryAdded { add { } remove { } }
    public IReadOnlyList<LogEntry> GetRecentlyAdded(int limit) => Array.Empty<LogEntry>();
    public void Verbose(string category, string message, string? detail = null) { }
    public void Info(string category, string message, string? detail = null, long? networkId = null, long? proxyId = null) { }
    public void Warning(string category, string message, string? detail = null, long? networkId = null, long? proxyId = null) { }
    public void Error(string category, string message, string? detail = null, long? networkId = null, long? proxyId = null) { }
}

public sealed class InMemoryProxyRepository : IProxyRepository
{
    public List<ProxyProfile> Items = new();
    private long _nextId = 1;

    public Task<IReadOnlyList<ProxyProfile>> GetAllAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProxyProfile>>(Items.ToList());

    public Task<IReadOnlyList<ProxyProfile>> GetEnabledAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProxyProfile>>(Items.Where(p => p.Enabled).ToList());

    public Task<ProxyProfile?> GetByIdAsync(long id, CancellationToken ct) =>
        Task.FromResult(Items.FirstOrDefault(p => p.Id == id));

    public Task<long> AddAsync(ProxyProfile profile, CancellationToken ct)
    {
        if (profile.Id == 0) profile.Id = _nextId++;
        Items.Add(profile);
        return Task.FromResult(profile.Id);
    }

    public Task UpdateAsync(ProxyProfile profile, CancellationToken ct)
    {
        var index = Items.FindIndex(p => p.Id == profile.Id);
        if (index >= 0) Items[index] = profile;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(long id, CancellationToken ct)
    {
        Items.RemoveAll(p => p.Id == id);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryNetworkRepository : INetworkRepository
{
    public List<NetworkProfile> Items = new();
    private long _nextId = 1;

    public Task<NetworkProfile?> GetByIdentifierAsync(string identifier, CancellationToken ct) =>
        Task.FromResult(Items.FirstOrDefault(n => n.NetworkIdentifier == identifier));

    public Task<IReadOnlyList<NetworkProfile>> GetAllAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<NetworkProfile>>(Items.ToList());

    public Task SaveAsync(NetworkProfile profile, CancellationToken ct)
    {
        var index = Items.FindIndex(n => n.NetworkIdentifier == profile.NetworkIdentifier);
        if (index < 0)
        {
            profile.Id = _nextId++;
            Items.Add(profile);
        }
        else
        {
            profile.Id = Items[index].Id;
            Items[index] = profile;
        }

        return Task.CompletedTask;
    }

    public Task SetPreferredModeAsync(string identifier, string mode, long? preferredProxyId, CancellationToken ct)
    {
        var found = Items.FirstOrDefault(n => n.NetworkIdentifier == identifier);
        if (found is not null)
        {
            found.PreferredMode = mode;
            found.PreferredProxyId = preferredProxyId;
        }

        return Task.CompletedTask;
    }

    public Task DeleteByIdentifierAsync(string identifier, CancellationToken ct)
    {
        Items.RemoveAll(n => n.NetworkIdentifier == identifier);
        return Task.CompletedTask;
    }
}

public sealed class FakeSettingsRepository : ISettingsRepository
{
    public AppSettings Settings = new();

    public Task<AppSettings> LoadAsync(CancellationToken ct) => Task.FromResult(Settings);

    public Task SaveAsync(AppSettings settings, CancellationToken ct)
    {
        Settings = settings;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryHistoryRepository : IDetectionHistoryRepository
{
    public List<DetectionResult> Items = new();

    public Task AddAsync(DetectionResult result, CancellationToken ct)
    {
        Items.Add(result);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DetectionResult>> GetRecentAsync(int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DetectionResult>>(Items.TakeLast(limit).ToList());
}

public sealed class FakeNetworkMonitor : INetworkMonitor
{
    public CurrentNetwork? Network { get; set; }
    public string Signature { get; set; } = "constant-signature";

    public event EventHandler<NetworkChangedEventArgs>? NetworkChanged { add { } remove { } }
    public event EventHandler? NetworkUnavailable { add { } remove { } }

    public Task<CurrentNetwork?> GetCurrentNetworkAsync(CancellationToken ct) => Task.FromResult(Network);

    public string GetQuickSignature() => Signature;

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public Task StopAsync() => Task.CompletedTask;
}

public sealed class FakeConnectivityTester : IConnectivityTester
{
    private readonly Func<AppSettings, DirectTestResult> _producer;
    public int Calls { get; private set; }

    public FakeConnectivityTester(DirectTestResult result) => _producer = _ => result;

    public FakeConnectivityTester(Func<AppSettings, DirectTestResult> producer) => _producer = producer;

    public Task<DirectTestResult> TestDirectAsync(AppSettings settings, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(_producer(settings));
    }
}

public sealed class WorkingProxyTester : IProxyTester
{
    public Func<ProxyProfile, ProxyTestResult>? Behavior { get; set; }

    public Task<ProxyTestResult> TestAsync(ProxyProfile proxy, AppSettings settings, CancellationToken ct) =>
        Task.FromResult(Behavior?.Invoke(proxy) ?? new ProxyTestResult
        {
            Proxy = proxy,
            Outcome = ProxyTestOutcome.Success,
            LatencyMs = 5,
        });

    public Task<(bool IsServing, long LatencyMs)> GetProxyHealthAsync(
        ProxyProfile proxy, AppSettings settings, CancellationToken ct) =>
        Task.FromResult((true, 5L));
}

public sealed class FakeProxyManager : IWindowsProxyManager
{
    public ProxySettings Current { get; private set; } = ProxySettings.Disabled();
    public ProxySettings? Expected { get; private set; }
    public string? Host { get; private set; }
    public int Port { get; private set; }
    public bool Enabled => Current.Enabled;
    public bool RestoreCalled { get; private set; }
    public List<string> Calls { get; } = new();

    /// <summary>Optional failure injection: return false to make the apply fail.</summary>
    public Func<string, int, bool>? EnableHandler { get; set; }

    public event EventHandler? WindowsProxyChanged { add { } remove { } }

    public ProxySettings GetCurrentSettings() => Current;

    public ProxySettings? GetExpectedSettings() => Expected;

    public bool EnableProxy(string host, int port, string? bypassList = null)
    {
        if (EnableHandler?.Invoke(host, port) == false)
        {
            Calls.Add($"enable-fail:{host}:{port}");
            return false;
        }

        Host = host;
        Port = port;
        Current = ProxySettings.FromProxy(host, port);
        Expected = Current.Clone();
        Calls.Add($"enable:{host}:{port}");
        return true;
    }

    public bool DisableProxy()
    {
        Host = null;
        Port = 0;
        Current = ProxySettings.Disabled();
        Expected = Current.Clone();
        Calls.Add("disable");
        return true;
    }

    public bool RestorePreviousSettings()
    {
        RestoreCalled = true;
        return true;
    }

    public void SaveSnapshot(bool force) { }

    public void RestorePreviousSettingsOnStartup() { }

    public string DescribeScope() => "test";
}

public sealed class TestHarness
{
    public FakeNetworkMonitor Monitor { get; }
    public FakeConnectivityTester Connectivity { get; }
    public WorkingProxyTester ProxyTester { get; }
    public InMemoryProxyRepository ProxyRepo { get; } = new();
    public InMemoryNetworkRepository NetworkRepo { get; } = new();
    public FakeSettingsRepository SettingsRepo { get; } = new();
    public InMemoryHistoryRepository HistoryRepo { get; } = new();
    public SilentLog Log { get; } = new();
    public AppStateService State { get; } = new();
    public FakeProxyManager ProxyManager { get; } = new();
    public DetectionEngine Engine { get; }

    private readonly IConnectivityTester _connectivity;

    public TestHarness(
        IConnectivityTester? connectivity = null,
        CurrentNetwork? network = null,
        WorkingProxyTester? proxyTester = null)
    {
        Monitor = new FakeNetworkMonitor { Network = network ?? Connected(HomeNetwork) };
        _connectivity = connectivity ?? new FakeConnectivityTester(DirectOk());
        Connectivity = _connectivity as FakeConnectivityTester ?? new FakeConnectivityTester(DirectOk());
        ProxyTester = proxyTester ?? new WorkingProxyTester();
        Engine = new DetectionEngine(
            Monitor, _connectivity, ProxyTester, ProxyRepo, NetworkRepo, SettingsRepo,
            HistoryRepo, Log, new DecisionEngine());
    }

    public MonitoringService CreateService() =>
        new(Monitor, Engine, _connectivity, ProxyTester, ProxyManager,
            new ApplyEngine(ProxyManager, ProxyTester, Log),
            SettingsRepo, Log, State);

    public static readonly NetworkIdentity HomeNetwork = new()
    {
        Identifier = "wifi|abc|HomeNet",
        DisplayName = "HomeNet",
        InterfaceType = "wifi",
    };

    public static CurrentNetwork Connected(NetworkIdentity identity, bool hasRoute = true) =>
        new() { Identity = identity, HasActiveRoute = hasRoute, DetectedAt = DateTime.UtcNow };

    public static DirectTestResult DirectOk(params long[] latencies) =>
        new()
        {
            Endpoints = latencies.Length == 0
                ? new[] { Ok("https://probe/204", 20) }
                : latencies.Select(l => Ok("https://probe/204", l)).ToArray(),
        };

    public static DirectTestResult DirectFail(string error = "timeout") =>
        new()
        {
            Endpoints = new[]
            {
                new EndpointResult { Url = "https://probe/204", Success = false, Error = error },
            },
        };

    public static EndpointResult Ok(string url, long latency) =>
        new() { Url = url, Success = true, HttpStatus = 204, LatencyMs = latency };
}