using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Models;
using Microsoft.Data.Sqlite;

namespace AutoProxy.Data;

public class NetworkRepository : INetworkRepository
{
    private readonly DatabaseContext _context;

    public NetworkRepository(DatabaseContext context) => _context = context;

    public async Task<NetworkProfile?> GetByIdentifierAsync(
        string identifier, CancellationToken cancellationToken)
    {
        using var connection = _context.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, NetworkIdentifier, Ssid, Gateway, InterfaceType,
                   PreferredMode, PreferredProxyId, LastSeen, CreatedAt
            FROM Networks
            WHERE NetworkIdentifier = $id;
            """;
        command.Parameters.AddWithValue("$id", identifier);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<NetworkProfile>> GetAllAsync(CancellationToken cancellationToken)
    {
        using var connection = _context.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, NetworkIdentifier, Ssid, Gateway, InterfaceType,
                   PreferredMode, PreferredProxyId, LastSeen, CreatedAt
            FROM Networks
            ORDER BY LastSeen DESC;
            """;

        var list = new List<NetworkProfile>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add(Map(reader));

        return list;
    }

    public async Task SaveAsync(NetworkProfile profile, CancellationToken cancellationToken)
    {
        using var connection = _context.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Networks (NetworkIdentifier, Ssid, Gateway, InterfaceType,
                                  PreferredMode, PreferredProxyId, LastSeen, CreatedAt)
            VALUES ($identifier, $ssid, $gateway, $iface, $mode, $proxyId, $lastSeen, $createdAt)
            ON CONFLICT(NetworkIdentifier) DO UPDATE SET
                Ssid = excluded.Ssid,
                Gateway = excluded.Gateway,
                InterfaceType = excluded.InterfaceType,
                PreferredMode = excluded.PreferredMode,
                PreferredProxyId = excluded.PreferredProxyId,
                LastSeen = excluded.LastSeen;
            """;
        command.Parameters.AddWithValue("$identifier", profile.NetworkIdentifier);
        command.Parameters.AddWithValue("$ssid", (object?)profile.Ssid ?? DBNull.Value);
        command.Parameters.AddWithValue("$gateway", (object?)profile.Gateway ?? DBNull.Value);
        command.Parameters.AddWithValue("$iface", (object?)profile.InterfaceType ?? DBNull.Value);
        command.Parameters.AddWithValue("$mode", profile.PreferredMode);
        command.Parameters.AddWithValue("$proxyId", (object?)profile.PreferredProxyId ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastSeen", profile.LastSeen.ToString("O"));
        command.Parameters.AddWithValue("$createdAt", profile.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetPreferredModeAsync(
        string identifier, string mode, long? preferredProxyId, CancellationToken cancellationToken)
    {
        using var connection = _context.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Networks
            SET PreferredMode = $mode, PreferredProxyId = $proxyId, LastSeen = $lastSeen
            WHERE NetworkIdentifier = $identifier;
            """;
        command.Parameters.AddWithValue("$mode", mode);
        command.Parameters.AddWithValue("$proxyId", (object?)preferredProxyId ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastSeen", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$identifier", identifier);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteByIdentifierAsync(string identifier, CancellationToken cancellationToken)
    {
        using var connection = _context.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Networks WHERE NetworkIdentifier = $identifier;";
        command.Parameters.AddWithValue("$identifier", identifier);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NetworkProfile Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        NetworkIdentifier = reader.GetString(1),
        Ssid = reader.IsDBNull(2) ? null : reader.GetString(2),
        Gateway = reader.IsDBNull(3) ? null : reader.GetString(3),
        InterfaceType = reader.IsDBNull(4) ? null : reader.GetString(4),
        PreferredMode = reader.GetString(5),
        PreferredProxyId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
        LastSeen = DateTime.Parse(reader.GetString(7)).ToLocalTime(),
        CreatedAt = DateTime.Parse(reader.GetString(8)).ToLocalTime(),
    };
}