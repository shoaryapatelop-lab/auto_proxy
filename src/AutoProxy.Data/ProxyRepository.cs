using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Models;
using Microsoft.Data.Sqlite;

namespace AutoProxy.Data;

public class ProxyRepository : IProxyRepository
{
    private readonly DatabaseContext _context;

    public ProxyRepository(DatabaseContext context) => _context = context;

    public async Task<IReadOnlyList<ProxyProfile>> GetAllAsync(CancellationToken cancellationToken)
    {
        using var connection = _context.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Host, Port, AuthenticationRequired, CredentialId, Enabled,
                   SuccessCount, FailureCount, AverageLatencyMs,
                   LastSuccessfulTest, LastFailedTest, LastError
            FROM Proxies
            ORDER BY Name COLLATE NOCASE;
            """;

        var list = new List<ProxyProfile>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add(Map(reader));

        return list;
    }

    public async Task<IReadOnlyList<ProxyProfile>> GetEnabledAsync(CancellationToken cancellationToken) =>
        (await GetAllAsync(cancellationToken)).Where(p => p.Enabled).ToList();

    public async Task<ProxyProfile?> GetByIdAsync(long id, CancellationToken cancellationToken)
    {
        using var connection = _context.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Host, Port, AuthenticationRequired, CredentialId, Enabled,
                   SuccessCount, FailureCount, AverageLatencyMs,
                   LastSuccessfulTest, LastFailedTest, LastError
            FROM Proxies
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    public async Task<long> AddAsync(ProxyProfile profile, CancellationToken cancellationToken)
    {
        using var connection = _context.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Proxies (Name, Host, Port, AuthenticationRequired, CredentialId, Enabled,
                                 SuccessCount, FailureCount, AverageLatencyMs,
                                 LastSuccessfulTest, LastFailedTest, LastError)
            VALUES ($name, $host, $port, $auth, $cred, $enabled,
                    $success, $failure, $avg, $lastOk, $lastFail, $lastError);
            SELECT last_insert_rowid();
            """;
        AddParameters(command, profile);
        var id = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        return id;
    }

    public async Task UpdateAsync(ProxyProfile profile, CancellationToken cancellationToken)
    {
        using var connection = _context.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Proxies SET
                Name = $name, Host = $host, Port = $port,
                AuthenticationRequired = $auth, CredentialId = $cred, Enabled = $enabled,
                SuccessCount = $success, FailureCount = $failure, AverageLatencyMs = $avg,
                LastSuccessfulTest = $lastOk, LastFailedTest = $lastFail, LastError = $lastError
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", profile.Id);
        AddParameters(command, profile);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken)
    {
        using var connection = _context.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Proxies WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameters(SqliteCommand command, ProxyProfile p)
    {
        command.Parameters.AddWithValue("$name", p.Name);
        command.Parameters.AddWithValue("$host", p.Host);
        command.Parameters.AddWithValue("$port", p.Port);
        command.Parameters.AddWithValue("$auth", p.AuthenticationRequired ? 1 : 0);
        command.Parameters.AddWithValue("$cred", (object?)p.CredentialTarget ?? DBNull.Value);
        command.Parameters.AddWithValue("$enabled", p.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$success", p.SuccessCount);
        command.Parameters.AddWithValue("$failure", p.FailureCount);
        command.Parameters.AddWithValue("$avg", (object?)p.AverageLatencyMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastOk", ToIso(p.LastSuccessfulTest));
        command.Parameters.AddWithValue("$lastFail", ToIso(p.LastFailedTest));
        command.Parameters.AddWithValue("$lastError", (object?)p.LastError ?? DBNull.Value);
    }

    private static ProxyProfile Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Name = reader.GetString(1),
        Host = reader.GetString(2),
        Port = reader.GetInt32(3),
        AuthenticationRequired = reader.GetInt32(4) != 0,
        CredentialTarget = reader.IsDBNull(5) ? null : reader.GetString(5),
        Enabled = reader.GetInt32(6) != 0,
        SuccessCount = reader.GetInt32(7),
        FailureCount = reader.GetInt32(8),
        AverageLatencyMs = reader.IsDBNull(9) ? null : reader.GetInt64(9),
        LastSuccessfulTest = FromIso(reader.IsDBNull(10) ? null : reader.GetString(10)),
        LastFailedTest = FromIso(reader.IsDBNull(11) ? null : reader.GetString(11)),
        LastError = reader.IsDBNull(12) ? null : reader.GetString(12),
    };

    internal static object ToIso(DateTime? value) =>
        value is null ? DBNull.Value : value.Value.ToString("O");

    internal static DateTime? FromIso(string? value) =>
        value is null ? null : DateTime.Parse(value).ToLocalTime();
}