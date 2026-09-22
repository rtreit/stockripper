using Stockripper.Domain.Common;

namespace Stockripper.Domain.Goals;

public sealed record Money
{
    public Money(decimal amount, string currency)
    {
        if (amount <= 0m)
        {
            throw new DomainRuleViolationException("The capital allocation must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(currency)
            || currency.Length != 3
            || currency.Any(character => character is < 'A' or > 'Z'))
        {
            throw new DomainRuleViolationException("The currency must be a three-letter uppercase code.");
        }

        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public string Currency { get; }
}
