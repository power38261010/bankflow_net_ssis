-- Include the actual execution plan in SSMS and compare logical reads/timing.
SET STATISTICS IO ON;
SET STATISTICS TIME ON;

DECLARE @SourceSystem nvarchar(40) = N'SQLServer';
DECLARE @FromDate date = '2025-12-01';
DECLARE @ToDate date = '2025-12-31';

-- Sargable range: should be eligible for IX_AccountSnapshot_Source_Date.
SELECT [SourceAccountId], [Balance]
FROM [learning].[AccountSnapshot]
WHERE [SourceSystem] = @SourceSystem
  AND [SnapshotDate] >= @FromDate
  AND [SnapshotDate] < DATEADD(day, 1, @ToDate);

-- The inline table-valued function should expand into a relational query.
SELECT [SourceAccountId], [Balance]
FROM [learning].[AccountSnapshotsBySource](@SourceSystem, @FromDate, @ToDate);

-- Non-sargable comparison: compare its plan and logical reads with the range above.
SELECT [SourceAccountId], [Balance]
FROM [learning].[AccountSnapshot]
WHERE [SourceSystem] = @SourceSystem
  AND CONVERT(char(10), [SnapshotDate], 23) = '2025-12-31';

SET STATISTICS TIME OFF;
SET STATISTICS IO OFF;
