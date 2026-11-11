using Microsoft.Data.Sqlite;
using ProductionDataScrubber.Core.Engine;
using ProductionDataScrubber.Core.Model;
using ProductionDataScrubber.Core.Schema;
using ProductionDataScrubber.Core.Scrubbing;

namespace ProductionDataScrubber.Tests;

/// <summary>Builds a small SQLite database, runs the scrubber against it and exposes the result.</summary>
public sealed class ScrubFixture : IAsyncDisposable
{
    private readonly SqliteConnection _source;
    private readonly SqliteConnection _target;

    public ScrubFixture(string schemaSql, string seedSql)
    {
        _source = new SqliteConnection("Data Source=:memory:");
        _source.Open();
        Execute(_source, schemaSql);
        Execute(_source, seedSql);

        _target = new SqliteConnection("Data Source=:memory:");
        _target.Open();
    }

    public SqliteConnection Source => _source;

    public SqliteConnection Target => _target;

    public async Task<ScrubReport> RunAsync(ScrubPolicy policy)
    {
        var scrubber = new DeterministicValueScrubber(policy.Seed, policy.Salt);
        var engine = new ScrubEngine(new SqliteSchemaReader(), scrubber);
        var schema = await engine.ReadSchemaAsync(_source);

        return await engine.RunAsync(_source, _target, schema, policy);
    }

    public async Task<List<Dictionary<string, object?>>> ReadTargetAsync(string table)
    {
        var rows = new List<Dictionary<string, object?>>();

        await using var command = _target.CreateCommand();
        command.CommandText = $"SELECT * FROM \"{table}\";";

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    public async Task<int> CountTargetAsync(string table)
    {
        await using var command = _target.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\";";

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task<string?> ScalarTargetAsync(string sql)
    {
        await using var command = _target.CreateCommand();
        command.CommandText = sql;

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : Convert.ToString(result);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public async ValueTask DisposeAsync()
    {
        await _source.DisposeAsync();
        await _target.DisposeAsync();
    }
}
