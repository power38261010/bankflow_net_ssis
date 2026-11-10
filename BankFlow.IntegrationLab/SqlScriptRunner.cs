using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace BankFlow.IntegrationLab;

public static partial class SqlScriptRunner
{
    [GeneratedRegex(@"^\s*GO\s*(?:--.*)?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex BatchSeparator();

    public static async Task RunDirectoryAsync(string connectionString, string directory, CancellationToken cancellationToken = default)
    {
        var scripts = Directory.GetFiles(directory, "*.sql", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (scripts.Length == 0)
            throw new DirectoryNotFoundException($"No SQL deployment scripts were found in '{directory}'.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var script in scripts)
        {
            var contents = await File.ReadAllTextAsync(script, cancellationToken);
            foreach (var batch in BatchSeparator().Split(contents).Where(batch => !string.IsNullOrWhiteSpace(batch)))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = batch;
                command.CommandTimeout = 120;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }
}
