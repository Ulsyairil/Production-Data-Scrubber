using ProductionDataScrubber.Core.Model;
using ProductionDataScrubber.Core.Policy;
using Xunit;

namespace ProductionDataScrubber.Tests;

public sealed class PolicyLoaderTests
{
    [Fact]
    public void ParsesStrategiesAndOptions()
    {
        var policy = PolicyLoader.Parse("""
            version: 1
            seed: 7
            salt: my-salt
            default-strategy: keep

            tables:
              Customers:
                row-limit: 100
                columns:
                  Email:
                    strategy: hash
                  Name:
                    strategy: fake
                  Phone:
                    strategy: "null"
                  CreatedAt:
                    strategy: shift
                    shift-days: -90
                  Id:
                    strategy: surrogate
                  Note:
                    strategy: drop
                  Status:
                    strategy: constant
                    value: active
            """);

        Assert.Equal(7, policy.Seed);
        Assert.Equal("my-salt", policy.Salt);
        Assert.Equal(ScrubStrategy.Keep, policy.DefaultStrategy);
        Assert.Equal(100, policy.RowLimitFor("Customers"));

        var customers = policy.Tables["Customers"];

        Assert.Equal(ScrubStrategy.Hash, policy.ForColumn("Customers", "Email")!.Strategy);
        Assert.Equal(ScrubStrategy.Fake, policy.ForColumn("Customers", "Name")!.Strategy);
        Assert.Equal(ScrubStrategy.Null, policy.ForColumn("Customers", "Phone")!.Strategy);
        Assert.Equal(-90, policy.ForColumn("Customers", "CreatedAt")!.ShiftDays);
        Assert.Equal(ScrubStrategy.Surrogate, policy.ForColumn("Customers", "Id")!.Strategy);
        Assert.Equal(ScrubStrategy.Drop, policy.ForColumn("Customers", "Note")!.Strategy);
        Assert.Equal("active", policy.ForColumn("Customers", "Status")!.Value);
    }

    [Fact]
    public void ColumnNamesAreCaseInsensitive()
    {
        var policy = PolicyLoader.Parse("""
            tables:
              Customers:
                columns:
                  email:
                    strategy: hash
            """);

        Assert.Equal(ScrubStrategy.Hash, policy.ForColumn("customers", "EMAIL")!.Strategy);
    }

    [Fact]
    public void UnknownStrategyThrows()
    {
        var exception = Assert.Throws<InvalidDataException>(() => PolicyLoader.Parse("""
            tables:
              Customers:
                columns:
                  Email:
                    strategy: teleport
            """));

        Assert.Contains("teleport", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingPolicyFileThrows()
    {
        Assert.Throws<FileNotFoundException>(() => PolicyLoader.Load("/nonexistent/policy.yaml"));
    }

    [Fact]
    public void UnmentionedColumnReturnsNull()
    {
        var policy = PolicyLoader.Parse("""
            tables:
              Customers:
                columns:
                  Email:
                    strategy: hash
            """);

        Assert.Null(policy.ForColumn("Customers", "Phone"));
        Assert.Null(policy.ForColumn("Invoices", "Email"));
    }
}
