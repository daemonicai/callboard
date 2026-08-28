namespace Callboard.Cards;

/// <summary>
/// What <see cref="CardIdentityResolver.Resolve"/> answers for one requested card <c>id</c> — a
/// closed union of exactly five cases, the same discipline every other union in <c>Cards/</c>
/// follows (see <see cref="CardKind"/>'s own doc comment). No case is a silent "pick one" or a
/// silent "treat as absent": <see cref="Duplicate"/>, <see cref="Corrupt"/> and
/// <see cref="Unreadable"/> exist precisely so a caller cannot collapse any of them into
/// <see cref="Found"/> or <see cref="NotFound"/> without naming the case explicitly in a
/// <see cref="Match{TResult}"/> call.
///
/// <list type="bullet">
/// <item><see cref="Found"/> — exactly one file that parsed cleanly carries the requested id.
/// Decided unconditionally over every other case: a parsed file's frontmatter is the record, so a
/// file that failed to parse can never outrank it, even if that file also claims the id (§13.6).
/// </item>
/// <item><see cref="NotFound"/> — no file anywhere the resolver searched carries the id, every
/// file the walk touched parsed cleanly, so the record has been exhaustively checked.</item>
/// <item><see cref="Duplicate"/> — more than one parsed file claims the same id (§7 block B, the
/// defect §6 fail-closed on twice: "a duplicate id is a refusal, never a pick"). Never "whichever
/// sorted first".</item>
/// <item><see cref="Corrupt"/> — no file that parsed cleanly carries the id, but at least one file
/// the walk could not parse <em>declares</em> the id in its own leading frontmatter fence (§13.6
/// best-effort recovery, Product Owner ruling). Distinct from <see cref="Unreadable"/> on purpose:
/// this case names the wrong remedy for the other — a resolver that only ever said "not found"
/// sends an agent hunting for a typo when the file that would answer is sitting right there,
/// unparseable.</item>
/// <item><see cref="Unreadable"/> — no file that parsed cleanly carries the id, and no unparseable
/// file declares it either, but at least one file the walk touched could not be read at all (§6
/// remediation B3, re-applied here): that file might still carry the requested id without
/// declaring it recoverably, so the resolver cannot claim <see cref="NotFound"/> — "could not
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
        Func<string, IReadOnlyList<UnreadableCard>, TResult> onCorrupt,
        Func<string, IReadOnlyList<UnreadableCard>, TResult> onUnreadable);

    internal static CardIdentityResolution Found(string filePath, CardFile card) => new FoundCase(filePath, card);

    internal static CardIdentityResolution NotFound(string id) => new NotFoundCase(id);

    internal static CardIdentityResolution Duplicate(string id, IReadOnlyList<string> filePaths) => new DuplicateCase(id, filePaths);

    internal static CardIdentityResolution Corrupt(string id, IReadOnlyList<UnreadableCard> claimants) => new CorruptCase(id, claimants);

    internal static CardIdentityResolution Unreadable(string id, IReadOnlyList<UnreadableCard> files) => new UnreadableCase(id, files);

    private sealed record FoundCase(string FilePath, CardFile Card) : CardIdentityResolution
    {
        internal override TResult Match<TResult>(
            Func<string, CardFile, TResult> onFound,
            Func<string, TResult> onNotFound,
            Func<string, IReadOnlyList<string>, TResult> onDuplicate,
            Func<string, IReadOnlyList<UnreadableCard>, TResult> onCorrupt,
            Func<string, IReadOnlyList<UnreadableCard>, TResult> onUnreadable) =>
            onFound(FilePath, Card);
    }

    private sealed record NotFoundCase(string Id) : CardIdentityResolution
    {
        internal override TResult Match<TResult>(
            Func<string, CardFile, TResult> onFound,
            Func<string, TResult> onNotFound,
            Func<string, IReadOnlyList<string>, TResult> onDuplicate,
            Func<string, IReadOnlyList<UnreadableCard>, TResult> onCorrupt,
            Func<string, IReadOnlyList<UnreadableCard>, TResult> onUnreadable) =>
            onNotFound(Id);
    }

    /// <param name="Id">The requested id, repeated here so a caller building a message does not
    /// have to thread it separately from the resolution outcome.</param>
    /// <param name="FilePaths">Every parsed file claiming <paramref name="Id"/>, ordered
    /// <see cref="StringComparer.Ordinal"/> for a deterministic message.</param>
    private sealed record DuplicateCase(string Id, IReadOnlyList<string> FilePaths) : CardIdentityResolution
    {
        internal override TResult Match<TResult>(
            Func<string, CardFile, TResult> onFound,
            Func<string, TResult> onNotFound,
            Func<string, IReadOnlyList<string>, TResult> onDuplicate,
            Func<string, IReadOnlyList<UnreadableCard>, TResult> onCorrupt,
            Func<string, IReadOnlyList<UnreadableCard>, TResult> onUnreadable) =>
            onDuplicate(Id, FilePaths);
    }

    /// <param name="Id">The requested id.</param>
    /// <param name="Claimants">Every unparseable file whose own leading frontmatter fence declares
    /// <paramref name="Id"/>, path and parse reason together (<see cref="UnreadableCard"/>),
    /// ordered <see cref="StringComparer.Ordinal"/> by path.</param>
    private sealed record CorruptCase(string Id, IReadOnlyList<UnreadableCard> Claimants) : CardIdentityResolution
    {
        internal override TResult Match<TResult>(
            Func<string, CardFile, TResult> onFound,
            Func<string, TResult> onNotFound,
            Func<string, IReadOnlyList<string>, TResult> onDuplicate,
            Func<string, IReadOnlyList<UnreadableCard>, TResult> onCorrupt,
            Func<string, IReadOnlyList<UnreadableCard>, TResult> onUnreadable) =>
            onCorrupt(Id, Claimants);
    }

    /// <param name="Id">The requested id.</param>
    /// <param name="Files">Every file the walk could not parse, path and parse reason together
    /// (<see cref="UnreadableCard"/>), ordered <see cref="StringComparer.Ordinal"/> by path — any
    /// one of them might be the card actually carrying <paramref name="Id"/>, without declaring it
    /// recoverably.</param>
    private sealed record UnreadableCase(string Id, IReadOnlyList<UnreadableCard> Files) : CardIdentityResolution
    {
        internal override TResult Match<TResult>(
            Func<string, CardFile, TResult> onFound,
            Func<string, TResult> onNotFound,
            Func<string, IReadOnlyList<string>, TResult> onDuplicate,
            Func<string, IReadOnlyList<UnreadableCard>, TResult> onCorrupt,
            Func<string, IReadOnlyList<UnreadableCard>, TResult> onUnreadable) =>
            onUnreadable(Id, Files);
    }
}
