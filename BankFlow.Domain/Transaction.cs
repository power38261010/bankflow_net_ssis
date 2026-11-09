namespace BankFlow.Domain;

public sealed class Transaction
{
    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public decimal Amount { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public DateTime CreatedAt { get; private set; }
    public Guid? TransferId { get; private set; }

    private Transaction() { }

    public Transaction(Guid id, Guid accountId, decimal amount, string type, string description, Guid? transferId = null)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be greater than zero.");

        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("Transaction type is required.", nameof(type));

        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("Description is required.", nameof(description));

        Id = id;
        AccountId = accountId;
        Amount = amount;
        Type = type;
        Description = description;
        CreatedAt = DateTime.UtcNow;
        TransferId = transferId;
    }
}
