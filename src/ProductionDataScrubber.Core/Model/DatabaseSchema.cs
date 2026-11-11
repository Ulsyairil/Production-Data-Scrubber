namespace ProductionDataScrubber.Core.Model;

/// <summary>A column of a source table.</summary>
public sealed record ColumnInfo
{
    /// <summary>Column name.</summary>
    public required string Name { get; init; }

    /// <summary>Store data type.</summary>
    public required string DataType { get; init; }

    /// <summary>Whether the column accepts NULL.</summary>
    public bool IsNullable { get; init; }

    /// <summary>Whether the column holds a character or binary string.</summary>
    public bool IsText { get; init; }

    /// <summary>Whether the column holds an integer.</summary>
    public bool IsInteger { get; init; }

    /// <summary>Whether the column holds a decimal or floating point number.</summary>
    public bool IsDecimal { get; init; }

    /// <summary>Whether the column holds a date or timestamp.</summary>
    public bool IsTemporal { get; init; }

    /// <summary>Whether the column holds a boolean.</summary>
    public bool IsBoolean { get; init; }

    /// <summary>Whether the column holds a UUID or GUID.</summary>
    public bool IsGuid { get; init; }

    /// <summary>Ordinal position in the table.</summary>
    public int Ordinal { get; init; }
}

/// <summary>A foreign key relationship.</summary>
public sealed record ForeignKeyInfo
{
    /// <summary>Constraint name.</summary>
    public required string Name { get; init; }

    /// <summary>Child table.</summary>
    public required string ChildTable { get; init; }

    /// <summary>Child column.</summary>
    public required string ChildColumn { get; init; }

    /// <summary>Parent table.</summary>
    public required string ParentTable { get; init; }

    /// <summary>Parent column.</summary>
    public required string ParentColumn { get; init; }

    /// <summary>Whether the child column is nullable.</summary>
    public bool ChildColumnNullable { get; init; }
}

/// <summary>A primary key or unique constraint.</summary>
public sealed record KeyInfo
{
    /// <summary>Constraint name.</summary>
    public required string Name { get; init; }

    /// <summary>Table the key belongs to.</summary>
    public required string Table { get; init; }

    /// <summary>Columns of the key, in order.</summary>
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>Whether this is the primary key rather than a unique constraint.</summary>
    public bool IsPrimaryKey { get; init; }
}

/// <summary>A table in the source database.</summary>
public sealed class TableInfo
{
    /// <summary>Table name.</summary>
    public required string Name { get; init; }

    /// <summary>Columns of the table, in ordinal order.</summary>
    public required List<ColumnInfo> Columns { get; init; }

    /// <summary>Primary key of the table, when it has one.</summary>
    public KeyInfo? PrimaryKey { get; set; }

    /// <summary>Unique keys of the table.</summary>
    public List<KeyInfo> UniqueKeys { get; init; } = [];
}

/// <summary>The shape of the source database.</summary>
public sealed record DatabaseSchema
{
    /// <summary>Tables in the database.</summary>
    public required IReadOnlyList<TableInfo> Tables { get; init; }

    /// <summary>Foreign keys in the database.</summary>
    public required IReadOnlyList<ForeignKeyInfo> ForeignKeys { get; init; }

    /// <summary>Returns a table by name, or null when it does not exist.</summary>
    public TableInfo? Table(string name)
        => Tables.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Foreign keys whose parent is the given table.</summary>
    public IEnumerable<ForeignKeyInfo> ForeignKeysTo(string table)
        => ForeignKeys.Where(fk => string.Equals(fk.ParentTable, table, StringComparison.OrdinalIgnoreCase));

    /// <summary>Foreign keys whose child is the given table.</summary>
    public IEnumerable<ForeignKeyInfo> ForeignKeysFrom(string table)
        => ForeignKeys.Where(fk => string.Equals(fk.ChildTable, table, StringComparison.OrdinalIgnoreCase));
}
