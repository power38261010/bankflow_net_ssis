namespace BankFlow.Domain;

public sealed class Transfer
{
    public Guid Id { get; private set; }
    public Guid SourceAccountId { get; private set; }
    public Guid DestinationAccountId { get; private set; }
    public string DestinationAccountNumber { get; private set; } = string.Empty;
    public string ActorUserId { get; private set; } = string.Empty;
    public Guid IdempotencyKey { get; private set; }
    public decimal Amount { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }

    private Transfer() { }

    public Transfer(Account source, Account destination, string actorUserId, Guid idempotencyKey,
        decimal amount, string description)
    {
        if (source.Id == destination.Id) throw new ArgumentException("Select a different destination account.");
        if (amount <= 0 || amount > 1000000m || decimal.Round(amount, 2) != amount)
            throw new ArgumentException("Invalid transfer amount.");
        if (string.IsNullOrWhiteSpace(actorUserId) || actorUserId.Length > 450 || idempotencyKey == Guid.Empty)
            throw new ArgumentException("Transfer identity is required.");
        if (description.Length > 140) throw new ArgumentException("Description is too long.");
        if (destination.Balance > 9999999999999999.99m - amount)
            throw new InvalidOperationException("Destination balance limit exceeded.");

        Id = Guid.NewGuid();
        SourceAccountId = source.Id;
        DestinationAccountId = destination.Id;
        DestinationAccountNumber = destination.AccountNumber;
        ActorUserId = actorUserId;
        IdempotencyKey = idempotencyKey;
        Amount = amount;
        Description = description;
        CreatedAt = DateTimeOffset.UtcNow;
        source.Withdraw(amount);
        destination.Deposit(amount);
    }

    public bool Matches(Guid sourceAccountId, string destinationAccountNumber, decimal amount, string description) =>
        SourceAccountId == sourceAccountId &&
        string.Equals(DestinationAccountNumber, destinationAccountNumber, StringComparison.OrdinalIgnoreCase) &&
        Amount == amount && Description == description;
}