-- Teradata extraction template; adapt database and column names to your warehouse.
-- Export the result as CSV using the shared six-column account snapshot contract.
SELECT
    'Teradata' AS SourceSystem,
    CAST(Account_Id AS VARCHAR(100)) AS SourceAccountId,
    CAST(Customer_Name AS VARCHAR(120)) AS CustomerName,
    CAST(Account_Number AS VARCHAR(40)) AS AccountNumber,
    CAST(Balance AS DECIMAL(19,2)) AS Balance,
    CURRENT_DATE AS SnapshotDate
FROM FinanceDb.LegacyAccount
WHERE Is_Active = 1
QUALIFY ROW_NUMBER() OVER (PARTITION BY Account_Id ORDER BY Effective_Timestamp DESC) = 1;

-- Practice: explain QUALIFY, Teradata primary indexes, and why a primary index
-- is a distribution choice rather than a SQL Server-style secondary index.
