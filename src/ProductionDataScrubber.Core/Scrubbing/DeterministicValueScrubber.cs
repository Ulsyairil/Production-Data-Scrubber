using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ProductionDataScrubber.Core.Model;

namespace ProductionDataScrubber.Core.Scrubbing;

/// <summary>Rewrites a single value according to a column policy.</summary>
public interface IValueScrubber
{
    /// <summary>Rewrites a value. Returns null when the column is dropped.</summary>
    object? Scrub(object? value, ColumnInfo column, ColumnPolicy policy);
}

/// <summary>
/// Deterministic value scrubber. The same input always produces the same output for a given
/// seed and salt, so a bug reproduced locally still reproduces in a colleague's environment.
/// </summary>
public sealed class DeterministicValueScrubber : IValueScrubber
{
    private readonly byte[] _salt;
    private readonly long _seed;

    /// <summary>Creates a scrubber.</summary>
    public DeterministicValueScrubber(long seed, string salt)
    {
        _seed = seed;
        _salt = Encoding.UTF8.GetBytes(salt);
    }

    /// <inheritdoc />
    public object? Scrub(object? value, ColumnInfo column, ColumnPolicy policy)
    {
        if (value is null || value is DBNull)
        {
            return null;
        }

        return policy.Strategy switch
        {
            ScrubStrategy.Keep => value,
            ScrubStrategy.Null => null,
            ScrubStrategy.Constant => ConstantValue(policy.Value, column),
            ScrubStrategy.Hash => HashValue(value, column),
            ScrubStrategy.Fake => FakeValue(value, column),
            ScrubStrategy.Redact => RedactValue(value, column, policy.Shape),
            ScrubStrategy.Shift => ShiftValue(value, column, policy.ShiftDays),
            ScrubStrategy.Surrogate => value,
            ScrubStrategy.Drop => null,
            _ => value,
        };
    }

    /// <summary>Computes the deterministic hash of a value.</summary>
    public string ComputeHash(object value)
    {
        var bytes = Encoding.UTF8.GetBytes(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);

        using var hmac = new HMACSHA256(_salt);
        var hash = hmac.ComputeHash(bytes);

        return Convert.ToHexStringLower(hash);
    }

    private object? ConstantValue(string? constant, ColumnInfo column)
    {
        if (constant is null)
        {
            return null;
        }

        if (column.IsInteger)
        {
            return long.TryParse(constant, CultureInfo.InvariantCulture, out var integer) ? integer : 0L;
        }

        if (column.IsDecimal)
        {
            return decimal.TryParse(constant, CultureInfo.InvariantCulture, out var number) ? number : 0m;
        }

        if (column.IsBoolean)
        {
            return bool.TryParse(constant, out var flag) && flag;
        }

        return constant;
    }

    private object HashValue(object value, ColumnInfo column)
    {
        var hash = ComputeHash(value);

        if (column.IsInteger)
        {
            // Map the hash into the signed 64 bit range so the value stays insertable.
            var bits = BitConverter.ToInt64(Convert.FromHexString(hash), 0);
            return bits;
        }

        if (column.IsGuid)
        {
            var bytes = Convert.FromHexString(hash);
            return new Guid(bytes.AsSpan(0, 16));
        }

        return hash;
    }

    private object FakeValue(object value, ColumnInfo column)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        var hash = ComputeHash(value);

        if (column.IsInteger)
        {
            return (long)(BitConverter.ToInt64(Convert.FromHexString(hash), 0) & 0x7FFFFFFF);
        }

        if (column.IsDecimal)
        {
            return Math.Round(
                Math.Abs(BitConverter.ToInt64(Convert.FromHexString(hash), 0) % 100000) / 100m,
                2);
        }

        if (column.IsBoolean)
        {
            return hash[0] % 2 == 0;
        }

        if (column.IsGuid)
        {
            return new Guid(Convert.FromHexString(hash).AsSpan(0, 16));
        }

        if (column.IsTemporal)
        {
            // Keep the temporal shape but move the value into a fixed past window.
            return value;
        }

        return FakeText(text, hash);
    }

    private string FakeText(string original, string hash)
    {
        var first = FirstName(hash);
        var last = LastName(hash);

        if (original.Contains('@', StringComparison.Ordinal))
        {
            return $"{first}.{last}@example.com";
        }

        return $"{first} {last}";
    }

    private static readonly string[] FirstNames =
    [
        "Avery", "Blake", "Casey", "Dana", "Ellis", "Finley", "Gray", "Harper",
        "Indigo", "Jordan", "Kai", "Logan", "Morgan", "Noel", "Oakley", "Parker",
    ];

    private static readonly string[] LastNames =
    [
        "Adams", "Brooks", "Carter", "Dunn", "Ellis", "Foster", "Grant", "Hayes",
        "Irwin", "Jensen", "Klein", "Lopez", "Meyer", "Nash", "Ortiz", "Pace",
    ];

    private static string FirstName(string hash) => FirstNames[Pick(hash, 0)];

    private static string LastName(string hash) => LastNames[Pick(hash, 1)];

    private static int Pick(string hash, int salt)
    {
        var bytes = Convert.FromHexString(hash);
        var value = BitConverter.ToInt32(bytes, salt * 4) & 0x7FFFFFFF;
        return value % 16;
    }

    private object? RedactValue(object value, ColumnInfo column, string? shape)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        var wantsEmail = shape is not null
                         && (shape.Contains('@', StringComparison.Ordinal)
                             || shape.Equals("email", StringComparison.OrdinalIgnoreCase));

        if (wantsEmail)
        {
            var at = text.IndexOf('@', StringComparison.Ordinal);
            if (at > 0)
            {
                var local = text[..at];
                var visible = local.Length > 1 ? local[..1] : local;
                return $"{visible}***{text[at..]}";
            }
        }

        if (column.IsText)
        {
            return text.Length <= 4 ? "***" : $"{text[..2]}***";
        }

        if (column.IsInteger)
        {
            return 0L;
        }

        return null;
    }

    private object? ShiftValue(object value, ColumnInfo column, int shiftDays)
    {
        var offset = TimeSpan.FromDays(shiftDays);

        if (value is DateTime dateTime)
        {
            return dateTime.Add(offset);
        }

        if (value is DateTimeOffset dateTimeOffset)
        {
            return dateTimeOffset.Add(offset);
        }

        if (value is DateOnly dateOnly)
        {
            return dateOnly.AddDays(shiftDays);
        }

        if (value is TimeSpan timeSpan)
        {
            return timeSpan.Add(offset);
        }

        // SQLite and some providers store dates as text, so the original format is preserved.
        if (value is string text
            && DateTime.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            var shifted = parsed.Add(offset);

            if (text.Contains('T', StringComparison.Ordinal))
            {
                return shifted.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            }

            if (text.Contains(':', StringComparison.Ordinal))
            {
                return shifted.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }

            return shifted.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return value;
    }
}
