using Stockripper.Domain.Common;

namespace Stockripper.Domain.Execution;

public enum ExecutionMode
{
    REPLAY,
    RESEARCH,
    PAPER_REVIEW,
    PAPER_AUTO,
    LIVE_REVIEW,
    LIVE_AUTO
}

public static class ExecutionModeRules
{
    public static ExecutionMode Parse(string? value) => value switch
    {
        "REPLAY" => ExecutionMode.REPLAY,
        "RESEARCH" => ExecutionMode.RESEARCH,
        "PAPER_REVIEW" => ExecutionMode.PAPER_REVIEW,
        "PAPER_AUTO" => ExecutionMode.PAPER_AUTO,
        "LIVE_REVIEW" => ExecutionMode.LIVE_REVIEW,
        "LIVE_AUTO" => ExecutionMode.LIVE_AUTO,
        _ => throw new DomainRuleViolationException("The execution mode is missing or unknown.")
    };

    public static string ToWireValue(this ExecutionMode mode) => mode switch
    {
        ExecutionMode.REPLAY => "REPLAY",
        ExecutionMode.RESEARCH => "RESEARCH",
        ExecutionMode.PAPER_REVIEW => "PAPER_REVIEW",
        ExecutionMode.PAPER_AUTO => "PAPER_AUTO",
        ExecutionMode.LIVE_REVIEW => "LIVE_REVIEW",
        ExecutionMode.LIVE_AUTO => "LIVE_AUTO",
        _ => throw new DomainRuleViolationException("The execution mode is unknown.")
    };

    public static bool IsExecutable(this ExecutionMode mode) => mode switch
    {
        ExecutionMode.REPLAY or ExecutionMode.RESEARCH => false,
        ExecutionMode.PAPER_REVIEW or ExecutionMode.PAPER_AUTO => true,
        ExecutionMode.LIVE_REVIEW or ExecutionMode.LIVE_AUTO => true,
        _ => throw new DomainRuleViolationException("The execution mode is unknown.")
    };

    public static bool RequiresIntentReview(this ExecutionMode mode) => mode switch
    {
        ExecutionMode.PAPER_REVIEW or ExecutionMode.LIVE_REVIEW => true,
        ExecutionMode.REPLAY or ExecutionMode.RESEARCH => false,
        ExecutionMode.PAPER_AUTO or ExecutionMode.LIVE_AUTO => false,
        _ => throw new DomainRuleViolationException("The execution mode is unknown.")
    };

    public static BrokerEnvironment? RequiredBrokerEnvironment(this ExecutionMode mode) => mode switch
    {
        ExecutionMode.REPLAY or ExecutionMode.RESEARCH => null,
        ExecutionMode.PAPER_REVIEW or ExecutionMode.PAPER_AUTO => BrokerEnvironment.PAPER,
        ExecutionMode.LIVE_REVIEW or ExecutionMode.LIVE_AUTO => BrokerEnvironment.LIVE,
        _ => throw new DomainRuleViolationException("The execution mode is unknown.")
    };
}
