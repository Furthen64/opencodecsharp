namespace OpenCode.Data;

public sealed class Database
{
    private const string DefaultDbDir = "~/.local/share/opencode";
    private const string DefaultDbName = "opencode.db";
    private const int BusyTimeoutMs = 5000;
    private const int CacheSizeKb = 64000;

    private static readonly Lazy<string> LazyPath = new(ResolveDbPath);
    public static string Path => LazyPath.Value;

    public SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection($"Data Source={Path}");
        connection.Open();
        SetPragmas(connection);
        return connection;
    }

    public async Task InitializeAsync()
    {
        await using var connection = CreateConnection();
        await EnsureMigrationTableAsync(connection);
        await CreateTablesAsync(connection);
    }

    private static string ResolveDbPath()
    {
        var envPath = Environment.GetEnvironmentVariable("OPENCODE_DB");
        if (!string.IsNullOrEmpty(envPath))
        {
            return envPath;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            home = Environment.GetEnvironmentVariable("HOME") ?? ".";
        }

        var dir = System.IO.Path.Combine(home, ".local", "share", "opencode");
        System.IO.Directory.CreateDirectory(dir);
        return System.IO.Path.Combine(dir, DefaultDbName);
    }

    private static void SetPragmas(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            PRAGMA cache_size = -64000;
            PRAGMA foreign_keys = ON;";
        cmd.ExecuteNonQuery();
    }

    private static async Task EnsureMigrationTableAsync(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS ""migration"" (
                ""id"" TEXT NOT NULL PRIMARY KEY,
                ""time_completed"" INTEGER NOT NULL
            );";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task CreateTablesAsync(SqliteConnection connection)
    {
        var statements = Schema.GetAllCreateStatements();
        foreach (var statement in statements)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = statement;
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
