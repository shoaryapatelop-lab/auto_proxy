using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Models;
using Microsoft.Data.Sqlite;

namespace AutoProxy.Data;

public class DetectionHistoryRepository : IDetectionHistoryRepository
{
    private readonly DatabaseContext _context;

    public DetectionHistoryRepository(DatabaseContext context) => _context = context;

    public async Task AddAsync(DetectionResult result, CancellationToken cancellationToken)
    {
        long? networkId = null;
        long? proxyId = result.SelectedProxy?.Id;

        if (result.Network is not null)
        {
            networkId = await ResolveNetworkIdAsync(result.Network.Identifier, cancellationToken);
        }

        using var connection = _context.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO DetectionHistory (NetworkId, Mode, ProxyId, Result, LatencyMs, Timestamp)
            VALUES ($networkId, $mode, $proxyId, $result, $latency, $timestamp);
            """;
        command.Parameters.AddWithValue("$networkId", (object?)networkId ?? DBNull.Value);
        command.Parameters.AddWithValue("$mode", result.State.ToString());
        command.Parameters.AddWithValue("$proxyId", (object?)proxyId ?? DBNull.Value);
        command.Parameters.AddWithValue("$result", (object?)result.Detail ?? DBNull.Value);
        command.Parameters.AddWithValue("$latency", result.LatencyMs);
        command.Parameters.AddWithValue("$timestamp", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DetectionResult>> GetRecentAsync(
        int limit, CancellationToken cancellationToken)
    {
        using var connection = _context.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, NetworkId, Mode, ProxyId, Result, LatencyMs, Timestamp
            FROM DetectionHistory
            ORDER BY Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var list = new List<DetectionResult>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new DetectionResult
            {
                State = Enum.TryParse<ConnectionState>(reader.GetString(2), out var mode)
                    ? mode
                    : ConnectionState.Error,
                SelectedProxy = null,
                LatencyMs = reader.GetInt64(5),
                Detail = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                Timestamp = DateTime.Parse(reader.GetString(6)).ToLocalTime(),
            });
        }

        return list;
    }

    private async Task<long?> ResolveNetworkIdAsync(
        string identifier, CancellationToken cancellationToken)
    {
        using var connection = _context.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM Networks WHERE NetworkIdentifier = $id;";
        command.Parameters.AddWithValue("$id", identifier);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null ? null : (long)result;
    }
}