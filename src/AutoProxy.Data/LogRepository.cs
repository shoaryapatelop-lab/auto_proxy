using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Models;
using Microsoft.Data.Sqlite;

namespace AutoProxy.Data;

public class LogRepository : ILogRepository
{
    private readonly DatabaseContext _context;

    public LogRepository(DatabaseContext context) => _context = context;

    public async Task AddAsync(LogEntry entry, CancellationToken cancellationToken) =>
        await AddManyAsync(new[] { entry }, cancellationToken);

    public async Task AddManyAsync(IEnumerable<LogEntry> entries, CancellationToken cancellationToken)
    {
        var batch = entries as IReadOnlyList<LogEntry> ?? entries.ToList();
        if (batch.Count == 0) return;

        using var connection = _context.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Logs (Timestamp, Level, Category, Message, Detail, NetworkId, ProxyId)
            VALUES ($ts, $level, $category, $message, $detail, $networkId, $proxyId);
            """;

        var ts = command.Parameters.Add("$ts", SqliteType.Text);
        var level = command.Parameters.Add("$level", SqliteType.Text);
        var category = command.Parameters.Add("$category", SqliteType.Text);
        var message = command.Parameters.Add("$message", SqliteType.Text);
        var detail = command.Parameters.Add("$detail", SqliteType.Text);
        var networkId = command.Parameters.Add("$networkId", SqliteType.Integer);
        var proxyId = command.Parameters.Add("$proxyId", SqliteType.Integer);

        foreach (var entry in batch)
        {
            ts.Value = entry.Timestamp.ToString("O");
            level.Value = entry.Level.ToString();
            category.Value = entry.Category;
            message.Value = entry.Message;
            detail.Value = (object?)entry.Detail ?? DBNull.Value;
            networkId.Value = (object?)entry.NetworkId ?? DBNull.Value;
            proxyId.Value = (object?)entry.ProxyId ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LogEntry>> GetRecentAsync(
        int limit, CancellationToken cancellationToken)
    {
        using var connection = _context.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Timestamp, Level, Category, Message, Detail, NetworkId, ProxyId
            FROM Logs
            ORDER BY Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var list = new List<LogEntry>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new LogEntry
            {
                Id = reader.GetInt64(0),
                Timestamp = DateTime.Parse(reader.GetString(1)),
                Level = Enum.TryParse<LogLevel>(reader.GetString(2), out var level)
                    ? level
                    : LogLevel.Info,
                Category = reader.GetString(3),
                Message = reader.GetString(4),
                Detail = reader.IsDBNull(5) ? null : reader.GetString(5),
                NetworkId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                ProxyId = reader.IsDBNull(7) ? null : reader.GetInt64(7),
            });
        }

        return list;
    }
}