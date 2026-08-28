namespace Callboard.Cards;

/// <summary>
/// What <see cref="NitResolver.Resolve"/> answers for one requested nit id — the same four-case
/// shape as <see cref="CardIdentityResolution"/> was before §13.6 (see that type's own doc comment
/// for why each surviving case exists), applied to a comment's id instead of a card's <c>id</c>
/// frontmatter field. A nit's id is generated once (<c>CardStore.RaiseNit</c>... — §8 block B,
/// appended alongside the raising comment) and never recycled, but nothing in this codebase
/// enforces global uniqueness across comments the way <see cref="CardIdentityAllocator"/> enforces
/// it for card ids, so <see cref="Duplicate"/> is modelled here for the same fail-closed reason
/// <see cref="CardIdentityResolution.Duplicate"/> is, not because a collision has ever been
/// observed.
///
/// <para>
/// <b>No <c>Corrupt</c> case, deliberately (§13.6 obligation, discharged by declining rather than
/// building).</b> <see cref="CardIdentityResolution.Corrupt"/> exists because a card's own id lives
/// in a fixed, structurally-bounded span — the leading <c>---</c> frontmatter fence — that a
/// best-effort scan can walk without trusting anything else in the file. A nit's id lives one level
/// down, on a <see cref="CardComment"/>'s own header line, which can appear anywhere after the
/// frontmatter, interleaved with ordinary body text and every other header line kind
/// (<c>CardFileParser.Parse</c> recognises handover, transition, verdict, authorisation, claim,
/// limit and refusal lines in the same stream). A comment header has no fixed leading span to
/// bound a scan the way the frontmatter fence does, and body content lines are free text a card's
/// author controls verbatim — so a scan for a line that merely looks like <c>&lt;!-- callboard:
/// comment ... id=... --&gt;</c> risks attributing a match to text the author wrote, never intended
/// as structure, exactly the class of defect §11 named. Recovering a nit id from an unparseable
/// file is therefore not the same operation as recovering a card id from one, and is not honest to
/// attempt the same way: this type converges its <see cref="Unreadable"/> case onto <see cref="
/// UnreadableCard"/> (path and reason, mechanical — the same convergence §13.5 held it back for),
/// and stops there.
/// </para>
/// </summary>
internal abstract record NitResolution
{
    private NitResolution()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<string, CardFile, CardComment, TResult> onFound,
        Func<string, TResult> onNotFound,
        Func<string, IReadOnlyList<string>, TResult> onDuplicate,
        Func<string, IReadOnlyList<UnreadableCard>, TResult> onUnreadable);

    internal static NitResolution Found(string filePath, CardFile card, CardComment comment) => new FoundCase(filePath, card, comment);

    internal static NitResolution NotFound(string nitId) => new NotFoundCase(nitId);

    internal static NitResolution Duplicate(string nitId, IReadOnlyList<string> filePaths) => new DuplicateCase(nitId, filePaths);

    internal static NitResolution Unreadable(string nitId, IReadOnlyList<UnreadableCard> files) => new UnreadableCase(nitId, files);

    private sealed record FoundCase(string FilePath, CardFile Card, CardComment Comment) : NitResolution
    {
        internal override TResult Match<TResult>(
            Func<string, CardFile, CardComment, TResult> onFound,
            Func<string, TResult> onNotFound,
            Func<string, IReadOnlyList<string>, TResult> onDuplicate,
            Func<string, IReadOnlyList<UnreadableCard>, TResult> onUnreadable) =>
            onFound(FilePath, Card, Comment);
    }

    private sealed record NotFoundCase(string NitId) : NitResolution
    {
        internal override TResult Match<TResult>(
            Func<string, CardFile, CardComment, TResult> onFound,
            Func<string, TResult> onNotFound,
            Func<string, IReadOnlyList<string>, TResult> onDuplicate,
            Func<string, IReadOnlyList<UnreadableCard>, TResult> onUnreadable) =>
            onNotFound(NitId);
    }

    private sealed record DuplicateCase(string NitId, IReadOnlyList<string> FilePaths) : NitResolution
    {
        internal override TResult Match<TResult>(
            Func<string, CardFile, CardComment, TResult> onFound,
            Func<string, TResult> onNotFound,
            Func<string, IReadOnlyList<string>, TResult> onDuplicate,
            Func<string, IReadOnlyList<UnreadableCard>, TResult> onUnreadable) =>
            onDuplicate(NitId, FilePaths);
    }

    private sealed record UnreadableCase(string NitId, IReadOnlyList<UnreadableCard> Files) : NitResolution
    {
        internal override TResult Match<TResult>(
            Func<string, CardFile, CardComment, TResult> onFound,
            Func<string, TResult> onNotFound,
            Func<string, IReadOnlyList<string>, TResult> onDuplicate,
            Func<string, IReadOnlyList<UnreadableCard>, TResult> onUnreadable) =>
            onUnreadable(NitId, Files);
    }
}
