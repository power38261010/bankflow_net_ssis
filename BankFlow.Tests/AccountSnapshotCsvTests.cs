using BankFlow.IntegrationLab;

namespace BankFlow.Tests;

public sealed class AccountSnapshotCsvTests
{
    [Fact]
    public void Read_ShouldSupportQuotedCommasAndInvariantDecimals()
    {
        const string csv = """
            SourceSystem,SourceAccountId,CustomerName,AccountNumber,Balance,SnapshotDate
            SQLServer,ACC-1,"Arrua, Ana",A-100,1520.75,2026-10-01
            """;

        var rows = new AccountSnapshotCsv().Read(new StringReader(csv));

        var row = Assert.Single(rows);
        Assert.Null(row.ValidationError);
        Assert.Equal("Arrua, Ana", row.CustomerName);
        Assert.Equal(1520.75m, row.Balance);
        Assert.Equal(new DateOnly(2026, 10, 1), row.SnapshotDate);
    }

    [Fact]
    public void Read_ShouldRejectInvalidBalancesAndDuplicateSnapshotKeys()
    {
        const string csv = """
            SourceSystem,SourceAccountId,CustomerName,AccountNumber,Balance,SnapshotDate
            SQLServer,ACC-1,Ana,A-100,10.00,2026-10-01
            SQLServer,ACC-1,Ana,A-100,10.00,2026-10-01
            SQLServer,ACC-2,Bruno,A-200,-1.00,2026-10-01
            SQLServer,ACC-3,Carmen,A-300,1.005,2026-10-01
            """;

        var rows = new AccountSnapshotCsv().Read(new StringReader(csv));

        Assert.Equal(4, rows.Count);
        Assert.Null(rows[0].ValidationError);
        Assert.Contains("Duplicate", rows[1].ValidationError);
        Assert.Contains("non-negative", rows[2].ValidationError);
        Assert.Contains("decimal(19,2)", rows[3].ValidationError);
    }

    [Fact]
    public void Read_ShouldRejectUnexpectedHeaders()
    {
        const string csv = "AccountId,Balance\nA-1,10.00\n";

        var exception = Assert.Throws<InvalidDataException>(() =>
            new AccountSnapshotCsv().Read(new StringReader(csv)));

        Assert.Contains("Expected CSV headers", exception.Message);
    }
}
