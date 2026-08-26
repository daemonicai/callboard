namespace Callboard.Cards;

/// <summary>
/// Closed union over how <see cref="CardStore.RecordSectionAuthorisation"/> (§8a block C,
/// work-lifecycle: "Remediation beyond the second round requires recorded authorisation") can end.
/// Same shape as <see cref="CardApprovalOutcome"/> — a caller-correctable refusal
/// (<see cref="RoleNotPermitted"/>, <see cref="NotASectionCard"/>, <see cref="CardNotFound"/>,
/// <see cref="LayoutMismatch"/>, <see cref="NotAtBound"/>) is kept structurally apart from a reported problem with the
/// record's own content (<see cref="CardCorrupt"/>) and from enforcement itself being unavailable
/// (<see cref="ToolFailure"/>).
/// </summary>
internal abstract record CardSectionAuthorisationOutcome
{
    private CardSectionAuthorisationOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Recorded, TResult> onRecorded,
        Func<RoleNotPermitted, TResult> onRoleNotPermitted,
        Func<NotASectionCard, TResult> onNotASectionCard,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<NotAtBound, TResult> onNotAtBound,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure);

    /// <param name="Card">The section card as written, carrying the newly appended authorisation
    /// entry.</param>
    /// <param name="Entry">The authorisation entry actually recorded.</param>
    internal sealed record Recorded(CardFile Card, SectionAuthorisationEntry Entry) : CardSectionAuthorisationOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<NotAtBound, TResult> onNotAtBound, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onRecorded(this);
    }

    /// <summary>work-lifecycle: "The authorisation SHALL be part of the record, not a permission
    /// granted out of band" — only <see cref="CardOwner.ProductOwner"/> may record one (§8a block C
    /// brief: "the one permission in the system that exists to be granted from outside the
    /// agents"). Checked immediately after a successful <see cref="CardStore.ReadCard"/>, not ahead
    /// of <see cref="File.Exists(string)"/> — the same ordering <see
    /// cref="CardApprovalOutcome.RoleNotPermitted"/> now establishes (§9 block B reviewer/architect
    /// ruling). Refusal-shaped and card-addressed: <c>section authorise</c> is the Product-Owner-only
    /// verb by which a section exceeds its remediation bound, so an agent attempting it is the same
    /// pattern as an architect approving its own work — the one attempt this project's premise
    /// requires to leave a mark (§9 remediation S3).</summary>
    internal sealed record RoleNotPermitted(CardOwner AttemptedRole) : CardSectionAuthorisationOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<NotAtBound, TResult> onNotAtBound, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onRoleNotPermitted(this);

        public string RefusingRule => "work-lifecycle: an authorisation is part of the record, not a permission granted out of band";

        public string Remedy => $"only {CardOwner.ProductOwner.ToWireString()} may record a section authorisation; {AttemptedRole.ToWireString()} cannot.";
    }

    /// <summary>The target card exists and parses, but its <c>kind</c> is not <c>section</c> —
    /// authorisations are only recorded on a section card. Refusal-shaped.</summary>
    internal sealed record NotASectionCard(CardKind Kind) : CardSectionAuthorisationOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<NotAtBound, TResult> onNotAtBound, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onNotASectionCard(this);

        public string RefusingRule => "work-lifecycle: authorisations are recorded only on a section card";

        public string Remedy => "target a card whose kind is 'section'.";
    }

    /// <summary>No card exists at the target path. Refusal-shaped.</summary>
    internal sealed record CardNotFound(string FilePath) : CardSectionAuthorisationOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<NotAtBound, TResult> onNotAtBound, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardNotFound(this);
    }

    /// <summary>The target path does not resolve under the given root/scope/change name
    /// (<see cref="AnchoredCardPath.TryCreate"/>). Refusal-shaped.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardSectionAuthorisationOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<NotAtBound, TResult> onNotAtBound, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onLayoutMismatch(this);
    }

    /// <summary>work-lifecycle: "Recording an authorisation SHALL be refused unless the section is
    /// already at the bound with none unspent" (§8a block C remediation, Architect ruling: banking
    /// authorisations ahead of need satisfies the one-for-one count literally while defeating what
    /// it is for — a reason written before the round it discharges cannot be a reason for that
    /// round). <see cref="PriorRequestChanges"/> and <see cref="UnspentAuthorisations"/> are the two
    /// counts the refusal is decided from, reported so the message states the fact, not just the
    /// rule. Refusal-shaped.</summary>
    internal sealed record NotAtBound(int PriorRequestChanges, int UnspentAuthorisations) : CardSectionAuthorisationOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<NotAtBound, TResult> onNotAtBound, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onNotAtBound(this);

        public string RefusingRule => "work-lifecycle: an authorisation is recorded against a refused request-changes verdict, not in advance of one";

        public string Remedy =>
            $"the section carries {PriorRequestChanges} 'request-changes' verdict{(PriorRequestChanges == 1 ? "" : "s")} and " +
            $"{UnspentAuthorisations} unspent authorisation{(UnspentAuthorisations == 1 ? "" : "s")}, so it is not currently at the bound; " +
            "record this once 'section verdict' has actually refused a verdict for want of one.";
    }

    /// <summary>The card exists but could not be parsed. Neither refusal nor tool-failure — a
    /// reported problem with the record's own content.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardSectionAuthorisationOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<NotAtBound, TResult> onNotAtBound, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardCorrupt(this);
    }

    /// <summary>Enforcement itself is unavailable: a card's lock could not be acquired within its
    /// timeout, or an I/O error occurred while writing. Tool-failure-shaped.</summary>
    internal sealed record ToolFailure(string Reason) : CardSectionAuthorisationOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<NotAtBound, TResult> onNotAtBound, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onToolFailure(this);
    }
}
