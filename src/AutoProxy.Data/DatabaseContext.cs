using Microsoft.Data.Sqlite;

namespace AutoProxy.Data;

public class DatabaseContext
{
    private readonly string _connectionString;

    public DatabaseContext(string? databasePath = null)
    {
        var dbPath = ResolveDatabasePath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            RecursiveTriggers = true,
        }.ToString();

        Initialize();
    }

    public string DatabasePath =>
        new SqliteConnectionStringBuilder(_connectionString).DataSource;

    /// <summary>
    /// Resolves either a directory (defaults to %LocalAppData%\AutoProxy) or an
    /// explicit database file path to a concrete .db file path.
    /// </summary>
    private static string ResolveDatabasePath(string? databasePath)
    {
        var baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutoProxy");

        if (string.IsNullOrWhiteSpace(databasePath))
            return Path.Combine(baseDirectory, "autoproxy.db");

        var full = Path.GetFullPath(databasePath);
        var looksLikeDirectory =
            Directory.Exists(full) ||
            databasePath.EndsWith(Path.DirectorySeparatorChar) ||
            databasePath.EndsWith(Path.AltDirectorySeparatorChar) ||
            Path.GetExtension(full).Length == 0;

        return looksLikeDirectory ? Path.Combine(full, "autoproxy.db") : full;
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
            command.ExecuteNonQuery();
        }
        return connection;
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Proxies (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Host TEXT NOT NULL,
                Port INTEGER NOT NULL,
                AuthenticationRequired INTEGER NOT NULL DEFAULT 0,
                CredentialId TEXT NULL,
                Enabled INTEGER NOT NULL DEFAULT 1,
                SuccessCount INTEGER NOT NULL DEFAULT 0,
                FailureCount INTEGER NOT NULL DEFAULT 0,
                AverageLatencyMs INTEGER NULL,
                LastSuccessfulTest TEXT NULL,
                LastFailedTest TEXT NULL,
                LastError TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS Networks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                NetworkIdentifier TEXT NOT NULL UNIQUE,
                Ssid TEXT NULL,
                Gateway TEXT NULL,
                InterfaceType TEXT NULL,
                PreferredMode TEXT NOT NULL DEFAULT 'Detecting',
                PreferredProxyId INTEGER NULL,
                LastSeen TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                FOREIGN KEY (PreferredProxyId) REFERENCES Proxies(Id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS DetectionHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                NetworkId INTEGER NULL,
                Mode TEXT NOT NULL,
                ProxyId INTEGER NULL,
                Result TEXT NULL,
                LatencyMs INTEGER NOT NULL DEFAULT 0,
                Timestamp TEXT NOT NULL,
                FOREIGN KEY (NetworkId) REFERENCES Networks(Id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS Logs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp TEXT NOT NULL,
                Level TEXT NOT NULL,
                Category TEXT NOT NULL,
                Message TEXT NOT NULL,
                Detail TEXT NULL,
                NetworkId INTEGER NULL,
                ProxyId INTEGER NULL
            );
            """;
        command.ExecuteNonQuery();

        // Enable WAL for better concurrent read/write behaviour.
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();
    }
}