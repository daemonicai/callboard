namespace Callboard.Cards;

/// <summary>
/// Closed union over how <see cref="CardStore.DispositionNit"/> (§8 block B, <c>nit disposition</c>)
/// can end. Same refusal/tool-failure/reported-failure discipline every other outcome type in this
/// codebase applies — see <see cref="CardApprovalOutcome"/>'s own doc comment for the general
/// shape. <see cref="RoleNotPermitted"/>, <see cref="NitNotFound"/>, <see cref="AlreadyDispositioned"/>,
/// <see cref="NotABlockCard"/>, <see cref="CardNotFound"/>, <see cref="LayoutMismatch"/>,
/// <see cref="RaisedCardLayoutMismatch"/> and <see cref="RaisedCardAlreadyExists"/> are
/// refusal-shaped (caller-correctable); <see cref="CardCorrupt"/> and <see cref="ToolFailure"/> are
/// not — a caller wired over this type (<see cref="Callboard.Cli.CommandDispatcher.RunNitDisposition"/>)
/// must route those two to a tool-failure exit, never a refusal.
/// </summary>
internal abstract record CardNitDispositionOutcome
{
    private CardNitDispositionOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Dispositioned, TResult> onDispositioned,
        Func<RoleNotPermitted, TResult> onRoleNotPermitted,
        Func<NotABlockCard, TResult> onNotABlockCard,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<NitNotFound, TResult> onNitNotFound,
        Func<AlreadyDispositioned, TResult> onAlreadyDispositioned,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch,
        Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure);

    /// <param name="Card">The block card as written: the appended disposition comment, and — for
    /// <c>fix-before-land</c>, only when this call is the one that leaves it live in <c>in-review</c>
    /// with every other nit already dispositioned — the new status, <c>round</c> and appended
    /// <see cref="CardBlockTransitionEntry"/>.</param>
    /// <param name="DispositionComment">The disposition comment recorded by this call.</param>
    /// <param name="RaisedCard">The card raised alongside the disposition, for <c>defer</c>/
    /// <c>decline</c>; <see langword="null"/> for <c>fix-before-land</c>.</param>
    /// <param name="Transitioned"><see langword="true"/> when this call also applied the
    /// <c>fix-before-land</c> edge (work-lifecycle: "<c>in-review → briefed</c> …
    /// <c>round += 1</c>"). See <see cref="CardStore.DispositionNit"/>'s own doc comment for when
    /// that is and is not the case.</param>
    internal sealed record Dispositioned(CardFile Card, CardComment DispositionComment, CardFile? RaisedCard, bool Transitioned) : CardNitDispositionOutcome
    {
        internal override TResult Match<TResult>(Func<Dispositioned, TResult> onDispositioned, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NitNotFound, TResult> onNitNotFound, Func<AlreadyDispositioned, TResult> onAlreadyDispositioned, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onDispositioned(this);
    }

    /// <summary>review-certification: "Every nit SHALL receive a disposition chosen by the
    /// architect" — the Architect's reading of "chosen by the architect" as role-bounding the verb
    /// (§8 block B brief item 6, the reading most open to challenge).</summary>
    internal sealed record RoleNotPermitted(CardOwner AttemptedRole) : CardNitDispositionOutcome
    {
        internal override TResult Match<TResult>(Func<Dispositioned, TResult> onDispositioned, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NitNotFound, TResult> onNitNotFound, Func<AlreadyDispositioned, TResult> onAlreadyDispositioned, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onRoleNotPermitted(this);
    }

    /// <summary>The target card exists and parses, but its <c>kind</c> is not <c>block</c>.
    /// Refusal-shaped: caller pointed the verb at the wrong card.</summary>
    internal sealed record NotABlockCard(CardKind Kind) : CardNitDispositionOutcome
    {
        internal override TResult Match<TResult>(Func<Dispositioned, TResult> onDispositioned, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NitNotFound, TResult> onNitNotFound, Func<AlreadyDispositioned, TResult> onAlreadyDispositioned, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onNotABlockCard(this);
    }

    /// <summary>No card exists at the target path. Refusal-shaped: caller-correctable.</summary>
    internal sealed record CardNotFound(string FilePath) : CardNitDispositionOutcome
    {
        internal override TResult Match<TResult>(Func<Dispositioned, TResult> onDispositioned, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NitNotFound, TResult> onNitNotFound, Func<AlreadyDispositioned, TResult> onAlreadyDispositioned, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardNotFound(this);
    }

    /// <summary>The block card was found, but no comment on it carries <c>NitId</c> as a live nit —
    /// checked again under the lock (<see cref="NitResolver"/> found it before the lock was
    /// acquired). Refusal-shaped: caller-correctable.</summary>
    internal sealed record NitNotFound(string NitId) : CardNitDispositionOutcome
    {
        internal override TResult Match<TResult>(Func<Dispositioned, TResult> onDispositioned, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NitNotFound, TResult> onNitNotFound, Func<AlreadyDispositioned, TResult> onAlreadyDispositioned, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onNitNotFound(this);
    }

    /// <summary>The nit already carries a disposition (review-certification: "A nit SHALL cease to
    /// be live only through one of these three dispositions" — implying exactly one). Refusal-
    /// shaped: caller-correctable.</summary>
    internal sealed record AlreadyDispositioned(string NitId) : CardNitDispositionOutcome
    {
        internal override TResult Match<TResult>(Func<Dispositioned, TResult> onDispositioned, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NitNotFound, TResult> onNitNotFound, Func<AlreadyDispositioned, TResult> onAlreadyDispositioned, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onAlreadyDispositioned(this);
    }

    /// <summary>The block card's own target path does not resolve under the given root/scope/change
    /// name. Refusal-shaped: caller-correctable.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardNitDispositionOutcome
    {
        internal override TResult Match<TResult>(Func<Dispositioned, TResult> onDispositioned, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NitNotFound, TResult> onNitNotFound, Func<AlreadyDispositioned, TResult> onAlreadyDispositioned, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onLayoutMismatch(this);
    }

    /// <summary>The raised (obligation/decision) card's own target path does not resolve under the
    /// given root/scope/change name. Refusal-shaped: caller-correctable.</summary>
    internal sealed record RaisedCardLayoutMismatch(string Reason) : CardNitDispositionOutcome
    {
        internal override TResult Match<TResult>(Func<Dispositioned, TResult> onDispositioned, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NitNotFound, TResult> onNitNotFound, Func<AlreadyDispositioned, TResult> onAlreadyDispositioned, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onRaisedCardLayoutMismatch(this);
    }

    /// <summary>A card already exists at the raised card's target path. Refusal-shaped: caller-
    /// correctable.</summary>
    internal sealed record RaisedCardAlreadyExists(string FilePath) : CardNitDispositionOutcome
    {
        internal override TResult Match<TResult>(Func<Dispositioned, TResult> onDispositioned, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NitNotFound, TResult> onNitNotFound, Func<AlreadyDispositioned, TResult> onAlreadyDispositioned, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onRaisedCardAlreadyExists(this);
    }

    /// <summary>The card exists but its content could not be parsed, or carries a <c>status</c> this
    /// build does not recognise. Neither refusal nor tool-failure.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardNitDispositionOutcome
    {
        internal override TResult Match<TResult>(Func<Dispositioned, TResult> onDispositioned, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NitNotFound, TResult> onNitNotFound, Func<AlreadyDispositioned, TResult> onAlreadyDispositioned, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardCorrupt(this);
    }

    /// <summary>Enforcement itself is unavailable: a lock could not be acquired within its timeout,
    /// or an I/O error occurred while writing. Tool-failure-shaped — the board is not refusing
    /// anything.</summary>
    internal sealed record ToolFailure(string Reason) : CardNitDispositionOutcome
    {
        internal override TResult Match<TResult>(Func<Dispositioned, TResult> onDispositioned, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NitNotFound, TResult> onNitNotFound, Func<AlreadyDispositioned, TResult> onAlreadyDispositioned, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onToolFailure(this);
    }
}
