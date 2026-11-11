using System.CommandLine;
using ProductionDataScrubber.Core.Connection;
using ProductionDataScrubber.Core.Engine;
using ProductionDataScrubber.Core.Model;
using ProductionDataScrubber.Core.Policy;
using ProductionDataScrubber.Core.Schema;
using ProductionDataScrubber.Core.Scrubbing;

namespace ProductionDataScrubber.Cli;

/// <summary>Entry point of the data-scrubber command line tool.</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var sourceOption = new Option<string>("--source", "-s")
        {
            Description = "Source connection string. SQLite path or PostgreSQL connection string.",
            DefaultValueFactory = _ => "Data Source=source.db",
        };

        var targetOption = new Option<string>("--target", "-t")
        {
            Description = "Target connection string. SQLite path or PostgreSQL connection string.",
            DefaultValueFactory = _ => "Data Source=scrubbed.db",
        };

        var policyOption = new Option<string>("--policy", "-p")
        {
            Description = "Path to the YAML scrub policy.",
        };

        var seedOption = new Option<long>("--seed")
        {
            Description = "Seed used by the deterministic generators. Overrides the value in the policy.",
            DefaultValueFactory = _ => -1,
        };

        var saltOption = new Option<string>("--salt")
        {
            Description = "Salt used by the hash strategy. Overrides the value in the policy.",
        };

        var jsonOption = new Option<bool>("--json")
        {
            Description = "Emit the run summary as JSON.",
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Read the source schema and report what would be copied, without writing.",
        };

        var rootCommand = new RootCommand(
            "Clone a production database into a local database, scrub PII per declarative policy, and preserve foreign key integrity.")
        {
            sourceOption,
            targetOption,
            policyOption,
            seedOption,
            saltOption,
            jsonOption,
            dryRunOption,
        };

        rootCommand.SetAction(async parseResult =>
        {
            var sourceText = parseResult.GetValue(sourceOption) ?? "Data Source=source.db";
            var targetText = parseResult.GetValue(targetOption) ?? "Data Source=scrubbed.db";
            var policyPath = parseResult.GetValue(policyOption);
            var seedOverride = parseResult.GetValue(seedOption);
            var saltOverride = parseResult.GetValue(saltOption);
            var json = parseResult.GetValue(jsonOption);
            var dryRun = parseResult.GetValue(dryRunOption);

            if (!dryRun && string.IsNullOrWhiteSpace(policyPath))
            {
                Console.Error.WriteLine("error: --policy is required unless --dry-run is used.");
                return 2;
            }

            if (policyPath is not null && !File.Exists(policyPath))
            {
                Console.Error.WriteLine($"error: policy file not found: {policyPath}");
                return 2;
            }

            var policy = policyPath is null
                ? new ScrubPolicy()
                : PolicyLoader.Load(policyPath);

            if (seedOverride >= 0)
            {
                policy = policy with { Seed = seedOverride };
            }

            if (!string.IsNullOrWhiteSpace(saltOverride))
            {
                policy = policy with { Salt = saltOverride };
            }

            var sourceKind = ConnectionFactory.Guess(sourceText);
            var targetKind = ConnectionFactory.Guess(targetText);

            var schemaReader = sourceKind == DatabaseKind.Postgres
                ? (ISchemaReader)new PostgresSchemaReader()
                : new SqliteSchemaReader();

            var scrubber = new DeterministicValueScrubber(policy.Seed, policy.Salt);
            var engine = new ScrubEngine(schemaReader, scrubber);

            return await RunAsync(engine, sourceKind, sourceText, targetKind, targetText, policy, json, dryRun)
                .ConfigureAwait(false);
        });

        return await rootCommand.Parse(args).InvokeAsync();
    }

    private static async Task<int> RunAsync(
        ScrubEngine engine,
        DatabaseKind sourceKind,
        string sourceText,
        DatabaseKind targetKind,
        string targetText,
        ScrubPolicy policy,
        bool json,
        bool dryRun)
    {
        await using var source = await ConnectionFactory.OpenAsync(sourceKind, sourceText).ConfigureAwait(false);
        var schema = await engine.ReadSchemaAsync(source).ConfigureAwait(false);

        if (dryRun)
        {
            Console.WriteLine($"tables: {schema.Tables.Count}");
            Console.WriteLine($"foreign keys: {schema.ForeignKeys.Count}");

            foreach (var table in schema.Tables)
            {
                var columns = table.Columns.Count;
                var mentioned = policy.Tables.ContainsKey(table.Name) ? " (in policy)" : string.Empty;
                Console.WriteLine($"  {table.Name}: {columns} columns{mentioned}");
            }

            return 0;
        }

        await using var target = await ConnectionFactory.OpenAsync(targetKind, targetText).ConfigureAwait(false);

        // Progress goes to stderr so the JSON on stdout stays machine readable.
        var progress = json ? null : new ConsoleProgress();
        var report = await engine.RunAsync(source, target, schema, policy, progress).ConfigureAwait(false);

        if (json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    tool = "data-scrubber",
                    version = "0.1.0",
                    summary = new
                    {
                        tableCount = report.TableCount,
                        totalRowsRead = report.TotalRowsRead,
                        totalRowsWritten = report.TotalRowsWritten,
                    },
                    tables = report.Tables.Select(t => new
                    {
                        table = t.Table,
                        rowsRead = t.RowsRead,
                        rowsWritten = t.RowsWritten,
                        rowsSkipped = t.RowsSkipped,
                    }),
                    warnings = report.Warnings,
                },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine(
                $"copied {report.TableCount} tables, {report.TotalRowsWritten} rows " +
                $"(read {report.TotalRowsRead})");

            foreach (var warning in report.Warnings)
            {
                Console.WriteLine($"warning: {warning}");
            }
        }

        return 0;
    }

    private sealed class ConsoleProgress : IWriteProgress
    {
        public void Report(string table, TableCopyResult result)
            => Console.Error.WriteLine($"  {table}: {result.RowsWritten} rows");
    }
}
