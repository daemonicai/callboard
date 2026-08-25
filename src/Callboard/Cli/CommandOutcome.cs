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

    /// <param name="Code">The machine-readable refusal code.</param>
    /// <param name="Message">The human-readable refusal text.</param>
    /// <param name="Rule">The rule that refused the attempt (process-enforcement: "Refusals are
    /// explained and attributable", §9 block A) — populated only for a refusal sourced from an
    /// outcome case that implements <see cref="Cards.ICardRefusalReason"/>; <see langword="null"/>
    /// for every refusal this build has not yet retrofitted onto that mechanism.</param>
    /// <param name="Remedy">What would satisfy <paramref name="Rule"/> — same population rule as
    /// <paramref name="Rule"/>.</param>
    internal sealed record Refusal(string Code, string Message, string? Rule = null, string? Remedy = null) : CommandOutcome
    {
        internal override TResult Match<TResult>(Func<Success, TResult> onSuccess, Func<Refusal, TResult> onRefusal) =>
            onRefusal(this);
    }
}
