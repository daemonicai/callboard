namespace Callboard.Cards;

/// <summary>
/// Closed union over how <see cref="CardStore.CreateCard"/> can end — the general-purpose creation
/// path §7 block A introduces for the four register kinds and <c>section</c> (five kinds, one card
/// each, no dual-lock complexity <see cref="CardStore.RecordFinding"/> needs for its two-card
/// write). Same split-by-disposition reasoning as <see cref="CardWriteResult"/>: refusal,
/// tool-failure and reported-failure carry opposite instructions to the caller, so they are
/// distinct cases rather than one flat failure a caller could accidentally collapse.
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
        Func<ToolFailure, TResult> onToolFailure);

    /// <param name="Card">The card exactly as written.</param>
    internal sealed record Created(CardFile Card) : CardCreateOutcome
    {
        internal override TResult Match<TResult>(Func<Created, TResult> onCreated, Func<ScopeRefused, TResult> onScopeRefused, Func<AlreadyExists, TResult> onAlreadyExists, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<ToolFailure, TResult> onToolFailure) =>
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
        internal override TResult Match<TResult>(Func<Created, TResult> onCreated, Func<ScopeRefused, TResult> onScopeRefused, Func<AlreadyExists, TResult> onAlreadyExists, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<ToolFailure, TResult> onToolFailure) =>
            onScopeRefused(this);
    }

    /// <summary>A card already exists at the target path. Refusal-shaped.</summary>
    internal sealed record AlreadyExists(string FilePath) : CardCreateOutcome
    {
        internal override TResult Match<TResult>(Func<Created, TResult> onCreated, Func<ScopeRefused, TResult> onScopeRefused, Func<AlreadyExists, TResult> onAlreadyExists, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<ToolFailure, TResult> onToolFailure) =>
            onAlreadyExists(this);
    }

    /// <summary>The target path does not resolve under the given root/scope/change name
    /// (<see cref="AnchoredCardPath.TryCreate"/>). Refusal-shaped.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardCreateOutcome
    {
        internal override TResult Match<TResult>(Func<Created, TResult> onCreated, Func<ScopeRefused, TResult> onScopeRefused, Func<AlreadyExists, TResult> onAlreadyExists, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<ToolFailure, TResult> onToolFailure) =>
            onLayoutMismatch(this);
    }

    /// <summary>Enforcement itself is unavailable: identity allocation failed, the card's lock
    /// could not be acquired within its timeout, or an I/O error occurred while writing.
    /// Tool-failure-shaped.</summary>
    internal sealed record ToolFailure(string Reason) : CardCreateOutcome
    {
        internal override TResult Match<TResult>(Func<Created, TResult> onCreated, Func<ScopeRefused, TResult> onScopeRefused, Func<AlreadyExists, TResult> onAlreadyExists, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<ToolFailure, TResult> onToolFailure) =>
            onToolFailure(this);
    }
}
