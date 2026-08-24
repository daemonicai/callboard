namespace Callboard.Cards;

/// <summary>
/// What <see cref="NitResolver.Resolve"/> answers for one requested nit id — the same four-case
/// shape as <see cref="CardIdentityResolution"/> (see that type's own doc comment for why each case
/// exists), applied to a comment's id instead of a card's <c>id</c> frontmatter field. A nit's id is
/// generated once (<c>CardStore.RaiseNit</c>... — §8 block B, appended alongside the raising
/// comment) and never recycled, but nothing in this codebase enforces global uniqueness across
/// comments the way <see cref="CardIdentityAllocator"/> enforces it for card ids, so
/// <see cref="Duplicate"/> is modelled here for the same fail-closed reason
/// <see cref="CardIdentityResolution.Duplicate"/> is, not because a collision has ever been
/// observed.
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
        Func<string, IReadOnlyList<string>, TResult> onUnreadable);

    internal static NitResolution Found(string filePath, CardFile card, CardComment comment) => new FoundCase(filePath, card, comment);

    internal static NitResolution NotFound(string nitId) => new NotFoundCase(nitId);

    internal static NitResolution Duplicate(string nitId, IReadOnlyList<string> filePaths) => new DuplicateCase(nitId, filePaths);

    internal static NitResolution Unreadable(string nitId, IReadOnlyList<string> filePaths) => new UnreadableCase(nitId, filePaths);

    private sealed record FoundCase(string FilePath, CardFile Card, CardComment Comment) : NitResolution
    {
        internal override TResult Match<TResult>(
            Func<string, CardFile, CardComment, TResult> onFound,
            Func<string, TResult> onNotFound,
            Func<string, IReadOnlyList<string>, TResult> onDuplicate,
            Func<string, IReadOnlyList<string>, TResult> onUnreadable) =>
            onFound(FilePath, Card, Comment);
    }

    private sealed record NotFoundCase(string NitId) : NitResolution
    {
        internal override TResult Match<TResult>(
            Func<string, CardFile, CardComment, TResult> onFound,
            Func<string, TResult> onNotFound,
            Func<string, IReadOnlyList<string>, TResult> onDuplicate,
            Func<string, IReadOnlyList<string>, TResult> onUnreadable) =>
            onNotFound(NitId);
    }

    private sealed record DuplicateCase(string NitId, IReadOnlyList<string> FilePaths) : NitResolution
    {
        internal override TResult Match<TResult>(
            Func<string, CardFile, CardComment, TResult> onFound,
            Func<string, TResult> onNotFound,
            Func<string, IReadOnlyList<string>, TResult> onDuplicate,
            Func<string, IReadOnlyList<string>, TResult> onUnreadable) =>
            onDuplicate(NitId, FilePaths);
    }

    private sealed record UnreadableCase(string NitId, IReadOnlyList<string> FilePaths) : NitResolution
    {
        internal override TResult Match<TResult>(
            Func<string, CardFile, CardComment, TResult> onFound,
            Func<string, TResult> onNotFound,
            Func<string, IReadOnlyList<string>, TResult> onDuplicate,
            Func<string, IReadOnlyList<string>, TResult> onUnreadable) =>
            onUnreadable(NitId, FilePaths);
    }
}
