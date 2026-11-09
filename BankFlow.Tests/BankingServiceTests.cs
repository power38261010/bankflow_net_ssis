using BankFlow.Application.Services;
using BankFlow.Domain;

namespace BankFlow.Tests;

public class BankingServiceTests
{
    [Fact]
    public async Task CreateAccount_ShouldPersistNewAccount()
    {
        var repo = new InMemoryAccountRepository();
        var transactionRepo = new InMemoryTransactionRepository();
        var service = new BankingService(repo, transactionRepo);

        var account = await service.CreateAccountAsync("Ana García", "1001");

        Assert.Equal("Ana García", account.CustomerName);
        Assert.Equal(0m, account.Balance);
        Assert.NotNull(await repo.GetByAccountNumberAsync("1001"));
    }

    [Fact]
    public async Task Deposit_ShouldIncreaseBalanceAndCreateTransaction()
    {
        var repo = new InMemoryAccountRepository();
        var transactionRepo = new InMemoryTransactionRepository();
        var service = new BankingService(repo, transactionRepo);

        var account = await service.CreateAccountAsync("Luis Pérez", "1002");
        var updated = await service.DepositAsync(account.Id, 250m);

        Assert.Equal(250m, updated.Balance);
        Assert.Single(await transactionRepo.GetByAccountIdAsync(account.Id));
    }

    [Fact]
    public async Task Withdraw_ShouldRejectWhenInsufficientFunds()
    {
        var repo = new InMemoryAccountRepository();
        var transactionRepo = new InMemoryTransactionRepository();
        var service = new BankingService(repo, transactionRepo);

        var account = await service.CreateAccountAsync("María López", "1003");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.WithdrawAsync(account.Id, 100m));
    }

    private sealed class InMemoryAccountRepository : BankFlow.Application.Interfaces.IAccountRepository
    {
        private readonly Dictionary<Guid, Account> _accounts = new();

        public Task<Account?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(_accounts.TryGetValue(id, out var account) ? account : null);

        public Task<Account?> GetByAccountNumberAsync(string accountNumber, CancellationToken cancellationToken = default)
            => Task.FromResult(_accounts.Values.FirstOrDefault(a => a.AccountNumber == accountNumber));

        public Task<IEnumerable<Account>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<Account>>(_accounts.Values.ToList());

        public Task<IEnumerable<Account>> GetByOwnerAsync(string ownerUserId, CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<Account>>(_accounts.Values.Where(account => account.OwnerUserId == ownerUserId).ToList());

        public Task AddAsync(Account account, CancellationToken cancellationToken = default)
        {
            _accounts[account.Id] = account;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Account account, CancellationToken cancellationToken = default)
        {
            _accounts[account.Id] = account;
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class InMemoryTransactionRepository : BankFlow.Application.Interfaces.ITransactionRepository
    {
        private readonly List<Transaction> _transactions = new();

        public Task AddAsync(Transaction transaction, CancellationToken cancellationToken = default)
        {
            _transactions.Add(transaction);
            return Task.CompletedTask;
        }

        public Task<IEnumerable<Transaction>> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<Transaction>>(_transactions.Where(t => t.AccountId == accountId).ToList());

        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
