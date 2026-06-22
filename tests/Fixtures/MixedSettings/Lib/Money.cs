namespace Lib;

public sealed class Money
{
    public decimal Amount { get; init; }

    // No local `using` for System / System.Collections.Generic — relies on implicit usings.
    public string Currency { get; init; } = "USD";

    public List<string> Tags { get; init; } = new();
}
