namespace Callboard.Cards;

/// <summary>
/// Closed union over the two shapes checking a <see cref="CardKind"/>/<see cref="CardScope"/> pair
/// against card-model's "Scope determines lifetime" table can end in — same shape as
/// <see cref="CardWriteResult"/> and for the same reason: a refused pairing is an expected outcome
/// a caller must handle, not an exception. No verb wires this to a
/// <see cref="Callboard.Cli.CliRefusal"/> in this block — 4.4 checks the pairing, it does not wire
/// a CLI surface — so this stays library-internal until a future section's create/promote verb
/// converts a <see cref="Refused"/> at the CLI boundary.
/// </summary>
internal abstract record CardScopeValidationResult
{
    private CardScopeValidationResult()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<TResult> onValid,
        Func<Refused, TResult> onRefused);

    internal static readonly CardScopeValidationResult Valid = new ValidCase();

    private sealed record ValidCase : CardScopeValidationResult
    {
        internal override TResult Match<TResult>(Func<TResult> onValid, Func<Refused, TResult> onRefused) => onValid();
    }

    internal sealed record Refused(string Reason) : CardScopeValidationResult
    {
        internal override TResult Match<TResult>(Func<TResult> onValid, Func<Refused, TResult> onRefused) =>
            onRefused(this);
    }
}
