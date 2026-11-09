using BankFlow.Application.Interfaces;
using BankFlow.Domain;

namespace BankFlow.Application.Services;

public sealed class BankingService
{
    private readonly IAccountRepository _accountRepository;
    private readonly ITransactionRepository _transactionRepository;

    public BankingService(IAccountRepository accountRepository, ITransactionRepository transactionRepository)
    {
        _accountRepository = accountRepository;
        _transactionRepository = transactionRepository;
    }

    public async Task<Account> CreateAccountAsync(string customerName, string accountNumber, CancellationToken cancellationToken = default, string? ownerUserId = null)
    {
        if (string.IsNullOrWhiteSpace(customerName))
            throw new ArgumentException("Customer name is required.", nameof(customerName));

        if (string.IsNullOrWhiteSpace(accountNumber))
            throw new ArgumentException("Account number is required.", nameof(accountNumber));

        var existing = await _accountRepository.GetByAccountNumberAsync(accountNumber, cancellationToken);
        if (existing is not null)
            throw new InvalidOperationException("Account number already exists.");

        var account = new Account(Guid.NewGuid(), customerName, accountNumber, ownerUserId);
        await _accountRepository.AddAsync(account, cancellationToken);
        await _accountRepository.SaveChangesAsync(cancellationToken);

        return account;
    }

    public async Task<Account> DepositAsync(Guid accountId, decimal amount, CancellationToken cancellationToken = default)
    {
        var account = await _accountRepository.GetByIdAsync(accountId, cancellationToken)
            ?? throw new KeyNotFoundException("Account not found.");

        account.Deposit(amount);

        var transaction = new Transaction(Guid.NewGuid(), account.Id, amount, "Deposit", "Cash deposit");
        await _transactionRepository.AddAsync(transaction, cancellationToken);
        await _accountRepository.UpdateAsync(account, cancellationToken);
        await _transactionRepository.SaveChangesAsync(cancellationToken);
        await _accountRepository.SaveChangesAsync(cancellationToken);

        return account;
    }

    public async Task<Account> WithdrawAsync(Guid accountId, decimal amount, CancellationToken cancellationToken = default)
    {
        var account = await _accountRepository.GetByIdAsync(accountId, cancellationToken)
            ?? throw new KeyNotFoundException("Account not found.");

        account.Withdraw(amount);

        var transaction = new Transaction(Guid.NewGuid(), account.Id, amount, "Withdrawal", "Cash withdrawal");
        await _transactionRepository.AddAsync(transaction, cancellationToken);
        await _accountRepository.UpdateAsync(account, cancellationToken);
        await _transactionRepository.SaveChangesAsync(cancellationToken);
        await _accountRepository.SaveChangesAsync(cancellationToken);

        return account;
    }

    public async Task<IEnumerable<Transaction>> GetTransactionsAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var account = await _accountRepository.GetByIdAsync(accountId, cancellationToken)
            ?? throw new KeyNotFoundException("Account not found.");

        return await _transactionRepository.GetByAccountIdAsync(account.Id, cancellationToken);
    }
}
