using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Models;
using Xunit;

namespace AutoProxy.Core.Tests;

public sealed class StagedConnectivityTester : IConnectivityTester
{
    private readonly DirectTestResult _subsequentResult;
    private readonly TaskCompletionSource _firstStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);
    public bool FirstCallWasCancelled { get; private set; }
    public Task FirstCallStarted => _firstStarted.Task;

    public StagedConnectivityTester(DirectTestResult subsequentResult)
    {
        _subsequentResult = subsequentResult;
    }

    public Task<DirectTestResult> TestDirectAsync(AppSettings settings, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _calls) == 1)
        {
            _firstStarted.TrySetResult();
            var tcs = new TaskCompletionSource<DirectTestResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() =>
            {
                FirstCallWasCancelled = true;
                tcs.TrySetCanceled();
            });
            return tcs.Task;
        }

        return Task.FromResult(_subsequentResult);
    }
}

public class MonitoringServiceTests
{
    [Fact]
    public async Task New_trigger_cancels_in_flight_detection_and_runs_newest()
    {
        var connectivity = new StagedConnectivityTester(TestHarness.DirectOk());
        var harness = new TestHarness(connectivity: connectivity);
        harness.ProxyRepo.Items.Add(new ProxyProfile
        {
            Id = 1,
            Name = "corp",
            Host = "10.0.0.1",
            Port = 3128,
            Enabled = true,
        });

        await using var service = harness.CreateService();

        var first = service.TriggerDetectionAsync(
            force: true, reason: "startup", CancellationToken.None);
        await connectivity.FirstCallStarted;

        // Supersede the in-flight detection while it is blocked.
        var second = service.TriggerDetectionAsync(
            force: true, reason: "nudge", CancellationToken.None);

        await Task.WhenAll(first, second);

        Assert.True(connectivity.FirstCallWasCancelled);
        Assert.Equal(2, connectivity.Calls);
        Assert.Equal(ConnectionState.Direct, harness.State.LastResult!.State);
        Assert.True(harness.State.LastResult.Timestamp <= DateTime.UtcNow.AddSeconds(1));
        // The stale (proxy-select) result must never have been applied.
        Assert.DoesNotContain(harness.ProxyManager.Calls, c => c.StartsWith("enable:"));
    }

    [Fact]
    public async Task Offline_direct_with_working_proxy_applies_proxy()
    {
        var harness = new TestHarness(connectivity: new FakeConnectivityTester(TestHarness.DirectFail()));
        harness.ProxyRepo.Items.Add(new ProxyProfile
        {
            Id = 1,
            Name = "corp",
            Host = "10.0.0.1",
            Port = 3128,
            Enabled = true,
        });

        await using var service = harness.CreateService();

        await service.TriggerDetectionAsync(
            force: true, reason: "startup", CancellationToken.None);

        Assert.Equal(ConnectionState.Proxy, harness.State.LastResult!.State);
        Assert.Equal("corp", harness.State.LastResult.SelectedProxy!.Name);
        Assert.Equal("10.0.0.1", harness.ProxyManager.Host);
        Assert.Equal(3128, harness.ProxyManager.Port);
        Assert.True(harness.ProxyManager.Enabled);
    }

    [Fact]
    public async Task Manual_test_reports_without_touching_the_system_proxy()
    {
        var harness = new TestHarness(connectivity: new FakeConnectivityTester(TestHarness.DirectFail()));
        harness.ProxyRepo.Items.Add(new ProxyProfile
        {
            Id = 1,
            Name = "corp",
            Host = "10.0.0.1",
            Port = 3128,
            Enabled = true,
        });

        await using var service = harness.CreateService();

        var result = await service.ManualTestNowAsync(CancellationToken.None);

        Assert.Equal(ConnectionState.Proxy, result.State);
        Assert.False(harness.ProxyManager.Enabled);
        Assert.Empty(harness.ProxyManager.Calls);
    }

    [Fact]
    public async Task Manual_mode_commits_a_truthful_state()
    {
        var harness = new TestHarness();
        await using var service = harness.CreateService();

        await service.SetModeAsync(
            AppMode.ManualDirect, manualProxy: null, CancellationToken.None);

        Assert.Equal(AppMode.ManualDirect, harness.State.Mode);
        Assert.Equal(ConnectionState.Direct, harness.State.State);
        Assert.Equal("Manual Direct mode — Windows proxy disabled.", harness.State.LastResult?.Detail);
    }

    [Fact]
    public async Task Apply_failure_sets_error_state_and_rolls_back()
    {
        var harness = new TestHarness(connectivity: new FakeConnectivityTester(TestHarness.DirectFail()));
        harness.ProxyRepo.Items.Add(new ProxyProfile
        {
            Id = 1,
            Name = "corp",
            Host = "10.0.0.1",
            Port = 3128,
            Enabled = true,
        });
        harness.ProxyManager.EnableHandler = (_, _) => false;

        await using var service = harness.CreateService();

        await service.TriggerDetectionAsync(
            force: true, reason: "startup", CancellationToken.None);

        Assert.Equal(ConnectionState.Error, harness.State.State);
        Assert.True(harness.ProxyManager.RestoreCalled);
        Assert.Equal("enable-fail:10.0.0.1:3128", Assert.Single(harness.ProxyManager.Calls));
    }
}