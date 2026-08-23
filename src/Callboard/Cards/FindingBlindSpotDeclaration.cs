namespace Callboard.Cards;

/// <summary>
/// What a clean finding declares about its blind spot (findings: "A clean finding requires a
/// blind-spot declaration") — either <see cref="None"/>, an explicit assertion that there is none,
/// or <see cref="RaisedAs"/>, a reference to the card the blind spot was raised as an
/// <c>obligation</c> or <c>hazard</c> on. This type carries the <em>declaration</em> only, never the
/// blind spot's own content — the card the declaration points to is where that content lives
/// (Architect ruling, §6 block A brief).
///
/// <para>
/// <b>A third state meaning "not declared" is unrepresentable on a constructed finding</b> — this is
/// the whole reason this type exists rather than a nullable <c>string?</c> on
/// <see cref="FindingCardFields"/>. <see cref="FindingCardFields.BlindSpot"/> is typed
/// <see cref="FindingBlindSpotDeclaration"/>, not <c>FindingBlindSpotDeclaration?</c>, so nullable
/// reference types (<c>TreatWarningsAsErrors</c>) turn any attempt to construct or <c>with</c> a
/// finding without supplying one of this type's two cases into a build error (CS8625 assigning
/// <see langword="null"/> to a non-nullable reference type), not a runtime null check reached late.
/// That is what makes 6.2's refusal a refusal about <em>input</em> — the caller could not have
/// omitted the declaration in the first place — rather than a nullable field discovered empty after
/// the fact.
/// </para>
///
/// <para>
/// <b>"Unrepresentable" means "not without an explicit <c>!</c>", not "impossible".</b>
/// <c>new FindingCardFields(..., BlindSpot: null!)</c> and <c>with { BlindSpot = default! }</c> both
/// compile and produce a <see cref="FindingCardFields"/> whose <c>BlindSpot</c> is <see
/// langword="null"/> at runtime — nullable reference types are a compile-time discipline, not a
/// runtime guard, and this type is exactly as bypassable as every other non-nullable reference-typed
/// domain field in this codebase. The backstop is this project's own C# idiom rule that a
/// null-forgiving <c>!</c> requires a comment justifying it — a reviewable marker at the call site,
/// not a structural impossibility (reviewer finding, §6 block A).
/// </para>
/// </summary>
internal abstract record FindingBlindSpotDeclaration
{
    private FindingBlindSpotDeclaration()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<TResult> onNone,
        Func<string, TResult> onRaisedAs);

    /// <summary>An explicit assertion that this finding has no blind spot.</summary>
    internal static readonly FindingBlindSpotDeclaration None = new NoneCase();

    /// <summary>The blind spot was raised as the <c>obligation</c> or <c>hazard</c> card identified
    /// by <paramref name="cardId"/> — the verb that actually raises that card is not this type's
    /// job (§6 block A brief: "the verb that actually raises the obligation/hazard card is block
    /// B's, not yours"). Never empty or whitespace-only — see this type's own validating
    /// accessor.</summary>
    internal static FindingBlindSpotDeclaration RaisedAs(string cardId) => new RaisedAsCase(cardId);

    private sealed record NoneCase : FindingBlindSpotDeclaration
    {
        internal override TResult Match<TResult>(Func<TResult> onNone, Func<string, TResult> onRaisedAs) => onNone();
    }

    private sealed record RaisedAsCase : FindingBlindSpotDeclaration
    {
        // Initialized to a placeholder here only to satisfy definite-assignment nullability
        // analysis across the constructor/init-accessor boundary — the constructor below always
        // overwrites it through the validating CardId accessor before the value escapes.
        private readonly string _cardId = string.Empty;

        internal string CardId
        {
            get => _cardId;
            init => _cardId = RequireNonEmpty(value);
        }

        internal RaisedAsCase(string cardId)
        {
            CardId = cardId;
        }

        internal override TResult Match<TResult>(Func<TResult> onNone, Func<string, TResult> onRaisedAs) => onRaisedAs(CardId);

        private static string RequireNonEmpty(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "a raised-as blind-spot declaration must name the card id it was raised as — an empty " +
                    "id is indistinguishable from no reference at all.",
                    nameof(value));
            }

            return value;
        }
    }
}
