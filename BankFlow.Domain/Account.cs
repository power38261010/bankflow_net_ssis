namespace BankFlow.Domain;

public sealed class Account
{
    public Guid Id { get; private set; }
    public string CustomerName { get; private set; } = string.Empty;
    public string AccountNumber { get; private set; } = string.Empty;
    public string? OwnerUserId { get; private set; }
    public decimal Balance { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private Account() { }

    public Account(Guid id, string customerName, string accountNumber, string? ownerUserId = null)
    {
        if (string.IsNullOrWhiteSpace(customerName))
            throw new ArgumentException("Customer name is required.", nameof(customerName));

        if (string.IsNullOrWhiteSpace(accountNumber))
            throw new ArgumentException("Account number is required.", nameof(accountNumber));

        Id = id;
        CustomerName = customerName;
        AccountNumber = accountNumber;
        if (ownerUserId is not null) AssignOwner(ownerUserId);
        Balance = 0m;
        CreatedAt = DateTime.UtcNow;
    }

    public void Deposit(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Deposit amount must be greater than zero.");

        Balance += amount;
    }

    public void AssignOwner(string ownerUserId)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new ArgumentException("Account owner is required.", nameof(ownerUserId));
        if (OwnerUserId is not null)
            throw new InvalidOperationException("Account already has an owner.");
        OwnerUserId = ownerUserId;
    }

    public void Withdraw(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Withdrawal amount must be greater than zero.");

        if (amount > Balance)
            throw new InvalidOperationException("Insufficient funds.");

        Balance -= amount;
    }
}
