using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using ProductionDataScrubber.Core.Model;
using ProductionDataScrubber.Core.Schema;
using ProductionDataScrubber.Core.Scrubbing;

namespace ProductionDataScrubber.Core.Engine;

/// <summary>Receives progress updates while tables are copied.</summary>
public interface IWriteProgress
{
    /// <summary>Called after a table is copied.</summary>
    void Report(string table, TableCopyResult result);
}

/// <summary>Copies a database from one connection to another, applying a scrub policy.</summary>
public sealed class ScrubEngine
{
    private readonly ISchemaReader _schemaReader;
    private readonly IValueScrubber _valueScrubber;
    private readonly KeyMap _keyMap = new();
    private long _surrogateCounter;

    private const long SurrogateKeyOffset = 1_000_000L;

    /// <summary>Creates an engine.</summary>
    public ScrubEngine(ISchemaReader schemaReader, IValueScrubber valueScrubber)
    {
        _schemaReader = schemaReader;
        _valueScrubber = valueScrubber;
    }

    /// <summary>Reads the source schema.</summary>
    public Task<DatabaseSchema> ReadSchemaAsync(
        DbConnection source,
        CancellationToken cancellationToken = default)
        => _schemaReader.ReadAsync(source, cancellationToken);

    /// <summary>
    /// Copies every table of the source into the target. Tables are ordered so that a parent
    /// table is always written before the child tables that reference it.
    /// </summary>
    public async Task<ScrubReport> RunAsync(
        DbConnection source,
        DbConnection target,
        DatabaseSchema schema,
        ScrubPolicy policy,
        IWriteProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var order = SortTables(schema);
        var results = new List<TableCopyResult>();
        var warnings = new List<string>();

        foreach (var table in order)
        {
            var result = await CopyTableAsync(
                source,
                target,
                schema,
                table,
                policy,
                warnings,
                cancellationToken).ConfigureAwait(false);

            results.Add(result);
            progress?.Report(table.Name, result);
        }

        return new ScrubReport
        {
            Tables = results,
            Warnings = warnings,
        };
    }

    private async Task<TableCopyResult> CopyTableAsync(
        DbConnection source,
        DbConnection target,
        DatabaseSchema schema,
        TableInfo table,
        ScrubPolicy policy,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var rowLimit = policy.RowLimitFor(table.Name);

        var writes = new List<(ColumnInfo Column, ColumnPolicy? Policy)>();

        foreach (var column in table.Columns)
        {
            var columnPolicy = policy.ForColumn(table.Name, column.Name);

            if (columnPolicy?.Strategy == ScrubStrategy.Drop)
            {
                continue;
            }

            writes.Add((column, columnPolicy));
        }

        // Foreign keys where this table is the child. Their parent values may have been remapped.
        var outgoingForeignKeys = schema.ForeignKeysFrom(table.Name).ToList();

        var selectSql = BuildSelectSql(table, writes, rowLimit);
        var createSql = BuildCreateTableSql(target, table, writes);
        var insertSql = BuildInsertSql(table, writes);

        await ExecuteNonQueryAsync(target, createSql, cancellationToken).ConfigureAwait(false);

        long rowsRead = 0;
        long rowsWritten = 0;
        var skipped = 0L;

        await using var selectCommand = source.CreateCommand();
        selectCommand.CommandText = selectSql;

        await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        await using var transaction = await target.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var insertCommand = target.CreateCommand();
        insertCommand.Transaction = (DbTransaction)transaction;
        insertCommand.CommandText = insertSql;

        var surrogateIndex = FindSurrogateIndex(writes);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rowsRead++;

            var parameters = new List<object?>(writes.Count);

            for (var i = 0; i < writes.Count; i++)
            {
                var (column, columnPolicy) = writes[i];
                var raw = reader.IsDBNull(i) ? null : reader.GetValue(i);

                if (i == surrogateIndex)
                {
                    var surrogate = BuildSurrogate(raw, table, column);
                    _keyMap.Set(table.Name, column.Name, raw, surrogate);
                    parameters.Add(surrogate);
                    continue;
                }

                // When the parent key was remapped, the child must use the same surrogate so the
                // relationship survives the scrub.
                var fk = outgoingForeignKeys.FirstOrDefault(f =>
                    string.Equals(f.ChildColumn, column.Name, StringComparison.OrdinalIgnoreCase));

                if (fk is not null
                    && _keyMap.TryGet(fk.ParentTable, fk.ParentColumn, raw, out var mapped))
                {
                    parameters.Add(mapped);
                    continue;
                }

                var strategy = columnPolicy?.Strategy ?? policy.DefaultStrategy;
                var effective = new ColumnPolicy
                {
                    Strategy = strategy,
                    Value = columnPolicy?.Value,
                    ShiftDays = columnPolicy?.ShiftDays ?? 0,
                    Shape = columnPolicy?.Shape,
                };

                parameters.Add(_valueScrubber.Scrub(raw, column, effective));
            }

            insertCommand.Parameters.Clear();
            for (var i = 0; i < parameters.Count; i++)
            {
                var parameter = insertCommand.CreateParameter();
                parameter.ParameterName = $"@p{i}";
                parameter.Value = parameters[i] ?? DBNull.Value;
                insertCommand.Parameters.Add(parameter);
            }

            await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            rowsWritten++;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (rowsWritten == 0)
        {
            warnings.Add($"Table '{table.Name}' produced no rows.");
        }

        if (rowLimit is { } limit && rowsRead > limit)
        {
            skipped = rowsRead - limit;
        }
        return new TableCopyResult
        {
            Table = table.Name,
            RowsRead = rowsRead,
            RowsWritten = rowsWritten,
            RowsSkipped = skipped,
        };
    }

    private static int FindSurrogateIndex(List<(ColumnInfo Column, ColumnPolicy? Policy)> writes)
    {
        for (var i = 0; i < writes.Count; i++)
        {
            if (writes[i].Policy?.Strategy == ScrubStrategy.Surrogate)
            {
                return i;
            }
        }

        return -1;
    }

    private object BuildSurrogate(object? original, TableInfo table, ColumnInfo column)
    {
        if (column.IsGuid)
        {
            var bytes = new byte[16];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            return new Guid(bytes);
        }

        if (column.IsInteger)
        {
            // Sequential and deterministic, so a rerun produces the same keys. The offset keeps
            // the surrogate from colliding with a small original identifier.
            return SurrogateKeyOffset + Interlocked.Increment(ref _surrogateCounter);
        }

        var text = Convert.ToString(original, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        return $"surrogate-{table.Name}-{text.GetHashCode(StringComparison.Ordinal)}";
    }

    private static string BuildSelectSql(
        TableInfo table,
        List<(ColumnInfo Column, ColumnPolicy? Policy)> writes,
        int? rowLimit)
    {
        var quoted = writes.Select(w => QuoteIdentifier(w.Column.Name)).ToList();
        var sql = $"SELECT {string.Join(", ", quoted)} FROM {QuoteIdentifier(table.Name)}";

        if (rowLimit is { } limit && limit > 0)
        {
            sql += $" LIMIT {limit}";
        }

        return sql;
    }

    private static string BuildCreateTableSql(
        DbConnection target,
        TableInfo table,
        List<(ColumnInfo Column, ColumnPolicy? Policy)> writes)
    {
        var definitions = writes.Select(w =>
            $"{QuoteIdentifier(w.Column.Name)} {MapTargetType(target, w.Column)}");

        return $"CREATE TABLE IF NOT EXISTS {QuoteIdentifier(table.Name)} ({string.Join(", ", definitions)});";
    }

    private static string MapTargetType(DbConnection target, ColumnInfo column)
    {
        if (target is not SqliteConnection)
        {
            return "text";
        }

        if (column.IsInteger)
        {
            return "INTEGER";
        }

        if (column.IsDecimal)
        {
            return "NUMERIC";
        }

        if (column.IsBoolean)
        {
            return "INTEGER";
        }

        if (column.IsGuid)
        {
            return "TEXT";
        }

        if (column.IsTemporal)
        {
            return "TEXT";
        }

        return "TEXT";
    }

    private static string BuildInsertSql(
        TableInfo table,
        List<(ColumnInfo Column, ColumnPolicy? Policy)> writes)
    {
        var columns = writes.Select(w => QuoteIdentifier(w.Column.Name));
        var placeholders = writes.Select((_, i) => $"@p{i}");

        return $"INSERT INTO {QuoteIdentifier(table.Name)} ({string.Join(", ", columns)}) " +
               $"VALUES ({string.Join(", ", placeholders)});";
    }

    private static string QuoteIdentifier(string name) => $"\"{name.Replace("\"", "\"\"")}\"";

    private static async Task ExecuteNonQueryAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Orders tables so that a parent table comes before every child table that references it.
    /// A cycle is broken at the table where it is detected, and the remaining tables keep their
    /// original order.
    /// </summary>
    public static List<TableInfo> SortTables(DatabaseSchema schema)
    {
        var result = new List<TableInfo>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in schema.Tables)
        {
            Visit(table);
        }

        return result;

        void Visit(TableInfo table)
        {
            if (visited.Contains(table.Name))
            {
                return;
            }

            if (!visiting.Add(table.Name))
            {
                // Cycle detected. The table is appended by the caller that started the cycle.
                return;
            }

            foreach (var parent in schema.ForeignKeysFrom(table.Name)
                         .Select(fk => schema.Table(fk.ParentTable))
                         .Where(t => t is not null)
                         .Select(t => t!))
            {
                Visit(parent);
            }

            visiting.Remove(table.Name);
            visited.Add(table.Name);
            result.Add(table);
        }
    }
}
