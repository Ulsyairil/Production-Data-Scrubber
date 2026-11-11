using System.Data.Common;
using ProductionDataScrubber.Core.Model;

namespace ProductionDataScrubber.Core.Schema;

/// <summary>Reads schema metadata from a PostgreSQL database.</summary>
public sealed class PostgresSchemaReader : ISchemaReader
{
    /// <inheritdoc />
    public async Task<DatabaseSchema> ReadAsync(DbConnection connection, CancellationToken cancellationToken = default)
    {
        var tables = await ReadTablesAsync(connection, cancellationToken).ConfigureAwait(false);
        var keys = await ReadKeysAsync(connection, cancellationToken).ConfigureAwait(false);
        var foreignKeys = await ReadForeignKeysAsync(connection, cancellationToken).ConfigureAwait(false);

        foreach (var table in tables)
        {
            if (table.PrimaryKey is not null)
            {
                continue;
            }

            var match = keys.FirstOrDefault(k =>
                string.Equals(k.Table, table.Name, StringComparison.OrdinalIgnoreCase)
                && k.IsPrimaryKey);

            if (match is null)
            {
                continue;
            }

            table.PrimaryKey = match;
            table.UniqueKeys.RemoveAll(u => u.IsPrimaryKey);
        }

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
        var lookup = new Dictionary<string, TableInfo>(StringComparer.OrdinalIgnoreCase);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.table_name, c.column_name, c.data_type, c.is_nullable, c.ordinal_position
            FROM information_schema.columns c
            JOIN information_schema.tables t
              ON t.table_schema = c.table_schema AND t.table_name = c.table_name
            WHERE c.table_schema = 'public' AND t.table_type = 'BASE TABLE'
            ORDER BY c.table_name, c.ordinal_position;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var tableName = reader.GetString(0);
            var columnName = reader.GetString(1);
            var dataType = reader.GetString(2);
            var isNullable = reader.GetString(3) == "YES";
            var ordinal = reader.GetInt32(4) - 1;

            if (!lookup.TryGetValue(tableName, out var table))
            {
                table = new TableInfo
                {
                    Name = tableName,
                    Columns = [],
                };
                lookup[tableName] = table;
                tables.Add(table);
            }

            var column = DataTypeClassifier.Classify(dataType, columnName, isNullable, ordinal);

            table.Columns.Add(column);
        }

        return tables;
    }

    private static async Task<List<KeyInfo>> ReadKeysAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var keys = new List<KeyInfo>();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tc.constraint_name, tc.table_name, kcu.column_name,
                   tc.constraint_type = 'PRIMARY KEY'
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON kcu.constraint_name = tc.constraint_name
             AND kcu.table_schema = tc.table_schema
            WHERE tc.table_schema = 'public'
              AND tc.constraint_type IN ('PRIMARY KEY', 'UNIQUE')
            ORDER BY tc.constraint_name, kcu.ordinal_position;
            """;

        var grouped = new Dictionary<string, (string Table, bool IsPrimary, List<string> Columns)>(
            StringComparer.OrdinalIgnoreCase);

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                var table = reader.GetString(1);
                var column = reader.GetString(2);
                var isPrimary = reader.GetBoolean(3);

                if (!grouped.TryGetValue(name, out var entry))
                {
                    entry = (table, isPrimary, []);
                    grouped[name] = entry;
                }

                entry.Columns.Add(column);
            }
        }

        foreach (var (name, entry) in grouped)
        {
            keys.Add(new KeyInfo
            {
                Name = name,
                Table = entry.Table,
                Columns = entry.Columns,
                IsPrimaryKey = entry.IsPrimary,
            });
        }

        return keys;
    }

    private static async Task<List<ForeignKeyInfo>> ReadForeignKeysAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var foreignKeys = new List<ForeignKeyInfo>();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tc.constraint_name, tc.table_name, kcu.column_name,
                   ccu.table_name AS foreign_table_name, ccu.column_name AS foreign_column_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON kcu.constraint_name = tc.constraint_name
            JOIN information_schema.constraint_column_usage ccu
              ON ccu.constraint_name = tc.constraint_name
            WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_schema = 'public';
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            foreignKeys.Add(new ForeignKeyInfo
            {
                Name = reader.GetString(0),
                ChildTable = reader.GetString(1),
                ChildColumn = reader.GetString(2),
                ParentTable = reader.GetString(3),
                ParentColumn = reader.GetString(4),
            });
        }

        return foreignKeys;
    }
}
