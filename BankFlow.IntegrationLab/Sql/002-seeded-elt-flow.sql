IF SCHEMA_ID(N'source') IS NULL EXEC(N'CREATE SCHEMA [source]');
GO

IF OBJECT_ID(N'[source].[AccountSnapshotRaw]', N'U') IS NULL
BEGIN
    CREATE TABLE [source].[AccountSnapshotRaw]
    (
        [SourceRowId] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_AccountSnapshotRaw] PRIMARY KEY,
        [SeedKey] nvarchar(80) NOT NULL,
        [SourceSystem] nvarchar(40) NULL,
        [SourceAccountId] nvarchar(100) NULL,
        [CustomerName] nvarchar(120) NULL,
        [AccountNumber] nvarchar(40) NULL,
        [BalanceText] nvarchar(60) NULL,
        [SnapshotDateText] nvarchar(30) NULL,
        [RawData] nvarchar(4000) NOT NULL,
        [UpdatedAtUtc] datetime2(3) NOT NULL
    );

    CREATE UNIQUE INDEX [UX_AccountSnapshotRaw_SeedKey]
        ON [source].[AccountSnapshotRaw] ([SeedKey]);
END;
GO

CREATE OR ALTER PROCEDURE [learning].[SeedAccountSourceData]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Rows TABLE
    (
        [SeedKey] nvarchar(80) NOT NULL PRIMARY KEY,
        [SourceSystem] nvarchar(40) NOT NULL,
        [SourceAccountId] nvarchar(100) NOT NULL,
        [CustomerName] nvarchar(120) NOT NULL,
        [AccountNumber] nvarchar(40) NOT NULL,
        [BalanceText] nvarchar(60) NOT NULL,
        [SnapshotDateText] nvarchar(30) NOT NULL
    );

    INSERT INTO @Rows
        ([SeedKey], [SourceSystem], [SourceAccountId], [CustomerName],
         [AccountNumber], [BalanceText], [SnapshotDateText])
    VALUES
        (N'demo:sqlserver:1001', N'SQLServer', N'ACC-1001', N'Ana Demo', N'SQL-1001', N'1520.75', N'2026-10-01'),
        (N'demo:sqlserver:1002', N'SQLServer', N'ACC-1002', N'Bruno Demo', N'SQL-1002', N'800.00', N'2026-10-01'),
        (N'demo:sqlserver:1003', N'SQLServer', N'ACC-1003', N'Invalid Balance', N'SQL-1003', N'-50.00', N'2026-10-01'),
        (N'demo:sybase:7001', N'Sybase', N'LEG-7001', N'Carmen Demo', N'SYB-7001', N'125.40', N'2026-10-01'),
        (N'demo:sybase:7002', N'Sybase', N'LEG-7002', N'Diego Demo', N'SYB-7002', N'9500.00', N'2026-10-01'),
        (N'demo:teradata:9001', N'Teradata', N'WH-9001', N'Elena Demo', N'TD-9001', N'0.00', N'2026-10-01'),
        (N'demo:teradata:9002', N'Teradata', N'WH-9002', N'Fermin Demo', N'TD-9002', N'72.35', N'2026-10-01'),
        (N'demo:teradata:9003', N'Teradata', N'WH-9003', N'Invalid Amount', N'TD-9003', N'not-a-number', N'2026-10-01');

    BEGIN TRY
        BEGIN TRANSACTION;

        UPDATE target
        SET target.[SourceSystem] = source.[SourceSystem],
            target.[SourceAccountId] = source.[SourceAccountId],
            target.[CustomerName] = source.[CustomerName],
            target.[AccountNumber] = source.[AccountNumber],
            target.[BalanceText] = source.[BalanceText],
            target.[SnapshotDateText] = source.[SnapshotDateText],
            target.[RawData] = CONCAT(source.[SourceSystem], N'|', source.[SourceAccountId], N'|',
                                      source.[CustomerName], N'|', source.[AccountNumber], N'|',
                                      source.[BalanceText], N'|', source.[SnapshotDateText]),
            target.[UpdatedAtUtc] = SYSUTCDATETIME()
        FROM [source].[AccountSnapshotRaw] AS target
        INNER JOIN @Rows AS source ON source.[SeedKey] = target.[SeedKey];

        INSERT INTO [source].[AccountSnapshotRaw]
            ([SeedKey], [SourceSystem], [SourceAccountId], [CustomerName], [AccountNumber],
             [BalanceText], [SnapshotDateText], [RawData], [UpdatedAtUtc])
        SELECT source.[SeedKey], source.[SourceSystem], source.[SourceAccountId], source.[CustomerName],
               source.[AccountNumber], source.[BalanceText], source.[SnapshotDateText],
               CONCAT(source.[SourceSystem], N'|', source.[SourceAccountId], N'|',
                      source.[CustomerName], N'|', source.[AccountNumber], N'|',
                      source.[BalanceText], N'|', source.[SnapshotDateText]),
               SYSUTCDATETIME()
        FROM @Rows AS source
        WHERE NOT EXISTS
        (
            SELECT 1 FROM [source].[AccountSnapshotRaw] AS target WITH (UPDLOCK, HOLDLOCK)
            WHERE target.[SeedKey] = source.[SeedKey]
        );

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT COUNT(*) FROM @Rows;
END;
GO

CREATE OR ALTER PROCEDURE [etl].[LoadSeededAccountSnapshotBatch]
    @BatchId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRY
        BEGIN TRANSACTION;

        INSERT INTO [etl].[ImportBatch] ([BatchId], [SourceSystem], [SourceFile], [Status], [StartedAtUtc])
        VALUES (@BatchId, N'SeededSources', N'source.AccountSnapshotRaw (ELT)', N'Loading', SYSUTCDATETIME());

        ;WITH RawRows AS
        (
            SELECT raw.[SourceRowId], raw.[SourceSystem], raw.[SourceAccountId], raw.[CustomerName],
                   raw.[AccountNumber], raw.[BalanceText], raw.[SnapshotDateText], raw.[RawData],
                   TRY_CONVERT(decimal(38,10), NULLIF(LTRIM(RTRIM(raw.[BalanceText])), N'')) AS [ParsedBalance],
                   TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(raw.[SnapshotDateText])), N''), 23) AS [ParsedSnapshotDate],
                   CONVERT(int, ROW_NUMBER() OVER (ORDER BY raw.[SourceSystem], raw.[SourceRowId])) AS [RowNumber]
            FROM [source].[AccountSnapshotRaw] AS raw
            WHERE raw.[SeedKey] LIKE N'demo:%'
        ),
        Validated AS
        (
            SELECT raw.*,
                NULLIF(CONCAT(
                    CASE WHEN NULLIF(LTRIM(RTRIM(raw.[SourceSystem])), N'') IS NULL OR LEN(raw.[SourceSystem]) > 40
                         THEN N'SourceSystem is required and must be at most 40 characters. ' ELSE N'' END,
                    CASE WHEN NULLIF(LTRIM(RTRIM(raw.[SourceAccountId])), N'') IS NULL OR LEN(raw.[SourceAccountId]) > 100
                         THEN N'SourceAccountId is required and must be at most 100 characters. ' ELSE N'' END,
                    CASE WHEN NULLIF(LTRIM(RTRIM(raw.[CustomerName])), N'') IS NULL OR LEN(raw.[CustomerName]) > 120
                         THEN N'CustomerName is required and must be at most 120 characters. ' ELSE N'' END,
                    CASE WHEN NULLIF(LTRIM(RTRIM(raw.[AccountNumber])), N'') IS NULL OR LEN(raw.[AccountNumber]) > 40
                         THEN N'AccountNumber is required and must be at most 40 characters. ' ELSE N'' END,
                    CASE WHEN raw.[ParsedBalance] IS NULL
                         THEN N'Balance is not a valid decimal. '
                         WHEN raw.[ParsedBalance] < 0 OR raw.[ParsedBalance] > 99999999999999999.99
                         THEN N'Balance is outside the non-negative decimal(19,2) range. '
                         WHEN raw.[ParsedBalance] <> ROUND(raw.[ParsedBalance], 2)
                         THEN N'Balance has more than two fractional digits. ' ELSE N'' END,
                    CASE WHEN raw.[ParsedSnapshotDate] IS NULL
                         THEN N'SnapshotDate must use yyyy-MM-dd. ' ELSE N'' END
                ), N'') AS [ValidationError]
            FROM RawRows AS raw
        )
        INSERT INTO [etl].[AccountSnapshotStage]
            ([BatchId], [RowNumber], [SourceSystem], [SourceAccountId], [CustomerName],
             [AccountNumber], [Balance], [SnapshotDate], [ValidationError], [RawData])
        SELECT @BatchId, [RowNumber], NULLIF(LTRIM(RTRIM([SourceSystem])), N''),
               NULLIF(LTRIM(RTRIM([SourceAccountId])), N''), NULLIF(LTRIM(RTRIM([CustomerName])), N''),
               NULLIF(LTRIM(RTRIM([AccountNumber])), N''), TRY_CONVERT(decimal(19,2), [ParsedBalance]), [ParsedSnapshotDate],
               [ValidationError], LEFT([RawData], 4000)
        FROM Validated;

        EXEC [etl].[ProcessAccountSnapshotBatch] @BatchId = @BatchId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        DECLARE @Error nvarchar(2000) = LEFT(ERROR_MESSAGE(), 2000);
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;

        IF NOT EXISTS (SELECT 1 FROM [etl].[ImportBatch] WHERE [BatchId] = @BatchId)
        BEGIN
            INSERT INTO [etl].[ImportBatch]
                ([BatchId], [SourceSystem], [SourceFile], [Status], [StartedAtUtc],
                 [CompletedAtUtc], [ErrorMessage])
            VALUES
                (@BatchId, N'SeededSources', N'source.AccountSnapshotRaw (ELT)', N'Failed',
                 SYSUTCDATETIME(), SYSUTCDATETIME(), @Error);
        END;

        THROW;
    END CATCH;
END;
GO
