namespace Callboard.Cards;

/// <summary>
/// The four recognised card scopes (card-model: "Scope determines lifetime"), modelled as a
/// closed union for the same reason as <see cref="CardKind"/> — see that type's doc comment.
/// Scope is an attribute of the card, not implied by its kind, so a card can be promoted to a
/// wider scope without losing identity or thread — modelled here as its own type rather than
/// derived from <see cref="CardKind"/>. The per-kind scope table (a rule may not be
/// section-scoped, etc.) is 4.4's refusal, not this type's job: this only models the field.
/// </summary>
internal abstract record CardScope
{
    private CardScope()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<TResult> onSection,
        Func<TResult> onChange,
        Func<TResult> onCapability,
        Func<TResult> onRepository);

    internal static readonly CardScope Section = new SectionCase();
    internal static readonly CardScope Change = new ChangeCase();
    internal static readonly CardScope Capability = new CapabilityCase();
    internal static readonly CardScope Repository = new RepositoryCase();

    private sealed record SectionCase : CardScope
    {
        internal override TResult Match<TResult>(Func<TResult> onSection, Func<TResult> onChange, Func<TResult> onCapability, Func<TResult> onRepository) => onSection();
    }

    private sealed record ChangeCase : CardScope
    {
        internal override TResult Match<TResult>(Func<TResult> onSection, Func<TResult> onChange, Func<TResult> onCapability, Func<TResult> onRepository) => onChange();
    }

    private sealed record CapabilityCase : CardScope
    {
        internal override TResult Match<TResult>(Func<TResult> onSection, Func<TResult> onChange, Func<TResult> onCapability, Func<TResult> onRepository) => onCapability();
    }

    private sealed record RepositoryCase : CardScope
    {
        internal override TResult Match<TResult>(Func<TResult> onSection, Func<TResult> onChange, Func<TResult> onCapability, Func<TResult> onRepository) => onRepository();
    }
}

/// <summary>
/// Wire form of <see cref="CardScope"/> and the parse path back, matched with explicit
/// <see cref="StringComparer.Ordinal"/> — see <see cref="CardKindWireFormat"/> for why.
/// </summary>
internal static class CardScopeWireFormat
{
    private static readonly IReadOnlyDictionary<string, CardScope> ByWireValue =
        new Dictionary<string, CardScope>(StringComparer.Ordinal)
        {
            ["section"] = CardScope.Section,
            ["change"] = CardScope.Change,
            ["capability"] = CardScope.Capability,
            ["repository"] = CardScope.Repository,
        };

    internal static string ToWireString(this CardScope scope) => scope.Match(
        onSection: static () => "section",
        onChange: static () => "change",
        onCapability: static () => "capability",
        onRepository: static () => "repository");

    internal static string RecognisedValues => string.Join(", ", ByWireValue.Keys);

    internal static bool TryParse(string value, out CardScope scope)
    {
        var found = ByWireValue.TryGetValue(value, out var match);
        // See CardKindWireFormat.TryParse: every stored value is non-null, so `match` is
        // non-null whenever `found` is true, and the fallback on failure is always discarded.
        scope = found ? match! : CardScope.Section;
        return found;
    }
}
