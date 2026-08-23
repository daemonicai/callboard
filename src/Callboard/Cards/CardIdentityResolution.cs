namespace Callboard.Cards;

/// <summary>
/// What <see cref="CardIdentityResolver.Resolve"/> answers for one requested card <c>id</c> — a
/// closed union of exactly four cases, the same discipline every other union in <c>Cards/</c>
/// follows (see <see cref="CardKind"/>'s own doc comment). No case is a silent "pick one" or a
/// silent "treat as absent": <see cref="Duplicate"/> and <see cref="Unreadable"/> exist precisely
/// so a caller cannot collapse either into <see cref="Found"/> or <see cref="NotFound"/> without
/// naming the case explicitly in a <see cref="Match{TResult}"/> call.
///
/// <list type="bullet">
/// <item><see cref="Found"/> — exactly one file in the record carries the requested id.</item>
/// <item><see cref="NotFound"/> — no file anywhere the resolver searched carries the id, and every
/// file the walk touched parsed cleanly, so the record has been exhaustively checked.</item>
/// <item><see cref="Duplicate"/> — more than one file claims the same id (§7 block B, the defect
/// §6 fail-closed on twice: "a duplicate id is a refusal, never a pick"). Never "whichever sorted
/// first".</item>
/// <item><see cref="Unreadable"/> — no file that parsed cleanly carries the id, but at least one
/// file the walk touched could not be read at all (§6 remediation B3, re-applied here): that file
/// might carry the requested id, so the resolver cannot claim <see cref="NotFound"/> — "could not
/// confirm" is a different fact from "confirmed absent".</item>
/// </list>
/// </summary>
internal abstract record CardIdentityResolution
{
    private CardIdentityResolution()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<string, CardFile, TResult> onFound,
        Func<string, TResult> onNotFound,
        Func<string, IReadOnlyList<string>, TResult> onDuplicate,
        Func<string, IReadOnlyList<string>, TResult> onUnreadable);

    internal static CardIdentityResolution Found(string filePath, CardFile card) => new FoundCase(filePath, card);

    internal static CardIdentityResolution NotFound(string id) => new NotFoundCase(id);

    internal static CardIdentityResolution Duplicate(string id, IReadOnlyList<string> filePaths) => new DuplicateCase(id, filePaths);

    internal static CardIdentityResolution Unreadable(string id, IReadOnlyList<string> filePaths) => new UnreadableCase(id, filePaths);

    private sealed record FoundCase(string FilePath, CardFile Card) : CardIdentityResolution
    {
        internal override TResult Match<TResult>(
            Func<string, CardFile, TResult> onFound,
            Func<string, TResult> onNotFound,
            Func<string, IReadOnlyList<string>, TResult> onDuplicate,
            Func<string, IReadOnlyList<string>, TResult> onUnreadable) =>
            onFound(FilePath, Card);
    }

    private sealed record NotFoundCase(string Id) : CardIdentityResolution
    {
        internal override TResult Match<TResult>(
            Func<string, CardFile, TResult> onFound,
            Func<string, TResult> onNotFound,
            Func<string, IReadOnlyList<string>, TResult> onDuplicate,
            Func<string, IReadOnlyList<string>, TResult> onUnreadable) =>
            onNotFound(Id);
    }

    /// <param name="Id">The requested id, repeated here so a caller building a message does not
    /// have to thread it separately from the resolution outcome.</param>
    /// <param name="FilePaths">Every file claiming <paramref name="Id"/>, ordered
    /// <see cref="StringComparer.Ordinal"/> for a deterministic message.</param>
    private sealed record DuplicateCase(string Id, IReadOnlyList<string> FilePaths) : CardIdentityResolution
    {
        internal override TResult Match<TResult>(
            Func<string, CardFile, TResult> onFound,
            Func<string, TResult> onNotFound,
            Func<string, IReadOnlyList<string>, TResult> onDuplicate,
            Func<string, IReadOnlyList<string>, TResult> onUnreadable) =>
            onDuplicate(Id, FilePaths);
    }

    /// <param name="Id">The requested id.</param>
    /// <param name="FilePaths">Every file the walk could not read, ordered
    /// <see cref="StringComparer.Ordinal"/> — any one of them might be the card actually carrying
    /// <paramref name="Id"/>.</param>
    private sealed record UnreadableCase(string Id, IReadOnlyList<string> FilePaths) : CardIdentityResolution
    {
        internal override TResult Match<TResult>(
            Func<string, CardFile, TResult> onFound,
            Func<string, TResult> onNotFound,
            Func<string, IReadOnlyList<string>, TResult> onDuplicate,
            Func<string, IReadOnlyList<string>, TResult> onUnreadable) =>
            onUnreadable(Id, FilePaths);
    }
}
