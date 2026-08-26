namespace Callboard.Cards;

/// <summary>
/// Closed union over how archiving a change (§7 block D, <see cref="CardStore.ArchiveChange"/>) can
/// end. Same shape and reasoning as <see cref="CardSectionCloseOutcome"/>/<see cref="CardRegisterDischargeOutcome"/>
/// — a private constructor and sealed nested cases close the hierarchy to this file, and
/// <see cref="Match{TResult}"/> is the only way to consume a value.
/// </summary>
internal abstract record ChangeArchiveOutcome
{
    private ChangeArchiveOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Archived, TResult> onArchived,
        Func<ChangeNotFound, TResult> onChangeNotFound,
        Func<AlreadyArchived, TResult> onAlreadyArchived,
        Func<InvalidChangeName, TResult> onInvalidChangeName,
        Func<CardsUnreadable, TResult> onCardsUnreadable,
        Func<OrphanedObligations, TResult> onOrphanedObligations,
        Func<ToolFailure, TResult> onToolFailure);

    /// <param name="ChangeName">The archived change's name.</param>
    /// <param name="ArchivedDirectory">Where the change's cards now live — the same directory
    /// tree, one level under <see cref="CardLayout.ArchiveDirectory"/>, moved wholesale and
    /// nothing else (register: "the register lives above the change"). Every card in the change
    /// directory, obligations included, moves exactly as written — this verb makes no write of its
    /// own (§9 block F: the silent discharge this type's own doc comment used to describe is gone;
    /// see <see cref="OrphanedObligations"/> and <see cref="CardStore.ArchiveChange"/>'s own doc
    /// comment for what replaced it).</param>
    internal sealed record Archived(string ChangeName, string ArchivedDirectory) : ChangeArchiveOutcome
    {
        internal override TResult Match<TResult>(Func<Archived, TResult> onArchived, Func<ChangeNotFound, TResult> onChangeNotFound, Func<AlreadyArchived, TResult> onAlreadyArchived, Func<InvalidChangeName, TResult> onInvalidChangeName, Func<CardsUnreadable, TResult> onCardsUnreadable, Func<OrphanedObligations, TResult> onOrphanedObligations, Func<ToolFailure, TResult> onToolFailure) =>
            onArchived(this);
    }

    /// <summary>No live change directory exists under this name. Refusal-shaped.</summary>
    internal sealed record ChangeNotFound(string ChangeName) : ChangeArchiveOutcome
    {
        internal override TResult Match<TResult>(Func<Archived, TResult> onArchived, Func<ChangeNotFound, TResult> onChangeNotFound, Func<AlreadyArchived, TResult> onAlreadyArchived, Func<InvalidChangeName, TResult> onInvalidChangeName, Func<CardsUnreadable, TResult> onCardsUnreadable, Func<OrphanedObligations, TResult> onOrphanedObligations, Func<ToolFailure, TResult> onToolFailure) =>
            onChangeNotFound(this);
    }

    /// <summary>A directory already exists under <see cref="CardLayout.ArchiveDirectory"/> for
    /// this change name. Refusal-shaped — archiving does not silently merge into, or overwrite, an
    /// existing archived change.</summary>
    internal sealed record AlreadyArchived(string ChangeName) : ChangeArchiveOutcome
    {
        internal override TResult Match<TResult>(Func<Archived, TResult> onArchived, Func<ChangeNotFound, TResult> onChangeNotFound, Func<AlreadyArchived, TResult> onAlreadyArchived, Func<InvalidChangeName, TResult> onInvalidChangeName, Func<CardsUnreadable, TResult> onCardsUnreadable, Func<OrphanedObligations, TResult> onOrphanedObligations, Func<ToolFailure, TResult> onToolFailure) =>
            onAlreadyArchived(this);
    }

    /// <summary><paramref name="Reason"/> is <see cref="CardLayout.ChangesDirectory"/>'s own
    /// message — the reserved name <c>archive</c>, or an unsafe path segment
    /// (<see cref="CardLayout.RequireSafePathSegment"/>). Refusal-shaped.</summary>
    internal sealed record InvalidChangeName(string Reason) : ChangeArchiveOutcome
    {
        internal override TResult Match<TResult>(Func<Archived, TResult> onArchived, Func<ChangeNotFound, TResult> onChangeNotFound, Func<AlreadyArchived, TResult> onAlreadyArchived, Func<InvalidChangeName, TResult> onInvalidChangeName, Func<CardsUnreadable, TResult> onCardsUnreadable, Func<OrphanedObligations, TResult> onOrphanedObligations, Func<ToolFailure, TResult> onToolFailure) =>
            onInvalidChangeName(this);
    }

    /// <summary>
    /// At least one <c>*.md</c> file directly inside the live change directory could not be
    /// parsed. Fail-closed rather than optimistic (the same reasoning as <see cref="
    /// CardIdentityResolution.Unreadable"/>): an unreadable file might be an open obligation this
    /// verb needs to classify before the directory moves, so archiving proceeds only once every
    /// card in the directory has actually been read. Refusal-shaped, not tool-failure — the record
    /// itself, not the tool, is what is in an unreadable state.
    /// </summary>
    internal sealed record CardsUnreadable(IReadOnlyList<string> FilePaths) : ChangeArchiveOutcome
    {
        internal override TResult Match<TResult>(Func<Archived, TResult> onArchived, Func<ChangeNotFound, TResult> onChangeNotFound, Func<AlreadyArchived, TResult> onAlreadyArchived, Func<InvalidChangeName, TResult> onInvalidChangeName, Func<CardsUnreadable, TResult> onCardsUnreadable, Func<OrphanedObligations, TResult> onOrphanedObligations, Func<ToolFailure, TResult> onToolFailure) =>
            onCardsUnreadable(this);
    }

    /// <summary>
    /// At least one open change-scoped <c>obligation</c> is owed by a section with no remaining
    /// open card in this directory — either a <c>section</c> card that exists and is already
    /// closed (<see cref="SectionCardFields.ClosedBy"/>/<see cref="SectionCardFields.ClosedAt"/>
    /// both set), or no section card of that id at all (process-enforcement, "Archive settles
    /// orphaned obligations": "the system SHALL refuse to archive a change while any change-scoped
    /// obligation owed by no remaining section is open. Each SHALL be discharged, promoted to a
    /// wider scope, or declined with a recorded reason."). Refusal-shaped — <b>an obligation owed
    /// by a section that is still open is deliberately absent from this list</b>: 9.4 already
    /// refuses that section's own close while the obligation remains open, and register's own "no
    /// carry-forward step" principle (the same reasoning that lets an open question outlive its
    /// change) means it simply moves into the archive untouched, exactly as an open question does.
    /// </summary>
    ///
    /// <remarks>
    /// <b>Not an <see cref="ICardRefusalReason"/>, unlike most refusal-shaped cases in §9 — a
    /// deliberate carve-out, decided and recorded as one, not an oversight.</b> "No single card to
    /// record against" is not the reason: recording against every obligation named here is
    /// mechanically workable, one <see cref="CardStore.RefuseAndRecord{TOutcome, TRefusal}"/> call
    /// per card, the same as anywhere else in this codebase. The real reason is proportion. Block
    /// A's contract is that a refusal is not reported as recorded until its line is durable on the
    /// card — over N obligations that means either all N durably written or the whole call reports
    /// <see cref="ToolFailure"/> instead, which is real all-or-nothing multi-card write machinery
    /// built for a failure path, in the last block of the section. §9 builds that pattern-visibility
    /// discipline (see <see cref="CardApprovalOutcome.RoleNotPermitted"/>'s own reasoning) because a
    /// refusal an agent hits repeatedly, under deadline, needs to be visible on the card the next
    /// agent reads. <c>change archive</c> is the opposite case: a once-per-change act performed by
    /// the Architect, not a card any agent pokes at repeatedly — the pattern-visibility argument is
    /// at its weakest exactly here, which is why this is the one place §9 knowingly does not apply
    /// its own recording rule. Every other <see cref="ChangeArchiveOutcome"/> refusal case
    /// (<see cref="CardsUnreadable"/> included) is unrecorded for the same reason, not because no
    /// card exists to record against.
    /// </remarks>
    internal sealed record OrphanedObligations(string ChangeName, IReadOnlyList<(string Id, string Title)> Obligations) : ChangeArchiveOutcome
    {
        internal override TResult Match<TResult>(Func<Archived, TResult> onArchived, Func<ChangeNotFound, TResult> onChangeNotFound, Func<AlreadyArchived, TResult> onAlreadyArchived, Func<InvalidChangeName, TResult> onInvalidChangeName, Func<CardsUnreadable, TResult> onCardsUnreadable, Func<OrphanedObligations, TResult> onOrphanedObligations, Func<ToolFailure, TResult> onToolFailure) =>
            onOrphanedObligations(this);

        public string RefusingRule => "process-enforcement: Archive settles orphaned obligations";

        public string Remedy =>
            $"'{ChangeName}' still carries {Obligations.Count} open obligation(s) owed by no remaining section: " +
            string.Join(", ", Obligations.Select(static o => $"{o.Id} (\"{o.Title}\")")) +
            " — each must be discharged ('obligation discharge'), promoted to a wider scope " +
            "('obligation promote'), or declined with a recorded reason ('obligation decline') before this change can archive.";
    }

    /// <summary>Enforcement itself is unavailable: a lock could not be acquired while scanning the
    /// directory, or the directory move itself failed part way. Tool-failure-shaped.</summary>
    internal sealed record ToolFailure(string Reason) : ChangeArchiveOutcome
    {
        internal override TResult Match<TResult>(Func<Archived, TResult> onArchived, Func<ChangeNotFound, TResult> onChangeNotFound, Func<AlreadyArchived, TResult> onAlreadyArchived, Func<InvalidChangeName, TResult> onInvalidChangeName, Func<CardsUnreadable, TResult> onCardsUnreadable, Func<OrphanedObligations, TResult> onOrphanedObligations, Func<ToolFailure, TResult> onToolFailure) =>
            onToolFailure(this);
    }
}
