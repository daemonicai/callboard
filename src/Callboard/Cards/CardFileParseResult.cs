namespace Callboard.Cards;

/// <summary>
/// Closed union over the two shapes parsing a card file can end in — the same shape as
/// <see cref="Callboard.Cli.CommandOutcome"/> and for the same reason: the constructor is
/// private, every case is a sealed nested record, and <see cref="Match{TResult}"/> is abstract on
/// the base, so a caller cannot consume this without handling both, and no other assembly can add
/// a third case. Parse failures are not exceptions — malformed card text is an expected input
/// (a hand-edited file, a half-written concurrent append caught mid-flight) that this type turns
/// into a returned result rather than a thrown one.
/// </summary>
internal abstract record CardFileParseResult
{
    private CardFileParseResult()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Success, TResult> onSuccess,
        Func<Failure, TResult> onFailure);

    internal sealed record Success(CardFile Card) : CardFileParseResult
    {
        internal override TResult Match<TResult>(Func<Success, TResult> onSuccess, Func<Failure, TResult> onFailure) =>
            onSuccess(this);
    }

    internal sealed record Failure(string Reason) : CardFileParseResult
    {
        internal override TResult Match<TResult>(Func<Success, TResult> onSuccess, Func<Failure, TResult> onFailure) =>
            onFailure(this);
    }
}
