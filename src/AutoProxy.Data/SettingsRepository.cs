using System.Text.Json;
using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Models;
using Microsoft.Data.Sqlite;

namespace AutoProxy.Data;

public class SettingsRepository : ISettingsRepository
{
    private readonly DatabaseContext _context;

    public SettingsRepository(DatabaseContext context) => _context = context;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        AppSettings defaults = new();

        using var connection = _context.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Key, Value FROM Settings WHERE Key = 'AppSettings';";

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return defaults;

        try
        {
            var json = reader.GetString(1);
            var parsed = JsonSerializer.Deserialize<AppSettings>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return parsed ?? defaults;
        }
        catch (JsonException)
        {
            return defaults;
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(settings);
        using var connection = _context.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Settings (Key, Value) VALUES ('AppSettings', $value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        command.Parameters.AddWithValue("$value", json);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}