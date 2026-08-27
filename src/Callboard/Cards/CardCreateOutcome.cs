namespace Callboard.Cards;

/// <summary>
/// Closed union over how <see cref="CardStore.CreateCard"/> can end — the general-purpose creation
/// path §7 block A introduces for the four register kinds and <c>section</c> (five kinds, one card
/// each, no dual-lock complexity <see cref="CardStore.RecordFinding"/> needs for its two-card
/// write) — and, from §13, the sixth: <c>block</c> (work-lifecycle: "Every block card is minted by
/// the tool"). Same split-by-disposition reasoning as <see cref="CardWriteResult"/>: refusal,
/// tool-failure and reported-failure carry opposite instructions to the caller, so they are
/// distinct cases rather than one flat failure a caller could accidentally collapse.
///
/// <para>
/// <b>Three of the four refusal cases do not implement <see cref="ICardRefusalReason"/> (§9 block
/// A3).</b> <see cref="CardStore.CreateCard"/> never reads or resolves the card being created: <see
/// cref="ScopeRefused"/> fires before any identity is even allocated; <see cref="AlreadyExists"/>
/// comes back from <see cref="CardStore.WriteCard"/>'s own create-only <see cref="System.IO.File.
/// Exists(string)"/> check, never a parse of whatever already occupies the path; <see cref="
/// LayoutMismatch"/> is an anchoring failure on the not-yet-existing target — the categorical "a
/// layout mismatch has nothing to record against" case the Architect's §9 ruling names explicitly.
/// </para>
///
/// <para>
/// <b><see cref="IdentityAlreadyBorne"/> is the exception (§13, card-model: "the system SHALL
/// refuse to issue an identity that a card in the record already bears").</b> Unlike the other three
/// refusals, this one <em>does</em> resolve a real card — just not the one being created: <see
/// cref="CardIdentityAllocator.Allocate"/> confirmed, against the record, that <see
/// cref="CardIdentityAllocationResult.Borne.CardFilePaths"/> already carries the identity the
/// counter just issued. It is therefore <see cref="ICardRefusalReason"/>-shaped and recorded — not
/// against the card being refused (there isn't one yet), but against the card already bearing the
/// contested identity, the same "only a card-addressed refusal records" rule read the other way:
/// this refusal resolved a card, so it has something to record against.
/// </para>
/// </summary>
internal abstract record CardCreateOutcome
{
    private CardCreateOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Created, TResult> onCreated,
        Func<ScopeRefused, TResult> onScopeRefused,
        Func<AlreadyExists, TResult> onAlreadyExists,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<IdentityAlreadyBorne, TResult> onIdentityAlreadyBorne,
        Func<ToolFailure, TResult> onToolFailure);

    /// <param name="Card">The card exactly as written.</param>
    internal sealed record Created(CardFile Card) : CardCreateOutcome
    {
        internal override TResult Match<TResult>(Func<Created, TResult> onCreated, Func<ScopeRefused, TResult> onScopeRefused, Func<AlreadyExists, TResult> onAlreadyExists, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<IdentityAlreadyBorne, TResult> onIdentityAlreadyBorne, Func<ToolFailure, TResult> onToolFailure) =>
            onCreated(this);
    }

    /// <summary>
    /// <see cref="CardScopeRules.Validate"/> refused the requested <see cref="CardKind"/>/
    /// <see cref="CardScope"/> pair. Refusal-shaped — caller-correctable, and checked before any
    /// identity is allocated or any directory is created, so a scope refusal never burns an
    /// identity number.
    /// </summary>
    internal sealed record ScopeRefused(string Reason) : CardCreateOutcome
    {
        internal override TResult Match<TResult>(Func<Created, TResult> onCreated, Func<ScopeRefused, TResult> onScopeRefused, Func<AlreadyExists, TResult> onAlreadyExists, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<IdentityAlreadyBorne, TResult> onIdentityAlreadyBorne, Func<ToolFailure, TResult> onToolFailure) =>
            onScopeRefused(this);
    }

    /// <summary>A card already exists at the target path. Refusal-shaped.</summary>
    internal sealed record AlreadyExists(string FilePath) : CardCreateOutcome
    {
        internal override TResult Match<TResult>(Func<Created, TResult> onCreated, Func<ScopeRefused, TResult> onScopeRefused, Func<AlreadyExists, TResult> onAlreadyExists, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<IdentityAlreadyBorne, TResult> onIdentityAlreadyBorne, Func<ToolFailure, TResult> onToolFailure) =>
            onAlreadyExists(this);
    }

    /// <summary>The target path does not resolve under the given root/scope/change name
    /// (<see cref="AnchoredCardPath.TryCreate"/>). Refusal-shaped.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardCreateOutcome
    {
        internal override TResult Match<TResult>(Func<Created, TResult> onCreated, Func<ScopeRefused, TResult> onScopeRefused, Func<AlreadyExists, TResult> onAlreadyExists, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<IdentityAlreadyBorne, TResult> onIdentityAlreadyBorne, Func<ToolFailure, TResult> onToolFailure) =>
            onLayoutMismatch(this);
    }

    /// <summary>
    /// <see cref="CardIdentityAllocator.Allocate"/> issued <paramref name="Id"/> from <paramref
    /// name="Kind"/>'s counter, then confirmed against the record that <paramref
    /// name="CardFilePaths"/> already carries it (<see cref="CardIdentityAllocationResult.
    /// Borne"/>). Refusal-shaped, and <see cref="ICardRefusalReason"/>-shaped — see this type's own
    /// doc comment for why this one case of the four differs from its three siblings. <see
    /// cref="CardStore.CreateCard"/> records this against the first (ordinally sorted) card in
    /// <paramref name="CardFilePaths"/>, not against the card being created — there isn't one.
    /// </summary>
    internal sealed record IdentityAlreadyBorne(CardKind Kind, string Id, IReadOnlyList<string> CardFilePaths) : CardCreateOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Created, TResult> onCreated, Func<ScopeRefused, TResult> onScopeRefused, Func<AlreadyExists, TResult> onAlreadyExists, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<IdentityAlreadyBorne, TResult> onIdentityAlreadyBorne, Func<ToolFailure, TResult> onToolFailure) =>
            onIdentityAlreadyBorne(this);

        public string RefusingRule =>
            "card-model: \"the system SHALL refuse to issue an identity that a card in the record already bears\" " +
            $"— the '{Kind.ToWireString()}' identity counter issued '{Id}', which the record already carries.";

        public string Remedy =>
            $"run 'index rebuild' to reconcile the '{Kind.ToWireString()}' identity counter against the record, then retry the creation.";
    }

    /// <summary>Enforcement itself is unavailable: identity allocation failed, the card's lock
    /// could not be acquired within its timeout, or an I/O error occurred while writing.
    /// Tool-failure-shaped.</summary>
    internal sealed record ToolFailure(string Reason) : CardCreateOutcome
    {
        internal override TResult Match<TResult>(Func<Created, TResult> onCreated, Func<ScopeRefused, TResult> onScopeRefused, Func<AlreadyExists, TResult> onAlreadyExists, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<IdentityAlreadyBorne, TResult> onIdentityAlreadyBorne, Func<ToolFailure, TResult> onToolFailure) =>
            onToolFailure(this);
    }
}
