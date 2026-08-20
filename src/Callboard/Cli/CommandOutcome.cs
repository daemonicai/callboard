namespace Callboard.Cli;

/// <summary>
/// Closed union over the two shapes a command can end in. The constructor is private and every
/// case is a sealed nested record, so no other assembly can add a case. <see cref="Match{TResult}"/>
/// is the only way to consume the value: it is abstract on the base, so adding a third case is a
/// compile error everywhere it is not yet handled — C#'s switch-expression exhaustiveness
/// analysis does not itself treat a private-constructor record hierarchy as closed, so this
/// visitor is what actually makes an unhandled case a compile error (ADR-0001: a refusal is a
/// returned result, never a thrown exception).
/// </summary>
internal abstract record CommandOutcome
{
    private CommandOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Success, TResult> onSuccess,
        Func<Refusal, TResult> onRefusal);

    internal sealed record Success(ICommandResult Result) : CommandOutcome
    {
        internal override TResult Match<TResult>(Func<Success, TResult> onSuccess, Func<Refusal, TResult> onRefusal) =>
            onSuccess(this);
    }

    internal sealed record Refusal(string Code, string Message) : CommandOutcome
    {
        internal override TResult Match<TResult>(Func<Success, TResult> onSuccess, Func<Refusal, TResult> onRefusal) =>
            onRefusal(this);
    }
}
