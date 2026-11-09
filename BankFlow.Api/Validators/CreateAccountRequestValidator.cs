using FluentValidation;

namespace BankFlow.Api;

public sealed class CreateAccountRequestValidator : AbstractValidator<CreateAccountRequest>
{
    public CreateAccountRequestValidator()
    {
        RuleFor(x => x.OwnerUserId).NotEmpty().MaximumLength(450);
        RuleFor(x => x.CustomerName)
            .NotEmpty()
            .MinimumLength(2)
            .MaximumLength(100);

        RuleFor(x => x.AccountNumber)
            .NotEmpty()
            .Length(4, 20)
            .Matches("^[a-zA-Z0-9-]+$");
    }
}

public sealed class DepositRequestValidator : AbstractValidator<DepositRequest>
{
    public DepositRequestValidator()
    {
        RuleFor(x => x.Amount)
            .GreaterThan(0)
            .LessThanOrEqualTo(1000000m)
            .PrecisionScale(18, 2, true);
    }
}
