using System.Data.Common;
using Microsoft.Data.Sqlite;
using ProductionDataScrubber.Core.Model;

namespace ProductionDataScrubber.Core.Schema;

/// <summary>Reads schema metadata from a SQLite database.</summary>
public sealed class SqliteSchemaReader : ISchemaReader
{
    /// <inheritdoc />
    public async Task<DatabaseSchema> ReadAsync(DbConnection connection, CancellationToken cancellationToken = default)
    {
        var tables = await ReadTablesAsync(connection, cancellationToken).ConfigureAwait(false);
        var keys = await ReadKeysAsync(connection, cancellationToken).ConfigureAwait(false);
        var foreignKeys = await ReadForeignKeysAsync(connection, cancellationToken).ConfigureAwait(false);

        return new DatabaseSchema
        {
            Tables = tables,
            ForeignKeys = foreignKeys,
        };
    }

    private static async Task<List<TableInfo>> ReadTablesAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var tables = new List<TableInfo>();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;

        var names = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                names.Add(reader.GetString(0));
            }
        }

        foreach (var name in names)
        {
            var columns = await ReadColumnsAsync(connection, name, cancellationToken).ConfigureAwait(false);
            tables.Add(new TableInfo
            {
                Name = name,
                Columns = columns,
            });
        }

        return tables;
    }

    private static async Task<List<ColumnInfo>> ReadColumnsAsync(
        DbConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        var columns = new List<ColumnInfo>();

        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\");";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var ordinal = 0;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(1);
            var type = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var notNull = reader.GetInt32(3) == 1;
            var primaryKey = reader.GetInt32(5) > 0;

            columns.Add(DataTypeClassifier.Classify(
                type,
                name,
                nullable: !notNull && !primaryKey,
                ordinal++));
        }

        return columns;
    }

    private static async Task<List<KeyInfo>> ReadKeysAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var keys = new List<KeyInfo>();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, tbl_name, sql FROM sqlite_master
            WHERE type = 'table' AND sql IS NOT NULL AND sql LIKE '%PRIMARY KEY%';
            """;

        var tables = new List<(string Name, string Table, string Sql)>();

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        foreach (var (name, table, sql) in tables)
        {
            var columns = ExtractPrimaryKeyColumns(sql);
            if (columns.Count > 0)
            {
                keys.Add(new KeyInfo
                {
                    Name = $"PK_{table}",
                    Table = table,
                    Columns = columns,
                    IsPrimaryKey = true,
                });
            }
        }

        return keys;
    }

    private static List<string> ExtractPrimaryKeyColumns(string sql)
    {
        var open = sql.IndexOf("PRIMARY KEY", StringComparison.OrdinalIgnoreCase);
        if (open < 0)
        {
            return [];
        }

        var openParen = sql.IndexOf('(', open);
        if (openParen < 0)
        {
            return [];
        }

        var closeParen = sql.IndexOf(')', openParen);
        if (closeParen < 0)
        {
            return [];
        }

        return sql[(openParen + 1)..closeParen]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Trim().Trim('"', '[', ']', '`'))
            .Where(part => part.Length > 0)
            .ToList();
    }

    private static async Task<List<ForeignKeyInfo>> ReadForeignKeysAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var foreignKeys = new List<ForeignKeyInfo>();

        var tables = new List<string>();

        await using (var listCommand = connection.CreateCommand())
        {
            listCommand.CommandText = """
                SELECT name FROM sqlite_master
                WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
                ORDER BY name;
                """;

            await using var listReader = await listCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await listReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add(listReader.GetString(0));
            }
        }

        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA foreign_key_list(\"{table.Replace("\"", "\"\"")}\");";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetInt32(0);
                var parentTable = reader.GetString(2);
                var fromColumn = reader.GetString(3);
                var toColumn = reader.GetString(4);

                foreignKeys.Add(new ForeignKeyInfo
                {
                    Name = $"FK_{table}_{parentTable}_{fromColumn}_{id}",
                    ChildTable = table,
                    ChildColumn = fromColumn,
                    ParentTable = parentTable,
                    ParentColumn = toColumn,
                });
            }
        }

        return foreignKeys;
    }
}
