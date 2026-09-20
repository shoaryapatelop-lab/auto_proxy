using AutoProxy.Core.Models;
using Xunit;

namespace AutoProxy.Core.Tests;

public class DetectionEngineIntegrationTests
{
    [Fact]
    public async Task Empty_proxy_store_and_direct_ok_is_direct()
    {
        var harness = new TestHarness();

        var result = await harness.Engine.DetectAsync(CancellationToken.None);

        Assert.Equal(ConnectionState.Direct, result.State);
        Assert.Null(result.SelectedProxy);
        Assert.False(result.NetworkChangedDuringTest);
    }

    [Fact]
    public async Task Direct_failure_with_working_proxy_selects_and_remembers_proxy()
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

        var result = await harness.Engine.DetectAsync(CancellationToken.None);

        Assert.Equal(ConnectionState.Proxy, result.State);
        Assert.Equal("corp", result.SelectedProxy!.Name);

        var remembered = await harness.NetworkRepo.GetByIdentifierAsync(
            TestHarness.HomeNetwork.Identifier, CancellationToken.None);
        Assert.NotNull(remembered);
        Assert.Equal("Proxy", remembered!.PreferredMode);
        Assert.Equal(1, remembered.PreferredProxyId);
    }

    [Fact]
    public async Task Preferred_proxy_is_tested_first()
    {
        var harness = new TestHarness(connectivity: new FakeConnectivityTester(TestHarness.DirectFail()));
        harness.ProxyRepo.Items.Add(new ProxyProfile
        {
            Id = 1,
            Name = "a",
            Host = "1.1.1.1",
            Port = 1,
            Enabled = true,
            SuccessCount = 10,
            AverageLatencyMs = 100,
        });
        harness.ProxyRepo.Items.Add(new ProxyProfile
        {
            Id = 2,
            Name = "b",
            Host = "2.2.2.2",
            Port = 2,
            Enabled = true,
            SuccessCount = 10,
            AverageLatencyMs = 500,
        });
        harness.NetworkRepo.Items.Add(new NetworkProfile
        {
            NetworkIdentifier = TestHarness.HomeNetwork.Identifier,
            PreferredMode = "Proxy",
            PreferredProxyId = 2,
        });

        var result = await harness.Engine.DetectAsync(CancellationToken.None);

        Assert.Equal(ConnectionState.Proxy, result.State);
        Assert.Equal("b", result.SelectedProxy!.Name);
    }

    [Fact]
    public async Task No_active_network_is_offline()
    {
        var harness = new TestHarness(network: new CurrentNetwork
        {
            Identity = TestHarness.HomeNetwork,
            HasActiveRoute = false,
        });

        var result = await harness.Engine.DetectAsync(CancellationToken.None);

        Assert.Equal(ConnectionState.Offline, result.State);
    }

    [Fact]
    public async Task Network_profile_is_upserted_on_first_detection()
    {
        var harness = new TestHarness();

        var result = await harness.Engine.DetectAsync(CancellationToken.None);

        Assert.Equal(ConnectionState.Direct, result.State);

        var stored = await harness.NetworkRepo.GetByIdentifierAsync(
            TestHarness.HomeNetwork.Identifier, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal("Direct", stored!.PreferredMode);
    }

    [Fact]
    public async Task Result_carries_direct_and_proxy_diagnostics()
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
        harness.ProxyRepo.Items.Add(new ProxyProfile
        {
            Id = 2,
            Name = "alternate",
            Host = "10.0.0.2",
            Port = 3128,
            Enabled = true,
        });

        var result = await harness.Engine.DetectAsync(CancellationToken.None);

        Assert.NotNull(result.DirectTest);
        Assert.False(result.DirectTest!.InternetAvailable);
        Assert.Equal(1, result.ProxiesTested);
        Assert.Equal(1, result.ProxiesPassed);
        Assert.Equal(ConnectionState.Proxy, result.State);
        Assert.Equal("corp", result.SelectedProxy!.Name);
    }
}