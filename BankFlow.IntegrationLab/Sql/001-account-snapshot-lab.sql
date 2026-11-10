IF SCHEMA_ID(N'etl') IS NULL EXEC(N'CREATE SCHEMA [etl]');
IF SCHEMA_ID(N'learning') IS NULL EXEC(N'CREATE SCHEMA [learning]');
GO

IF OBJECT_ID(N'[etl].[ImportBatch]', N'U') IS NULL
BEGIN
    CREATE TABLE [etl].[ImportBatch]
    (
        [BatchId] uniqueidentifier NOT NULL CONSTRAINT [PK_ImportBatch] PRIMARY KEY,
        [SourceSystem] nvarchar(40) NOT NULL,
        [SourceFile] nvarchar(260) NOT NULL,
        [Status] nvarchar(16) NOT NULL,
        [StartedAtUtc] datetime2(3) NOT NULL,
        [CompletedAtUtc] datetime2(3) NULL,
        [ReadCount] int NOT NULL CONSTRAINT [DF_ImportBatch_ReadCount] DEFAULT (0),
        [AcceptedCount] int NOT NULL CONSTRAINT [DF_ImportBatch_AcceptedCount] DEFAULT (0),
        [RejectedCount] int NOT NULL CONSTRAINT [DF_ImportBatch_RejectedCount] DEFAULT (0),
        [InsertedCount] int NOT NULL CONSTRAINT [DF_ImportBatch_InsertedCount] DEFAULT (0),
        [UpdatedCount] int NOT NULL CONSTRAINT [DF_ImportBatch_UpdatedCount] DEFAULT (0),
        [ErrorMessage] nvarchar(2000) NULL,
        CONSTRAINT [CK_ImportBatch_Status] CHECK ([Status] IN (N'Loading', N'Completed', N'Failed'))
    );
END;

IF OBJECT_ID(N'[etl].[AccountSnapshotStage]', N'U') IS NULL
BEGIN
    CREATE TABLE [etl].[AccountSnapshotStage]
    (
        [BatchId] uniqueidentifier NOT NULL,
        [RowNumber] int NOT NULL,
        [SourceSystem] nvarchar(40) NULL,
        [SourceAccountId] nvarchar(100) NULL,
        [CustomerName] nvarchar(120) NULL,
        [AccountNumber] nvarchar(40) NULL,
        [Balance] decimal(19,2) NULL,
        [SnapshotDate] date NULL,
        [ValidationError] nvarchar(1000) NULL,
        [RawData] nvarchar(4000) NOT NULL,
        CONSTRAINT [PK_AccountSnapshotStage] PRIMARY KEY ([BatchId], [RowNumber]),
        CONSTRAINT [FK_AccountSnapshotStage_ImportBatch] FOREIGN KEY ([BatchId])
            REFERENCES [etl].[ImportBatch] ([BatchId]),
        CONSTRAINT [CK_AccountSnapshotStage_ValidRow] CHECK
        (
            [ValidationError] IS NOT NULL OR
            ([SourceSystem] IS NOT NULL AND [SourceAccountId] IS NOT NULL AND [CustomerName] IS NOT NULL
             AND [AccountNumber] IS NOT NULL AND [Balance] IS NOT NULL AND [Balance] >= 0
             AND [SnapshotDate] IS NOT NULL)
        )
    );
END;

IF OBJECT_ID(N'[etl].[RejectedRow]', N'U') IS NULL
BEGIN
    CREATE TABLE [etl].[RejectedRow]
    (
        [BatchId] uniqueidentifier NOT NULL,
        [RowNumber] int NOT NULL,
        [RawData] nvarchar(4000) NOT NULL,
        [Reason] nvarchar(1000) NOT NULL,
        [RejectedAtUtc] datetime2(3) NOT NULL,
        CONSTRAINT [PK_RejectedRow] PRIMARY KEY ([BatchId], [RowNumber]),
        CONSTRAINT [FK_RejectedRow_ImportBatch] FOREIGN KEY ([BatchId])
            REFERENCES [etl].[ImportBatch] ([BatchId])
    );
END;
GO

IF OBJECT_ID(N'[learning].[AccountSnapshot]', N'U') IS NULL
BEGIN
    CREATE TABLE [learning].[AccountSnapshot]
    (
        [SourceSystem] nvarchar(40) NOT NULL,
        [SourceAccountId] nvarchar(100) NOT NULL,
        [SnapshotDate] date NOT NULL,
        [CustomerName] nvarchar(120) NOT NULL,
        [AccountNumber] nvarchar(40) NOT NULL,
        [Balance] decimal(19,2) NOT NULL,
        [LastBatchId] uniqueidentifier NOT NULL,
        [UpdatedAtUtc] datetime2(3) NOT NULL,
        CONSTRAINT [PK_AccountSnapshot] PRIMARY KEY ([SourceSystem], [SourceAccountId], [SnapshotDate]),
        CONSTRAINT [CK_AccountSnapshot_Balance] CHECK ([Balance] >= 0),
        CONSTRAINT [FK_AccountSnapshot_ImportBatch] FOREIGN KEY ([LastBatchId])
            REFERENCES [etl].[ImportBatch] ([BatchId])
    );

END;

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'[learning].[AccountSnapshot]')
      AND [name] = N'IX_AccountSnapshot_Source_Date'
)
    CREATE INDEX [IX_AccountSnapshot_Source_Date]
        ON [learning].[AccountSnapshot] ([SourceSystem], [SnapshotDate], [SourceAccountId])
        INCLUDE ([CustomerName], [AccountNumber], [Balance]);

IF OBJECT_ID(N'[learning].[AccountSnapshotAudit]', N'U') IS NULL
BEGIN
    CREATE TABLE [learning].[AccountSnapshotAudit]
    (
        [AuditId] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_AccountSnapshotAudit] PRIMARY KEY,
        [SourceSystem] nvarchar(40) NOT NULL,
        [SourceAccountId] nvarchar(100) NOT NULL,
        [SnapshotDate] date NOT NULL,
        [Action] nvarchar(10) NOT NULL,
        [OldBalance] decimal(19,2) NULL,
        [NewBalance] decimal(19,2) NOT NULL,
        [BatchId] uniqueidentifier NOT NULL,
        [ChangedAtUtc] datetime2(3) NOT NULL
    );
END;
GO

CREATE OR ALTER FUNCTION [learning].[AccountSnapshotsBySource]
(
    @SourceSystem nvarchar(40),
    @FromDate date,
    @ToDate date
)
RETURNS TABLE
AS
RETURN
(
    SELECT [SourceSystem], [SourceAccountId], [SnapshotDate], [CustomerName],
           [AccountNumber], [Balance], [LastBatchId], [UpdatedAtUtc]
    FROM [learning].[AccountSnapshot]
    WHERE [SourceSystem] = @SourceSystem
      AND [SnapshotDate] >= @FromDate
      AND [SnapshotDate] <= @ToDate
);
GO

CREATE OR ALTER VIEW [learning].[AccountExposureBySource]
AS
    SELECT [SourceSystem], [SnapshotDate], COUNT_BIG(*) AS [AccountCount],
           SUM([Balance]) AS [TotalBalance]
    FROM [learning].[AccountSnapshot]
    GROUP BY [SourceSystem], [SnapshotDate];
GO

CREATE OR ALTER TRIGGER [learning].[TR_AccountSnapshot_Audit]
ON [learning].[AccountSnapshot]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO [learning].[AccountSnapshotAudit]
        ([SourceSystem], [SourceAccountId], [SnapshotDate], [Action],
         [OldBalance], [NewBalance], [BatchId], [ChangedAtUtc])
    SELECT i.[SourceSystem], i.[SourceAccountId], i.[SnapshotDate],
           CASE WHEN d.[SourceAccountId] IS NULL THEN N'INSERT' ELSE N'UPDATE' END,
           d.[Balance], i.[Balance], i.[LastBatchId], SYSUTCDATETIME()
    FROM inserted AS i
    LEFT JOIN deleted AS d
      ON d.[SourceSystem] = i.[SourceSystem]
     AND d.[SourceAccountId] = i.[SourceAccountId]
     AND d.[SnapshotDate] = i.[SnapshotDate];
END;
GO

CREATE OR ALTER PROCEDURE [etl].[ProcessAccountSnapshotBatch]
    @BatchId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF NOT EXISTS
        (
            SELECT 1 FROM [etl].[ImportBatch] WITH (UPDLOCK, HOLDLOCK)
            WHERE [BatchId] = @BatchId AND [Status] = N'Loading'
        )
            THROW 51001, 'Import batch is missing or is not in Loading status.', 1;

        IF EXISTS
        (
            SELECT 1
            FROM [etl].[AccountSnapshotStage]
            WHERE [BatchId] = @BatchId
              AND [ValidationError] IS NULL
              AND ([SourceSystem] IS NULL OR [SourceAccountId] IS NULL OR [CustomerName] IS NULL
                   OR [AccountNumber] IS NULL OR [Balance] IS NULL OR [Balance] < 0 OR [SnapshotDate] IS NULL)
        )
            THROW 51002, 'A staged row marked valid violates the account snapshot contract.', 1;

        INSERT INTO [etl].[RejectedRow] ([BatchId], [RowNumber], [RawData], [Reason], [RejectedAtUtc])
        SELECT s.[BatchId], s.[RowNumber], s.[RawData], s.[ValidationError], SYSUTCDATETIME()
        FROM [etl].[AccountSnapshotStage] AS s
        WHERE s.[BatchId] = @BatchId AND s.[ValidationError] IS NOT NULL;

        ;WITH Duplicates AS
        (
            SELECT [SourceSystem], [SourceAccountId], [SnapshotDate]
            FROM [etl].[AccountSnapshotStage]
            WHERE [BatchId] = @BatchId AND [ValidationError] IS NULL
            GROUP BY [SourceSystem], [SourceAccountId], [SnapshotDate]
            HAVING COUNT_BIG(*) > 1
        )
        INSERT INTO [etl].[RejectedRow] ([BatchId], [RowNumber], [RawData], [Reason], [RejectedAtUtc])
        SELECT s.[BatchId], s.[RowNumber], s.[RawData],
               N'Duplicate source account and snapshot date in this batch.', SYSUTCDATETIME()
        FROM [etl].[AccountSnapshotStage] AS s
        INNER JOIN Duplicates AS d
          ON d.[SourceSystem] = s.[SourceSystem]
         AND d.[SourceAccountId] = s.[SourceAccountId]
         AND d.[SnapshotDate] = s.[SnapshotDate]
        WHERE s.[BatchId] = @BatchId AND s.[ValidationError] IS NULL;

        SELECT s.[SourceSystem], s.[SourceAccountId], s.[SnapshotDate], s.[CustomerName],
               s.[AccountNumber], s.[Balance]
        INTO #ValidRows
        FROM [etl].[AccountSnapshotStage] AS s
        WHERE s.[BatchId] = @BatchId AND s.[ValidationError] IS NULL
          AND NOT EXISTS
          (
              SELECT 1
              FROM [etl].[AccountSnapshotStage] AS duplicate
              WHERE duplicate.[BatchId] = s.[BatchId]
                AND duplicate.[ValidationError] IS NULL
                AND duplicate.[SourceSystem] = s.[SourceSystem]
                AND duplicate.[SourceAccountId] = s.[SourceAccountId]
                AND duplicate.[SnapshotDate] = s.[SnapshotDate]
              GROUP BY duplicate.[SourceSystem], duplicate.[SourceAccountId], duplicate.[SnapshotDate]
              HAVING COUNT_BIG(*) > 1
          );

        UPDATE target
        SET target.[CustomerName] = source.[CustomerName],
            target.[AccountNumber] = source.[AccountNumber],
            target.[Balance] = source.[Balance],
            target.[LastBatchId] = @BatchId,
            target.[UpdatedAtUtc] = SYSUTCDATETIME()
        FROM [learning].[AccountSnapshot] AS target
        INNER JOIN #ValidRows AS source
          ON source.[SourceSystem] = target.[SourceSystem]
         AND source.[SourceAccountId] = target.[SourceAccountId]
         AND source.[SnapshotDate] = target.[SnapshotDate]
        WHERE target.[CustomerName] <> source.[CustomerName]
           OR target.[AccountNumber] <> source.[AccountNumber]
           OR target.[Balance] <> source.[Balance];
        DECLARE @UpdatedCount int = @@ROWCOUNT;

        INSERT INTO [learning].[AccountSnapshot]
            ([SourceSystem], [SourceAccountId], [SnapshotDate], [CustomerName],
             [AccountNumber], [Balance], [LastBatchId], [UpdatedAtUtc])
        SELECT source.[SourceSystem], source.[SourceAccountId], source.[SnapshotDate],
               source.[CustomerName], source.[AccountNumber], source.[Balance],
               @BatchId, SYSUTCDATETIME()
        FROM #ValidRows AS source
        WHERE NOT EXISTS
        (
            SELECT 1 FROM [learning].[AccountSnapshot] AS target WITH (UPDLOCK, HOLDLOCK)
            WHERE target.[SourceSystem] = source.[SourceSystem]
              AND target.[SourceAccountId] = source.[SourceAccountId]
              AND target.[SnapshotDate] = source.[SnapshotDate]
        );
        DECLARE @InsertedCount int = @@ROWCOUNT;

        UPDATE [etl].[ImportBatch]
        SET [Status] = N'Completed',
            [CompletedAtUtc] = SYSUTCDATETIME(),
            [ReadCount] = (SELECT COUNT(*) FROM [etl].[AccountSnapshotStage] WHERE [BatchId] = @BatchId),
            [AcceptedCount] = (SELECT COUNT(*) FROM #ValidRows),
            [RejectedCount] = (SELECT COUNT(*) FROM [etl].[RejectedRow] WHERE [BatchId] = @BatchId),
            [InsertedCount] = @InsertedCount,
            [UpdatedCount] = @UpdatedCount
        WHERE [BatchId] = @BatchId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO
