namespace Callboard.Cards;

/// <summary>
/// Closed union over the two shapes a write to the record can end in — the same shape as
/// <see cref="CardFileParseResult"/> and for the same reason: a write failure (a lock timeout, a
/// missing target for an append) is an expected outcome the caller must handle, not an exception
/// escaping past the caller's control. No verb wires this to a <see cref="Callboard.Cli.CliRefusal"/>
/// in this block — nothing in 2.5–2.8 adds a CLI command — so this stays library-internal until a
/// future verb converts a <see cref="Failure"/> at the CLI boundary.
/// </summary>
internal abstract record CardWriteResult
{
    private CardWriteResult()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Success, TResult> onSuccess,
        Func<Failure, TResult> onFailure);

    internal sealed record Success : CardWriteResult
    {
        internal override TResult Match<TResult>(Func<Success, TResult> onSuccess, Func<Failure, TResult> onFailure) =>
            onSuccess(this);
    }

    internal sealed record Failure(string Reason) : CardWriteResult
    {
        internal override TResult Match<TResult>(Func<Success, TResult> onSuccess, Func<Failure, TResult> onFailure) =>
            onFailure(this);
    }
}
