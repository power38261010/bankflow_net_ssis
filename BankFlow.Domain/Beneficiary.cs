namespace BankFlow.Domain;

public sealed class Beneficiary
{
    public Guid Id { get; private set; }
    public string OwnerUserId { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string AccountNumber { get; private set; } = string.Empty;
    public bool Active { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid Version { get; private set; }

    private Beneficiary() { }
    public Beneficiary(string ownerUserId, string name, string accountNumber, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId) || ownerUserId.Length > 450) throw new ArgumentException("Owner is required.");
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80) throw new ArgumentException("Invalid beneficiary name.");
        if (string.IsNullOrWhiteSpace(accountNumber) || accountNumber.Length > 25) throw new ArgumentException("Invalid account number.");
        Id = Guid.NewGuid(); OwnerUserId = ownerUserId; Name = name.Trim(); AccountNumber = accountNumber;
        CreatedAt = now; Version = Guid.NewGuid();
    }
    public void Archive() { Active = false; Version = Guid.NewGuid(); }
}