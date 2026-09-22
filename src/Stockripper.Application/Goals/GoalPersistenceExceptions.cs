namespace Stockripper.Application.Goals;

public sealed class GoalDraftNotFoundException() : Exception("The Goal draft was not found.");

public sealed class GoalDraftConcurrencyException()
    : Exception("The Goal draft changed before the operation completed.");

public sealed class GoalDraftAlreadyPublishedException()
    : Exception("The Goal draft is already published.");
