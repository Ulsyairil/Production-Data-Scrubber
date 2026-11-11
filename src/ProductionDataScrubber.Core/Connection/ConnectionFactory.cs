using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace ProductionDataScrubber.Core.Connection;

/// <summary>Kind of database a connection points at.</summary>
public enum DatabaseKind
{
    /// <summary>SQLite database file.</summary>
    Sqlite = 0,

    /// <summary>PostgreSQL server.</summary>
    Postgres = 1,
}

/// <summary>Builds connections from a connection string.</summary>
public static class ConnectionFactory
{
    /// <summary>Opens a connection for the given kind.</summary>
    public static async Task<DbConnection> OpenAsync(
        DatabaseKind kind,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        DbConnection connection = kind switch
        {
            DatabaseKind.Sqlite => new SqliteConnection(connectionString),
            DatabaseKind.Postgres => new NpgsqlConnection(connectionString),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>Guesses the database kind from a connection string.</summary>
    public static DatabaseKind Guess(string connectionString)
    {
        if (connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase)
            || connectionString.Contains("Server=", StringComparison.OrdinalIgnoreCase)
            || connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            || connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return DatabaseKind.Postgres;
        }

        return DatabaseKind.Sqlite;
    }
}
