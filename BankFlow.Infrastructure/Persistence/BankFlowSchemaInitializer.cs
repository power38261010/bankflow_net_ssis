using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace BankFlow.Infrastructure.Persistence;

public static class BankFlowSchemaInitializer
{
    public static async Task InitializeAsync(BankFlowDbContext db, CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsSqlServer())
        {
            await db.Database.EnsureCreatedAsync(cancellationToken);
            return;
        }

        if (await db.GetService<IRelationalDatabaseCreator>().ExistsAsync(cancellationToken))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                IF OBJECT_ID(N'[dbo].[__EFMigrationsHistory]', N'U') IS NULL
                BEGIN
                    IF OBJECT_ID(N'[dbo].[Accounts]', N'U') IS NOT NULL
                       AND OBJECT_ID(N'[dbo].[Transactions]', N'U') IS NOT NULL
                    BEGIN
                        IF COL_LENGTH('dbo.Accounts', 'AccountNumber') IS NULL
                           OR COL_LENGTH('dbo.Accounts', 'Balance') IS NULL
                           OR COL_LENGTH('dbo.Transactions', 'AccountId') IS NULL
                            THROW 50001, 'Unsupported legacy banking schema.', 1;
                        CREATE TABLE [dbo].[__EFMigrationsHistory] (
                            [MigrationId] nvarchar(150) NOT NULL PRIMARY KEY,
                            [ProductVersion] nvarchar(32) NOT NULL
                        );
                        INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
                        VALUES (N'20261007205749_InitialBanking', N'8.0.22');
                    END
                    ELSE IF OBJECT_ID(N'[dbo].[Accounts]', N'U') IS NOT NULL
                         OR OBJECT_ID(N'[dbo].[Transactions]', N'U') IS NOT NULL
                        THROW 50002, 'Incomplete legacy banking schema.', 1;
                END
                """, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await db.Database.MigrateAsync(cancellationToken);
    }
}