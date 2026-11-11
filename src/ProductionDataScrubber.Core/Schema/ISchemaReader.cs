using System.Data.Common;
using ProductionDataScrubber.Core.Model;

namespace ProductionDataScrubber.Core.Schema;

/// <summary>Reads table, column and foreign key metadata from a database.</summary>
public interface ISchemaReader
{
    /// <summary>Reads the schema of the open connection.</summary>
    Task<DatabaseSchema> ReadAsync(DbConnection connection, CancellationToken cancellationToken = default);
}

/// <summary>Maps provider data type names to the categories the scrubber cares about.</summary>
public static class DataTypeClassifier
{
    /// <summary>Classifies a store type name.</summary>
    public static ColumnInfo Classify(string name, string columnName, bool nullable, int ordinal)
    {
        var type = name.ToLowerInvariant();

        var isText = type.Contains("char") || type.Contains("text")
                     || type.Contains("citext") || type.Contains("name")
                     || type.Contains("string");

        var isInteger = type.Contains("int") || type.Contains("serial")
                        || type.Contains("smallint") || type.Contains("bigint")
                        || type.Contains("tinyint") || type.Contains("mediumint");

        var isDecimal = type.Contains("decimal") || type.Contains("numeric")
                        || type.Contains("float") || type.Contains("double")
                        || type.Contains("real") || type.Contains("money");

        var isTemporal = type.Contains("date") || type.Contains("time")
                         || type.Contains("interval");

        var isBoolean = type.Contains("bool");

        var isGuid = type.Contains("uuid") || type.Contains("guid");

        return new ColumnInfo
        {
            Name = columnName,
            DataType = name,
            IsNullable = nullable,
            IsText = isText,
            IsInteger = isInteger,
            IsDecimal = isDecimal,
            IsTemporal = isTemporal,
            IsBoolean = isBoolean,
            IsGuid = isGuid,
            Ordinal = ordinal,
        };
    }
}
