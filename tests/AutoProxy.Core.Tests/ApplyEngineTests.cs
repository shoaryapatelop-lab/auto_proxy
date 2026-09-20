using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Apply;
using AutoProxy.Core.Models;
using Xunit;

namespace AutoProxy.Core.Tests;

public class ApplyEngineTests
{
    private sealed class FakeApplyProxyManager : IWindowsProxyManager
    {
        public ProxySettings Current { get; set; } = ProxySettings.Disabled();
        public Func<ProxySettings>? CurrentOverride { get; set; }
        public bool EnableResult = true;
        public bool DisableResult = true;
        public bool RestoreResult = true;
        public int EnableCount;
        public int DisableCount;
        public int RestoreCount;
        public int SnapshotCount;
        public ProxySettings? Expected;

        public ProxySettings GetCurrentSettings() => CurrentOverride?.Invoke() ?? Current;
        public ProxySettings? GetExpectedSettings() => Expected;

        public bool EnableProxy(string host, int port, string? bypassList = null)
        {
            EnableCount++;
            if (!EnableResult) return false;
            Current = ProxySettings.FromProxy(host, port);
            Expected = Current.Clone();
            return true;
        }

        public bool DisableProxy()
        {
            DisableCount++;
            if (!DisableResult) return false;
            Current = ProxySettings.Disabled();
            Expected = ProxySettings.Disabled();
            return true;
        }

        public bool RestorePreviousSettings()
        {
            RestoreCount++;
            return RestoreResult;
        }

        public void SaveSnapshot(bool force) => SnapshotCount++;

        public void RestorePreviousSettingsOnStartup()
        {
        }

        public string DescribeScope() => "test";

        public event EventHandler? WindowsProxyChanged { add { } remove { } }
    }

    private sealed class ConfigurableProxyTester : IProxyTester
    {
        public Func<ProxyProfile, (bool IsServing, long LatencyMs)>? HealthBehavior { get; set; }
        public int HealthCalls { get; private set; }

        public Task<ProxyTestResult> TestAsync(
            ProxyProfile proxy, AppSettings settings, CancellationToken ct) =>
            Task.FromResult(new ProxyTestResult
            {
                Proxy = proxy,
                Outcome = ProxyTestOutcome.Success,
                LatencyMs = 5,
            });

        public Task<(bool IsServing, long LatencyMs)> GetProxyHealthAsync(
            ProxyProfile proxy, AppSettings settings, CancellationToken ct)
        {
            HealthCalls++;
            return Task.FromResult(HealthBehavior?.Invoke(proxy) ?? (true, 5L));
        }
    }

    private static ProxyProfile Corp() =>
        new() { Id = 1, Name = "corp", Host = "10.0.0.1", Port = 3128 };

    private static AppSettings Settings() => new();

    private static (FakeApplyProxyManager pm, ConfigurableProxyTester pt, ApplyEngine engine) Build()
    {
        var pm = new FakeApplyProxyManager();
        var pt = new ConfigurableProxyTester();
        return (pm, pt, new ApplyEngine(pm, pt, new SilentLog()));
    }

    [Fact]
    public async Task Proxy_apply_succeeds_and_verifies_traffic()
    {
        var (pm, pt, engine) = Build();

        var outcome = await engine.ApplyDesiredAsync(
            ConnectionState.Proxy, Corp(), Settings(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.True(outcome.ConnectivityVerified);
        Assert.Equal(1, pm.EnableCount);
        Assert.Equal(0, pm.DisableCount);
        Assert.Equal(1, pt.HealthCalls);
        Assert.True(pm.Current.Enabled);
    }

    [Fact]
    public async Task Direct_apply_disables_without_connectivity_verify()
    {
        var (pm, pt, engine) = Build();
        pm.Current = ProxySettings.FromProxy("10.9.9.9", 8080);

        var outcome = await engine.ApplyDesiredAsync(
            ConnectionState.Direct, proxy: null, Settings(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(1, pm.DisableCount);
        Assert.Equal(0, pm.EnableCount);
        Assert.Equal(0, pt.HealthCalls);
        Assert.False(pm.Current.Enabled);
    }

    [Theory]
    [InlineData(ConnectionState.Offline)]
    [InlineData(ConnectionState.CaptivePortal)]
    public async Task Non_connectable_states_also_disable_the_proxy(ConnectionState state)
    {
        var (pm, pt, engine) = Build();
        pm.Current = ProxySettings.FromProxy("10.9.9.9", 8080);

        var outcome = await engine.ApplyDesiredAsync(
            state, proxy: null, Settings(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(1, pm.DisableCount);
        Assert.Equal(0, pt.HealthCalls);
    }

    [Fact]
    public async Task Already_in_desired_state_is_a_no_op()
    {
        var (pm, pt, engine) = Build();
        pm.Current = ProxySettings.FromProxy("10.0.0.1", 3128);

        var outcome = await engine.ApplyDesiredAsync(
            ConnectionState.Proxy, Corp(), Settings(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(0, pm.EnableCount);
        Assert.Equal(0, pm.DisableCount);
        Assert.Equal(0, pt.HealthCalls);
    }

    [Fact]
    public async Task Proxy_state_without_a_proxy_profile_fails()
    {
        var (pm, pt, engine) = Build();

        var outcome = await engine.ApplyDesiredAsync(
            ConnectionState.Proxy, proxy: null, Settings(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(0, pm.EnableCount);
    }

    [Fact]
    public async Task Enable_failure_triggers_rollback()
    {
        var (pm, _, engine) = Build();
        pm.EnableResult = false;

        var outcome = await engine.ApplyDesiredAsync(
            ConnectionState.Proxy, Corp(), Settings(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.True(outcome.RolledBack);
        Assert.Equal(1, pm.RestoreCount);
        Assert.Contains("failed to apply", outcome.Describe(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Enable_failure_with_failed_rollback_is_explicitly_flagged()
    {
        var (pm, _, engine) = Build();
        pm.EnableResult = false;
        pm.RestoreResult = false;

        var outcome = await engine.ApplyDesiredAsync(
            ConnectionState.Proxy, Corp(), Settings(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.False(outcome.RolledBack);
        Assert.Contains("manually", outcome.Describe());
    }

    [Fact]
    public async Task Read_back_mismatch_triggers_rollback_without_health_check()
    {
        var (pm, pt, engine) = Build();
        pm.CurrentOverride = () => ProxySettings.Disabled();

        var outcome = await engine.ApplyDesiredAsync(
            ConnectionState.Proxy, Corp(), Settings(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.True(outcome.RolledBack);
        Assert.Equal(1, pm.RestoreCount);
        Assert.Equal(0, pt.HealthCalls);
        Assert.Contains("read-back", outcome.Error);
    }

    [Fact]
    public async Task Proxy_traffic_verification_failure_triggers_rollback()
    {
        var (pm, pt, engine) = Build();
        pt.HealthBehavior = _ => (false, 0);

        var outcome = await engine.ApplyDesiredAsync(
            ConnectionState.Proxy, Corp(), Settings(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.True(outcome.RolledBack);
        Assert.Equal(1, pm.RestoreCount);
        Assert.Equal(1, pt.HealthCalls);
    }

    [Fact]
    public async Task Verification_is_skipped_when_disabled_in_settings()
    {
        var (pm, pt, engine) = Build();
        var settings = Settings();
        settings.VerifyProxyAfterApply = false;

        var outcome = await engine.ApplyDesiredAsync(
            ConnectionState.Proxy, Corp(), settings, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.False(outcome.ConnectivityVerified);
        Assert.Equal(0, pt.HealthCalls);
    }
}