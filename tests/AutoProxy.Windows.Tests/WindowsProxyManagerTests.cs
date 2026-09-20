using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Models;
using Microsoft.Win32;
using Xunit;

namespace AutoProxy.Windows.Tests;

public class WindowsProxyManagerTests
{
    private const string InternetSettingsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string ConnectionsKey =
        InternetSettingsKey + @"\Connections";

    private sealed record RegistryState(
        object? Enable, object? Server, object? Bypass, object? Dcs, object? Sls);

    [Fact]
    public void Apply_and_restore_round_trips_the_system_proxy()
    {
        var original = Capture();
        var manager = new WindowsProxyManager(new NullLogService());

        try
        {
            Assert.True(manager.EnableProxy("127.0.0.1", 9, "localhost;<local>"));

            var applied = manager.GetCurrentSettings();
            Assert.True(applied.Enabled);
            Assert.Equal("127.0.0.1:9", applied.Server);
            Assert.Equal("localhost;<local>", applied.BypassList);

            var expected = manager.GetExpectedSettings();
            Assert.NotNull(expected);
            Assert.Equal("127.0.0.1:9", expected!.Server);

            Assert.True(manager.RestorePreviousSettings());
        }
        finally
        {
            manager.RestorePreviousSettings();
            Restore(original);
        }

        var finalSettings = new WindowsProxyManager(new NullLogService()).GetCurrentSettings();
        Assert.Equal(original.Enable is int e && e != 0, finalSettings.Enabled);
        Assert.Equal(original.Server as string, finalSettings.Server);
        Assert.Equal(original.Bypass as string, finalSettings.BypassList);
    }

    [Fact]
    public void Disable_and_restore_round_trips_the_system_proxy()
    {
        var original = Capture();
        var manager = new WindowsProxyManager(new NullLogService());

        try
        {
            Assert.True(manager.DisableProxy());
            Assert.False(manager.GetCurrentSettings().Enabled);
            Assert.True(manager.RestorePreviousSettings());
        }
        finally
        {
            manager.RestorePreviousSettings();
            Restore(original);
        }

        var finalSettings = new WindowsProxyManager(new NullLogService()).GetCurrentSettings();
        Assert.Equal(original.Enable is int e && e != 0, finalSettings.Enabled);
        Assert.Equal(original.Server as string, finalSettings.Server);
        Assert.Equal(original.Bypass as string, finalSettings.BypassList);
    }

    private static RegistryState Capture()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey);
        using var conn = Registry.CurrentUser.OpenSubKey(ConnectionsKey);
        return new RegistryState(
            key?.GetValue("ProxyEnable"),
            key?.GetValue("ProxyServer"),
            key?.GetValue("ProxyOverride"),
            conn?.GetValue("DefaultConnectionSettings"),
            conn?.GetValue("SavedLegacySettings"));
    }

    private static void Restore(RegistryState state)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(InternetSettingsKey, writable: true))
        {
            SetValue(key, "ProxyEnable", state.Enable);
            SetValue(key, "ProxyServer", state.Server);
            SetValue(key, "ProxyOverride", state.Bypass);
        }

        using (var conn = Registry.CurrentUser.CreateSubKey(ConnectionsKey, writable: true))
        {
            SetValue(conn, "DefaultConnectionSettings", state.Dcs);
            SetValue(conn, "SavedLegacySettings", state.Sls);
        }
    }

    private static void SetValue(RegistryKey key, string name, object? value)
    {
        switch (value)
        {
            case null:
                key.DeleteValue(name, throwOnMissingValue: false);
                break;
            case int i:
                key.SetValue(name, i, RegistryValueKind.DWord);
                break;
            case byte[] bytes:
                key.SetValue(name, bytes, RegistryValueKind.Binary);
                break;
            case string s:
                key.SetValue(name, s, RegistryValueKind.String);
                break;
        }
    }

    private sealed class NullLogService : ILogService
    {
#pragma warning disable CS0067
        public event EventHandler<LogEntry>? EntryAdded;
#pragma warning restore CS0067

        public IReadOnlyList<LogEntry> GetRecentlyAdded(int limit) => Array.Empty<LogEntry>();

        public void Verbose(string category, string message, string? detail = null)
        {
        }

        public void Info(string category, string message, string? detail = null,
            long? networkId = null, long? proxyId = null)
        {
        }

        public void Warning(string category, string message, string? detail = null,
            long? networkId = null, long? proxyId = null)
        {
        }

        public void Error(string category, string message, string? detail = null,
            long? networkId = null, long? proxyId = null)
        {
        }
    }
}
