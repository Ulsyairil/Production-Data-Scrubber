using ProductionDataScrubber.Core.Model;
using Xunit;

namespace ProductionDataScrubber.Tests;

public sealed class ScrubEngineTests
{
    private const string Schema = """
        CREATE TABLE Customers (
            Id INTEGER NOT NULL PRIMARY KEY,
            Name TEXT NOT NULL,
            Email TEXT NOT NULL,
            CreatedAt TEXT NOT NULL
        );

        CREATE TABLE Invoices (
            Id INTEGER NOT NULL PRIMARY KEY,
            CustomerId INTEGER NOT NULL REFERENCES Customers(Id),
            Amount NUMERIC NOT NULL,
            Note TEXT NULL
        );
        """;

    private const string Seed = """
        INSERT INTO Customers (Id, Name, Email, CreatedAt) VALUES
            (1, 'Alice Smith', 'alice@example.com', '2024-01-15 10:00:00'),
            (2, 'Bob Jones', 'bob@example.com', '2024-02-20 11:30:00');

        INSERT INTO Invoices (Id, CustomerId, Amount, Note) VALUES
            (10, 1, 100.50, 'first'),
            (11, 2, 250.00, NULL),
            (12, 1, 75.25, 'third');
        """;

    [Fact]
    public async Task CopiesAllTablesAndRows()
    {
        await using var fixture = new ScrubFixture(Schema, Seed);

        var report = await fixture.RunAsync(new ScrubPolicy());

        Assert.Equal(2, report.TableCount);
        Assert.Equal(5, report.TotalRowsWritten);
        Assert.Equal(2, await fixture.CountTargetAsync("Customers"));
        Assert.Equal(3, await fixture.CountTargetAsync("Invoices"));
    }

    [Fact]
    public async Task HashStrategyIsDeterministic()
    {
        await using var fixture = new ScrubFixture(Schema, Seed);

        var policy = new ScrubPolicy
        {
            Tables = new Dictionary<string, TablePolicy>(StringComparer.OrdinalIgnoreCase)
            {
                ["Customers"] = new TablePolicy
                {
                    Columns = new Dictionary<string, ColumnPolicy?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Email"] = new ColumnPolicy { Strategy = ScrubStrategy.Hash },
                    },
                },
            },
        };

        await fixture.RunAsync(policy);

        var first = await fixture.ScalarTargetAsync("SELECT Email FROM Customers WHERE Id = 1;");
        var second = await fixture.ScalarTargetAsync("SELECT Email FROM Customers WHERE Id = 2;");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.DoesNotContain("alice", first, StringComparison.OrdinalIgnoreCase);

        // The same input and seed give the same output.
        await using var other = new ScrubFixture(Schema, Seed);
        await other.RunAsync(policy);

        var otherFirst = await other.ScalarTargetAsync("SELECT Email FROM Customers WHERE Id = 1;");
        Assert.Equal(first, otherFirst);
    }

    [Fact]
    public async Task NullStrategyEmitsNull()
    {
        await using var fixture = new ScrubFixture(Schema, Seed);

        var policy = new ScrubPolicy
        {
            Tables = new Dictionary<string, TablePolicy>(StringComparer.OrdinalIgnoreCase)
            {
                ["Customers"] = new TablePolicy
                {
                    Columns = new Dictionary<string, ColumnPolicy?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Email"] = new ColumnPolicy { Strategy = ScrubStrategy.Null },
                    },
                },
            },
        };

        await fixture.RunAsync(policy);

        Assert.Null(await fixture.ScalarTargetAsync("SELECT Email FROM Customers WHERE Id = 1;"));
    }

    [Fact]
    public async Task SurrogateStrategyRemapsForeignKey()
    {
        await using var fixture = new ScrubFixture(Schema, Seed);

        var policy = new ScrubPolicy
        {
            Tables = new Dictionary<string, TablePolicy>(StringComparer.OrdinalIgnoreCase)
            {
                ["Customers"] = new TablePolicy
                {
                    Columns = new Dictionary<string, ColumnPolicy?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Id"] = new ColumnPolicy { Strategy = ScrubStrategy.Surrogate },
                    },
                },
            },
        };

        await fixture.RunAsync(policy);

        var customerId = await fixture.ScalarTargetAsync("SELECT Id FROM Customers WHERE Name = 'Alice Smith';");
        var invoiceCustomerId = await fixture.ScalarTargetAsync(
            "SELECT CustomerId FROM Invoices WHERE Id = 10;");

        Assert.NotNull(customerId);
        Assert.Equal(customerId, invoiceCustomerId);
        Assert.NotEqual("1", customerId);
    }

    [Fact]
    public async Task SurrogateStrategyKeepsForeignKeyIntegrity()
    {
        await using var fixture = new ScrubFixture(Schema, Seed);

        var policy = new ScrubPolicy
        {
            Tables = new Dictionary<string, TablePolicy>(StringComparer.OrdinalIgnoreCase)
            {
                ["Customers"] = new TablePolicy
                {
                    Columns = new Dictionary<string, ColumnPolicy?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Id"] = new ColumnPolicy { Strategy = ScrubStrategy.Surrogate },
                    },
                },
            },
        };

        await fixture.RunAsync(policy);

        // Every invoice still points at an existing customer.
        var orphans = await fixture.ScalarTargetAsync("""
            SELECT COUNT(*) FROM Invoices i
            LEFT JOIN Customers c ON i.CustomerId = c.Id
            WHERE c.Id IS NULL;
            """);

        Assert.Equal("0", orphans);
    }

    [Fact]
    public async Task DropStrategyRemovesColumn()
    {
        await using var fixture = new ScrubFixture(Schema, Seed);

        var policy = new ScrubPolicy
        {
            Tables = new Dictionary<string, TablePolicy>(StringComparer.OrdinalIgnoreCase)
            {
                ["Customers"] = new TablePolicy
                {
                    Columns = new Dictionary<string, ColumnPolicy?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Email"] = new ColumnPolicy { Strategy = ScrubStrategy.Drop },
                    },
                },
            },
        };

        await fixture.RunAsync(policy);

        var rows = await fixture.ReadTargetAsync("Customers");
        Assert.DoesNotContain("Email", rows[0].Keys);
    }

    [Fact]
    public async Task RowLimitCapsCopiedRows()
    {
        await using var fixture = new ScrubFixture(Schema, Seed);

        var policy = new ScrubPolicy
        {
            Tables = new Dictionary<string, TablePolicy>(StringComparer.OrdinalIgnoreCase)
            {
                ["Invoices"] = new TablePolicy { RowLimit = 1 },
            },
        };

        await fixture.RunAsync(policy);

        Assert.Equal(1, await fixture.CountTargetAsync("Invoices"));
    }

    [Fact]
    public async Task RedactKeepsEmailDomain()
    {
        await using var fixture = new ScrubFixture(Schema, Seed);

        var policy = new ScrubPolicy
        {
            Tables = new Dictionary<string, TablePolicy>(StringComparer.OrdinalIgnoreCase)
            {
                ["Customers"] = new TablePolicy
                {
                    Columns = new Dictionary<string, ColumnPolicy?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Email"] = new ColumnPolicy { Strategy = ScrubStrategy.Redact, Shape = "email" },
                    },
                },
            },
        };

        await fixture.RunAsync(policy);

        var email = await fixture.ScalarTargetAsync("SELECT Email FROM Customers WHERE Id = 1;");

        Assert.NotNull(email);
        Assert.Contains("@example.com", email, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", email, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ParentTableIsCopiedBeforeChild()
    {
        await using var fixture = new ScrubFixture(Schema, Seed);

        var report = await fixture.RunAsync(new ScrubPolicy());

        var customersIndex = report.Tables.ToList().FindIndex(t => t.Table == "Customers");
        var invoicesIndex = report.Tables.ToList().FindIndex(t => t.Table == "Invoices");

        Assert.True(customersIndex >= 0);
        Assert.True(invoicesIndex >= 0);
        Assert.True(customersIndex < invoicesIndex);
    }

    [Fact]
    public async Task ShiftStrategyMovesDates()
    {
        await using var fixture = new ScrubFixture(Schema, Seed);

        var policy = new ScrubPolicy
        {
            Tables = new Dictionary<string, TablePolicy>(StringComparer.OrdinalIgnoreCase)
            {
                ["Customers"] = new TablePolicy
                {
                    Columns = new Dictionary<string, ColumnPolicy?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["CreatedAt"] = new ColumnPolicy { Strategy = ScrubStrategy.Shift, ShiftDays = -90 },
                    },
                },
            },
        };

        await fixture.RunAsync(policy);

        var createdAt = await fixture.ScalarTargetAsync("SELECT CreatedAt FROM Customers WHERE Id = 1;");

        Assert.NotNull(createdAt);
        Assert.Contains("2023", createdAt, StringComparison.Ordinal);
    }
}
