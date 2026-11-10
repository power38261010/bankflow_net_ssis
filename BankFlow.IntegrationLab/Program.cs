using BankFlow.IntegrationLab;

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    PrintUsage();
    return;
}

var connectionString = Environment.GetEnvironmentVariable("BANKFLOW_ETL_SQL");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("Set BANKFLOW_ETL_SQL to a SQL Server training-database connection string.");

switch (args[0])
{
    case "setup":
        await SqlScriptRunner.RunDirectoryAsync(connectionString, Path.Combine(AppContext.BaseDirectory, "Sql"));
        Console.WriteLine("ETL training schema is ready.");
        break;

    case "seed":
        var seededRows = await new SqlTrainingSeeder(connectionString).SeedAsync();
        Console.WriteLine($"Seeded {seededRows} synthetic source rows (repeatable).");
        break;

    case "elt":
        PrintResult("ELT", await new SqlTrainingSeeder(connectionString).RunEltAsync());
        break;

    case "demo":
        var seeder = new SqlTrainingSeeder(connectionString);
        var count = await seeder.SeedAsync();
        Console.WriteLine($"Seeded {count} synthetic source rows (repeatable).");
        PrintResult("ELT", await seeder.RunEltAsync());
        break;

    case "import" when args.Length == 2:
        using (var input = File.OpenText(args[1]))
        {
            var result = await new AccountSnapshotImporter(connectionString)
                .ImportCsvAsync(input, Path.GetFileName(args[1]));
            PrintResult("ETL", result);
        }
        break;

    default:
        PrintUsage();
        Environment.ExitCode = 2;
        break;
}

static void PrintUsage()
{
    Console.WriteLine("""
        BankFlow SQL Server integration lab

        Commands:
          setup                 Create the isolated ETL training schema and SQL objects
          seed                  Idempotently seed synthetic SQL Server, Sybase, and Teradata source rows
          elt                   Transform seeded raw rows inside SQL Server and load the learning model
          demo                  Run seed and ELT end to end
          import <file.csv>     Validate, stage, and process account snapshots (ETL)

        Set BANKFLOW_ETL_SQL to a SQL Server training-database connection string before running.
        """);
}

static void PrintResult(string mode, ImportResult result)
{
    Console.WriteLine($"{mode} batch {result.BatchId}: {result.Status}; read={result.ReadCount}, accepted={result.AcceptedCount}, rejected={result.RejectedCount}, inserted={result.InsertedCount}, updated={result.UpdatedCount}.");
}
