namespace ProductionDataScrubber.Core.Model;

/// <summary>Outcome of one table copy.</summary>
public sealed record TableCopyResult
{
    /// <summary>Table that was copied.</summary>
    public required string Table { get; init; }

    /// <summary>Number of rows read from the source.</summary>
    public required long RowsRead { get; init; }

    /// <summary>Number of rows written to the target.</summary>
    public required long RowsWritten { get; init; }

    /// <summary>Number of rows skipped by the row limit.</summary>
    public long RowsSkipped { get; init; }
}

/// <summary>Outcome of one scrub run.</summary>
public sealed record ScrubReport
{
    /// <summary>Tables that were copied.</summary>
    public required IReadOnlyList<TableCopyResult> Tables { get; init; }

    /// <summary>Total rows read from the source.</summary>
    public long TotalRowsRead => Tables.Sum(t => t.RowsRead);

    /// <summary>Total rows written to the target.</summary>
    public long TotalRowsWritten => Tables.Sum(t => t.RowsWritten);

    /// <summary>Number of tables copied.</summary>
    public int TableCount => Tables.Count;

    /// <summary>Warnings raised during the run.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
