using ProductionDataScrubber.Core.Model;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ProductionDataScrubber.Core.Policy;

/// <summary>Loads a scrub policy from a YAML file.</summary>
public static class PolicyLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Reads and parses a policy file.</summary>
    public static ScrubPolicy Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Policy file not found: {path}", path);
        }

        return Parse(File.ReadAllText(path));
    }

    /// <summary>Parses policy YAML.</summary>
    public static ScrubPolicy Parse(string yaml)
    {
        var raw = Deserializer.Deserialize<RawPolicy>(yaml)
                  ?? throw new InvalidDataException("Policy file is empty.");

        var tables = new Dictionary<string, TablePolicy>(StringComparer.OrdinalIgnoreCase);

        foreach (var (tableName, rawTable) in raw.Tables)
        {
            var columns = new Dictionary<string, ColumnPolicy?>(StringComparer.OrdinalIgnoreCase);

            foreach (var (columnName, rawColumn) in rawTable.Columns)
            {
                columns[columnName] = rawColumn is null
                    ? null
                    : new ColumnPolicy
                    {
                        Strategy = ParseStrategy(rawColumn.Strategy),
                        Value = rawColumn.Value,
                        ShiftDays = rawColumn.ShiftDays,
                        Shape = rawColumn.Shape,
                    };
            }

            tables[tableName] = new TablePolicy
            {
                Columns = columns,
                RowLimit = rawTable.RowLimit,
            };
        }

        return new ScrubPolicy
        {
            Version = raw.Version,
            Seed = raw.Seed,
            Salt = raw.Salt,
            DefaultStrategy = raw.DefaultStrategy is null
                ? ScrubStrategy.Keep
                : ParseStrategy(raw.DefaultStrategy),
            Tables = tables,
        };
    }

    private static ScrubStrategy ParseStrategy(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" or "keep" => ScrubStrategy.Keep,
        "null" => ScrubStrategy.Null,
        "constant" => ScrubStrategy.Constant,
        "hash" => ScrubStrategy.Hash,
        "fake" => ScrubStrategy.Fake,
        "redact" => ScrubStrategy.Redact,
        "shift" => ScrubStrategy.Shift,
        "surrogate" => ScrubStrategy.Surrogate,
        "drop" => ScrubStrategy.Drop,
        _ => throw new InvalidDataException($"Unknown scrub strategy: {text}"),
    };

    private sealed class RawPolicy
    {
        public int Version { get; set; } = 1;

        public long Seed { get; set; } = 42;

        public string Salt { get; set; } = "production-data-scrubber";

        public string? DefaultStrategy { get; set; }

        public Dictionary<string, RawTable> Tables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class RawTable
    {
        public int? RowLimit { get; set; }

        public Dictionary<string, RawColumn?> Columns { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class RawColumn
    {
        public string? Strategy { get; set; }

        public string? Value { get; set; }

        public int ShiftDays { get; set; }

        public string? Shape { get; set; }
    }
}
