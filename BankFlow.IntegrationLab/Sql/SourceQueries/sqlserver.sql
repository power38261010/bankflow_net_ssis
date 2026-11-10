-- SQL Server source template. Replace dbo.LegacyAccount with the source table.
-- Keep these aliases stable: the import contract is identical across source engines.
SELECT
    N'SQLServer' AS SourceSystem,
    CONVERT(nvarchar(100), a.AccountId) AS SourceAccountId,
    CONVERT(nvarchar(120), a.CustomerName) AS CustomerName,
    CONVERT(nvarchar(40), a.AccountNumber) AS AccountNumber,
    CONVERT(decimal(19,2), a.Balance) AS Balance,
    CONVERT(date, SYSUTCDATETIME()) AS SnapshotDate
FROM dbo.LegacyAccount AS a;
