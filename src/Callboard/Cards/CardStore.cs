using System.Text;

namespace Callboard.Cards;

/// <summary>
/// Where a card first reaches disk. Every write goes through <see cref="CardLock"/> (2.6) and
/// writes via a temp file beside the target followed by a rename (2.5) — never in place, and
/// never through the system temp directory, since a rename across filesystems degrades to a copy
/// and stops being atomic (ADR-0003 / design.md D7). <see cref="AppendComment"/> is the append-only
/// public surface — a caller cannot use this type to remove or rewrite an existing comment,
/// because the only mutation it exposes is "read the current card, add one more comment, write the
/// result" under the lock, closing the gap the block A review named: append-only was structural at
/// the format layer but only conventional at the write boundary until this type existed.
///
/// <para>
/// <b>Anchored to the repository root (4.5, O-1):</b> every write takes a <c>cardsRoot</c> —
/// the same root every other rooted path in this codebase resolves under
/// (<see cref="RepoRootResolver"/>, <see cref="Index.IndexPaths"/>) — and the only path that ever
/// reaches disk is an <see cref="AnchoredCardPath"/>, which can only be constructed by proving the
/// target file's directory resolves under that exact root. See that type's own doc comment for
/// what this closes and why it is structural rather than a convention a caller could forget.
/// </para>
///
/// <para>
/// <b>The lock is the only source of the path it guards (4.5, O-2 remediation):</b> the
/// <c>*UnderExistingLock</c> methods below take a <see cref="CardLock"/> and never a separate
/// <c>filePath</c> alongside it — the target is <see cref="CardLock.CardPath"/>, read off the lock
/// itself. The first shape shipped (a <c>CardLock heldLock</c> parameter <em>plus</em> a
/// <c>filePath</c> parameter) let a caller hold the lock for one card and act on a different one —
/// both parameters were individually real, but nothing tied them together, so "lock X, write Y"
/// compiled and ran clean. Removing the second parameter removes the thing that could disagree
/// with it: there is exactly one path in play in this method's signature, and it is the one the
/// lock was actually acquired for.
/// </para>
///
/// <para>
/// <b>Durability decision:</b> the temp file's content is flushed and <c>fsync</c>'d
/// (<see cref="FileStream.Flush(bool)"/> with <c>flushToDisk: true</c>) before the rename, so the
/// bytes being renamed into place are durable against a power loss, not only a process kill. The
/// directory entry update the rename itself performs is not additionally fsync'd — that would need
/// a separate fsync of the containing directory's file descriptor, which has no direct
/// <c>System.IO</c> surface and was judged disproportionate for this block. The residual gap (a
/// rename that completed in the OS but whose directory-entry update is not itself confirmed durable
/// on power loss, on filesystems where that distinction matters) is accepted, not overlooked.
/// </para>
/// </summary>
internal static class CardStore
{
    /// <summary>
    /// Writes a new card file, or fully replaces an existing one at the same path.
    /// <paramref name="cardsRoot"/> is the repository root every card in this call must live under
    /// (4.5, O-1) — see <see cref="AnchoredCardPath"/>. <paramref name="changeName"/> is required
    /// exactly when <c>card.Frontmatter.Scope</c> is <see cref="CardScope.Change"/> or
    /// <see cref="CardScope.Section"/> — see <see cref="CardLayout.DirectoryFor"/>.
    /// </summary>
    internal static CardWriteResult WriteCard(string cardsRoot, string filePath, CardFile card, TimeSpan lockTimeout, string? changeName = null)
    {
        var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, card.Frontmatter.Scope, changeName, out var layoutFailure);
        if (anchored is null)
        {
            return layoutFailure!;
        }

        // The containing directory has to exist before the lock file beside the target can be
        // created — done here, ahead of acquiring the lock, rather than only inside AtomicWrite,
        // or a brand-new card's first write would spend its whole lock-acquire loop retrying a
        // create that can never succeed until something else creates the directory first.
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
        {
            return new CardWriteResult.Failure($"'{filePath}' has no containing directory to write into.");
        }

        Directory.CreateDirectory(directory);

        return WithLock(filePath, lockTimeout, _ => AtomicWrite(anchored, CardFileWriter.Serialize(card)));
    }

    /// <summary>
    /// Appends <paramref name="comment"/> to the card at <paramref name="filePath"/>: reads the
    /// current file, parses it, adds the comment, and writes the result back — all under the
    /// card's lock, so two concurrent appends serialise rather than racing (record-retrieval:
    /// "the thread's order is preserved"). <paramref name="cardsRoot"/> and
    /// <paramref name="changeName"/> are passed through to the same layout reconciliation
    /// <see cref="WriteCard"/> applies, checked against the scope the card itself declares once it
    /// has been read — see <see cref="AnchoredCardPath"/>.
    /// </summary>
    internal static CardWriteResult AppendComment(string cardsRoot, string filePath, CardComment comment, TimeSpan lockTimeout, string? changeName = null) =>
        WithLock(filePath, lockTimeout, heldLock => AppendCommentUnderExistingLock(heldLock, cardsRoot, comment, changeName));

    /// <summary>
    /// The read-modify-write step of <see cref="AppendComment"/>, exposed separately so a test can
    /// hold a <see cref="CardLock"/> itself, drive this directly to establish a known append
    /// order, then start a second concurrent <see cref="AppendComment"/> that must wait for the
    /// same lock — proving 2.7's ordering guarantee deterministically rather than by chance timing.
    ///
    /// <para>
    /// <b>Structural, not conventional (O-2):</b> <paramref name="heldLock"/> is mandatory — the
    /// only way to obtain a <see cref="CardLock"/> instance at all is a successful
    /// <see cref="CardLock.Acquire"/>, so a caller cannot reach the read-modify-write below without
    /// having actually taken a card's lock. And it is <em>this</em> card's lock specifically: the
    /// target is <see cref="CardLock.CardPath"/>, not a separately supplied <c>filePath</c> — see
    /// this type's own doc comment for why the first shape (both a lock and a path) was not
    /// enough. <see cref="ArgumentNullException.ThrowIfNull"/> closes the one remaining gap
    /// nullable reference types cannot: a caller passing <c>null!</c> to defeat the compile-time
    /// hint.
    /// </para>
    /// </summary>
    internal static CardWriteResult AppendCommentUnderExistingLock(CardLock heldLock, string cardsRoot, CardComment comment, string? changeName = null)
    {
        ArgumentNullException.ThrowIfNull(heldLock);
        var filePath = heldLock.CardPath;

        if (!File.Exists(filePath))
        {
            return new CardWriteResult.Failure($"no card file exists at '{filePath}' to append a comment to.");
        }

        var current = ReadCard(filePath);
        return current.Match<CardWriteResult>(
            onSuccess: success =>
            {
                var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, success.Card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return layoutFailure!;
                }

                var updated = success.Card with { Comments = [.. success.Card.Comments, comment] };
                return AtomicWrite(anchored, CardFileWriter.Serialize(updated));
            },
            onFailure: failure =>
                new CardWriteResult.Failure($"cannot append to '{filePath}': the card file is corrupt: {failure.Reason}"));
    }

    /// <summary>
    /// Reassigns <paramref name="filePath"/>'s card to <paramref name="newOwner"/> and appends a
    /// <see cref="CardHandover"/> entry recording the handover (card-model: "Ownership names whose
    /// turn it is" — "**Every** ownership change SHALL record the acting role and the time it
    /// occurred"). <paramref name="actingRole"/> is the role performing the transfer, which need
    /// not be — and ordinarily is not — either the outgoing or incoming owner (an architect
    /// reassigning worker to reviewer is the common case).
    ///
    /// <para>
    /// <b>Why an append-only sequence, not overwritable frontmatter scalars (reviewer round 1,
    /// finding 3):</b> the spec's "every" is unconditional, and a card handed over more than
    /// once — the ordinary lifecycle, not an edge case — needs every prior handover's attribution
    /// still recoverable, not just the most recent. <see cref="CardFrontmatter.Owner"/> stays the
    /// queryable <em>current</em> owner; <see cref="CardFile.Handovers"/> is the append-only
    /// <em>history</em> that can never disagree with it, because <see cref="CardFrontmatter.Owner"/> is set, in this
    /// same write, to exactly the <see cref="CardHandover.To"/> of the entry this call appends —
    /// there is no second code path that could set one without the other.
    /// </para>
    /// </summary>
    internal static CardWriteResult TransferOwnership(
        string cardsRoot, string filePath, CardOwner newOwner, CardOwner actingRole, DateTimeOffset timestamp, TimeSpan lockTimeout, string? changeName = null) =>
        WithLock(filePath, lockTimeout, heldLock => TransferOwnershipUnderExistingLock(heldLock, cardsRoot, newOwner, actingRole, timestamp, changeName));

    /// <summary>
    /// The read-modify-write step of <see cref="TransferOwnership"/>. Same structural lock
    /// precondition as <see cref="AppendCommentUnderExistingLock"/> (O-2's fix applied to every
    /// method on this surface with the same shape, not just the one line the obligation named) —
    /// the target is <see cref="CardLock.CardPath"/>, not a separately supplied <c>filePath</c>.
    /// </summary>
    internal static CardWriteResult TransferOwnershipUnderExistingLock(
        CardLock heldLock, string cardsRoot, CardOwner newOwner, CardOwner actingRole, DateTimeOffset timestamp, string? changeName = null)
    {
        ArgumentNullException.ThrowIfNull(heldLock);
        var filePath = heldLock.CardPath;

        if (!File.Exists(filePath))
        {
            return new CardWriteResult.Failure($"no card file exists at '{filePath}' to transfer ownership of.");
        }

        var current = ReadCard(filePath);
        return current.Match<CardWriteResult>(
            onSuccess: success =>
            {
                var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, success.Card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return layoutFailure!;
                }

                var handover = new CardHandover(actingRole, newOwner, timestamp, []);
                var updatedFrontmatter = success.Card.Frontmatter with { Owner = newOwner, Updated = timestamp };
                var updated = success.Card with
                {
                    Frontmatter = updatedFrontmatter,
                    Handovers = [.. success.Card.Handovers, handover],
                };

                return AtomicWrite(anchored, CardFileWriter.Serialize(updated));
            },
            onFailure: failure =>
                new CardWriteResult.Failure($"cannot transfer ownership of '{filePath}': the card file is corrupt: {failure.Reason}"));
    }

    /// <summary>
    /// Reads and parses one card file. I/O failures (the file vanished, permissions) are caught
    /// and folded into <see cref="CardFileParseResult.Failure"/> alongside format-level failures,
    /// so a caller enumerating many cards (see <see cref="ReadAllCards"/>) never has to
    /// distinguish "could not read" from "could not parse" — both mean this one card is unusable
    /// right now, and neither should stop the caller from reading any other card.
    /// </summary>
    internal static CardFileParseResult ReadCard(string filePath)
    {
        string text;
        try
        {
            text = File.ReadAllText(filePath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CardFileParseResult.Failure($"could not read '{filePath}': {ex.Message}");
        }

        return CardFileParser.Parse(text);
    }

    /// <summary>
    /// Reads every <c>*.md</c> card file directly inside <paramref name="directory"/>, one at a
    /// time, isolating each file's outcome from every other's — this is the read path 2.8 asserts
    /// against: damage to one card's bytes must never prevent any other card in the same directory
    /// from being read. Ordered by path (<see cref="StringComparer.Ordinal"/>) so a caller's
    /// output is deterministic regardless of filesystem enumeration order.
    /// </summary>
    internal static IReadOnlyList<(string FilePath, CardFileParseResult Result)> ReadAllCards(string directory)
    {
        var paths = Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToList();

        var results = new List<(string FilePath, CardFileParseResult Result)>(paths.Count);
        foreach (var path in paths)
        {
            results.Add((path, ReadCard(path)));
        }

        return results;
    }

    private static CardWriteResult WithLock(string filePath, TimeSpan lockTimeout, Func<CardLock, CardWriteResult> action)
    {
        var lockResult = CardLock.Acquire(filePath, lockTimeout);
        return lockResult.Match(
            onAcquired: acquired =>
            {
                using (acquired.Lock)
                {
                    return action(acquired.Lock);
                }
            },
            onTimedOut: timedOut => new CardWriteResult.Failure(timedOut.Message));
    }

    /// <summary>
    /// The one place bytes actually reach disk. Takes an <see cref="AnchoredCardPath"/>, never a
    /// raw <see cref="string"/> — there is no overload that would let a caller skip the
    /// root-and-layout check <see cref="AnchoredCardPath.TryCreate"/> performs (O-1: "structural,
    /// not conventional").
    /// </summary>
    private static CardWriteResult AtomicWrite(AnchoredCardPath anchored, string content)
    {
        var filePath = anchored.FilePath;
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
        {
            return new CardWriteResult.Failure($"'{filePath}' has no containing directory to write into.");
        }

        Directory.CreateDirectory(directory);

        // Beside the target, on the same filesystem, never the system temp directory — a rename
        // across filesystems degrades to a copy and stops being atomic (ADR-0003 / D7).
        var tempPath = Path.Combine(directory, $"{Path.GetFileName(filePath)}.tmp-{Guid.NewGuid():N}");

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, filePath, overwrite: true);
            return new CardWriteResult.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CardWriteResult.Failure($"could not write '{filePath}': {ex.Message}");
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
