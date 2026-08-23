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
        Func<ToolFailure, TResult> onToolFailure);

    /// <param name="ChangeName">The archived change's name.</param>
    /// <param name="ArchivedDirectory">Where the change's cards now live — the same directory
    /// tree, one level under <see cref="CardLayout.ArchiveDirectory"/>, moved wholesale and
    /// nothing else (register: "the register lives above the change").</param>
    /// <param name="SettledObligationIds">The ids of every change-scoped <c>obligation</c> card
    /// that was <c>open</c> and is now <c>discharged</c> — register's "its change-scoped
    /// obligations are settled". Every other card in the change directory (blocks, sections, a
    /// change-scoped rule) is moved untouched; settling is this verb's one deliberate write.</param>
    internal sealed record Archived(string ChangeName, string ArchivedDirectory, IReadOnlyList<string> SettledObligationIds) : ChangeArchiveOutcome
    {
        internal override TResult Match<TResult>(Func<Archived, TResult> onArchived, Func<ChangeNotFound, TResult> onChangeNotFound, Func<AlreadyArchived, TResult> onAlreadyArchived, Func<InvalidChangeName, TResult> onInvalidChangeName, Func<CardsUnreadable, TResult> onCardsUnreadable, Func<ToolFailure, TResult> onToolFailure) =>
            onArchived(this);
    }

    /// <summary>No live change directory exists under this name. Refusal-shaped.</summary>
    internal sealed record ChangeNotFound(string ChangeName) : ChangeArchiveOutcome
    {
        internal override TResult Match<TResult>(Func<Archived, TResult> onArchived, Func<ChangeNotFound, TResult> onChangeNotFound, Func<AlreadyArchived, TResult> onAlreadyArchived, Func<InvalidChangeName, TResult> onInvalidChangeName, Func<CardsUnreadable, TResult> onCardsUnreadable, Func<ToolFailure, TResult> onToolFailure) =>
            onChangeNotFound(this);
    }

    /// <summary>A directory already exists under <see cref="CardLayout.ArchiveDirectory"/> for
    /// this change name. Refusal-shaped — archiving does not silently merge into, or overwrite, an
    /// existing archived change.</summary>
    internal sealed record AlreadyArchived(string ChangeName) : ChangeArchiveOutcome
    {
        internal override TResult Match<TResult>(Func<Archived, TResult> onArchived, Func<ChangeNotFound, TResult> onChangeNotFound, Func<AlreadyArchived, TResult> onAlreadyArchived, Func<InvalidChangeName, TResult> onInvalidChangeName, Func<CardsUnreadable, TResult> onCardsUnreadable, Func<ToolFailure, TResult> onToolFailure) =>
            onAlreadyArchived(this);
    }

    /// <summary><paramref name="Reason"/> is <see cref="CardLayout.ChangesDirectory"/>'s own
    /// message — the reserved name <c>archive</c>, or an unsafe path segment
    /// (<see cref="CardLayout.RequireSafePathSegment"/>). Refusal-shaped.</summary>
    internal sealed record InvalidChangeName(string Reason) : ChangeArchiveOutcome
    {
        internal override TResult Match<TResult>(Func<Archived, TResult> onArchived, Func<ChangeNotFound, TResult> onChangeNotFound, Func<AlreadyArchived, TResult> onAlreadyArchived, Func<InvalidChangeName, TResult> onInvalidChangeName, Func<CardsUnreadable, TResult> onCardsUnreadable, Func<ToolFailure, TResult> onToolFailure) =>
            onInvalidChangeName(this);
    }

    /// <summary>
    /// At least one <c>*.md</c> file directly inside the live change directory could not be
    /// parsed. Fail-closed rather than optimistic (the same reasoning as <see cref="
    /// CardIdentityResolution.Unreadable"/>): an unreadable file might be an open obligation this
    /// verb is supposed to settle before the directory moves, so archiving proceeds only once
    /// every card in the directory has actually been read. Refusal-shaped, not tool-failure — the
    /// record itself, not the tool, is what is in an unreadable state.
    /// </summary>
    internal sealed record CardsUnreadable(IReadOnlyList<string> FilePaths) : ChangeArchiveOutcome
    {
        internal override TResult Match<TResult>(Func<Archived, TResult> onArchived, Func<ChangeNotFound, TResult> onChangeNotFound, Func<AlreadyArchived, TResult> onAlreadyArchived, Func<InvalidChangeName, TResult> onInvalidChangeName, Func<CardsUnreadable, TResult> onCardsUnreadable, Func<ToolFailure, TResult> onToolFailure) =>
            onCardsUnreadable(this);
    }

    /// <summary>Enforcement itself is unavailable: a lock could not be acquired while settling an
    /// obligation, an obligation's discharge landed in a state this verb did not itself account
    /// for having just scanned it as open (an inherent race — see <see cref="CardStore.
    /// ArchiveChange"/>'s own doc comment), or the directory move itself failed part way. Tool-
    /// failure-shaped.</summary>
    internal sealed record ToolFailure(string Reason) : ChangeArchiveOutcome
    {
        internal override TResult Match<TResult>(Func<Archived, TResult> onArchived, Func<ChangeNotFound, TResult> onChangeNotFound, Func<AlreadyArchived, TResult> onAlreadyArchived, Func<InvalidChangeName, TResult> onInvalidChangeName, Func<CardsUnreadable, TResult> onCardsUnreadable, Func<ToolFailure, TResult> onToolFailure) =>
            onToolFailure(this);
    }
}
