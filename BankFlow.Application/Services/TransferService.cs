using BankFlow.Application.Interfaces;
using BankFlow.Domain;

namespace BankFlow.Application.Services;

public sealed class TransferConflictException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}

public sealed class TransferService(ITransferUnitOfWork unitOfWork)
{
    public async Task<TransferOutcome> ExecuteAsync(Guid sourceAccountId, string destinationAccountNumber,
        decimal amount, string? description, string actorUserId, bool isOperator, Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (amount <= 0 || amount > 1000000m || decimal.Round(amount, 2) != amount)
            throw new ArgumentException("Invalid transfer amount.");
        if (string.IsNullOrWhiteSpace(destinationAccountNumber) || destinationAccountNumber.Length > 25)
            throw new ArgumentException("Invalid destination account.");
        if (string.IsNullOrWhiteSpace(actorUserId) || idempotencyKey == Guid.Empty)
            throw new ArgumentException("Transfer identity is required.");
        var memo = description?.Trim() ?? string.Empty;
        var number = destinationAccountNumber.Trim();
        if (memo.Length > 140) throw new ArgumentException("Description is too long.");
        var source = await unitOfWork.GetAccountAsync(sourceAccountId, cancellationToken);
        if (source is null || source.OwnerUserId is null || (!isOperator && source.OwnerUserId != actorUserId))
            throw new KeyNotFoundException("Source account is not available.");

        var existing = await unitOfWork.GetByKeyAsync(actorUserId, idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            EnsureSameRequest(existing, sourceAccountId, number, amount, memo);
            return new TransferOutcome(existing, true);
        }

        var destination = await unitOfWork.GetAccountByNumberAsync(number, cancellationToken);
        if (destination is null || destination.OwnerUserId is null)
            throw new KeyNotFoundException("Destination account is not available.");
        if (source.Id == destination.Id) throw new ArgumentException("Select a different destination account.");
        if (source.Balance < amount) throw new TransferConflictException("insufficient_funds");
        if (destination.Balance > 9999999999999999.99m - amount)
            throw new TransferConflictException("destination_limit");

        var transfer = new Transfer(source, destination, actorUserId, idempotencyKey, amount, memo);
        var debit = new Transaction(Guid.NewGuid(), source.Id, amount, "TransferOut", "Internal transfer sent", transfer.Id);
        var credit = new Transaction(Guid.NewGuid(), destination.Id, amount, "TransferIn", "Internal transfer received", transfer.Id);
        unitOfWork.Add(transfer, debit, credit);
        var outcome = await unitOfWork.CommitAsync(transfer, cancellationToken);
        EnsureSameRequest(outcome.Transfer, sourceAccountId, number, amount, memo);
        return outcome;
    }

    public async Task<Transfer?> GetReceiptAsync(Guid id, string actorUserId, bool isOperator,
        CancellationToken cancellationToken = default)
    {
        var transfer = await unitOfWork.GetByIdAsync(id, cancellationToken);
        if (transfer is null) return null;
        if (isOperator) return transfer;
        var source = await unitOfWork.GetAccountAsync(transfer.SourceAccountId, cancellationToken);
        return source?.OwnerUserId == actorUserId ? transfer : null;
    }

    private static void EnsureSameRequest(Transfer transfer, Guid sourceId, string number, decimal amount, string memo)
    {
        if (!transfer.Matches(sourceId, number, amount, memo))
            throw new TransferConflictException("idempotency_conflict");
    }
}