-- Sybase ASE extraction template; adjust schema/table and types to the source system.
-- Export the result as CSV using the shared six-column account snapshot contract.
SELECT
    'Sybase' AS SourceSystem,
    CONVERT(varchar(100), a.AccountId) AS SourceAccountId,
    CONVERT(varchar(120), a.CustomerName) AS CustomerName,
    CONVERT(varchar(40), a.AccountNumber) AS AccountNumber,
    CONVERT(numeric(19,2), a.Balance) AS Balance,
    CONVERT(date, GETUTCDATE()) AS SnapshotDate
FROM dbo.LegacyAccount a
WHERE a.IsActive = 1
ORDER BY a.AccountId;

-- Practice: compare CONVERT/date support in your ASE version and avoid applying
-- functions to indexed filter columns when defining an incremental extraction.
