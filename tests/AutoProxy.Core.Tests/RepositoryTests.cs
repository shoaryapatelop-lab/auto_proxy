using AutoProxy.Core.Models;
using AutoProxy.Data;
using Xunit;

namespace AutoProxy.Core.Tests;

public class RepositoryTests
{
    private static string NewDatabasePath()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "autoproxy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "test.db");
    }

    private static void Cleanup(string dbPath)
    {
        var directory = Path.GetDirectoryName(dbPath)!;
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // SQLite may keep WAL handles briefly; best-effort cleanup.
        }
    }

    private static ProxyProfile SampleProxy(string name = "corp") => new()
    {
        Name = name,
        Host = "proxy.example.com",
        Port = 8080,
        AuthenticationRequired = true,
        CredentialTarget = "AutoProxy:proxy:corp",
        Enabled = true,
    };

    // ---------- DatabaseContext path handling ----------

    [Fact]
    public async Task Explicit_file_path_is_used_as_the_database_file()
    {
        var path = NewDatabasePath();
        try
        {
            var context = new DatabaseContext(path);
            Assert.Equal(Path.GetFullPath(path), Path.GetFullPath(context.DatabasePath));
            Assert.True(File.Exists(path));
            await Task.CompletedTask;
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Extensionless_path_is_treated_as_a_directory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "autoproxy-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var context = new DatabaseContext(directory);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(directory, "autoproxy.db")),
                Path.GetFullPath(context.DatabasePath));
            Assert.True(File.Exists(context.DatabasePath));
        }
        finally
        {
            Cleanup(Path.Combine(directory, "autoproxy.db"));
        }
    }

    // ---------- ProxyRepository ----------

    [Fact]
    public async Task Proxy_crud_round_trips_all_fields()
    {
        var path = NewDatabasePath();
        try
        {
            var context = new DatabaseContext(path);
            var repo = new ProxyRepository(context);

            var id = await repo.AddAsync(SampleProxy(), CancellationToken.None);
            Assert.True(id > 0);

            var loaded = await repo.GetByIdAsync(id, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal("corp", loaded!.Name);
            Assert.Equal("proxy.example.com", loaded.Host);
            Assert.Equal(8080, loaded.Port);
            Assert.True(loaded.AuthenticationRequired);
            Assert.Equal("AutoProxy:proxy:corp", loaded.CredentialTarget);
            Assert.True(loaded.Enabled);

            loaded.Name = "corp-updated";
            loaded.Enabled = false;
            loaded.SuccessCount = 5;
            loaded.FailureCount = 2;
            loaded.AverageLatencyMs = 42;
            await repo.UpdateAsync(loaded, CancellationToken.None);

            var updated = await repo.GetByIdAsync(id, CancellationToken.None);
            Assert.Equal("corp-updated", updated!.Name);
            Assert.False(updated.Enabled);
            Assert.Equal(5, updated.SuccessCount);
            Assert.Equal(2, updated.FailureCount);
            Assert.Equal(42, updated.AverageLatencyMs);

            Assert.Empty(await repo.GetEnabledAsync(CancellationToken.None));

            await repo.DeleteAsync(id, CancellationToken.None);
            Assert.Null(await repo.GetByIdAsync(id, CancellationToken.None));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task GetEnabled_filters_disabled_proxies()
    {
        var path = NewDatabasePath();
        try
        {
            var context = new DatabaseContext(path);
            var repo = new ProxyRepository(context);

            await repo.AddAsync(SampleProxy("on"), CancellationToken.None);
            var off = SampleProxy("off");
            off.Enabled = false;
            await repo.AddAsync(off, CancellationToken.None);

            var enabled = await repo.GetEnabledAsync(CancellationToken.None);
            Assert.Single(enabled);
            Assert.Equal("on", enabled[0].Name);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ---------- NetworkRepository ----------

    [Fact]
    public async Task Network_upsert_assigns_id_and_updates_in_place()
    {
        var path = NewDatabasePath();
        try
        {
            var context = new DatabaseContext(path);
            var repo = new NetworkRepository(context);

            var profile = new NetworkProfile
            {
                NetworkIdentifier = "wifi|abc|HomeNet",
                Ssid = "HomeNet",
                InterfaceType = "wifi",
                PreferredMode = "Detecting",
                LastSeen = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
            };
            await repo.SaveAsync(profile, CancellationToken.None);

            var stored = await repo.GetByIdentifierAsync("wifi|abc|HomeNet", CancellationToken.None);
            Assert.NotNull(stored);
            Assert.True(stored!.Id > 0);
            Assert.Equal("HomeNet", stored.Ssid);

            stored.PreferredMode = "Proxy";
            stored.LastSeen = DateTime.UtcNow;
            await repo.SaveAsync(stored, CancellationToken.None);

            var all = await repo.GetAllAsync(CancellationToken.None);
            Assert.Single(all);

            await repo.SetPreferredModeAsync(
                "wifi|abc|HomeNet", "Direct", null, CancellationToken.None);
            var afterMode = await repo.GetByIdentifierAsync("wifi|abc|HomeNet", CancellationToken.None);
            Assert.Equal("Direct", afterMode!.PreferredMode);

            await repo.DeleteByIdentifierAsync("wifi|abc|HomeNet", CancellationToken.None);
            Assert.Null(await repo.GetByIdentifierAsync("wifi|abc|HomeNet", CancellationToken.None));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Deleting_a_preferred_proxy_clears_the_network_reference()
    {
        var path = NewDatabasePath();
        try
        {
            var context = new DatabaseContext(path);
            var proxyRepo = new ProxyRepository(context);
            var networkRepo = new NetworkRepository(context);

            var proxyId = await proxyRepo.AddAsync(SampleProxy(), CancellationToken.None);

            var profile = new NetworkProfile
            {
                NetworkIdentifier = "wifi|abc|Office",
                PreferredMode = "Proxy",
                PreferredProxyId = proxyId,
                LastSeen = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
            };
            await networkRepo.SaveAsync(profile, CancellationToken.None);

            var before = await networkRepo.GetByIdentifierAsync("wifi|abc|Office", CancellationToken.None);
            Assert.Equal(proxyId, before!.PreferredProxyId);

            await proxyRepo.DeleteAsync(proxyId, CancellationToken.None);

            var after = await networkRepo.GetByIdentifierAsync("wifi|abc|Office", CancellationToken.None);
            Assert.NotNull(after);
            Assert.Null(after!.PreferredProxyId);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ---------- SettingsRepository ----------

    [Fact]
    public async Task Settings_return_defaults_when_unset_and_round_trip_when_saved()
    {
        var path = NewDatabasePath();
        try
        {
            var context = new DatabaseContext(path);
            var repo = new SettingsRepository(context);

            var defaults = await repo.LoadAsync(CancellationToken.None);
            Assert.Equal(60, defaults.HealthCheckIntervalSeconds);
            Assert.True(defaults.StartMonitoringOnLaunch);

            defaults.HealthCheckIntervalSeconds = 15;
            defaults.StartMonitoringOnLaunch = false;
            defaults.CustomProbeUrl = "https://example.com/generate_204";
            await repo.SaveAsync(defaults, CancellationToken.None);

            var loaded = await repo.LoadAsync(CancellationToken.None);
            Assert.Equal(15, loaded.HealthCheckIntervalSeconds);
            Assert.False(loaded.StartMonitoringOnLaunch);
            Assert.Equal("https://example.com/generate_204", loaded.CustomProbeUrl);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ---------- DetectionHistoryRepository ----------

    [Fact]
    public async Task Detection_history_is_persisted_and_read_back()
    {
        var path = NewDatabasePath();
        try
        {
            var context = new DatabaseContext(path);
            var networkRepo = new NetworkRepository(context);
            var history = new DetectionHistoryRepository(context);

            await networkRepo.SaveAsync(new NetworkProfile
            {
                NetworkIdentifier = "eth|1|gw",
                PreferredMode = "Detecting",
                LastSeen = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
            }, CancellationToken.None);

            await history.AddAsync(new DetectionResult
            {
                State = ConnectionState.Direct,
                Network = new NetworkIdentity { Identifier = "eth|1|gw", DisplayName = "Ethernet" },
                LatencyMs = 123,
                Detail = "direct ok",
            }, CancellationToken.None);

            var recent = await history.GetRecentAsync(10, CancellationToken.None);
            Assert.Single(recent);
            Assert.Equal(ConnectionState.Direct, recent[0].State);
            Assert.Equal(123, recent[0].LatencyMs);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ---------- LogRepository ----------

    [Fact]
    public async Task Logs_are_batched_and_read_back_newest_first()
    {
        var path = NewDatabasePath();
        try
        {
            var context = new DatabaseContext(path);
            var repo = new LogRepository(context);

            await repo.AddManyAsync(new[]
            {
                new LogEntry
                {
                    Timestamp = DateTime.UtcNow.AddSeconds(-1),
                    Level = LogLevel.Info,
                    Category = "Test",
                    Message = "first",
                },
                new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = LogLevel.Warning,
                    Category = "Test",
                    Message = "second",
                    Detail = "detail",
                },
            }, CancellationToken.None);

            var recent = await repo.GetRecentAsync(10, CancellationToken.None);
            Assert.Equal(2, recent.Count);
            Assert.Equal("second", recent[0].Message);
            Assert.Equal(LogLevel.Warning, recent[0].Level);
            Assert.Equal("first", recent[1].Message);
        }
        finally
        {
            Cleanup(path);
        }
    }
}
