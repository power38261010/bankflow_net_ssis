using System.Globalization;
using Microsoft.VisualBasic.FileIO;

namespace BankFlow.IntegrationLab;

public sealed record AccountSnapshotRow(
    int RowNumber,
    string? SourceSystem,
    string? SourceAccountId,
    string? CustomerName,
    string? AccountNumber,
    decimal? Balance,
    DateOnly? SnapshotDate,
    string? ValidationError,
    string RawData);

public sealed class AccountSnapshotCsv
{
    private static readonly string[] ExpectedHeaders =
    [
        "SourceSystem", "SourceAccountId", "CustomerName",
        "AccountNumber", "Balance", "SnapshotDate"
    ];

    public IReadOnlyList<AccountSnapshotRow> Read(TextReader input, CancellationToken cancellationToken = default)
    {
        using var parser = new TextFieldParser(input)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");

        var headers = parser.ReadFields()
            ?? throw new InvalidDataException("The CSV file is empty.");
        if (!headers.SequenceEqual(ExpectedHeaders, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"Expected CSV headers: {string.Join(",", ExpectedHeaders)}.");

        var rows = new List<AccountSnapshotRow>();
        var seenKeys = new HashSet<(string Source, string Account, DateOnly Date)>();
        var rowNumber = 1;

        while (!parser.EndOfData)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowNumber++;
            var fields = parser.ReadFields();
            if (fields is null)
                break;

            var raw = string.Join(",", fields);
            var rawTooLong = raw.Length > 4000;
            if (rawTooLong)
                raw = raw[..4000];
            var error = fields.Length == ExpectedHeaders.Length ? null : "Expected exactly six columns.";
            if (rawTooLong)
                error ??= "CSV row exceeds the 4000-character staging limit.";
            var sourceSystem = Get(fields, 0);
            var sourceAccountId = Get(fields, 1);
            var customerName = Get(fields, 2);
            var accountNumber = Get(fields, 3);

            if (string.IsNullOrWhiteSpace(sourceSystem) || sourceSystem.Length > 40)
                error ??= "SourceSystem is required and must be at most 40 characters.";
            if (string.IsNullOrWhiteSpace(sourceAccountId) || sourceAccountId.Length > 100)
                error ??= "SourceAccountId is required and must be at most 100 characters.";
            if (string.IsNullOrWhiteSpace(customerName) || customerName.Length > 120)
                error ??= "CustomerName is required and must be at most 120 characters.";
            if (string.IsNullOrWhiteSpace(accountNumber) || accountNumber.Length > 40)
                error ??= "AccountNumber is required and must be at most 40 characters.";

            decimal? balance = null;
            if (decimal.TryParse(Get(fields, 4), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var parsedBalance) && parsedBalance >= 0 &&
                parsedBalance <= 99999999999999999.99m &&
                decimal.Round(parsedBalance, 2) == parsedBalance)
                balance = parsedBalance;
            else
                error ??= "Balance must fit decimal(19,2) and be non-negative with at most two fractional digits.";

            DateOnly? snapshotDate = null;
            if (DateOnly.TryParseExact(Get(fields, 5), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsedDate))
                snapshotDate = parsedDate;
            else
                error ??= "SnapshotDate must use yyyy-MM-dd.";

            if (error is null)
            {
                var key = (sourceSystem!, sourceAccountId!, snapshotDate!.Value);
                if (!seenKeys.Add(key))
                    error = "Duplicate source account and snapshot date in this file.";
            }

            rows.Add(new AccountSnapshotRow(rowNumber, sourceSystem, sourceAccountId, customerName,
                accountNumber, balance, snapshotDate, error, raw));
        }

        return rows;
    }

    private static string? Get(string[] fields, int index) =>
        index < fields.Length ? fields[index].Trim() : null;
}
