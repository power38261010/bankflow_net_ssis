-- Run only in the disposable BankFlowTraining database after setup.
SET NOCOUNT ON;

DECLARE @BatchId uniqueidentifier = NEWID();
DECLARE @RowCount int = 100000;

INSERT INTO [etl].[ImportBatch] ([BatchId], [SourceSystem], [SourceFile], [Status], [StartedAtUtc])
VALUES (@BatchId, N'PerformanceLab', N'synthetic-100k', N'Loading', SYSUTCDATETIME());

INSERT INTO [etl].[AccountSnapshotStage]
    ([BatchId], [RowNumber], [SourceSystem], [SourceAccountId], [CustomerName],
     [AccountNumber], [Balance], [SnapshotDate], [ValidationError], [RawData])
SELECT @BatchId, numbers.[n],
       CASE numbers.[n] % 3
           WHEN 0 THEN N'SQLServer'
           WHEN 1 THEN N'Sybase'
           ELSE N'Teradata'
       END,
       CONCAT(N'PERF-', RIGHT(CONCAT(N'000000', numbers.[n]), 6)),
       CONCAT(N'Customer ', numbers.[n]),
       CONCAT(N'PERF-ACCT-', numbers.[n]),
       CONVERT(decimal(19,2), (numbers.[n] % 10000000) / 100.0),
       DATEADD(day, -(numbers.[n] % 365), CONVERT(date, '20260101', 112)),
       NULL,
       CONCAT(N'Synthetic row ', numbers.[n])
FROM
(
    SELECT TOP (@RowCount)
        CONVERT(int, ROW_NUMBER() OVER (ORDER BY (SELECT NULL))) AS [n]
    FROM sys.all_objects AS a
    CROSS JOIN sys.all_objects AS b
) AS numbers
ORDER BY numbers.[n];

EXEC [etl].[ProcessAccountSnapshotBatch] @BatchId = @BatchId;

SELECT [BatchId], [ReadCount], [AcceptedCount], [RejectedCount], [InsertedCount], [UpdatedCount]
FROM [etl].[ImportBatch]
WHERE [BatchId] = @BatchId;
