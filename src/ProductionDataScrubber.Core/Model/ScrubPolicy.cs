namespace ProductionDataScrubber.Core.Model;

/// <summary>How a single column is rewritten.</summary>
public enum ScrubStrategy
{
    /// <summary>Copy the value unchanged.</summary>
    Keep = 0,

    /// <summary>Replace the value with NULL.</summary>
    Null,

    /// <summary>Replace the value with a fixed literal from the policy.</summary>
    Constant,

    /// <summary>Replace the value with a deterministic hash. Same input and salt give the same output.</summary>
    Hash,

    /// <summary>Replace the value with a deterministic fake value of the same shape.</summary>
    Fake,

    /// <summary>Keep the shape but hide the identifying part, for example <c>a***@example.com</c>.</summary>
    Redact,

    /// <summary>Shift a date or timestamp by a fixed offset so ordering is preserved.</summary>
    Shift,

    /// <summary>Replace the value with a sequential surrogate key and remap every foreign key that points at it.</summary>
    Surrogate,

    /// <summary>Do not copy the column to the target at all.</summary>
    Drop,
}

/// <summary>Policy for one column.</summary>
public sealed record ColumnPolicy
{
    /// <summary>How the column is rewritten.</summary>
    public required ScrubStrategy Strategy { get; init; }

    /// <summary>Literal used by the <see cref="ScrubStrategy.Constant"/> strategy.</summary>
    public string? Value { get; init; }

    /// <summary>Number of days used by the <see cref="ScrubStrategy.Shift"/> strategy.</summary>
    public int ShiftDays { get; init; }

    /// <summary>Shape used by the <see cref="ScrubStrategy.Redact"/> strategy.</summary>
    public string? Shape { get; init; }
}

/// <summary>Policy for one table.</summary>
public sealed record TablePolicy
{
    /// <summary>Column name to column policy. A null value means the column is kept unchanged.</summary>
    public IReadOnlyDictionary<string, ColumnPolicy?> Columns { get; init; }
        = new Dictionary<string, ColumnPolicy?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maximum number of rows to copy from this table. Null means every row.</summary>
    public int? RowLimit { get; init; }
}

/// <summary>A complete scrub policy.</summary>
public sealed record ScrubPolicy
{
    /// <summary>Policy format version. Only version 1 exists.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Seed used by every deterministic generator.</summary>
    public long Seed { get; init; } = 42;

    /// <summary>Salt used by the hash strategy. Change it to produce a different but stable result.</summary>
    public string Salt { get; init; } = "production-data-scrubber";

    /// <summary>Strategy applied to columns that the policy does not mention.</summary>
    public ScrubStrategy DefaultStrategy { get; init; } = ScrubStrategy.Keep;

    /// <summary>Table name to table policy.</summary>
    public IReadOnlyDictionary<string, TablePolicy> Tables { get; init; }
        = new Dictionary<string, TablePolicy>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the policy for a column, or null when the column is not mentioned.</summary>
    public ColumnPolicy? ForColumn(string table, string column)
    {
        if (!Tables.TryGetValue(table, out var tablePolicy))
        {
            return null;
        }

        return tablePolicy.Columns.TryGetValue(column, out var columnPolicy) ? columnPolicy : null;
    }

    /// <summary>Returns the row limit for a table, or null when there is none.</summary>
    public int? RowLimitFor(string table)
        => Tables.TryGetValue(table, out var tablePolicy) ? tablePolicy.RowLimit : null;
}
