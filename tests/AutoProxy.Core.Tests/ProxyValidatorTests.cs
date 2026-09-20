using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Models;
using AutoProxy.Core.Network;
using Xunit;

namespace AutoProxy.Core.Tests;

public class ProxyValidatorTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.1.10")]
    [InlineData("proxy.example.com")]
    [InlineData("my-proxy.local")]
    [InlineData("localhost")]
    public void Valid_hosts_are_accepted(string host) =>
        Assert.True(ProxyValidator.IsValidHost(host));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://proxy.example.com")]
    [InlineData("bad host")]
    [InlineData("-leading.example.com")]
    public void Invalid_hosts_are_rejected(string host) =>
        Assert.False(ProxyValidator.IsValidHost(host));

    [Theory]
    [InlineData(1)]
    [InlineData(8080)]
    [InlineData(65535)]
    public void Valid_ports_are_accepted(int port) =>
        Assert.True(ProxyValidator.IsValidPort(port));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Invalid_ports_are_rejected(int port) =>
        Assert.False(ProxyValidator.IsValidPort(port));

    [Fact]
    public void Validate_reports_missing_name()
    {
        var profile = new ProxyProfile { Name = "", Host = "proxy", Port = 8080 };

        Assert.NotNull(ProxyValidator.Validate(profile));
    }

    [Fact]
    public void Validate_reports_bad_port()
    {
        var profile = new ProxyProfile { Name = "corp", Host = "proxy", Port = 0 };

        Assert.NotNull(ProxyValidator.Validate(profile));
    }

    [Fact]
    public void Validate_accepts_a_complete_profile()
    {
        var profile = new ProxyProfile { Name = "corp", Host = "10.0.0.1", Port = 3128 };

        Assert.Null(ProxyValidator.Validate(profile));
    }

    [Theory]
    [InlineData("127.0.0.1", 8080, "127.0.0.1:8080")]
    [InlineData("proxy.example.com", 8080, "proxy.example.com:8080")]
    [InlineData("::1", 8080, "[::1]:8080")]
    [InlineData("2001:db8::5", 3128, "[2001:db8::5]:3128")]
    [InlineData(" 10.0.0.1 ", 8080, "10.0.0.1:8080")]
    public void FormatAddress_formats_endpoints_correctly(string host, int port, string expected)
    {
        Assert.Equal(expected, ProxyValidator.FormatAddress(host, port));
    }
}

public class AppSettingsTests
{
    [Fact]
    public void Effective_urls_include_defaults_and_custom()
    {
        var settings = new AppSettings
        {
            ProbeUrls = new[] { "https://a/204" },
            CustomProbeUrl = "https://custom/204",
        };

        var urls = settings.EffectiveProbeUrls();

        Assert.Equal(2, urls.Count);
        Assert.Contains("https://a/204", urls);
        Assert.Contains("https://custom/204", urls);
    }

    [Fact]
    public void Effective_urls_dedupe_and_ignore_blanks()
    {
        var settings = new AppSettings
        {
            ProbeUrls = new[] { "https://a/204", "  ", "https://a/204" },
            CustomProbeUrl = "https://a/204",
        };

        var urls = settings.EffectiveProbeUrls();

        Assert.Single(urls);
    }

    [Fact]
    public void Proxy_settings_factories_are_consistent()
    {
        var disabled = ProxySettings.Disabled();
        Assert.False(disabled.Enabled);
        Assert.Null(disabled.Server);

        var enabled = ProxySettings.FromProxy("proxy", 8080);
        Assert.True(enabled.Enabled);
        Assert.Equal("proxy:8080", enabled.Server);

        var ipv6 = ProxySettings.FromProxy("::1", 8080);
        Assert.Equal("[::1]:8080", ipv6.Server);
    }
}
