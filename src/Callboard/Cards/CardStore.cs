using System.Collections.Immutable;
using System.Linq;
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
    /// Writes a brand-new card file at <paramref name="filePath"/>. <b>Create-only</b> (4.6/4.8
    /// remediation, DEVLOG §4 block C review round 1): refuses under the lock when a card already
    /// exists at that path, rather than replacing it. A card is created exactly once; every later
    /// touch goes through a targeted read-modify-write (<see cref="AppendComment"/>,
    /// <see cref="TransferOwnership"/>) that reads the current file first and so can only add to
    /// it, never drop what it did not read. Full replacement is deliberately not reachable through
    /// this type at all — see this method's own existence check, not a convention layered on top
    /// of one that still allowed it (the shape that let a reviewer probe pass an empty
    /// <c>Comments</c> list over a card that had one and silently drop it).
    /// <paramref name="card"/> is a <see cref="NewCardFile"/>, not a <see cref="CardFile"/> (§4
    /// remediation, R3): it carries no <see cref="CardFile.Comments"/> and no
    /// <see cref="CardFile.Handovers"/>, so a caller cannot construct a brand-new card whose
    /// frontmatter <see cref="CardFrontmatter.Owner"/> disagrees with a handover history that
    /// should not exist yet, or one that silently discards an existing thread — both are simply
    /// not expressible in this method's input, rather than accepted and then rejected. See
    /// <see cref="NewCardFile"/>'s own doc comment.
    /// <paramref name="cardsRoot"/> is the repository root every card in this call must live under
    /// (4.5, O-1) — see <see cref="AnchoredCardPath"/>. <paramref name="changeName"/> is required
    /// exactly when <c>card.Frontmatter.Scope</c> is <see cref="CardScope.Change"/> or
    /// <see cref="CardScope.Section"/> — see <see cref="CardLayout.DirectoryFor"/>.
    /// </summary>
    internal static CardWriteResult WriteCard(string cardsRoot, string filePath, NewCardFile card, TimeSpan lockTimeout, string? changeName = null)
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
            return new CardWriteResult.LayoutMismatch($"'{filePath}' has no containing directory to write into.");
        }

        Directory.CreateDirectory(directory);

        // The existence check happens here, inside the lock's callback, not before WithLock is
        // called — a bare pre-lock File.Exists check would race a concurrent create for the same
        // path (TOCTOU); File.Move(overwrite: false) is not atomic on this platform (§2) and
        // FileShare.None gives no mutual exclusion either, so the lock is what makes this sound,
        // not the check by itself. A brand-new card has no comments and no handovers by
        // definition, so both are always empty in the CardFile actually serialised — see
        // NewCardFile.
        return WithLock(filePath, lockTimeout, _ =>
            File.Exists(filePath)
                ? new CardWriteResult.AlreadyExists(filePath)
                : AtomicWrite(anchored, CardFileWriter.Serialize(new CardFile(card.Frontmatter, card.Body, [], [], FindingFields: card.FindingFields, RegisterFields: card.RegisterFields, BlockFields: card.BlockFields))));
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
            return new CardWriteResult.NotFound(filePath);
        }

        var current = ReadCard(filePath);
        return current.Match<CardWriteResult>(
            onSuccess: success =>
            {
                // working-context (§10 block C): a card whose frontmatter already carries a
                // reserved derived-state key — only reachable by a hand edit made outside the
                // tool, never by this build's own writers — is refused before any further write,
                // so this generic surface never re-emits it. Checked first, ahead of the round
                // check below: a hand-tampered card is refused for that reason regardless of
                // whether its round also happens to agree with its history.
                if (ReservedDerivedStateFieldKeyIn(success.Card) is { } reservedKey)
                {
                    return RefuseAndRecord<CardWriteResult, CardWriteResult.HandEnteredDerivedState>(cardsRoot, success.Card, filePath, changeName, comment.Author, comment.Timestamp,
                        new CardWriteResult.HandEnteredDerivedState(reservedKey),
                        static reason => new CardWriteResult.ToolFailure(reason));
                }

                // A comment appends to any card kind, but a block card's own round has to agree
                // with its own history before this call is allowed to mutate it further (Architect
                // ruling, §8a block D brief: "act on that card" covers every writer that mutates a
                // block card).
                if (IsBlockCard(success.Card) && !RoundAgreesWithHistory(success.Card, out var storedRound, out var expectedRound))
                {
                    return RefuseAndRecord<CardWriteResult, CardWriteResult.RoundDisagreesWithHistory>(cardsRoot, success.Card, filePath, changeName, comment.Author, comment.Timestamp,
                        new CardWriteResult.RoundDisagreesWithHistory(storedRound, expectedRound),
                        static reason => new CardWriteResult.ToolFailure(reason));
                }

                var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, success.Card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return layoutFailure!;
                }

                var updated = success.Card with { Comments = [.. success.Card.Comments, comment] };
                return AtomicWrite(anchored, CardFileWriter.Serialize(updated));
            },
            onFailure: failure =>
                new CardWriteResult.Corrupt(filePath, failure.Reason));
    }

    /// <summary>
    /// <c>comment add</c> (§13, card-model: "The verbs that dispose of a thread SHALL NOT be the
    /// only ones that can start one") — its own read-decide-write, the same shape <see
    /// cref="ResolveCommentUnderExistingLock"/>/<see cref="PromoteCommentUnderLocks"/> already use
    /// for their own comment sub-verb rather than a bare call to <see cref="AppendComment"/>: <see
    /// cref="CardCommentAppendOutcome"/>'s own doc comment explains why <see
    /// cref="CardCommentAppendOutcome.ReplyToNotFound"/> cannot live on the generic <see
    /// cref="CardWriteResult"/> instead, and once a verb needs its own outcome type, reading and
    /// deciding once — rather than deciding once and then re-reading and re-deciding inside a
    /// delegated <see cref="AppendCommentUnderExistingLock"/> call — is the same single-read
    /// discipline every sibling verb's own method already follows. <see cref="ReservedDerivedStateFieldKeyIn"/>/<see
    /// cref="IsBlockCard"/>/<see cref="RoundAgreesWithHistory"/> are <see
    /// cref="AppendCommentUnderExistingLock"/>'s own two guards, reused directly rather than
    /// duplicated in spirit only.
    /// </summary>
    internal static CardCommentAppendOutcome AddComment(
        string cardsRoot, string filePath, CardComment comment, TimeSpan lockTimeout, string? changeName = null) =>
        WithLock(
            filePath,
            lockTimeout,
            heldLock => AddCommentUnderExistingLock(heldLock, cardsRoot, comment, changeName),
            onTimedOut: timedOut => new CardCommentAppendOutcome.ToolFailure(timedOut.Message));

    /// <summary>The read-decide-write step of <see cref="AddComment"/>. Same structural lock
    /// precondition as every other <c>*UnderExistingLock</c> method on this type.</summary>
    internal static CardCommentAppendOutcome AddCommentUnderExistingLock(CardLock heldLock, string cardsRoot, CardComment comment, string? changeName = null)
    {
        ArgumentNullException.ThrowIfNull(heldLock);
        var filePath = heldLock.CardPath;

        if (!File.Exists(filePath))
        {
            return new CardCommentAppendOutcome.CardNotFound(filePath);
        }

        var current = ReadCard(filePath);
        return current.Match<CardCommentAppendOutcome>(
            onSuccess: success =>
            {
                var card = success.Card;

                // Same ordering AppendCommentUnderExistingLock applies (§10 block C): a
                // hand-tampered card is refused on that basis regardless of any other check below.
                if (ReservedDerivedStateFieldKeyIn(card) is { } reservedKey)
                {
                    return RefuseAndRecord<CardCommentAppendOutcome, CardCommentAppendOutcome.HandEnteredDerivedState>(
                        cardsRoot, card, filePath, changeName, comment.Author, comment.Timestamp,
                        new CardCommentAppendOutcome.HandEnteredDerivedState(reservedKey),
                        static reason => new CardCommentAppendOutcome.ToolFailure(reason));
                }

                // A comment appends to any card kind, but a block card's own round has to agree
                // with its own history first (Architect ruling, §8a block D brief: "act on that
                // card" covers every writer that mutates a block card) — the same guard
                // AppendCommentUnderExistingLock applies to its own caller.
                if (IsBlockCard(card) && !RoundAgreesWithHistory(card, out var storedRound, out var expectedRound))
                {
                    return RefuseAndRecord<CardCommentAppendOutcome, CardCommentAppendOutcome.RoundDisagreesWithHistory>(
                        cardsRoot, card, filePath, changeName, comment.Author, comment.Timestamp,
                        new CardCommentAppendOutcome.RoundDisagreesWithHistory(storedRound, expectedRound),
                        static reason => new CardCommentAppendOutcome.ToolFailure(reason));
                }

                // card-model: "SHALL refuse" (Architect ruling, §13 block brief item 4) — a
                // '--reply-to' naming a comment not already in this card's own thread.
                if (comment.ReplyTo is { } replyTo
                    && !card.Comments.Any(existing => string.Equals(existing.Id, replyTo, StringComparison.Ordinal)))
                {
                    return RefuseAndRecord<CardCommentAppendOutcome, CardCommentAppendOutcome.ReplyToNotFound>(
                        cardsRoot, card, filePath, changeName, comment.Author, comment.Timestamp,
                        new CardCommentAppendOutcome.ReplyToNotFound(replyTo),
                        static reason => new CardCommentAppendOutcome.ToolFailure(reason));
                }

                var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return new CardCommentAppendOutcome.LayoutMismatch(layoutFailure!.Reason);
                }

                var updated = card with { Comments = [.. card.Comments, comment] };
                var writeResult = AtomicWrite(anchored, CardFileWriter.Serialize(updated));
                return writeResult.Match<CardCommentAppendOutcome>(
                    onSuccess: _ => new CardCommentAppendOutcome.Added(card, comment),
                    onNotFound: notFound => new CardCommentAppendOutcome.CardNotFound(notFound.FilePath),
                    onAlreadyExists: alreadyExists => throw new InvalidOperationException(
                        $"unexpected 'already exists' appending a comment to '{alreadyExists.FilePath}'."),
                    onLayoutMismatch: static _ => throw new InvalidOperationException("unreachable: the anchored path already resolved above."),
                    onCorrupt: corrupt => new CardCommentAppendOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason),
                    onToolFailure: toolFailure => new CardCommentAppendOutcome.ToolFailure(toolFailure.Reason),
                    onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: a reserved derived-state field is refused before this point."));
            },
            onFailure: failure => new CardCommentAppendOutcome.CardCorrupt(filePath, failure.Reason));
    }

    /// <summary>
    /// The first reserved derived-state key (<see cref="DerivedStateFieldKeys.All"/>) found on
    /// <paramref name="card"/>'s <see cref="CardFile.UnknownFrontmatterFields"/>, or
    /// <see langword="null"/> when none is present — <see cref="AppendCommentUnderExistingLock"/>
    /// and <see cref="TransferOwnershipUnderExistingLock"/>'s shared guard (§10 block C). Reads
    /// only <see cref="CardFile.UnknownFrontmatterFields"/>: a key this build's own parser assigns
    /// a typed home to (every field <see cref="BlockCardFields"/>, <see cref="RegisterCardFields"/>,
    /// <see cref="QuestionCardFields"/> and <see cref="SectionCardFields"/> know about) is never a
    /// reserved derived-state key in the first place, so this check can never collide with a
    /// legitimate field this build itself writes.
    /// </summary>
    private static string? ReservedDerivedStateFieldKeyIn(CardFile card)
    {
        foreach (var (key, _) in card.UnknownFrontmatterFields)
        {
            foreach (var reserved in DerivedStateFieldKeys.All)
            {
                if (string.Equals(key, reserved, StringComparison.Ordinal))
                {
                    return reserved;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Appends a nit comment (<paramref name="comment"/>, which SHALL carry
    /// <see cref="CardComment.IsNit"/>) to the block card at <paramref name="filePath"/>, but only
    /// while that card is <see cref="BlockFlowState.InReview"/> (review-certification: "A nit SHALL
    /// be raised only against a block that is under review" — §8 remediation, Product Owner ruling
    /// of 2026-08-24). Not built on <see cref="AppendComment"/> — that surface is generic over any
    /// comment on any card kind and has no notion of "under review"; this method re-reads and
    /// re-validates state under its own lock instead, the same read-decide-write shape
    /// <see cref="RecordApproval"/> already uses for its own verb-specific checks.
    ///
    /// <para>
    /// <b>What this bound closes, and why raising is the right place to close it (not a second
    /// check on every writer that can move a block).</b> <see cref="ApplyBlockTransitionUnderExistingLock"/>
    /// already refuses to leave <c>in-review</c> while a nit is undispositioned; <see cref="
    /// RecordApprovalUnderExistingLock"/> carries an equivalent check for its own <c>approve</c>
    /// edge. With raising confined here, those two guards are jointly sufficient: no card can
    /// reach <c>approved</c>, <c>landed</c> or <c>closed</c> carrying a live nit, because none of
    /// them could have acquired one to begin with while outside <c>in-review</c>, and the one
    /// state where a nit can be raised is exactly the one state neither guard permits to be left
    /// while it is live. This is what makes a nit raised against a terminal (<c>closed</c>) card
    /// unreachable rather than a live, undispositioned observation with no transition left to
    /// refuse it — the failure-open gap a state-scoped raise was chosen to close at the source.
    /// </para>
    /// </summary>
    internal static CardNitRaiseOutcome RaiseNit(
        string cardsRoot, string filePath, CardComment comment, TimeSpan lockTimeout, string? changeName = null) =>
        WithLock(
            filePath,
            lockTimeout,
            heldLock => RaiseNitUnderExistingLock(heldLock, cardsRoot, comment, changeName),
            onTimedOut: timedOut => new CardNitRaiseOutcome.ToolFailure(timedOut.Message));

    /// <summary>
    /// The read-decide-write step of <see cref="RaiseNit"/>. Same structural lock precondition as
    /// every other <c>*UnderExistingLock</c> method on this type — the target is
    /// <see cref="CardLock.CardPath"/>, not a separately supplied <c>filePath</c>.
    /// </summary>
    internal static CardNitRaiseOutcome RaiseNitUnderExistingLock(
        CardLock heldLock, string cardsRoot, CardComment comment, string? changeName = null)
    {
        ArgumentNullException.ThrowIfNull(heldLock);
        var filePath = heldLock.CardPath;

        if (!File.Exists(filePath))
        {
            return new CardNitRaiseOutcome.CardNotFound(filePath);
        }

        var current = ReadCard(filePath);
        return current.Match<CardNitRaiseOutcome>(
            onSuccess: success =>
            {
                var card = success.Card;
                if (ReservedDerivedStateFieldKeyIn(card) is { } reservedKey)
                {
                    return RefuseAndRecord<CardNitRaiseOutcome, CardNitRaiseOutcome.HandEnteredDerivedState>(cardsRoot, card, filePath, changeName, comment.Author, comment.Timestamp,
                        new CardNitRaiseOutcome.HandEnteredDerivedState(reservedKey),
                        static reason => new CardNitRaiseOutcome.ToolFailure(reason));
                }

                if (!IsBlockCard(card))
                {
                    return RefuseAndRecord<CardNitRaiseOutcome, CardNitRaiseOutcome.NotABlockCard>(cardsRoot, card, filePath, changeName, comment.Author, comment.Timestamp,
                        new CardNitRaiseOutcome.NotABlockCard(card.Frontmatter.Kind),
                        static reason => new CardNitRaiseOutcome.ToolFailure(reason));
                }

                if (!RoundAgreesWithHistory(card, out var storedRound, out var expectedRound))
                {
                    return RefuseAndRecord<CardNitRaiseOutcome, CardNitRaiseOutcome.RoundDisagreesWithHistory>(cardsRoot, card, filePath, changeName, comment.Author, comment.Timestamp,
                        new CardNitRaiseOutcome.RoundDisagreesWithHistory(storedRound, expectedRound),
                        static reason => new CardNitRaiseOutcome.ToolFailure(reason));
                }

                if (!BlockFlowStateWireFormat.TryParse(card.Frontmatter.Status, out var currentState))
                {
                    return new CardNitRaiseOutcome.CardCorrupt(
                        filePath, $"unrecognised status: '{card.Frontmatter.Status}'. Recognised statuses: {BlockFlowStateWireFormat.RecognisedValues}.");
                }

                if (currentState != BlockFlowState.InReview)
                {
                    return RefuseAndRecord<CardNitRaiseOutcome, CardNitRaiseOutcome.NotUnderReview>(cardsRoot, card, filePath, changeName, comment.Author, comment.Timestamp,
                        new CardNitRaiseOutcome.NotUnderReview(currentState),
                        static reason => new CardNitRaiseOutcome.ToolFailure(reason));
                }

                var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return new CardNitRaiseOutcome.LayoutMismatch(layoutFailure!.Reason);
                }

                var updated = card with { Comments = [.. card.Comments, comment] };
                var writeResult = AtomicWrite(anchored, CardFileWriter.Serialize(updated));
                return writeResult.Match<CardNitRaiseOutcome>(
                    onSuccess: _ => new CardNitRaiseOutcome.Raised(updated),
                    onNotFound: notFound => new CardNitRaiseOutcome.CardNotFound(notFound.FilePath),
                    onAlreadyExists: alreadyExists => new CardNitRaiseOutcome.LayoutMismatch(
                        $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite."),
                    onLayoutMismatch: layoutMismatch => new CardNitRaiseOutcome.LayoutMismatch(layoutMismatch.Reason),
                    onCorrupt: corrupt => new CardNitRaiseOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason),
                    onToolFailure: toolFailure => new CardNitRaiseOutcome.ToolFailure(toolFailure.Reason),
                    onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
            },
            onFailure: failure =>
                new CardNitRaiseOutcome.CardCorrupt(filePath, failure.Reason));
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
    /// <em>history</em> kept from disagreeing with it by every code path that can set either:
    /// <see cref="WriteCard"/> takes a <see cref="NewCardFile"/>, which carries no
    /// <see cref="CardFile.Handovers"/> at all (a brand-new card has none), and this method sets
    /// <see cref="CardFrontmatter.Owner"/>, in this same write, to exactly the
    /// <see cref="CardHandover.To"/> of the entry it appends — §4 remediation R3, after a
    /// <see cref="CardFile"/>-shaped <see cref="WriteCard"/> made the disagreement writable despite
    /// this doc comment's earlier claim that no such path existed.
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
            return new CardWriteResult.NotFound(filePath);
        }

        var current = ReadCard(filePath);
        return current.Match<CardWriteResult>(
            onSuccess: success =>
            {
                // Same reserved-derived-state guard AppendCommentUnderExistingLock applies, for
                // the same reason (§10 block C) — checked first, ahead of the round check below.
                if (ReservedDerivedStateFieldKeyIn(success.Card) is { } reservedKey)
                {
                    return RefuseAndRecord<CardWriteResult, CardWriteResult.HandEnteredDerivedState>(cardsRoot, success.Card, filePath, changeName, actingRole, timestamp,
                        new CardWriteResult.HandEnteredDerivedState(reservedKey),
                        static reason => new CardWriteResult.ToolFailure(reason));
                }

                // Same bound AppendCommentUnderExistingLock applies, for the same reason: this
                // surface is generic over any card kind, but a block card's own round has to agree
                // with its history before this call is allowed to mutate it further.
                if (IsBlockCard(success.Card) && !RoundAgreesWithHistory(success.Card, out var storedRound, out var expectedRound))
                {
                    return RefuseAndRecord<CardWriteResult, CardWriteResult.RoundDisagreesWithHistory>(cardsRoot, success.Card, filePath, changeName, actingRole, timestamp,
                        new CardWriteResult.RoundDisagreesWithHistory(storedRound, expectedRound),
                        static reason => new CardWriteResult.ToolFailure(reason));
                }

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
                new CardWriteResult.Corrupt(filePath, failure.Reason));
    }

    /// <summary>
    /// Applies one legal <see cref="BlockFlowState"/> edge to the block card at
    /// <paramref name="filePath"/> (work-lifecycle "Block cards move through a defined flow", §5
    /// block C): reads the current card, decides whether <paramref name="transitionName"/> is
    /// legal from its current status by reading <see cref="BlockFlowTransitions.
    /// GenericallyInvocableFrom"/> (never a second hand-maintained list, and never <see cref="
    /// BlockFlowTransitions.AvailableFrom"/> — §8a remediation: that wider table carries one-door
    /// edges this generic applier must never resolve), and only if it is legal writes the new
    /// status, the possibly-newly-recorded <c>base</c>, the possibly-incremented <c>round</c> —
    /// driven by <see cref="BlockFlowTransitions.RoundIncrementingTransitionNames"/>, not a name
    /// literal — and an appended <see cref="CardBlockTransitionEntry"/> — all under the card's
    /// lock, so the refusal and the write share one read (obligation O-3: a refusal must prevent
    /// the side effect it refuses, not merely follow it).
    /// </summary>
    /// <param name="baseCommit">The commit to record as <c>base</c> if the card does not already
    /// have one recorded. Ignored (but still checked for a mismatch — see
    /// <see cref="CardBlockTransitionOutcome.BaseImmutable"/>) once a <c>base</c> is already on the
    /// card, since work-lifecycle requires it never change across rounds.</param>
    internal static CardBlockTransitionOutcome ApplyBlockTransition(
        string cardsRoot, string filePath, string transitionName, CardOwner actingRole, DateTimeOffset timestamp, string? baseCommit, TimeSpan lockTimeout, string? changeName = null) =>
        WithLock(
            filePath,
            lockTimeout,
            heldLock => ApplyBlockTransitionUnderExistingLock(heldLock, cardsRoot, transitionName, actingRole, timestamp, baseCommit, changeName),
            onTimedOut: timedOut => new CardBlockTransitionOutcome.ToolFailure(timedOut.Message));

    /// <summary>
    /// The read-decide-write step of <see cref="ApplyBlockTransition"/>. Same structural lock
    /// precondition as <see cref="AppendCommentUnderExistingLock"/> and
    /// <see cref="TransferOwnershipUnderExistingLock"/> (O-2's fix applied to every method on this
    /// surface with the same shape) — the target is <see cref="CardLock.CardPath"/>, not a
    /// separately supplied <c>filePath</c>.
    /// </summary>
    internal static CardBlockTransitionOutcome ApplyBlockTransitionUnderExistingLock(
        CardLock heldLock, string cardsRoot, string transitionName, CardOwner actingRole, DateTimeOffset timestamp, string? baseCommit, string? changeName = null)
    {
        ArgumentNullException.ThrowIfNull(heldLock);
        var filePath = heldLock.CardPath;

        if (!File.Exists(filePath))
        {
            return new CardBlockTransitionOutcome.CardNotFound(filePath);
        }

        var current = ReadCard(filePath);
        return current.Match<CardBlockTransitionOutcome>(
            onSuccess: success =>
            {
                var card = success.Card;
                if (ReservedDerivedStateFieldKeyIn(card) is { } reservedKey)
                {
                    return RefuseAndRecord(cardsRoot, card, filePath, changeName, actingRole, timestamp, new CardBlockTransitionOutcome.HandEnteredDerivedState(reservedKey));
                }

                if (!IsBlockCard(card))
                {
                    return RefuseAndRecord(cardsRoot, card, filePath, changeName, actingRole, timestamp, new CardBlockTransitionOutcome.NotABlockCard(card.Frontmatter.Kind));
                }

                if (!RoundAgreesWithHistory(card, out var storedRound, out var expectedRound))
                {
                    return RefuseAndRecord(cardsRoot, card, filePath, changeName, actingRole, timestamp, new CardBlockTransitionOutcome.RoundDisagreesWithHistory(storedRound, expectedRound));
                }

                if (!BlockFlowStateWireFormat.TryParse(card.Frontmatter.Status, out var currentState))
                {
                    return new CardBlockTransitionOutcome.CardCorrupt(
                        filePath, $"unrecognised status: '{card.Frontmatter.Status}'. Recognised statuses: {BlockFlowStateWireFormat.RecognisedValues}.");
                }

                var available = BlockFlowTransitions.GenericallyInvocableFrom(currentState);
                var transition = available.FirstOrDefault(candidate => string.Equals(candidate.Name, transitionName, StringComparison.Ordinal));
                if (transition is null)
                {
                    return RefuseAndRecord(cardsRoot, card, filePath, changeName, actingRole, timestamp, new CardBlockTransitionOutcome.UndefinedTransition(currentState, available));
                }

                // review-certification: "A nit SHALL cease to be live only through one of these
                // three dispositions. It SHALL NOT lapse by neglect." (§8 block B). §8 remediation
                // blocker 2: the original guard checked only 'transition.From == InReview', so
                // 'approved's own exit ('land') and 'landed's exit ('close') carried no nit check
                // at all — a block holding a live undispositioned nit could reach 'landed', even
                // 'closed', with nothing refusing. A per-state
                // 'From ==' list has now been wrong once already (this one); rather than extend it
                // to a second hard-coded state, the check applies to every transition this table
                // permits, regardless of origin — a card with a live undispositioned nit simply
                // does not move until the nit is dispositioned. 'approve' carries its own copy of
                // this check (CardApprovalOutcome.UndispositionedNits) since it never reaches this
                // table (approve-via-transition-refused), and 'fix-before-land' never reaches this
                // method at all (refused at parse) — it is DispositionNitUnderLocks applying its
                // own edge, after the very disposition that clears the nit this check would
                // otherwise catch.
                var liveNitIds = CardCommentRouting.LiveUndispositionedNitIds(card.Comments);
                if (liveNitIds.Count > 0)
                {
                    return RefuseAndRecord(cardsRoot, card, filePath, changeName, actingRole, timestamp, new CardBlockTransitionOutcome.UndispositionedNits(liveNitIds));
                }

                // process-enforcement: "A verdict cannot leave threads unanswered" (§9 block B).
                // `changes-requested` is the only edge this generic applier can ever resolve from
                // `in-review` (BlockFlowTransitions.GenericallyInvocableFrom never offers `approve`
                // or `fix-before-land` from that state) — the guard reads transition.From rather
                // than a name literal so it stays correct if that ever changes.
                if (transition.From == BlockFlowState.InReview)
                {
                    var unresolvedThreadIds = CardCommentRouting.LiveThreadIdsAddressedTo(card.Comments, actingRole);
                    if (unresolvedThreadIds.Count > 0)
                    {
                        return RefuseAndRecord(cardsRoot, card, filePath, changeName, actingRole, timestamp, new CardBlockTransitionOutcome.UnresolvedThreadsAddressedToActor(actingRole, unresolvedThreadIds));
                    }
                }

                var recordedBase = card.BlockFields.Base;
                if (recordedBase is not null && baseCommit is not null && !string.Equals(recordedBase, baseCommit, StringComparison.Ordinal))
                {
                    return RefuseAndRecord(cardsRoot, card, filePath, changeName, actingRole, timestamp, new CardBlockTransitionOutcome.BaseImmutable(recordedBase, baseCommit));
                }

                var effectiveBase = recordedBase ?? baseCommit;
                if (transition.To == BlockFlowState.Briefed && effectiveBase is null)
                {
                    return RefuseAndRecord(cardsRoot, card, filePath, changeName, actingRole, timestamp, new CardBlockTransitionOutcome.BaseNotRecorded());
                }

                // process-enforcement: "Work cannot proceed past a stop-and-ask" (§9 block D, 9.8).
                // Exempts BlockFlowTransitions.RoundIncrementingTransitionNames — changes-requested
                // is the only one this generic applier itself resolves — because a back-edge returns
                // the card to earlier work rather than advancing it past the blocker (Architect
                // ruling, §9 block D DEVLOG post).
                if (!BlockFlowTransitions.RoundIncrementingTransitionNames.Contains(transition.Name, StringComparer.Ordinal)
                    && FindBlockingOpenProductOwnerQuestion(cardsRoot, card) is { } blockingQuestion)
                {
                    return RefuseAndRecord(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardBlockTransitionOutcome.BlockedByOpenProductOwnerQuestion(blockingQuestion.QuestionId, blockingQuestion.Title));
                }

                var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return new CardBlockTransitionOutcome.LayoutMismatch(layoutFailure!.Reason);
                }

                // "changes-requested" is work-lifecycle's own named increment; any other
                // transition that lands the card on Briefed for the first time (the initial
                // "brief") starts the round at 1 rather than leaving it unset. Unset reads as round
                // 1 (the same default RecordApprovalUnderExistingLock and
                // RecordGateResultUnderExistingLock apply, and the one RoundAgreesWithHistory
                // assumes) — §8a block D found this arm and DispositionNitUnderLocks's own
                // fix-before-land arm disagreeing on the unset default (`?? 0` here, `?? 1` at
                // finding-recurred's site), which would land round 1 off a card seeded straight
                // into in-review with no round set, disagreeing with its own one-entry transition
                // history the moment 8a.17's check was added. Fixed to agree.
                // §8a remediation (supervisor finding: "the generic applier is a second source of
                // truth for the round increment"): reads BlockFlowTransitions.
                // RoundIncrementingTransitionNames — the same table RoundAgreesWithHistory's own
                // count reads — rather than the "changes-requested" literal this arm used to test
                // for, which is the only edge GenericallyInvocableFrom can ever hand this method
                // anyway, but a second name added to that table later must count here without a
                // matching edit to this arm, not merely without one to the checker.
                var round = card.BlockFields.Round;
                if (BlockFlowTransitions.RoundIncrementingTransitionNames.Contains(transition.Name, StringComparer.Ordinal))
                {
                    round = (round ?? 1) + 1;
                }
                else if (transition.To == BlockFlowState.Briefed && round is null)
                {
                    round = 1;
                }

                var entry = new CardBlockTransitionEntry(actingRole, transition.Name, transition.From, transition.To, timestamp, []);
                var updated = card with
                {
                    Frontmatter = card.Frontmatter with { Status = transition.To.ToWireString(), Updated = timestamp },
                    BlockFields = card.BlockFields with { Base = effectiveBase, Round = round },
                    Transitions = [.. card.Transitions, entry],
                };

                var writeResult = AtomicWrite(anchored, CardFileWriter.Serialize(updated));
                return writeResult.Match<CardBlockTransitionOutcome>(
                    onSuccess: _ => new CardBlockTransitionOutcome.Applied(updated, transition),
                    onNotFound: notFound => new CardBlockTransitionOutcome.CardNotFound(notFound.FilePath),
                    onAlreadyExists: alreadyExists => new CardBlockTransitionOutcome.LayoutMismatch(
                        $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite."),
                    onLayoutMismatch: layoutMismatch => new CardBlockTransitionOutcome.LayoutMismatch(layoutMismatch.Reason),
                    onCorrupt: corrupt => new CardBlockTransitionOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason),
                    onToolFailure: toolFailure => new CardBlockTransitionOutcome.ToolFailure(toolFailure.Reason),
                    onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
            },
            onFailure: failure =>
                        new CardBlockTransitionOutcome.CardCorrupt(filePath, failure.Reason));
    }

    /// <summary>
    /// process-enforcement: "A refusal SHALL be recorded against the card with the acting role and
    /// the time" (§9 block A) — appends a <see cref="CardRefusalEntry"/> built from
    /// <paramref name="refusal"/>'s own <see cref="ICardRefusalReason.RefusingRule"/>/
    /// <see cref="ICardRefusalReason.Remedy"/> to <paramref name="card"/> and writes it back under
    /// the lock already held by the caller (<see cref="ApplyBlockTransitionUnderExistingLock"/>),
    /// so the record line lands under the same lock as the read that decided to refuse — then
    /// returns <paramref name="refusal"/> unchanged so the caller's decision is what gets reported.
    ///
    /// <para>
    /// <b>Two binding rulings (§9 architect ruling) shape this.</b> Only a refusal that resolved a
    /// card at a legally anchored path has anything to record against — if
    /// <paramref name="filePath"/> does not anchor under <paramref name="cardsRoot"/> for
    /// <paramref name="card"/>'s own scope, this reports <paramref name="refusal"/> without a
    /// write, the same disposition a genuine <see cref="CardBlockTransitionOutcome.LayoutMismatch"/>
    /// already gets. And a refusal must never be reported until its record line is durable: if the
    /// write itself fails, this returns a tool-failure instead of the refusal (ADR-0001:
    /// enforcement unavailable is a tool-failure, never a quieter refusal).
    /// </para>
    /// </summary>
    private static CardBlockTransitionOutcome RefuseAndRecord<TRefusal>(
        string cardsRoot, CardFile card, string filePath, string? changeName, CardOwner actingRole, DateTimeOffset timestamp, TRefusal refusal)
        where TRefusal : CardBlockTransitionOutcome, ICardRefusalReason
    {
        var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, card.Frontmatter.Scope, changeName, out _);
        if (anchored is null)
        {
            return refusal;
        }

        var entry = new CardRefusalEntry(actingRole, refusal.RefusingRule, refusal.Remedy, timestamp, []);
        var updated = card with { Refusals = [.. card.Refusals, entry] };
        var writeResult = AtomicWrite(anchored, CardFileWriter.Serialize(updated));
        return writeResult.Match<CardBlockTransitionOutcome>(
            onSuccess: _ => refusal,
            onNotFound: notFound => new CardBlockTransitionOutcome.ToolFailure($"could not record refusal against '{notFound.FilePath}': card not found."),
            onAlreadyExists: alreadyExists => new CardBlockTransitionOutcome.ToolFailure($"could not record refusal against '{alreadyExists.FilePath}': unexpected write conflict."),
            onLayoutMismatch: static _ => throw new InvalidOperationException("unreachable: the anchored path already resolved above."),
            onCorrupt: corrupt => new CardBlockTransitionOutcome.ToolFailure($"could not record refusal: {corrupt.Reason}"),
            onToolFailure: toolFailure => new CardBlockTransitionOutcome.ToolFailure(toolFailure.Reason),
            onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: refusal recording never touches round/history."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
    }

    /// <summary>
    /// Generalised sibling of the <see cref="CardBlockTransitionOutcome"/>-specific
    /// <see cref="RefuseAndRecord{TRefusal}(string, CardFile, string, string?, CardOwner,
    /// DateTimeOffset, TRefusal)"/> above, parameterised over the outcome union too so §9 block A2's
    /// five register/rules families can share one recording path instead of five copies of the same
    /// eleven-line body. Same contract, same two rulings (§9 architect ruling): only a refusal that
    /// resolved a real, anchored card is recorded — an anchor failure returns <paramref
    /// name="refusal"/> unchanged, with no write — and a refusal is never reported ahead of its
    /// record line landing: a failed write is mapped through <paramref name="onToolFailure"/>
    /// instead of the refusal (ADR-0001: enforcement unavailable is a tool-failure, never a quieter
    /// refusal).
    /// </summary>
    private static TOutcome RefuseAndRecord<TOutcome, TRefusal>(
        string cardsRoot, CardFile card, string filePath, string? changeName, CardOwner actingRole, DateTimeOffset timestamp, TRefusal refusal, Func<string, TOutcome> onToolFailure)
        where TRefusal : TOutcome, ICardRefusalReason
    {
        var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, card.Frontmatter.Scope, changeName, out _);
        if (anchored is null)
        {
            return refusal;
        }

        var entry = new CardRefusalEntry(actingRole, refusal.RefusingRule, refusal.Remedy, timestamp, []);
        var updated = card with { Refusals = [.. card.Refusals, entry] };
        var writeResult = AtomicWrite(anchored, CardFileWriter.Serialize(updated));
        return writeResult.Match<TOutcome>(
            onSuccess: _ => refusal,
            onNotFound: notFound => onToolFailure($"could not record refusal against '{notFound.FilePath}': card not found."),
            onAlreadyExists: alreadyExists => onToolFailure($"could not record refusal against '{alreadyExists.FilePath}': unexpected write conflict."),
            onLayoutMismatch: static _ => throw new InvalidOperationException("unreachable: the anchored path already resolved above."),
            onCorrupt: corrupt => onToolFailure($"could not record refusal: {corrupt.Reason}"),
            onToolFailure: toolFailure => onToolFailure(toolFailure.Reason),
            onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: refusal recording never touches round/history."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
    }

    /// <summary>
    /// <see langword="true"/> for the two roles review-certification's "Approval is role-bounded"
    /// permits to record an <c>approve</c> verdict — <c>reviewer</c> and <c>supervisor</c>. §8
    /// block A ships this half of 8.13's enforcement (approval's own role check).
    /// </summary>
    internal static bool IsApprovingRole(CardOwner role) => role.Match(
        onArchitect: static () => false,
        onWorker: static () => false,
        onReviewer: static () => true,
        onSupervisor: static () => true,
        onProductOwner: static () => false);

    /// <summary>
    /// <see langword="true"/> only for <see cref="CardOwner.ProductOwner"/> — work-lifecycle:
    /// "Remediation beyond the second round requires recorded authorisation" — "The authorisation
    /// SHALL be part of the record, not a permission granted out of band" (§8a block C). The one
    /// permission in the system that exists to be granted from outside the agents; unlike
    /// <see cref="IsApprovingRole"/>'s two roles, exactly one may ever satisfy this.
    /// </summary>
    internal static bool IsAuthorisingRole(CardOwner role) => role.Match(
        onArchitect: static () => false,
        onWorker: static () => false,
        onReviewer: static () => false,
        onSupervisor: static () => false,
        onProductOwner: static () => true);

    /// <summary>
    /// The one derivation both <see cref="RecordSectionVerdictUnderExistingLock"/>'s bound check
    /// and <see cref="RecordSectionAuthorisationUnderExistingLock"/>'s own precondition read from —
    /// so the two can never drift on what "at the bound" or "unspent" means (§8a block C
    /// remediation, Architect ruling). Nothing here is stored: both counts are read straight off
    /// <paramref name="fields"/> and recomputed on every call.
    /// </summary>
    /// <param name="fields">The section card's own fields, as currently on disk (or about to be —
    /// callers read this fresh under the card's lock before deciding).</param>
    /// <returns><c>PriorRequestChanges</c>: the count of <c>request-changes</c> verdicts already
    /// retained (work-lifecycle: "an approve is not a remediation round" — an <c>approve</c> never
    /// contributes). <c>UnspentAuthorisations</c>: <c>Authorisations.Length</c> minus every
    /// authorisation already spent by a <c>request-changes</c> verdict past the first two
    /// (<c>max(PriorRequestChanges - 2, 0)</c>) — consumed FIFO against that sequence, one per
    /// over-the-bound verdict, in the order those verdicts were themselves recorded.</returns>
    private static (int PriorRequestChanges, int UnspentAuthorisations) SectionRemediationBoundState(SectionCardFields fields)
    {
        var priorRequestChanges = fields.Verdicts.Count(static v => v.Verdict == SectionVerdict.RequestChanges);
        var alreadySpentAuthorisations = Math.Max(priorRequestChanges - 2, 0);
        var unspentAuthorisations = fields.Authorisations.Length - alreadySpentAuthorisations;
        return (priorRequestChanges, unspentAuthorisations);
    }

    /// <summary>
    /// Records a binary approval on the block card at <paramref name="filePath"/>
    /// (review-certification: "Approve is binary and certifies one state" / "Certification
    /// enumerates its claims", §8 block A) — the one door to <see cref="BlockFlowState.Approved"/>:
    /// stamps <c>reviewed_state</c>, appends the enumerated claim/limit sequence, and appends the
    /// <c>approve</c> <see cref="CardBlockTransitionEntry"/>, all under the card's lock, in the
    /// same write (Architect ruling: "the certification is stamped in the same write as the
    /// transition"). <see cref="ApplyBlockTransition"/>'s own <c>approve</c> edge is never reached
    /// through this call — <c>block transition</c>'s CLI parse arm refuses the name outright before
    /// any card is ever touched (§8 block A brief item 1), so this is the only path that can ever
    /// land a block on <c>approved</c>.
    /// </summary>
    /// <param name="reviewedState">The exact state this approval certifies, including any
    /// uncommitted working-tree content it covers — caller-supplied text, verified against nothing
    /// (§8's own standing fact: the tool does not shell out, does not resolve a SHA). Never empty or
    /// whitespace-only — checked by <see cref="Callboard.Cli.CommandParser"/>'s <c>block approve</c>
    /// parse arm before this is ever called, since that is argv-decidable.</param>
    /// <param name="claimTexts">The claims this approval enumerates, in the order given. May be
    /// empty only when <paramref name="limitTexts"/> is not — review-certification: "no claims and
    /// no limits" is refused, not "no claims" alone (§8 block A brief item 5) — checked by the CLI
    /// parse arm for the same argv-decidable reason as <paramref name="reviewedState"/>.</param>
    /// <param name="limitTexts">The limits this approval states, in the order given.</param>
    internal static CardApprovalOutcome RecordApproval(
        string cardsRoot, string filePath, string reviewedState, IReadOnlyList<string> claimTexts, IReadOnlyList<string> limitTexts,
        CardOwner actingRole, DateTimeOffset timestamp, TimeSpan lockTimeout, string? changeName = null) =>
        WithLock(
            filePath,
            lockTimeout,
            heldLock => RecordApprovalUnderExistingLock(heldLock, cardsRoot, reviewedState, claimTexts, limitTexts, actingRole, timestamp, changeName),
            onTimedOut: timedOut => new CardApprovalOutcome.ToolFailure(timedOut.Message));

    /// <summary>
    /// The read-decide-write step of <see cref="RecordApproval"/>. Same structural lock precondition
    /// as every other <c>*UnderExistingLock</c> method on this type — the target is
    /// <see cref="CardLock.CardPath"/>, not a separately supplied <c>filePath</c>. The role check
    /// runs immediately after a successful <see cref="ReadCard"/>, not ahead of
    /// <see cref="File.Exists(string)"/> the way <see cref="CompactRules"/> and
    /// <see cref="DispositionNit"/> check role (§9 block B reviewer/architect ruling, overruling this
    /// method's own first pass): neither of those methods' reasons for checking early apply here —
    /// this method's one lock is already held regardless of where the check sits, and
    /// <see cref="IsApprovingRole"/> is a pure function of <see cref="CardOwner"/> with no card
    /// dependency, so nothing is skipped by moving it here except a read every other case in this
    /// method already pays. Recording matters more than the reorder: a pattern of wrong-role approval
    /// attempts is exactly the pattern process-enforcement's "so that a pattern of refusals is itself
    /// visible" exists to catch. Everything else is validated — kind, current state, layout — before
    /// the one <see cref="AtomicWrite"/> call that lands claims, limits, <c>reviewed_state</c> and the
    /// transition together.
    /// </summary>
    internal static CardApprovalOutcome RecordApprovalUnderExistingLock(
        CardLock heldLock, string cardsRoot, string reviewedState, IReadOnlyList<string> claimTexts, IReadOnlyList<string> limitTexts,
        CardOwner actingRole, DateTimeOffset timestamp, string? changeName = null)
    {
        ArgumentNullException.ThrowIfNull(heldLock);
        var filePath = heldLock.CardPath;

        if (!File.Exists(filePath))
        {
            return new CardApprovalOutcome.CardNotFound(filePath);
        }

        var current = ReadCard(filePath);
        return current.Match<CardApprovalOutcome>(
            onSuccess: success =>
            {
                var card = success.Card;

                // review-certification: "Approval is role-bounded" (§9 block B reviewer/architect
                // ruling, overruling this block's own first pass). Checked here, immediately after
                // a successful ReadCard, rather than ahead of File.Exists the way CompactRules and
                // DispositionNit check role: neither of those methods' reasons for checking early
                // apply here — this method's one lock is already held regardless of where the check
                // sits, and IsApprovingRole is a pure function of CardOwner with no card dependency,
                // so nothing is skipped by moving it here except a read every other case in this
                // method already pays. Recording matters more than the reorder: a pattern of
                // wrong-role approval attempts is exactly the pattern process-enforcement's "so that
                // a pattern of refusals is itself visible" exists to catch.
                if (ReservedDerivedStateFieldKeyIn(card) is { } reservedKey)
                {
                    return RefuseAndRecord<CardApprovalOutcome, CardApprovalOutcome.HandEnteredDerivedState>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardApprovalOutcome.HandEnteredDerivedState(reservedKey),
                        static reason => new CardApprovalOutcome.ToolFailure(reason));
                }

                if (!IsApprovingRole(actingRole))
                {
                    return RefuseAndRecord<CardApprovalOutcome, CardApprovalOutcome.RoleNotPermitted>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardApprovalOutcome.RoleNotPermitted(actingRole),
                        static reason => new CardApprovalOutcome.ToolFailure(reason));
                }

                if (!IsBlockCard(card))
                {
                    return RefuseAndRecord<CardApprovalOutcome, CardApprovalOutcome.NotABlockCard>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardApprovalOutcome.NotABlockCard(card.Frontmatter.Kind),
                        static reason => new CardApprovalOutcome.ToolFailure(reason));
                }

                if (!RoundAgreesWithHistory(card, out var storedRound, out var expectedRound))
                {
                    return RefuseAndRecord<CardApprovalOutcome, CardApprovalOutcome.RoundDisagreesWithHistory>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardApprovalOutcome.RoundDisagreesWithHistory(storedRound, expectedRound),
                        static reason => new CardApprovalOutcome.ToolFailure(reason));
                }

                if (!BlockFlowStateWireFormat.TryParse(card.Frontmatter.Status, out var currentState))
                {
                    return new CardApprovalOutcome.CardCorrupt(
                        filePath, $"unrecognised status: '{card.Frontmatter.Status}'. Recognised statuses: {BlockFlowStateWireFormat.RecognisedValues}.");
                }

                var available = BlockFlowTransitions.AvailableFrom(currentState);
                var transition = available.FirstOrDefault(candidate => string.Equals(candidate.Name, "approve", StringComparison.Ordinal));
                if (transition is null)
                {
                    return RefuseAndRecord<CardApprovalOutcome, CardApprovalOutcome.UndefinedTransition>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardApprovalOutcome.UndefinedTransition(currentState, available),
                        static reason => new CardApprovalOutcome.ToolFailure(reason));
                }

                // review-certification: "Undispositioned nits block the verdict" (§8 block B) — an
                // approve is one of the transitions that moves a block out of in-review, so it is
                // bound by the same requirement ApplyBlockTransitionUnderExistingLock's own
                // changes-requested arm checks below.
                var liveNitIds = CardCommentRouting.LiveUndispositionedNitIds(card.Comments);
                if (liveNitIds.Count > 0)
                {
                    return RefuseAndRecord<CardApprovalOutcome, CardApprovalOutcome.UndispositionedNits>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardApprovalOutcome.UndispositionedNits(liveNitIds),
                        static reason => new CardApprovalOutcome.ToolFailure(reason));
                }

                // process-enforcement: "A verdict cannot leave threads unanswered" (§9 block B) —
                // `approve` is always a door out of `in-review` (CardApprovalOutcome.UndefinedTransition's
                // own doc comment: "approve is only a legal edge from in-review"), so this applies
                // unconditionally here, unlike ApplyBlockTransitionUnderExistingLock's own copy of
                // this check, which has to gate on transition.From because it also handles edges
                // that do not leave in-review.
                var unresolvedThreadIds = CardCommentRouting.LiveThreadIdsAddressedTo(card.Comments, actingRole);
                if (unresolvedThreadIds.Count > 0)
                {
                    return RefuseAndRecord<CardApprovalOutcome, CardApprovalOutcome.UnresolvedThreadsAddressedToActor>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardApprovalOutcome.UnresolvedThreadsAddressedToActor(actingRole, unresolvedThreadIds),
                        static reason => new CardApprovalOutcome.ToolFailure(reason));
                }

                // process-enforcement: "Work cannot proceed past a stop-and-ask" (§9 block D, 9.8).
                // 'approve' is always a forward transition — there is no back-edge on this surface
                // to exempt the way ApplyBlockTransitionUnderExistingLock exempts changes-requested.
                if (FindBlockingOpenProductOwnerQuestion(cardsRoot, card) is { } blockingQuestion)
                {
                    return RefuseAndRecord<CardApprovalOutcome, CardApprovalOutcome.BlockedByOpenProductOwnerQuestion>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardApprovalOutcome.BlockedByOpenProductOwnerQuestion(blockingQuestion.QuestionId, blockingQuestion.Title),
                        static reason => new CardApprovalOutcome.ToolFailure(reason));
                }

                var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return new CardApprovalOutcome.LayoutMismatch(layoutFailure!.Reason);
                }

                // The block's round at the moment this approval is recorded — the same "unset
                // reads as round 1" default ApplyBlockTransitionUnderExistingLock and
                // RecordGateResultUnderExistingLock both already apply, so a claim/limit recorded
                // against a block never yet briefed still reads back as round 1.
                var currentRound = card.BlockFields.Round ?? 1;
                var claims = claimTexts
                    .Select(text => new CardApprovalClaim(Guid.NewGuid().ToString("N"), currentRound, text, []))
                    .ToImmutableArray();
                var limits = limitTexts
                    .Select(text => new CardApprovalLimit(currentRound, text, []))
                    .ToImmutableArray();

                var entry = new CardBlockTransitionEntry(actingRole, transition.Name, transition.From, transition.To, timestamp, []);
                var updated = card with
                {
                    Frontmatter = card.Frontmatter with { Status = transition.To.ToWireString(), Updated = timestamp },
                    BlockFields = card.BlockFields with { ReviewedState = reviewedState },
                    Transitions = [.. card.Transitions, entry],
                    Claims = [.. card.Claims, .. claims],
                    Limits = [.. card.Limits, .. limits],
                };

                var writeResult = AtomicWrite(anchored, CardFileWriter.Serialize(updated));
                return writeResult.Match<CardApprovalOutcome>(
                    onSuccess: _ => new CardApprovalOutcome.Approved(updated, claims, limits),
                    onNotFound: notFound => new CardApprovalOutcome.CardNotFound(notFound.FilePath),
                    onAlreadyExists: alreadyExists => new CardApprovalOutcome.LayoutMismatch(
                        $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite."),
                    onLayoutMismatch: layoutMismatch => new CardApprovalOutcome.LayoutMismatch(layoutMismatch.Reason),
                    onCorrupt: corrupt => new CardApprovalOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason),
                    onToolFailure: toolFailure => new CardApprovalOutcome.ToolFailure(toolFailure.Reason),
                    onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
            },
            onFailure: failure =>
                new CardApprovalOutcome.CardCorrupt(filePath, failure.Reason));
    }

    /// <summary>
    /// review-certification: "Every nit SHALL receive a disposition chosen by the architect" — the
    /// Architect's reading of that sentence as role-bounding the verb (§8 block B brief item 6, the
    /// reading most open to challenge).
    /// </summary>
    internal static bool IsArchitectRole(CardOwner role) => role.Match(
        onArchitect: static () => true,
        onWorker: static () => false,
        onReviewer: static () => false,
        onSupervisor: static () => false,
        onProductOwner: static () => false);

    /// <summary>
    /// Records a disposition on the nit comment at <paramref name="nitFilePath"/>/<paramref
    /// name="nitId"/> (review-certification: "Nits carry a disposition", §8 block B): appends a
    /// disposition <see cref="CardComment"/> naming the nit it resolves — never a mutation of the
    /// nit comment itself, the append-only idiom <see cref="CardComment.Resolves"/>'s own doc
    /// comment already establishes for exactly this shape (Architect ruling, §8 block B brief item
    /// 1). For <see cref="NitDisposition.Defer"/>/<see cref="NitDisposition.Decline"/>,
    /// <paramref name="raiseRequest"/> is required and a second card (an <c>obligation</c> or a
    /// <c>decision</c>) is written alongside the disposition comment, in the same call.
    ///
    /// <para>
    /// <b><c>fix-before-land</c> applies its flow edge exactly once per round, and applies it
    /// whenever the round carries a fix-before-land nit (§8 block B remediation).</b> The edge is
    /// two-sided: at most once per round (three nits dispositioned <c>fix-before-land</c> in one
    /// sitting are one return to <c>briefed</c>, not three — once the first such disposition has
    /// already moved the card to <see cref="BlockFlowState.Briefed"/>, the edge is no longer
    /// available from the card's current state, and this method does not "helpfully" re-apply it),
    /// and at least once when any fix-before-land nit was dispositioned this round — even when the
    /// disposition that leaves no nit undispositioned is itself a <c>defer</c> or a <c>decline</c>.
    /// Whether the edge applies turns on the round as a whole (<see cref="CardCommentRouting.
    /// HasFixBeforeLandDisposition"/>, scoped to comments since the round began), not on whether
    /// <em>this call's</em> disposition happens to be <c>fix-before-land</c>. The disposition itself
    /// is <em>always</em> recorded regardless (review-certification: "SHALL NOT lapse by neglect" —
    /// a nit is not left undispositioned merely because the board already moved on) — see
    /// <see cref="CardNitDispositionOutcome.Dispositioned.Transitioned"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Lock order: the block card first, then the raised card (§8 block B brief, "single lock
    /// order").</b> Unlike <see cref="RecordFinding"/>'s acquire-probe-release-retry dance (needed
    /// because two concurrent <c>finding record</c> calls can each spell the identical <em>pair</em>
    /// of pre-existing files in a different order), a fixed order is sound here without that
    /// machinery: <paramref name="raiseRequest"/>'s path is always freshly allocated by this call
    /// (<see cref="AllocateIdentity"/>) and never shared with any other invocation, so no two calls
    /// to this method can ever contend over the same <em>pair</em> of paths in reverse order — the
    /// only resource two calls can ever share is the block card itself, and a fixed order over a
    /// singly-shared resource cannot cycle. The raised card is written first, so a failure on the
    /// second write (the block card) has something to roll back — the same ordering
    /// <see cref="RecordFinding"/> uses for its own two-card write, for the same reason.
    /// </para>
    ///
    /// <para>
    /// <b>Failure guarantee, stated honestly, not claimed as atomic.</b> If the raised card's write
    /// succeeds and the block card's own write then fails, <see cref="RollbackRaisedNitCard"/>
    /// deletes the raised card <em>only if its content still matches exactly what this call wrote</em>
    /// (compare-then-delete, never delete-by-path — the same discipline <see cref="RecordFinding"/>'s
    /// own rollback applies). Both cards' locks are held for this call's whole duration, so no
    /// concurrent writer can have legitimately taken over the raised card's path in between; the
    /// guard costs nothing regardless. If the rollback delete itself cannot complete (a filesystem
    /// error), the caller already has a failure to report and this is not the place to escalate a
    /// cleanup problem into a second, different one — the orphaned raised card is a residue of that
    /// rare double failure, not silently hidden as success.
    /// </para>
    /// </summary>
    internal static CardNitDispositionOutcome DispositionNit(
        string cardsRoot,
        string nitFilePath,
        string nitId,
        NitDisposition disposition,
        string body,
        CardOwner actingRole,
        DateTimeOffset timestamp,
        TimeSpan lockTimeout,
        string? changeName,
        NitDispositionRaiseRequest? raiseRequest)
    {
        if (!IsArchitectRole(actingRole))
        {
            return new CardNitDispositionOutcome.RoleNotPermitted(actingRole);
        }

        string? raisedId = null;
        if (raiseRequest is not null)
        {
            var (allocatedId, allocationFailure) = AllocateIdentity(cardsRoot, raiseRequest.Kind, lockTimeout);
            if (allocationFailure is not null)
            {
                return new CardNitDispositionOutcome.ToolFailure(allocationFailure);
            }

            raisedId = allocatedId;

            // The raised card's directory has to exist before its lock file (created beside the
            // target) can be — the same reason WriteCard/RecordFinding create it ahead of any lock
            // acquisition.
            var raisedDirectory = Path.GetDirectoryName(raiseRequest.FilePath);
            if (string.IsNullOrEmpty(raisedDirectory))
            {
                return new CardNitDispositionOutcome.RaisedCardLayoutMismatch($"'{raiseRequest.FilePath}' has no containing directory to write into.");
            }

            Directory.CreateDirectory(raisedDirectory);
        }

        var blockLockResult = CardLock.Acquire(nitFilePath, lockTimeout);
        return blockLockResult.Match<CardNitDispositionOutcome>(
            onAcquired: blockAcquired =>
            {
                using (blockAcquired.Lock)
                {
                    if (raiseRequest is null)
                    {
                        return DispositionNitUnderLocks(blockAcquired.Lock, cardsRoot, nitId, disposition, body, actingRole, timestamp, changeName, raiseRequest: null, raisedId: null);
                    }

                    var raisedLockResult = CardLock.Acquire(raiseRequest.FilePath, lockTimeout);
                    return raisedLockResult.Match<CardNitDispositionOutcome>(
                        onAcquired: raisedAcquired =>
                        {
                            using (raisedAcquired.Lock)
                            {
                                return DispositionNitUnderLocks(blockAcquired.Lock, cardsRoot, nitId, disposition, body, actingRole, timestamp, changeName, raiseRequest, raisedId);
                            }
                        },
                        onTimedOut: timedOut => new CardNitDispositionOutcome.ToolFailure(timedOut.Message));
                }
            },
            onTimedOut: timedOut => new CardNitDispositionOutcome.ToolFailure(timedOut.Message));
    }

    /// <summary>
    /// The locked step of <see cref="DispositionNit"/> — the block card's lock (and, for
    /// <c>defer</c>/<c>decline</c>, the raised card's lock) are already held by the time this runs.
    /// </summary>
    private static CardNitDispositionOutcome DispositionNitUnderLocks(
        CardLock blockLock,
        string cardsRoot,
        string nitId,
        NitDisposition disposition,
        string body,
        CardOwner actingRole,
        DateTimeOffset timestamp,
        string? changeName,
        NitDispositionRaiseRequest? raiseRequest,
        string? raisedId)
    {
        var nitFilePath = blockLock.CardPath;
        if (!File.Exists(nitFilePath))
        {
            return new CardNitDispositionOutcome.CardNotFound(nitFilePath);
        }

        var current = ReadCard(nitFilePath);
        return current.Match<CardNitDispositionOutcome>(
            onSuccess: success =>
            {
                var card = success.Card;
                if (ReservedDerivedStateFieldKeyIn(card) is { } reservedKey)
                {
                    return RefuseAndRecord<CardNitDispositionOutcome, CardNitDispositionOutcome.HandEnteredDerivedState>(cardsRoot, card, nitFilePath, changeName, actingRole, timestamp,
                        new CardNitDispositionOutcome.HandEnteredDerivedState(reservedKey),
                        static reason => new CardNitDispositionOutcome.ToolFailure(reason));
                }

                if (!IsBlockCard(card))
                {
                    return RefuseAndRecord<CardNitDispositionOutcome, CardNitDispositionOutcome.NotABlockCard>(cardsRoot, card, nitFilePath, changeName, actingRole, timestamp,
                        new CardNitDispositionOutcome.NotABlockCard(card.Frontmatter.Kind),
                        static reason => new CardNitDispositionOutcome.ToolFailure(reason));
                }

                if (!RoundAgreesWithHistory(card, out var storedRound, out var expectedRound))
                {
                    return RefuseAndRecord<CardNitDispositionOutcome, CardNitDispositionOutcome.RoundDisagreesWithHistory>(cardsRoot, card, nitFilePath, changeName, actingRole, timestamp,
                        new CardNitDispositionOutcome.RoundDisagreesWithHistory(storedRound, expectedRound),
                        static reason => new CardNitDispositionOutcome.ToolFailure(reason));
                }

                var nitIndex = -1;
                for (var i = 0; i < card.Comments.Count; i++)
                {
                    if (card.Comments[i].IsNit && string.Equals(card.Comments[i].Id, nitId, StringComparison.Ordinal))
                    {
                        nitIndex = i;
                        break;
                    }
                }

                if (nitIndex < 0)
                {
                    return RefuseAndRecord<CardNitDispositionOutcome, CardNitDispositionOutcome.NitNotFound>(cardsRoot, card, nitFilePath, changeName, actingRole, timestamp,
                        new CardNitDispositionOutcome.NitNotFound(nitId),
                        static reason => new CardNitDispositionOutcome.ToolFailure(reason));
                }

                if (CardCommentRouting.IsNitDispositioned(card.Comments, nitIndex))
                {
                    return RefuseAndRecord<CardNitDispositionOutcome, CardNitDispositionOutcome.AlreadyDispositioned>(cardsRoot, card, nitFilePath, changeName, actingRole, timestamp,
                        new CardNitDispositionOutcome.AlreadyDispositioned(nitId),
                        static reason => new CardNitDispositionOutcome.ToolFailure(reason));
                }

                var anchored = AnchoredCardPath.TryCreate(cardsRoot, nitFilePath, card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return new CardNitDispositionOutcome.LayoutMismatch(layoutFailure!.Reason);
                }

                var dispositionComment = new CardComment(
                    Id: $"disposition-{Guid.NewGuid():N}", Author: actingRole, Timestamp: timestamp, Body: body,
                    ReplyTo: nitId, To: null, Resolves: nitId, UnknownHeaderFields: [], Disposition: disposition);

                var updatedFrontmatter = card.Frontmatter with { Updated = timestamp };
                var updatedBlockFields = card.BlockFields;
                var updatedTransitions = card.Transitions;
                var transitioned = false;

                // §8 block B remediation (the original HAZARD fix over-narrowed): applies the
                // fix-before-land edge only while the card is still in-review — a disposition after
                // the round has already left in-review just records its own disposition, below,
                // regardless. This also folds in §8.7's own "every exit from in-review is bound":
                // if this disposition would leave a different nit still undispositioned, the edge
                // is withheld the same way — the transition, not the disposition, is what waits.
                //
                // The edge does NOT turn on whether *this call's* disposition is fix-before-land —
                // requiring both "this call is fix-before-land" and "nothing is left undispositioned"
                // stranded a card whenever the nit that emptied the live set was itself deferred or
                // declined, even though an earlier nit this round was already dispositioned
                // fix-before-land (the exact scenario the original brief item 4 only tested in the
                // opposite order). The question the edge asks is whether *the round, taken as a
                // whole* carries a fix-before-land nit — CardCommentRouting.
                // HasFixBeforeLandDisposition, LiveUndispositionedNitIds's own sibling.
                //
                // "The round" has to be scoped, not the card's whole history: comments are
                // append-only, so a fix-before-land disposition that already triggered the edge in
                // an earlier round remains in the thread forever and must not re-trigger it here.
                // The round's start is the most recent transition that (re-)entered in-review
                // ("submit-for-review" is its only door, BlockFlowTransitions) — comments at or
                // after that point are this round's; DateTimeOffset.MinValue when no such
                // transition is recorded yet (a card seeded directly into in-review, as tests do)
                // correctly folds in the whole thread, since there is only the one round.
                //
                // Computed here, before any write — including the raised card below, for a
                // defer/decline disposition that happens to be the one leaving no nit
                // undispositioned this round (see the "does NOT turn on whether this call's
                // disposition is fix-before-land" note above) — because process-enforcement's "A
                // verdict cannot leave threads unanswered" (§9 block B) has to refuse the whole
                // call, not merely the transition, when this disposition would leave in-review
                // with a thread still addressed to the acting role: a refusal must prevent the
                // side effect it refuses, not merely follow it (ADR-0001), and here the side effect
                // includes the disposition itself, not only the transition.
                if (BlockFlowStateWireFormat.TryParse(card.Frontmatter.Status, out var currentState)
                    && currentState == BlockFlowState.InReview)
                {
                    var commentsAfterThisDisposition = (IReadOnlyList<CardComment>)[.. card.Comments, dispositionComment];
                    var stillLiveNitIds = CardCommentRouting.LiveUndispositionedNitIds(commentsAfterThisDisposition);
                    if (stillLiveNitIds.Count == 0)
                    {
                        var roundStart = DateTimeOffset.MinValue;
                        for (var i = card.Transitions.Count - 1; i >= 0; i--)
                        {
                            if (card.Transitions[i].To == BlockFlowState.InReview)
                            {
                                roundStart = card.Transitions[i].Timestamp;
                                break;
                            }
                        }

                        var thisRoundComments = commentsAfterThisDisposition.Where(c => c.Timestamp >= roundStart).ToList();
                        if (CardCommentRouting.HasFixBeforeLandDisposition(thisRoundComments))
                        {
                            var unresolvedThreadIds = CardCommentRouting.LiveThreadIdsAddressedTo(commentsAfterThisDisposition, actingRole);
                            if (unresolvedThreadIds.Count > 0)
                            {
                                return RefuseAndRecord<CardNitDispositionOutcome, CardNitDispositionOutcome.UnresolvedThreadsAddressedToActor>(cardsRoot, card, nitFilePath, changeName, actingRole, timestamp,
                                    new CardNitDispositionOutcome.UnresolvedThreadsAddressedToActor(actingRole, unresolvedThreadIds),
                                    static reason => new CardNitDispositionOutcome.ToolFailure(reason));
                            }

                            var transition = BlockFlowTransitions.AvailableFrom(currentState)
                                .First(candidate => string.Equals(candidate.Name, "fix-before-land", StringComparison.Ordinal));
                            updatedFrontmatter = updatedFrontmatter with { Status = transition.To.ToWireString() };
                            // Unset reads as round 1, the same default every other increment site
                            // applies (§8a block D: this arm and ApplyBlockTransitionUnderExistingLock's
                            // changes-requested arm both read `?? 0` before this fix, disagreeing with
                            // RecordSectionVerdictUnderExistingLock's finding-recurred arm's `?? 1` —
                            // fixed to agree, see the DEVLOG post for this block).
                            updatedBlockFields = updatedBlockFields with { Round = (updatedBlockFields.Round ?? 1) + 1 };
                            updatedTransitions = [.. updatedTransitions, new CardBlockTransitionEntry(actingRole, transition.Name, transition.From, transition.To, timestamp, [])];
                            transitioned = true;
                        }
                    }
                }

                string? raisedContent = null;
                CardFile? raisedCardFile = null;
                if (raiseRequest is not null)
                {
                    var raisedScope = raiseRequest.Kind == CardKind.Obligation ? CardScope.Change : CardScope.Capability;
                    var raisedAnchored = AnchoredCardPath.TryCreate(cardsRoot, raiseRequest.FilePath, raisedScope, changeName, out var raisedLayoutFailure);
                    if (raisedAnchored is null)
                    {
                        return new CardNitDispositionOutcome.RaisedCardLayoutMismatch(raisedLayoutFailure!.Reason);
                    }

                    if (File.Exists(raiseRequest.FilePath))
                    {
                        return RefuseAndRecord<CardNitDispositionOutcome, CardNitDispositionOutcome.RaisedCardAlreadyExists>(cardsRoot, card, nitFilePath, changeName, actingRole, timestamp,
                            new CardNitDispositionOutcome.RaisedCardAlreadyExists(raiseRequest.FilePath),
                            static reason => new CardNitDispositionOutcome.ToolFailure(reason));
                    }

                    var raisedFrontmatter = new CardFrontmatter(
                        raisedId!, raiseRequest.Kind, raiseRequest.Title, "open", actingRole, raisedScope, OwningSectionId(card.Frontmatter), timestamp, timestamp);

                    // An obligation raised from a declined-or-deferred nit is owed to the same
                    // section the block itself belongs to — the same "give it a real owed_by, not a
                    // free-text label" ruling RecordFinding's own blind-spot obligation already
                    // applies (§7 block C). A decision carries no owed_by at all.
                    var raisedRegisterFields = raiseRequest.Kind == CardKind.Obligation
                        ? new RegisterCardFields(null, null, null, null, OwedBy: OwningSectionId(card.Frontmatter))
                        : RegisterCardFields.Empty;

                    var raisedBody = $"{raiseRequest.Body}\n\n(Raised from nit {nitId} on block {card.Frontmatter.Id}.)";
                    raisedCardFile = new CardFile(raisedFrontmatter, raisedBody, [], [], RegisterFields: raisedRegisterFields);
                    var serializedRaisedCard = CardFileWriter.Serialize(raisedCardFile);

                    var raisedWriteResult = AtomicWrite(raisedAnchored, serializedRaisedCard);
                    var raisedFailure = raisedWriteResult.Match<CardNitDispositionOutcome?>(
                        onSuccess: static _ => null,
                        onNotFound: static notFound => new CardNitDispositionOutcome.ToolFailure(
                            $"unexpected 'not found' writing a brand-new card at '{notFound.FilePath}'."),
                        onAlreadyExists: static alreadyExists => new CardNitDispositionOutcome.RaisedCardAlreadyExists(alreadyExists.FilePath),
                        onLayoutMismatch: static layoutMismatch => new CardNitDispositionOutcome.RaisedCardLayoutMismatch(layoutMismatch.Reason),
                        onCorrupt: static corrupt => new CardNitDispositionOutcome.ToolFailure(
                            $"unexpected corruption reported writing a brand-new card at '{corrupt.FilePath}': {corrupt.Reason}"),
                        onToolFailure: static toolFailure => new CardNitDispositionOutcome.ToolFailure(toolFailure.Reason),
                        onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
                    if (raisedFailure is not null)
                    {
                        return raisedFailure;
                    }

                    raisedContent = serializedRaisedCard;
                }

                var updatedCard = card with
                {
                    Frontmatter = updatedFrontmatter,
                    BlockFields = updatedBlockFields,
                    Transitions = updatedTransitions,
                    Comments = [.. card.Comments, dispositionComment],
                };

                var writeResult = AtomicWrite(anchored, CardFileWriter.Serialize(updatedCard));
                return writeResult.Match<CardNitDispositionOutcome>(
                    onSuccess: _ => new CardNitDispositionOutcome.Dispositioned(updatedCard, dispositionComment, raisedCardFile, transitioned),
                    onNotFound: notFound =>
                    {
                        RollbackRaisedNitCard(raiseRequest, raisedContent);
                        return new CardNitDispositionOutcome.CardNotFound(notFound.FilePath);
                    },
                    onAlreadyExists: alreadyExists =>
                    {
                        RollbackRaisedNitCard(raiseRequest, raisedContent);
                        return new CardNitDispositionOutcome.LayoutMismatch(
                            $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite.");
                    },
                    onLayoutMismatch: layoutMismatch =>
                    {
                        RollbackRaisedNitCard(raiseRequest, raisedContent);
                        return new CardNitDispositionOutcome.LayoutMismatch(layoutMismatch.Reason);
                    },
                    onCorrupt: corrupt =>
                    {
                        RollbackRaisedNitCard(raiseRequest, raisedContent);
                        return new CardNitDispositionOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason);
                    },
                    onToolFailure: toolFailure =>
                    {
                        RollbackRaisedNitCard(raiseRequest, raisedContent);
                        return new CardNitDispositionOutcome.ToolFailure(toolFailure.Reason);
                    },
                    onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
            },
            onFailure: failure =>
                new CardNitDispositionOutcome.CardCorrupt(nitFilePath, failure.Reason));
    }

    /// <summary>All-or-nothing's other half: deletes the raised card <see cref="DispositionNitUnderLocks"/>
    /// has already written, once the block card's own write, tried afterward, fails for any reason.
    /// Compare-then-delete, not delete-by-path — see <see cref="DispositionNit"/>'s own doc comment,
    /// and <see cref="RollbackRaisedCard"/>, the same shape applied to <see cref="RecordFinding"/>'s
    /// raised card.</summary>
    private static void RollbackRaisedNitCard(NitDispositionRaiseRequest? raiseRequest, string? raisedContent)
    {
        if (raiseRequest is null || raisedContent is null)
        {
            return;
        }

        try
        {
            if (!File.Exists(raiseRequest.FilePath))
            {
                return;
            }

            var currentContent = File.ReadAllText(raiseRequest.FilePath, Utf8NoBom);
            if (string.Equals(currentContent, raisedContent, StringComparison.Ordinal))
            {
                File.Delete(raiseRequest.FilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Records one gate's exit code on the block card at <paramref name="filePath"/>
    /// (work-lifecycle: "A gate result SHALL be recorded on the card as a label paired with the
    /// exit code the gate returned", §5 block D) — reads the current card, and only if it is a
    /// block card, writes the exit code under the card's lock, the same read-decide-write shape
    /// <see cref="ApplyBlockTransition"/> established. Recording a second result for
    /// <paramref name="label"/> <em>in the same round</em> replaces the one already recorded for
    /// that round; a result for a different round is a distinct entry, retained rather than
    /// replaced (§5 remediation, DEVLOG §5 finding B2) — see <see cref="BlockCardFields.
    /// GateResults"/>'s own doc comment. <paramref name="actingRole"/> is threaded through and
    /// returned on <see cref="CardGateResultOutcome.Recorded"/> (§5 remediation, finding B1) but
    /// not persisted on <see cref="GateResult"/> itself — see that outcome case's own doc comment
    /// for why.
    /// </summary>
    internal static CardGateResultOutcome RecordGateResult(
        string cardsRoot, string filePath, string label, int exitCode, CardOwner actingRole, DateTimeOffset timestamp, TimeSpan lockTimeout, string? changeName = null) =>
        WithLock(
            filePath,
            lockTimeout,
            heldLock => RecordGateResultUnderExistingLock(heldLock, cardsRoot, label, exitCode, actingRole, timestamp, changeName),
            onTimedOut: timedOut => new CardGateResultOutcome.ToolFailure(timedOut.Message));

    /// <summary>
    /// The read-decide-write step of <see cref="RecordGateResult"/>. Same structural lock
    /// precondition as every other <c>*UnderExistingLock</c> method on this type — the target is
    /// <see cref="CardLock.CardPath"/>, not a separately supplied <c>filePath</c>.
    /// </summary>
    internal static CardGateResultOutcome RecordGateResultUnderExistingLock(
        CardLock heldLock, string cardsRoot, string label, int exitCode, CardOwner actingRole, DateTimeOffset timestamp, string? changeName = null)
    {
        ArgumentNullException.ThrowIfNull(heldLock);
        var filePath = heldLock.CardPath;

        if (!File.Exists(filePath))
        {
            return new CardGateResultOutcome.CardNotFound(filePath);
        }

        var current = ReadCard(filePath);
        return current.Match<CardGateResultOutcome>(
            onSuccess: success =>
            {
                var card = success.Card;
                if (ReservedDerivedStateFieldKeyIn(card) is { } reservedKey)
                {
                    return RefuseAndRecord<CardGateResultOutcome, CardGateResultOutcome.HandEnteredDerivedState>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardGateResultOutcome.HandEnteredDerivedState(reservedKey),
                        static reason => new CardGateResultOutcome.ToolFailure(reason));
                }

                if (!IsBlockCard(card))
                {
                    return RefuseAndRecord<CardGateResultOutcome, CardGateResultOutcome.NotABlockCard>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardGateResultOutcome.NotABlockCard(card.Frontmatter.Kind),
                        static reason => new CardGateResultOutcome.ToolFailure(reason));
                }

                if (!RoundAgreesWithHistory(card, out var storedRound, out var expectedRound))
                {
                    return RefuseAndRecord<CardGateResultOutcome, CardGateResultOutcome.RoundDisagreesWithHistory>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardGateResultOutcome.RoundDisagreesWithHistory(storedRound, expectedRound),
                        static reason => new CardGateResultOutcome.ToolFailure(reason));
                }

                var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return new CardGateResultOutcome.LayoutMismatch(layoutFailure!.Reason);
                }

                // The block's round at the moment this result is recorded, not re-derived at read
                // time — GateResult.Round's own doc comment. Unset (a block never yet briefed)
                // reads as round 1, the same default ApplyBlockTransitionUnderExistingLock applies
                // the first time a block lands on Briefed.
                var currentRound = card.BlockFields.Round ?? 1;
                var result = new GateResult(label, exitCode, currentRound);
                var withoutCurrentRoundEntry = card.BlockFields.GateResults
                    .Where(existing => !(existing.Round == currentRound && string.Equals(existing.Label, label, StringComparison.Ordinal)))
                    .ToImmutableArray();
                var updated = card with
                {
                    Frontmatter = card.Frontmatter with { Updated = timestamp },
                    BlockFields = card.BlockFields with { GateResults = withoutCurrentRoundEntry.Add(result) },
                };

                var writeResult = AtomicWrite(anchored, CardFileWriter.Serialize(updated));
                return writeResult.Match<CardGateResultOutcome>(
                    onSuccess: _ => new CardGateResultOutcome.Recorded(updated, result, actingRole),
                    onNotFound: notFound => new CardGateResultOutcome.CardNotFound(notFound.FilePath),
                    onAlreadyExists: alreadyExists => new CardGateResultOutcome.LayoutMismatch(
                        $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite."),
                    onLayoutMismatch: layoutMismatch => new CardGateResultOutcome.LayoutMismatch(layoutMismatch.Reason),
                    onCorrupt: corrupt => new CardGateResultOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason),
                    onToolFailure: toolFailure => new CardGateResultOutcome.ToolFailure(toolFailure.Reason),
                    onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
            },
            onFailure: failure =>
                new CardGateResultOutcome.CardCorrupt(filePath, failure.Reason));
    }

    /// <summary>
    /// Adds <paramref name="blockingCardId"/> to the block card at <paramref name="filePath"/>'s
    /// <c>blocked_by</c> set (work-lifecycle: "Blocked is derived, not stored", §5 block D). Never
    /// touches <see cref="CardFrontmatter.Status"/> — see <see cref="CardBlockedByOutcome"/>'s own
    /// doc comment for why that is structural rather than a discipline this method happens to
    /// follow. <paramref name="actingRole"/> is threaded through and returned on
    /// <see cref="CardBlockedByOutcome.Updated"/> (§5 remediation, DEVLOG §5 finding B1) — see that
    /// outcome case's own doc comment for why it is not also persisted on the card.
    /// </summary>
    internal static CardBlockedByOutcome AddBlockedBy(
        string cardsRoot, string filePath, string blockingCardId, CardOwner actingRole, DateTimeOffset timestamp, TimeSpan lockTimeout, string? changeName = null) =>
        WithLock(
            filePath,
            lockTimeout,
            heldLock => AddBlockedByUnderExistingLock(heldLock, cardsRoot, blockingCardId, actingRole, timestamp, changeName),
            onTimedOut: timedOut => new CardBlockedByOutcome.ToolFailure(timedOut.Message));

    /// <summary>The read-decide-write step of <see cref="AddBlockedBy"/>. Resolves
    /// <paramref name="blockingCardId"/> through <see cref="CardIdentityResolver.Resolve"/> — never
    /// a hand-rolled directory walk (§7 carried item C) — and refuses via
    /// <see cref="CardBlockedByOutcome.BlockerUnresolvable"/> when it does not resolve to exactly
    /// one card (§11 block A, Product Owner ruling closing the fail-open carried from §10's
    /// <c>## NEXT</c>). Checked only once <see cref="UpdateBlockedByUnderExistingLock"/> has already
    /// decided this add is not a no-op — see that case's own doc comment for why the ordering runs
    /// the argument's own-field check first. <see cref="RemoveBlockedByUnderExistingLock"/>
    /// deliberately passes no such validator.</summary>
    internal static CardBlockedByOutcome AddBlockedByUnderExistingLock(
        CardLock heldLock, string cardsRoot, string blockingCardId, CardOwner actingRole, DateTimeOffset timestamp, string? changeName = null) =>
        UpdateBlockedByUnderExistingLock(
            heldLock, cardsRoot, blockingCardId, actingRole, timestamp, changeName,
            apply: (current, id) => current.Contains(id, StringComparer.Ordinal)
                ? (Updated: false, Result: current)
                : (Updated: true, Result: current.Add(id)),
            onNoChange: (card, filePath, id) =>
                RefuseAndRecord<CardBlockedByOutcome, CardBlockedByOutcome.AlreadyBlockedBy>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                    new CardBlockedByOutcome.AlreadyBlockedBy(id),
                    static reason => new CardBlockedByOutcome.ToolFailure(reason)),
            validateBeforeWrite: (card, filePath, id) =>
                CardIdentityResolver.Resolve(cardsRoot, id).Match<CardBlockedByOutcome?>(
                    onFound: static (_, _) => null,
                    onNotFound: notFoundId =>
                        RefuseAndRecord<CardBlockedByOutcome, CardBlockedByOutcome.BlockerUnresolvable>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                            new CardBlockedByOutcome.BlockerUnresolvable(notFoundId, "names no card in the record"),
                            static reason => new CardBlockedByOutcome.ToolFailure(reason)),
                    onDuplicate: (duplicateId, filePaths) =>
                        RefuseAndRecord<CardBlockedByOutcome, CardBlockedByOutcome.BlockerUnresolvable>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                            new CardBlockedByOutcome.BlockerUnresolvable(duplicateId, $"is claimed by {filePaths.Count} card files ({string.Join(", ", filePaths)}), so which one is meant cannot be decided"),
                            static reason => new CardBlockedByOutcome.ToolFailure(reason)),
                    onUnreadable: (unreadableId, filePaths) =>
                        RefuseAndRecord<CardBlockedByOutcome, CardBlockedByOutcome.BlockerUnresolvable>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                            new CardBlockedByOutcome.BlockerUnresolvable(unreadableId, $"cannot be confirmed or ruled out — {filePaths.Count} card file(s) elsewhere in the record could not be read ({string.Join(", ", filePaths)})"),
                            static reason => new CardBlockedByOutcome.ToolFailure(reason))));

    /// <summary>
    /// Removes <paramref name="blockingCardId"/> from the block card at <paramref name="filePath"/>'s
    /// <c>blocked_by</c> set — the "clearing what blocked it requires no state restoration" half of
    /// work-lifecycle's "Blocked is derived, not stored": this never touches
    /// <see cref="CardFrontmatter.Status"/> either, so the card's flow state is exactly what it was
    /// before it was ever blocked, not something this method has to put back. Same
    /// <paramref name="actingRole"/> threading as <see cref="AddBlockedBy"/>.
    /// </summary>
    internal static CardBlockedByOutcome RemoveBlockedBy(
        string cardsRoot, string filePath, string blockingCardId, CardOwner actingRole, DateTimeOffset timestamp, TimeSpan lockTimeout, string? changeName = null) =>
        WithLock(
            filePath,
            lockTimeout,
            heldLock => RemoveBlockedByUnderExistingLock(heldLock, cardsRoot, blockingCardId, actingRole, timestamp, changeName),
            onTimedOut: timedOut => new CardBlockedByOutcome.ToolFailure(timedOut.Message));

    /// <summary>The read-decide-write step of <see cref="RemoveBlockedBy"/>.</summary>
    internal static CardBlockedByOutcome RemoveBlockedByUnderExistingLock(
        CardLock heldLock, string cardsRoot, string blockingCardId, CardOwner actingRole, DateTimeOffset timestamp, string? changeName = null) =>
        UpdateBlockedByUnderExistingLock(
            heldLock, cardsRoot, blockingCardId, actingRole, timestamp, changeName,
            apply: (current, id) => current.Contains(id, StringComparer.Ordinal)
                ? (Updated: true, Result: current.Remove(id))
                : (Updated: false, Result: current),
            onNoChange: (card, filePath, id) =>
                RefuseAndRecord<CardBlockedByOutcome, CardBlockedByOutcome.NotBlockedBy>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                    new CardBlockedByOutcome.NotBlockedBy(id),
                    static reason => new CardBlockedByOutcome.ToolFailure(reason)));

    /// <summary>
    /// The read-decide-write shape <see cref="AddBlockedByUnderExistingLock"/> and
    /// <see cref="RemoveBlockedByUnderExistingLock"/> share — same structural lock precondition as
    /// every other <c>*UnderExistingLock</c> method — differing only in how
    /// <paramref name="apply"/> decides the new <c>blocked_by</c> set from the current one, what
    /// <paramref name="onNoChange"/> refuses with when nothing needed to change, and (§11 block A)
    /// an optional <paramref name="validateBeforeWrite"/> run only once <paramref name="apply"/> has
    /// decided a write is actually happening — <see cref="AddBlockedByUnderExistingLock"/> supplies
    /// one (the blocker-resolves check), <see cref="RemoveBlockedByUnderExistingLock"/> supplies
    /// none, by design.
    /// </summary>
    private static CardBlockedByOutcome UpdateBlockedByUnderExistingLock(
        CardLock heldLock,
        string cardsRoot,
        string blockingCardId,
        CardOwner actingRole,
        DateTimeOffset timestamp,
        string? changeName,
        Func<ImmutableArray<string>, string, (bool Updated, ImmutableArray<string> Result)> apply,
        Func<CardFile, string, string, CardBlockedByOutcome> onNoChange,
        Func<CardFile, string, string, CardBlockedByOutcome?>? validateBeforeWrite = null)
    {
        ArgumentNullException.ThrowIfNull(heldLock);
        var filePath = heldLock.CardPath;

        if (!File.Exists(filePath))
        {
            return new CardBlockedByOutcome.CardNotFound(filePath);
        }

        var current = ReadCard(filePath);
        return current.Match<CardBlockedByOutcome>(
            onSuccess: success =>
            {
                var card = success.Card;
                if (ReservedDerivedStateFieldKeyIn(card) is { } reservedKey)
                {
                    return RefuseAndRecord<CardBlockedByOutcome, CardBlockedByOutcome.HandEnteredDerivedState>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardBlockedByOutcome.HandEnteredDerivedState(reservedKey),
                        static reason => new CardBlockedByOutcome.ToolFailure(reason));
                }

                if (!IsBlockCard(card))
                {
                    return RefuseAndRecord<CardBlockedByOutcome, CardBlockedByOutcome.NotABlockCard>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardBlockedByOutcome.NotABlockCard(card.Frontmatter.Kind),
                        static reason => new CardBlockedByOutcome.ToolFailure(reason));
                }

                if (!RoundAgreesWithHistory(card, out var storedRound, out var expectedRound))
                {
                    return RefuseAndRecord<CardBlockedByOutcome, CardBlockedByOutcome.RoundDisagreesWithHistory>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardBlockedByOutcome.RoundDisagreesWithHistory(storedRound, expectedRound),
                        static reason => new CardBlockedByOutcome.ToolFailure(reason));
                }

                var (updated, newBlockedBy) = apply(card.BlockFields.BlockedBy, blockingCardId);
                if (!updated)
                {
                    return onNoChange(card, filePath, blockingCardId);
                }

                var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return new CardBlockedByOutcome.LayoutMismatch(layoutFailure!.Reason);
                }

                // §11 block A: checked last among the pre-write refusals — it is the only one that
                // walks the whole record (CardIdentityResolver.Resolve), so every cheaper,
                // structural check (reserved key, card kind, round history, this card's own
                // blocked_by field, this write's own anchored path) decides first.
                if (validateBeforeWrite?.Invoke(card, filePath, blockingCardId) is { } validationRefusal)
                {
                    return validationRefusal;
                }

                var updatedCard = card with
                {
                    Frontmatter = card.Frontmatter with { Updated = timestamp },
                    BlockFields = card.BlockFields with { BlockedBy = newBlockedBy },
                };

                var writeResult = AtomicWrite(anchored, CardFileWriter.Serialize(updatedCard));
                return writeResult.Match<CardBlockedByOutcome>(
                    onSuccess: _ => new CardBlockedByOutcome.Updated(updatedCard, actingRole),
                    onNotFound: notFound => new CardBlockedByOutcome.CardNotFound(notFound.FilePath),
                    onAlreadyExists: alreadyExists => new CardBlockedByOutcome.LayoutMismatch(
                        $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite."),
                    onLayoutMismatch: layoutMismatch => new CardBlockedByOutcome.LayoutMismatch(layoutMismatch.Reason),
                    onCorrupt: corrupt => new CardBlockedByOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason),
                    onToolFailure: toolFailure => new CardBlockedByOutcome.ToolFailure(toolFailure.Reason),
                    onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
            },
            onFailure: failure =>
                new CardBlockedByOutcome.CardCorrupt(filePath, failure.Reason));
    }

    /// <summary>
    /// Marks the question card at <paramref name="filePath"/> answered (§9 block D,
    /// process-enforcement: "An answer must be written down") — the only door to <see cref="
    /// QuestionStatus.Answered"/>. <paramref name="decisionId"/>/<paramref name="inlineAnswer"/>
    /// are mutually informative, not mutually enforced here: <see cref="Callboard.Cli.CommandParser.
    /// ParseQuestionAnswer"/> already refused a call naming neither before this is ever reached (see
    /// <see cref="CardQuestionAnswerOutcome"/>'s own doc comment for why that refusal belongs there,
    /// not here), and a caller that resolved <paramref name="decisionId"/> against
    /// <see cref="CardIdentityResolver"/> (<see cref="Callboard.Cli.CommandDispatcher.
    /// RunQuestionAnswer"/>) has already proven it names a real <c>decision</c> card by the time
    /// this method ever sees it.
    /// </summary>
    internal static CardQuestionAnswerOutcome AnswerQuestion(
        string cardsRoot, string filePath, string? decisionId, string? inlineAnswer, CardOwner actingRole, DateTimeOffset timestamp, TimeSpan lockTimeout, string? changeName = null) =>
        WithLock(
            filePath,
            lockTimeout,
            heldLock => AnswerQuestionUnderExistingLock(heldLock, cardsRoot, decisionId, inlineAnswer, actingRole, timestamp, changeName),
            onTimedOut: timedOut => new CardQuestionAnswerOutcome.ToolFailure(timedOut.Message));

    /// <summary>The read-decide-write step of <see cref="AnswerQuestion"/>.</summary>
    internal static CardQuestionAnswerOutcome AnswerQuestionUnderExistingLock(
        CardLock heldLock, string cardsRoot, string? decisionId, string? inlineAnswer, CardOwner actingRole, DateTimeOffset timestamp, string? changeName = null)
    {
        ArgumentNullException.ThrowIfNull(heldLock);
        var filePath = heldLock.CardPath;

        if (!File.Exists(filePath))
        {
            return new CardQuestionAnswerOutcome.CardNotFound(filePath);
        }

        var current = ReadCard(filePath);
        return current.Match<CardQuestionAnswerOutcome>(
            onSuccess: success =>
            {
                var card = success.Card;
                if (ReservedDerivedStateFieldKeyIn(card) is { } reservedKey)
                {
                    return RefuseAndRecord<CardQuestionAnswerOutcome, CardQuestionAnswerOutcome.HandEnteredDerivedState>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardQuestionAnswerOutcome.HandEnteredDerivedState(reservedKey),
                        static reason => new CardQuestionAnswerOutcome.ToolFailure(reason));
                }

                if (!IsQuestionCard(card))
                {
                    return RefuseAndRecord<CardQuestionAnswerOutcome, CardQuestionAnswerOutcome.NotAQuestionCard>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardQuestionAnswerOutcome.NotAQuestionCard(card.Frontmatter.Kind),
                        static reason => new CardQuestionAnswerOutcome.ToolFailure(reason));
                }

                if (!QuestionStatusWireFormat.TryParse(card.Frontmatter.Status, out var currentStatus))
                {
                    return new CardQuestionAnswerOutcome.CardCorrupt(
                        filePath, $"unrecognised status: '{card.Frontmatter.Status}'. Recognised statuses: {QuestionStatusWireFormat.RecognisedValues}.");
                }

                if (currentStatus != QuestionStatus.Open)
                {
                    return RefuseAndRecord<CardQuestionAnswerOutcome, CardQuestionAnswerOutcome.NotOpen>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardQuestionAnswerOutcome.NotOpen(currentStatus),
                        static reason => new CardQuestionAnswerOutcome.ToolFailure(reason));
                }

                var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return new CardQuestionAnswerOutcome.LayoutMismatch(layoutFailure!.Reason);
                }

                var updated = card with
                {
                    Frontmatter = card.Frontmatter with { Status = QuestionStatus.Answered.ToWireString(), Updated = timestamp },
                    QuestionFields = card.QuestionFields with
                    {
                        AnsweredBy = actingRole,
                        AnsweredAt = timestamp,
                        AnswerDecisionId = decisionId,
                        AnswerInline = inlineAnswer,
                    },
                };

                var writeResult = AtomicWrite(anchored, CardFileWriter.Serialize(updated));
                return writeResult.Match<CardQuestionAnswerOutcome>(
                    onSuccess: _ => new CardQuestionAnswerOutcome.Answered(updated),
                    onNotFound: notFound => new CardQuestionAnswerOutcome.CardNotFound(notFound.FilePath),
                    onAlreadyExists: alreadyExists => new CardQuestionAnswerOutcome.LayoutMismatch(
                        $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite."),
                    onLayoutMismatch: layoutMismatch => new CardQuestionAnswerOutcome.LayoutMismatch(layoutMismatch.Reason),
                    onCorrupt: corrupt => new CardQuestionAnswerOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason),
                    onToolFailure: toolFailure => new CardQuestionAnswerOutcome.ToolFailure(toolFailure.Reason),
                    onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: a question card never carries CardFile.Transitions."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
            },
            onFailure: failure =>
                new CardQuestionAnswerOutcome.CardCorrupt(filePath, failure.Reason));
    }

    /// <summary>
    /// Marks the question card at <paramref name="filePath"/> deferred to <paramref name="target"/>
    /// (§9 block D — the question status vocabulary entire, including <c>deferred</c>, register:
    /// "the question remains open and continues to surface to the role that owes its answer" —
    /// unlike <see cref="AnswerQuestion"/>, deferring does not settle who must still act, it only
    /// redirects when a section close may stop waiting on it) — the only door to <see cref="
    /// QuestionStatus.Deferred"/>. <paramref name="target"/> is free text — see <see cref="
    /// QuestionCardFields.DeferredTarget"/>'s own doc comment for why it is never resolved through
    /// <see cref="CardIdentityResolver"/> the way <paramref name="decisionId"/> above is.
    /// </summary>
    internal static CardQuestionDeferOutcome DeferQuestion(
        string cardsRoot, string filePath, string target, CardOwner actingRole, DateTimeOffset timestamp, TimeSpan lockTimeout, string? changeName = null) =>
        WithLock(
            filePath,
            lockTimeout,
            heldLock => DeferQuestionUnderExistingLock(heldLock, cardsRoot, target, actingRole, timestamp, changeName),
            onTimedOut: timedOut => new CardQuestionDeferOutcome.ToolFailure(timedOut.Message));

    /// <summary>The read-decide-write step of <see cref="DeferQuestion"/>.</summary>
    internal static CardQuestionDeferOutcome DeferQuestionUnderExistingLock(
        CardLock heldLock, string cardsRoot, string target, CardOwner actingRole, DateTimeOffset timestamp, string? changeName = null)
    {
        ArgumentNullException.ThrowIfNull(heldLock);
        var filePath = heldLock.CardPath;

        if (!File.Exists(filePath))
        {
            return new CardQuestionDeferOutcome.CardNotFound(filePath);
        }

        var current = ReadCard(filePath);
        return current.Match<CardQuestionDeferOutcome>(
            onSuccess: success =>
            {
                var card = success.Card;
                if (ReservedDerivedStateFieldKeyIn(card) is { } reservedKey)
                {
                    return RefuseAndRecord<CardQuestionDeferOutcome, CardQuestionDeferOutcome.HandEnteredDerivedState>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardQuestionDeferOutcome.HandEnteredDerivedState(reservedKey),
                        static reason => new CardQuestionDeferOutcome.ToolFailure(reason));
                }

                if (!IsQuestionCard(card))
                {
                    return RefuseAndRecord<CardQuestionDeferOutcome, CardQuestionDeferOutcome.NotAQuestionCard>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardQuestionDeferOutcome.NotAQuestionCard(card.Frontmatter.Kind),
                        static reason => new CardQuestionDeferOutcome.ToolFailure(reason));
                }

                if (!QuestionStatusWireFormat.TryParse(card.Frontmatter.Status, out var currentStatus))
                {
                    return new CardQuestionDeferOutcome.CardCorrupt(
                        filePath, $"unrecognised status: '{card.Frontmatter.Status}'. Recognised statuses: {QuestionStatusWireFormat.RecognisedValues}.");
                }

                if (currentStatus != QuestionStatus.Open)
                {
                    return RefuseAndRecord<CardQuestionDeferOutcome, CardQuestionDeferOutcome.NotOpen>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardQuestionDeferOutcome.NotOpen(currentStatus),
                        static reason => new CardQuestionDeferOutcome.ToolFailure(reason));
                }

                var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return new CardQuestionDeferOutcome.LayoutMismatch(layoutFailure!.Reason);
                }

                var updated = card with
                {
                    Frontmatter = card.Frontmatter with { Status = QuestionStatus.Deferred.ToWireString(), Updated = timestamp },
                    QuestionFields = card.QuestionFields with
                    {
                        DeferredBy = actingRole,
                        DeferredAt = timestamp,
                        DeferredTarget = target,
                    },
                };

                var writeResult = AtomicWrite(anchored, CardFileWriter.Serialize(updated));
                return writeResult.Match<CardQuestionDeferOutcome>(
                    onSuccess: _ => new CardQuestionDeferOutcome.Deferred(updated),
                    onNotFound: notFound => new CardQuestionDeferOutcome.CardNotFound(notFound.FilePath),
                    onAlreadyExists: alreadyExists => new CardQuestionDeferOutcome.LayoutMismatch(
                        $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite."),
                    onLayoutMismatch: layoutMismatch => new CardQuestionDeferOutcome.LayoutMismatch(layoutMismatch.Reason),
                    onCorrupt: corrupt => new CardQuestionDeferOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason),
                    onToolFailure: toolFailure => new CardQuestionDeferOutcome.ToolFailure(toolFailure.Reason),
                    onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: a question card never carries CardFile.Transitions."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
            },
            onFailure: failure =>
                new CardQuestionDeferOutcome.CardCorrupt(filePath, failure.Reason));
    }

    /// <summary>
    /// The blocking-question half of process-enforcement's "Work cannot proceed past a
    /// stop-and-ask" (§9 block D, <c>9.8</c>): the first <c>question</c> card among
    /// <paramref name="card"/>'s own <see cref="BlockCardFields.BlockedBy"/> ids that resolves, is
    /// not closed under <see cref="CardLifecycle.IsClosed"/> — i.e. not yet <see
    /// cref="QuestionStatus.Answered"/>, which per <see cref="CardLifecycle"/>'s own doc comment
    /// means a <see cref="QuestionStatus.Deferred"/> question still counts here (§10 remediation,
    /// round two, Product Owner ruling: deferring does not lift the halt) — owned by <see
    /// cref="CardOwner.ProductOwner"/>. <see langword="null"/> if none does. Resolves every id
    /// through <see cref="CardIdentityResolver.
    /// Resolve"/> (never a hand-rolled directory walk — §7 carried item C), which searches
    /// <see cref="CardLayout.ResolveRecordDirectories"/> in full, including archived changes: a
    /// question is always repository-scoped (<see cref="CardScope.Repository"/>, under <see cref="
    /// CardLayout.RegisterDirectory"/>) and is never itself archived, so this only ever matters for
    /// the id resolving at all, not for where it happens to be found.
    ///
    /// <para>
    /// <b>Only ownership decides, not kind (Architect ruling, §9 block D).</b> A card can be
    /// <c>blocked_by</c> any kind, and a <c>question</c> owned by any role other than
    /// <see cref="CardOwner.ProductOwner"/> does not halt anything here — it is surfaced to whoever
    /// owes its answer, not enforced, the asymmetry <c>10.10</c> restates. This checks
    /// <see cref="CardOwner.ProductOwner"/> ownership on a resolved <c>question</c> card
    /// specifically (never any other kind), which is what makes it "only a Product Owner's open
    /// question stops a card" rather than "any open question stops a card that happens to also be
    /// owned by the Product Owner" — the two would coincide today (a question is the only kind this
    /// build lets a caller name in <c>blocked_by</c> that carries an owner meaningfully distinct
    /// from its raiser), but this reads <see cref="IsQuestionCard"/> and <see cref="CardLifecycle.
    /// IsClosed"/> explicitly rather than owner alone, so a future kind added to <c>blocked_by</c>
    /// with its own owner does not silently start halting cards here too.
    /// </para>
    ///
    /// <para>
    /// <b>Resolution failures are conservative by omission, not by refusal (Architect ruling).</b>
    /// A <c>blocked_by</c> id that does not resolve, resolves to more than one file, or cannot be
    /// confirmed because some other file is unreadable is silently skipped by this check — none of
    /// those is evidence of an <em>open Product Owner question</em>, which is the one fact this
    /// guard exists to act on, and manufacturing a refusal out of an unrelated resolution problem
    /// would conflate two different failures under one rule.
    /// </para>
    ///
    /// <para>
    /// <b>Internal, not private (§10 block C).</b> <see cref="DerivedStateAssembler"/> reuses this
    /// exact method for <c>state</c>'s own halting determination (working-context: "a question
    /// owned by the Product Owner ... SHALL halt the cards it blocks") — the same "which blocking
    /// question halts this card" fact the write-path refusal above already computes, read rather
    /// than re-derived a second way that could silently drift from this one.
    /// </para>
    /// </summary>
    internal static (string QuestionId, string Title)? FindBlockingOpenProductOwnerQuestion(string cardsRoot, CardFile card)
    {
        foreach (var id in card.BlockFields.BlockedBy)
        {
            var resolvedQuestion = CardIdentityResolver.Resolve(cardsRoot, id).Match<CardFile?>(
                onFound: static (_, found) => found,
                onNotFound: static _ => null,
                onDuplicate: static (_, _) => null,
                onUnreadable: static (_, _) => null);

            if (resolvedQuestion is not { } questionCard || !IsQuestionCard(questionCard) || questionCard.Frontmatter.Owner != CardOwner.ProductOwner)
            {
                continue;
            }

            if (CardLifecycle.IsClosed(questionCard))
            {
                continue;
            }

            return (questionCard.Frontmatter.Id, questionCard.Frontmatter.Title);
        }

        return null;
    }

    /// <summary>
    /// Appends one Product Owner authorisation to the section card at <paramref name="filePath"/>
    /// (work-lifecycle: "Remediation beyond the second round requires recorded authorisation", §8a
    /// block C) — reads the current card, and only if the acting role is <see cref="CardOwner.
    /// ProductOwner"/> and the card is a section card, appends the entry under the card's lock, the
    /// same read-decide-write shape <see cref="RecordApproval"/> established for its own
    /// role-checked write. Recording one never spends it — see <see cref="SectionAuthorisationEntry"/>'s
    /// own doc comment for how spending is derived, entirely inside <see cref="
    /// RecordSectionVerdictUnderExistingLock"/>, from nothing this method itself does.
    /// </summary>
    /// <param name="reason">Why the bound was pushed further — never empty or whitespace-only,
    /// checked by <see cref="Callboard.Cli.CommandParser"/>'s <c>section authorise</c> parse arm
    /// before this is ever called, the same argv-decidable-first discipline every other free-text
    /// field here follows.</param>
    internal static CardSectionAuthorisationOutcome RecordSectionAuthorisation(
        string cardsRoot, string filePath, string reason, CardOwner actingRole, DateTimeOffset timestamp, TimeSpan lockTimeout, string? changeName = null) =>
        WithLock(
            filePath,
            lockTimeout,
            heldLock => RecordSectionAuthorisationUnderExistingLock(heldLock, cardsRoot, reason, actingRole, timestamp, changeName),
            onTimedOut: timedOut => new CardSectionAuthorisationOutcome.ToolFailure(timedOut.Message));

    /// <summary>
    /// The read-decide-write step of <see cref="RecordSectionAuthorisation"/>. Same structural lock
    /// precondition as every other <c>*UnderExistingLock</c> method on this type — the target is
    /// <see cref="CardLock.CardPath"/>, not a separately supplied <c>filePath</c>. The role check
    /// runs immediately after a successful <see cref="ReadCard"/>, not ahead of
    /// <see cref="File.Exists(string)"/> — the ordering <see cref="RecordApprovalUnderExistingLock"/>'s
    /// own doc comment now justifies (§9 block B reviewer/architect ruling, overruling this method's
    /// own first pass): this method's one lock is already held regardless of where the check sits,
    /// <see cref="IsAuthorisingRole"/> is a pure function of <see cref="CardOwner"/> with no card
    /// dependency, so nothing is skipped by moving it here except a read every other case in this
    /// method already pays — and recording matters more than the reorder, since a Product-Owner-only
    /// verb attempted by another role is exactly the pattern process-enforcement's "so that a pattern
    /// of refusals is itself visible" exists to catch (§9 remediation S3).
    ///
    /// <para>
    /// <b>§8a block C remediation, Architect ruling: recording is refused unless the section is
    /// already at the bound with none unspent.</b> An authorisation banked ahead of need satisfies
    /// the one-for-one count literally while defeating it — its reason would be written before the
    /// round it discharges, and a reason for a round that has not happened cannot be one. So this
    /// call refuses unless a <c>request-changes</c> verdict is, at this exact moment, being refused
    /// for want of one — checked via the same <see cref="SectionRemediationBoundState"/> derivation
    /// <see cref="RecordSectionVerdictUnderExistingLock"/> reads for its own bound check, so the
    /// two can never drift on what "at the bound" means.
    /// </para>
    /// </summary>
    internal static CardSectionAuthorisationOutcome RecordSectionAuthorisationUnderExistingLock(
        CardLock heldLock, string cardsRoot, string reason, CardOwner actingRole, DateTimeOffset timestamp, string? changeName)
    {
        ArgumentNullException.ThrowIfNull(heldLock);
        var filePath = heldLock.CardPath;

        if (!File.Exists(filePath))
        {
            return new CardSectionAuthorisationOutcome.CardNotFound(filePath);
        }

        var current = ReadCard(filePath);
        return current.Match<CardSectionAuthorisationOutcome>(
            onSuccess: success =>
            {
                var card = success.Card;

                if (ReservedDerivedStateFieldKeyIn(card) is { } reservedKey)
                {
                    return RefuseAndRecord<CardSectionAuthorisationOutcome, CardSectionAuthorisationOutcome.HandEnteredDerivedState>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardSectionAuthorisationOutcome.HandEnteredDerivedState(reservedKey),
                        static reason => new CardSectionAuthorisationOutcome.ToolFailure(reason));
                }

                if (!IsAuthorisingRole(actingRole))
                {
                    return RefuseAndRecord<CardSectionAuthorisationOutcome, CardSectionAuthorisationOutcome.RoleNotPermitted>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardSectionAuthorisationOutcome.RoleNotPermitted(actingRole),
                        static reason => new CardSectionAuthorisationOutcome.ToolFailure(reason));
                }

                if (!IsSectionCard(card))
                {
                    return RefuseAndRecord<CardSectionAuthorisationOutcome, CardSectionAuthorisationOutcome.NotASectionCard>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardSectionAuthorisationOutcome.NotASectionCard(card.Frontmatter.Kind),
                        static reason => new CardSectionAuthorisationOutcome.ToolFailure(reason));
                }

                var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return new CardSectionAuthorisationOutcome.LayoutMismatch(layoutFailure!.Reason);
                }

                // work-lifecycle: "Recording an authorisation SHALL be refused unless the section
                // is already at the bound with none unspent" (§8a block C remediation, Architect
                // ruling: banking authorisations ahead of need satisfies the one-for-one count
                // literally while defeating what it is for — the recorded reason has to describe a
                // round that has actually happened). Same derivation RecordSectionVerdictUnderExistingLock's
                // own bound check reads, so the two can never drift on what "at the bound" means.
                var (priorRequestChanges, unspentAuthorisations) = SectionRemediationBoundState(card.SectionFields);
                if (priorRequestChanges < 2 || unspentAuthorisations > 0)
                {
                    return RefuseAndRecord<CardSectionAuthorisationOutcome, CardSectionAuthorisationOutcome.NotAtBound>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardSectionAuthorisationOutcome.NotAtBound(priorRequestChanges, unspentAuthorisations),
                        static reason => new CardSectionAuthorisationOutcome.ToolFailure(reason));
                }

                var entry = new SectionAuthorisationEntry(actingRole, reason, timestamp, []);
                var updated = card with
                {
                    Frontmatter = card.Frontmatter with { Updated = timestamp },
                    SectionFields = card.SectionFields with { Authorisations = [.. card.SectionFields.Authorisations, entry] },
                };

                var writeResult = AtomicWrite(anchored, CardFileWriter.Serialize(updated));
                return writeResult.Match<CardSectionAuthorisationOutcome>(
                    onSuccess: _ => new CardSectionAuthorisationOutcome.Recorded(updated, entry),
                    onNotFound: notFound => new CardSectionAuthorisationOutcome.CardNotFound(notFound.FilePath),
                    onAlreadyExists: alreadyExists => new CardSectionAuthorisationOutcome.LayoutMismatch(
                        $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite."),
                    onLayoutMismatch: layoutMismatch => new CardSectionAuthorisationOutcome.LayoutMismatch(layoutMismatch.Reason),
                    onCorrupt: corrupt => new CardSectionAuthorisationOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason),
                    onToolFailure: toolFailure => new CardSectionAuthorisationOutcome.ToolFailure(toolFailure.Reason),
                    onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
            },
            onFailure: failure =>
                new CardSectionAuthorisationOutcome.CardCorrupt(filePath, failure.Reason));
    }

    /// <summary>
    /// Appends one supervisor verdict to the section card at <paramref name="filePath"/>
    /// (work-lifecycle: "Sections are entities" — "the verdict, the range and the acting role are
    /// recorded against that section entity", §5 block E) — reads the current card, and only if it
    /// is a section card, appends the entry under the card's lock, the same read-decide-write shape
    /// <see cref="ApplyBlockTransition"/> established. A second verdict for the same section is a
    /// second entry, not an upsert — see <see cref="SectionVerdictEntry"/>'s own doc comment for
    /// why (unlike <see cref="RecordGateResult"/>'s label-keyed upsert).
    ///
    /// <para>
    /// <b>§8a block B's addition: section remediation is discharged in the same write (work-lifecycle:
    /// "Section remediation follows the finding, not the verdict").</b> <paramref name="
    /// recurringFindingCardPaths"/> names every card (resolved from a caller's <c>--finding-recurred
    /// &lt;id&gt;</c>, at the CLI layer, before this call — the same "resolve, then pass a path"
    /// shape <c>block approve --id</c> already established) that this verdict reports as still
    /// unresolved: each is returned to <c>briefed</c> via <see cref="BlockFlowTransitions.
    /// FindingRecurredTransition"/>, with <c>round</c> incremented (work-lifecycle: "round += 1 on
    /// all three"). <paramref name="newFindings"/> names every first-time finding this verdict
    /// raises — any number of them (§8a block B revision, Architect ruling: a section with several
    /// new findings on its first review is the ordinary case, not a corner one; see the DEVLOG "§8a
    /// block B — architect: accept the design, reject the one-new-finding cap"): one brand-new
    /// <c>block</c> card is created per entry, in order, each carrying <see cref="
    /// NewFindingCardRequest.Body"/> as its brief and <see cref="NewFindingCardRequest.Key"/> as its
    /// <see cref="BlockCardFields.FindingKey"/>. A single call can carry both recurrences and new
    /// findings together (work-lifecycle: "A single verdict MAY do both") — see <see cref="
    /// RecordSectionVerdictUnderExistingLock"/>'s own doc comment for the locking and write-ordering
    /// this needs.
    /// </para>
    /// </summary>
    internal static CardSectionVerdictOutcome RecordSectionVerdict(
        string cardsRoot,
        string filePath,
        SectionVerdict verdict,
        string rangeFrom,
        string rangeTo,
        CardOwner actingRole,
        DateTimeOffset timestamp,
        TimeSpan lockTimeout,
        string? changeName,
        IReadOnlyList<string> recurringFindingCardPaths,
        IReadOnlyList<NewFindingCardRequest> newFindings) =>
        WithLock(
            filePath,
            lockTimeout,
            heldLock => RecordSectionVerdictUnderExistingLock(
                heldLock, cardsRoot, verdict, rangeFrom, rangeTo, actingRole, timestamp, lockTimeout, changeName, recurringFindingCardPaths, newFindings),
            onTimedOut: timedOut => new CardSectionVerdictOutcome.ToolFailure(timedOut.Message));

    /// <summary>
    /// The read-decide-write step of <see cref="RecordSectionVerdict"/>. Same structural lock
    /// precondition as every other <c>*UnderExistingLock</c> method on this type — the target is
    /// <see cref="CardLock.CardPath"/>, not a separately supplied <c>filePath</c>.
    ///
    /// <para>
    /// <b>Locking shape (§8a block B, same reasoning as <see cref="CloseSectionUnderExistingLock"/>'s
    /// own "N cards is not two").</b> The section's own lock is already held (<paramref name="
    /// heldLock"/>). <paramref name="recurringFindingCardPaths"/> is deduplicated and sorted
    /// ordinally, then locked in that order, <em>blocking</em>, against one shared deadline computed
    /// from <paramref name="lockTimeout"/> — the same "sort first, so two concurrent invocations
    /// naming the same set always compute the identical order" fix <see
    /// cref="CloseSectionUnderExistingLock"/>'s own doc comment explains, needed here because
    /// (unlike that method's directory-scan-derived candidate set) these paths are caller-typed:
    /// two invocations naming the same physical files in a different <c>--finding-recurred</c>
    /// order would otherwise risk an AB/BA deadlock, the same class of defect <see
    /// cref="AcquireLocksAndRecord"/>'s own doc comment discusses at length for <see
    /// cref="RecordFinding"/>. No other writer on this type ever holds a block's lock while
    /// blocking on a section's, so this fixed order cannot cycle against a concurrent writer either
    /// (the same argument <see cref="CloseSectionUnderExistingLock"/> already makes). Every brand-new
    /// card in <paramref name="newFindings"/> needs no lock of its own here — nothing else can
    /// reference a file that does not yet exist — and is written through the ordinary <see
    /// cref="WriteCard"/> create-only path, which acquires and releases its own lock for that one
    /// call.
    /// </para>
    ///
    /// <para>
    /// <b>Validate everything, then write (work-lifecycle: "A single verdict MAY do both" — 8a.10:
    /// "the write is all-or-none").</b> Every recurring target is re-read fresh once its lock is
    /// held and checked — task-implementing (work-lifecycle's own definition: "A block card
    /// carrying tasks is task-implementing; a remediation card carries none") and current-state —
    /// before any card is written. Every entry in <paramref name="newFindings"/> is checked against
    /// one key-ownership scan of the section's own directory, taken once, before any card is
    /// written: an entry whose key already names an owner (whether an existing on-disk card, or an
    /// <em>earlier</em> entry in this same call — §8a block B revision's own addition, since a
    /// single verdict can now name more than one new finding) is refused, the two cases
    /// distinguished only by whether <see cref="CardSectionVerdictOutcome.FindingAlreadyOwned.
    /// OwningCardId"/> names a real card id or the literal <c>"&lt;pending: this verdict&gt;"</c>
    /// sentinel, since an in-batch collision has no on-disk owner yet to name honestly. A refusal
    /// found at any point leaves the section card, every targeted remediation card, and the
    /// filesystem at every new card's path exactly as found. Writes then proceed recurring cards
    /// first, every new card second (in <paramref name="newFindings"/> order), the section's own
    /// verdict entry last — the same "the entity recording that the operation happened is written
    /// last" ordering <see cref="CloseSectionUnderExistingLock"/> already established, for the same
    /// reason: a crash after some writes land still leaves an honest, inspectable partial state
    /// rather than a section claiming a verdict it has not fully discharged. <b>Stated honestly,
    /// not claimed as atomic</b> (the same limitation <see cref="CloseSectionUnderExistingLock"/>'s
    /// own doc comment accepts for its own N-card write): a tool-failure partway through — as
    /// opposed to a refusal, which never writes anything — is not rolled back here. A retried call
    /// naming the same recurring targets would find them no longer <c>approved</c> and refuse
    /// loudly, rather than silently reapplying; that refusal is itself the record that the retry
    /// needs different arguments, not a corruption.
    /// </para>
    /// </summary>
    internal static CardSectionVerdictOutcome RecordSectionVerdictUnderExistingLock(
        CardLock heldLock,
        string cardsRoot,
        SectionVerdict verdict,
        string rangeFrom,
        string rangeTo,
        CardOwner actingRole,
        DateTimeOffset timestamp,
        TimeSpan lockTimeout,
        string? changeName,
        IReadOnlyList<string> recurringFindingCardPaths,
        IReadOnlyList<NewFindingCardRequest> newFindings)
    {
        ArgumentNullException.ThrowIfNull(heldLock);
        var filePath = heldLock.CardPath;

        if (!File.Exists(filePath))
        {
            return new CardSectionVerdictOutcome.CardNotFound(filePath);
        }

        var current = ReadCard(filePath);
        return current.Match<CardSectionVerdictOutcome>(
            onSuccess: success =>
            {
                var card = success.Card;
                if (ReservedDerivedStateFieldKeyIn(card) is { } reservedKey)
                {
                    return RefuseAndRecord<CardSectionVerdictOutcome, CardSectionVerdictOutcome.HandEnteredDerivedState>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardSectionVerdictOutcome.HandEnteredDerivedState(filePath, reservedKey),
                        static reason => new CardSectionVerdictOutcome.ToolFailure(reason));
                }

                if (!IsSectionCard(card))
                {
                    return RefuseAndRecord<CardSectionVerdictOutcome, CardSectionVerdictOutcome.NotASectionCard>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardSectionVerdictOutcome.NotASectionCard(card.Frontmatter.Kind),
                        static reason => new CardSectionVerdictOutcome.ToolFailure(reason));
                }

                var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return new CardSectionVerdictOutcome.LayoutMismatch(layoutFailure!.Reason);
                }

                // work-lifecycle: "Remediation beyond the second round requires recorded
                // authorisation" (§8a block C). Derived here, at the moment it is asked, from
                // nothing but SectionRemediationBoundState's own two counts — never a stored
                // figure (8a.14). The bound applies once this call's own verdict would become the
                // section's third-or-later request-changes verdict, and no unspent authorisation
                // covers it.
                if (verdict == SectionVerdict.RequestChanges)
                {
                    var (priorRequestChanges, unspentAuthorisations) = SectionRemediationBoundState(card.SectionFields);
                    if (priorRequestChanges >= 2 && unspentAuthorisations <= 0)
                    {
                        return RefuseAndRecord<CardSectionVerdictOutcome, CardSectionVerdictOutcome.RemediationBoundExceeded>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                            new CardSectionVerdictOutcome.RemediationBoundExceeded(
                                priorRequestChanges + 1, card.SectionFields.Authorisations.Length, unspentAuthorisations),
                            static reason => new CardSectionVerdictOutcome.ToolFailure(reason));
                    }
                }

                // Validated here, before anything else — pure computation, no filesystem side
                // effect — but the directory itself is deliberately NOT created yet (reviewer
                // finding, block B nit): creating it this early would let a call that goes on to
                // refuse (recurring-target validation, the key-ownership scan below) still leave a
                // stray empty directory behind for a finding that was never written. Each
                // survivor's directory is created inside WriteCard itself, immediately before that
                // one card's own write — see the newCardsWritten loop below — so nothing touches
                // the filesystem until the whole call is known to be going ahead.
                foreach (var newFinding in newFindings)
                {
                    if (string.IsNullOrEmpty(Path.GetDirectoryName(newFinding.FilePath)))
                    {
                        return new CardSectionVerdictOutcome.LayoutMismatch($"'{newFinding.FilePath}' has no containing directory to write into.");
                    }
                }

                var sortedRecurringPaths = recurringFindingCardPaths
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static path => path, StringComparer.Ordinal)
                    .ToList();

                var acquiredLocks = new List<CardLock>(sortedRecurringPaths.Count);
                try
                {
                    var deadline = DateTimeOffset.UtcNow + lockTimeout;
                    foreach (var recurringPath in sortedRecurringPaths)
                    {
                        var remaining = deadline - DateTimeOffset.UtcNow;
                        if (remaining < TimeSpan.Zero)
                        {
                            remaining = TimeSpan.Zero;
                        }

                        var lockResult = CardLock.Acquire(recurringPath, remaining);
                        var acquireFailure = lockResult.Match<CardSectionVerdictOutcome?>(
                            onAcquired: acquired =>
                            {
                                acquiredLocks.Add(acquired.Lock);
                                return null;
                            },
                            onTimedOut: timedOut => new CardSectionVerdictOutcome.ToolFailure(timedOut.Message));
                        if (acquireFailure is not null)
                        {
                            return acquireFailure;
                        }
                    }

                    var recurringCards = new List<(string FilePath, CardFile Card)>(sortedRecurringPaths.Count);
                    foreach (var recurringPath in sortedRecurringPaths)
                    {
                        if (!File.Exists(recurringPath))
                        {
                            return RefuseAndRecord<CardSectionVerdictOutcome, CardSectionVerdictOutcome.RecurringTargetNotFound>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                                new CardSectionVerdictOutcome.RecurringTargetNotFound(recurringPath),
                                static reason => new CardSectionVerdictOutcome.ToolFailure(reason));
                        }

                        var reread = ReadCard(recurringPath);
                        var rereadCard = reread.Match<CardFile?>(onSuccess: static s => s.Card, onFailure: static _ => null);
                        if (rereadCard is null)
                        {
                            var reason = reread.Match(onSuccess: static _ => string.Empty, onFailure: static failure => failure.Reason);
                            return new CardSectionVerdictOutcome.CardCorrupt(recurringPath, reason);
                        }

                        if (!IsBlockCard(rereadCard))
                        {
                            // The CLI layer's own ResolveCardReference already refused any
                            // --finding-recurred id that does not name a block card before this
                            // method was ever called — defensive-unreachable, not a live path (the
                            // same discipline ValidateBlockForLanding's own guard follows).
                            throw new InvalidOperationException($"'{recurringPath}' is not a block card; the caller must resolve --finding-recurred ids against block cards only.");
                        }

                        if (ReservedDerivedStateFieldKeyIn(rereadCard) is { } recurringReservedKey)
                        {
                            return RefuseAndRecord<CardSectionVerdictOutcome, CardSectionVerdictOutcome.HandEnteredDerivedState>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                                new CardSectionVerdictOutcome.HandEnteredDerivedState(recurringPath, recurringReservedKey),
                                static reason => new CardSectionVerdictOutcome.ToolFailure(reason));
                        }

                        if (!RoundAgreesWithHistory(rereadCard, out var recurringStoredRound, out var recurringExpectedRound))
                        {
                            return RefuseAndRecord<CardSectionVerdictOutcome, CardSectionVerdictOutcome.RoundDisagreesWithHistory>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                                new CardSectionVerdictOutcome.RoundDisagreesWithHistory(recurringPath, recurringStoredRound, recurringExpectedRound),
                                static reason => new CardSectionVerdictOutcome.ToolFailure(reason));
                        }

                        if (rereadCard.BlockFields.Tasks.Length > 0)
                        {
                            return RefuseAndRecord<CardSectionVerdictOutcome, CardSectionVerdictOutcome.RecurringFindingTargetsTaskImplementingBlock>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                                new CardSectionVerdictOutcome.RecurringFindingTargetsTaskImplementingBlock(rereadCard.Frontmatter.Id, recurringPath),
                                static reason => new CardSectionVerdictOutcome.ToolFailure(reason));
                        }

                        if (!BlockFlowStateWireFormat.TryParse(rereadCard.Frontmatter.Status, out var recurringState))
                        {
                            return new CardSectionVerdictOutcome.CardCorrupt(
                                recurringPath, $"unrecognised status: '{rereadCard.Frontmatter.Status}'. Recognised statuses: {BlockFlowStateWireFormat.RecognisedValues}.");
                        }

                        // §8a remediation: a direct state comparison, not a membership test against
                        // BlockFlowTransitions.AvailableFrom — "is this card approved?" was never
                        // honestly an "is finding-recurred on the edge table?" question, and asking
                        // it that way is what let AvailableFrom's own widening (§8a block B) turn
                        // into a state predicate in the first place (supervisor finding, §8a section
                        // review).
                        if (recurringState != BlockFlowState.Approved)
                        {
                            return RefuseAndRecord<CardSectionVerdictOutcome, CardSectionVerdictOutcome.RecurringFindingNotApproved>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                                new CardSectionVerdictOutcome.RecurringFindingNotApproved(rereadCard.Frontmatter.Id, recurringPath, recurringState),
                                static reason => new CardSectionVerdictOutcome.ToolFailure(reason));
                        }

                        recurringCards.Add((recurringPath, rereadCard));
                    }

                    if (newFindings.Count > 0)
                    {
                        // One scan of the section, seeding every existing owner (key -> (id, path));
                        // grown below as each newFindings entry is validated, so a later entry in
                        // this same call sees an earlier entry's key too (§8a block B revision).
                        var ownedKeys = new Dictionary<string, (string OwnerId, string OwnerFilePath)>(StringComparer.Ordinal);
                        var sectionDirectory = Path.GetDirectoryName(filePath)!;
                        foreach (var (candidatePath, parseResult) in ReadAllCards(sectionDirectory))
                        {
                            var corruptReason = parseResult.Match<string?>(onSuccess: static _ => null, onFailure: static failure => failure.Reason);
                            if (corruptReason is not null)
                            {
                                // Conservative by construction, same reason CloseSectionUnderExistingLock
                                // refuses on an unreadable candidate: an unreadable card's finding_key
                                // cannot be checked, so it cannot be ruled out as this key's owner.
                                return new CardSectionVerdictOutcome.CardCorrupt(candidatePath, corruptReason);
                            }

                            var parsedCard = parseResult.Match(onSuccess: static s => s.Card, onFailure: static _ => throw new InvalidOperationException("unreachable: corruptReason above already returned on failure."));
                            if (IsBlockCard(parsedCard)
                                && string.Equals(parsedCard.Frontmatter.Section, card.Frontmatter.Id, StringComparison.Ordinal)
                                && parsedCard.BlockFields.FindingKey is { } existingKey)
                            {
                                ownedKeys[existingKey] = (parsedCard.Frontmatter.Id, candidatePath);
                            }
                        }

                        var usedNewFindingFilePaths = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var newFinding in newFindings)
                        {
                            if (ownedKeys.TryGetValue(newFinding.Key, out var owner))
                            {
                                return RefuseAndRecord<CardSectionVerdictOutcome, CardSectionVerdictOutcome.FindingAlreadyOwned>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                                    new CardSectionVerdictOutcome.FindingAlreadyOwned(newFinding.Key, owner.OwnerId, owner.OwnerFilePath),
                                    static reason => new CardSectionVerdictOutcome.ToolFailure(reason));
                            }

                            if (File.Exists(newFinding.FilePath) || !usedNewFindingFilePaths.Add(newFinding.FilePath))
                            {
                                return RefuseAndRecord<CardSectionVerdictOutcome, CardSectionVerdictOutcome.NewFindingCardAlreadyExists>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                                    new CardSectionVerdictOutcome.NewFindingCardAlreadyExists(newFinding.FilePath),
                                    static reason => new CardSectionVerdictOutcome.ToolFailure(reason));
                            }

                            var newAnchored = AnchoredCardPath.TryCreate(cardsRoot, newFinding.FilePath, CardScope.Change, changeName, out var newLayoutFailure);
                            if (newAnchored is null)
                            {
                                return new CardSectionVerdictOutcome.LayoutMismatch(newLayoutFailure!.Reason);
                            }

                            // In-batch dedupe: a later entry in this same call naming the same key
                            // is caught above too, and reports this manifest's own path, not an
                            // on-disk card — there is none yet.
                            ownedKeys[newFinding.Key] = ("<pending: this verdict>", newFinding.FilePath);
                        }
                    }

                    var recurredWritten = new List<CardFile>(recurringCards.Count);
                    var findingRecurredTransition = BlockFlowTransitions.FindingRecurredTransition;
                    foreach (var (recurringPath, recurringCard) in recurringCards)
                    {
                        var recurringAnchored = AnchoredCardPath.TryCreate(cardsRoot, recurringPath, recurringCard.Frontmatter.Scope, changeName, out var recurringLayoutFailure);
                        if (recurringAnchored is null)
                        {
                            return new CardSectionVerdictOutcome.LayoutMismatch(recurringLayoutFailure!.Reason);
                        }

                        var recurringEntry = new CardBlockTransitionEntry(
                            actingRole, findingRecurredTransition.Name, findingRecurredTransition.From, findingRecurredTransition.To, timestamp, []);
                        var recurringUpdated = recurringCard with
                        {
                            Frontmatter = recurringCard.Frontmatter with { Status = findingRecurredTransition.To.ToWireString(), Updated = timestamp },
                            BlockFields = recurringCard.BlockFields with { Round = (recurringCard.BlockFields.Round ?? 1) + 1 },
                            Transitions = [.. recurringCard.Transitions, recurringEntry],
                        };

                        var recurringWriteResult = AtomicWrite(recurringAnchored, CardFileWriter.Serialize(recurringUpdated));
                        var recurringWriteFailure = recurringWriteResult.Match<CardSectionVerdictOutcome?>(
                            onSuccess: static _ => null,
                            onNotFound: notFound => new CardSectionVerdictOutcome.CardNotFound(notFound.FilePath),
                            onAlreadyExists: alreadyExists => new CardSectionVerdictOutcome.LayoutMismatch(
                                $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite."),
                            onLayoutMismatch: layoutMismatch => new CardSectionVerdictOutcome.LayoutMismatch(layoutMismatch.Reason),
                            onCorrupt: corrupt => new CardSectionVerdictOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason),
                            onToolFailure: toolFailure => new CardSectionVerdictOutcome.ToolFailure(toolFailure.Reason),
                            onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
                        if (recurringWriteFailure is not null)
                        {
                            return recurringWriteFailure;
                        }

                        recurredWritten.Add(recurringUpdated);
                    }

                    var newCardsWritten = new List<CardFile>(newFindings.Count);
                    foreach (var newFinding in newFindings)
                    {
                        var (newId, allocationFailure) = AllocateIdentity(cardsRoot, CardKind.Block, lockTimeout);
                        if (allocationFailure is not null)
                        {
                            return new CardSectionVerdictOutcome.ToolFailure(allocationFailure);
                        }

                        var newFrontmatter = new CardFrontmatter(
                            newId!, CardKind.Block, newFinding.Title, BlockFlowState.Briefed.ToWireString(), actingRole,
                            CardScope.Change, card.Frontmatter.Id, timestamp, timestamp);
                        var newBlockFields = new BlockCardFields(null, null, [], 1, [], [], newFinding.Key);

                        var newWriteResult = WriteCard(
                            cardsRoot, newFinding.FilePath, new NewCardFile(newFrontmatter, newFinding.Body, BlockFields: newBlockFields), lockTimeout, changeName);
                        var newWriteFailure = newWriteResult.Match<CardSectionVerdictOutcome?>(
                            onSuccess: static _ => null,
                            onNotFound: notFound => new CardSectionVerdictOutcome.CardNotFound(notFound.FilePath),
                            onAlreadyExists: alreadyExists => new CardSectionVerdictOutcome.NewFindingCardAlreadyExists(alreadyExists.FilePath),
                            onLayoutMismatch: layoutMismatch => new CardSectionVerdictOutcome.LayoutMismatch(layoutMismatch.Reason),
                            onCorrupt: corrupt => new CardSectionVerdictOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason),
                            onToolFailure: toolFailure => new CardSectionVerdictOutcome.ToolFailure(toolFailure.Reason),
                            onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
                        if (newWriteFailure is not null)
                        {
                            return newWriteFailure;
                        }

                        newCardsWritten.Add(new CardFile(newFrontmatter, newFinding.Body, [], [], BlockFields: newBlockFields));
                    }

                    var entry = new SectionVerdictEntry(actingRole, verdict, rangeFrom, rangeTo, timestamp, []);
                    var updated = card with
                    {
                        Frontmatter = card.Frontmatter with { Updated = timestamp },
                        SectionFields = card.SectionFields with { Verdicts = [.. card.SectionFields.Verdicts, entry] },
                    };

                    var writeResult = AtomicWrite(anchored, CardFileWriter.Serialize(updated));
                    return writeResult.Match<CardSectionVerdictOutcome>(
                        onSuccess: _ => new CardSectionVerdictOutcome.Recorded(updated, entry, recurredWritten, newCardsWritten),
                        onNotFound: notFound => new CardSectionVerdictOutcome.CardNotFound(notFound.FilePath),
                        onAlreadyExists: alreadyExists => new CardSectionVerdictOutcome.LayoutMismatch(
                            $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite."),
                        onLayoutMismatch: layoutMismatch => new CardSectionVerdictOutcome.LayoutMismatch(layoutMismatch.Reason),
                        onCorrupt: corrupt => new CardSectionVerdictOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason),
                        onToolFailure: toolFailure => new CardSectionVerdictOutcome.ToolFailure(toolFailure.Reason),
                        onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
                }
                finally
                {
                    foreach (var acquiredLock in acquiredLocks)
                    {
                        acquiredLock.Dispose();
                    }
                }
            },
            onFailure: failure =>
                new CardSectionVerdictOutcome.CardCorrupt(filePath, failure.Reason));
    }

    /// <summary>
    /// Closes the section card at <paramref name="filePath"/> (work-lifecycle: "Sections are
    /// entities" — "closing it SHALL record the acting role and the time", §5 block E; "Approval is
    /// provisional until the section closes", §8a block A) — reads the current card, and only if it
    /// is a section card not already closed, lands every <c>approved</c> block the section owns and
    /// then writes <c>status: closed</c> plus <c>closed_by</c>/<c>closed_at</c>, all under lock. §9
    /// block E's closing conditions (open obligations, undeferred questions, unresolved addressed
    /// threads, a landing block blocked by an open Product Owner question) are checked here too —
    /// see <see cref="CardSectionCloseOutcome"/>'s own doc comment for where each lives.
    /// </summary>
    internal static CardSectionCloseOutcome CloseSection(
        string cardsRoot, string filePath, CardOwner actingRole, DateTimeOffset timestamp, TimeSpan lockTimeout, string? changeName = null) =>
        WithLock(
            filePath,
            lockTimeout,
            heldLock => CloseSectionUnderExistingLock(heldLock, cardsRoot, actingRole, timestamp, lockTimeout, changeName),
            onTimedOut: timedOut => new CardSectionCloseOutcome.ToolFailure(timedOut.Message));

    /// <summary>
    /// The read-decide-write step of <see cref="CloseSection"/>. Same structural lock precondition
    /// as every other <c>*UnderExistingLock</c> method on this type — the target is
    /// <see cref="CardLock.CardPath"/>, not a separately supplied <c>filePath</c>.
    ///
    /// <para>
    /// <b>Locking shape (§8a block A brief: "N cards is not two").</b> The section's own lock is
    /// already held (<paramref name="heldLock"/>) by the time this runs. This method then discovers
    /// candidate blocks by scanning the section's own directory — <see cref="ReadAllCards"/> over
    /// <c>Path.GetDirectoryName(filePath)</c>, filtered to <see cref="IsBlockCard"/> cards whose
    /// <see cref="CardFrontmatter.Section"/> names this section's own <see cref="CardFrontmatter.Id"/>
    /// — and acquires each candidate's lock in turn, in the ordinal-sorted order
    /// <see cref="ReadAllCards"/> already returns them in, <em>blocking</em> for whatever remains of
    /// one overall deadline computed from <paramref name="lockTimeout"/>. Unlike <see
    /// cref="AcquireLocksAndRecord"/>'s acquire-probe-release-retry dance for <see
    /// cref="RecordFinding"/>'s two-card write, no probing or retrying is needed here, and blocking
    /// acquisition in a fixed order is safe: <see cref="AcquireLocksAndRecord"/>'s own doc comment
    /// explains that ordinal ordering breaks only when the <em>set</em> of paths being locked is
    /// caller-typed and can be spelled two different ways by two different invocations naming the
    /// same physical files (so two calls can disagree on the order and deadlock) — the same problem
    /// <see cref="SupersedeDecisionUnderLocks"/> does not have, for the same reason: every path this
    /// method locks comes from <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/>
    /// over one physical directory, not from caller-supplied text, so any two invocations that ever
    /// contend for the same physical file always compute it to the identical string and therefore
    /// the identical order. No other writer on this type ever holds a block's lock while blocking on
    /// a section's lock, or vice versa (every other block-mutating verb — <c>block gate</c>, <c>block
    /// approve</c>, <c>block transition</c> — takes exactly one lock, its
    /// own card's), so a fixed order over {this section's blocks} can never cycle against a
    /// concurrent writer holding one of them and wanting something else this method holds. If any
    /// acquisition in the chain times out, every lock already acquired in this call is released (the
    /// <c>finally</c> block below) and a tool-failure is returned naming what actually happened,
    /// before any card is read a second time or written at all.
    /// </para>
    ///
    /// <para>
    /// <b>Validate everything, then write — and even the write is honest about partial completion
    /// (§8a block A brief item 3).</b> Every candidate block is re-read fresh once its lock is held
    /// (a stale scan-time snapshot is never trusted) and checked by <see cref="
    /// ValidateBlockForLanding"/>; the first refusal found stops the whole call before any card is
    /// written, so a refusal leaves every card — section and blocks alike — exactly as it found them.
    /// Only once every block has passed does this method write: blocks first (skipping any already
    /// <c>landed</c>, work-lifecycle: "a block already landed is skipped rather than refused"), the
    /// section's own <c>status: closed</c> last. That ordering is deliberate, not incidental — a
    /// process crash or an I/O failure partway through the block writes leaves the section open and
    /// every already-landed block exactly that, so a retried <c>section close</c> call picks up
    /// wherever the last one stopped rather than needing to be undone first. No rollback is attempted
    /// on a write failure (unlike <see cref="RecordFindingUnderLocks"/>'s or <see
    /// cref="SupersedeDecisionUnderLocks"/>'s two-card compensating writes) — for N cards, undoing
    /// M &lt; N already-landed blocks would itself need to succeed to restore consistency, trading one
    /// partial-failure mode for another. Idempotent retry is the honest guarantee this method
    /// actually has, and the ordering above is what makes that guarantee true.
    /// </para>
    /// </summary>
    internal static CardSectionCloseOutcome CloseSectionUnderExistingLock(
        CardLock heldLock, string cardsRoot, CardOwner actingRole, DateTimeOffset timestamp, TimeSpan lockTimeout, string? changeName = null)
    {
        ArgumentNullException.ThrowIfNull(heldLock);
        var filePath = heldLock.CardPath;

        if (!File.Exists(filePath))
        {
            return new CardSectionCloseOutcome.CardNotFound(filePath);
        }

        var current = ReadCard(filePath);
        return current.Match<CardSectionCloseOutcome>(
            onSuccess: success =>
            {
                var card = success.Card;
                if (ReservedDerivedStateFieldKeyIn(card) is { } reservedKey)
                {
                    return RefuseAndRecord<CardSectionCloseOutcome, CardSectionCloseOutcome.HandEnteredDerivedState>(
                        cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardSectionCloseOutcome.HandEnteredDerivedState(filePath, reservedKey),
                        onToolFailure: static reason => new CardSectionCloseOutcome.ToolFailure(reason));
                }

                if (!IsSectionCard(card))
                {
                    return RefuseAndRecord<CardSectionCloseOutcome, CardSectionCloseOutcome.NotASectionCard>(
                        cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardSectionCloseOutcome.NotASectionCard(card.Frontmatter.Kind),
                        onToolFailure: static reason => new CardSectionCloseOutcome.ToolFailure(reason));
                }

                if (!SectionFlowStateWireFormat.TryParse(card.Frontmatter.Status, out var currentSectionState))
                {
                    return new CardSectionCloseOutcome.CardCorrupt(
                        filePath, $"unrecognised status: '{card.Frontmatter.Status}'. Recognised statuses: {SectionFlowStateWireFormat.RecognisedValues}.");
                }

                if (currentSectionState == SectionFlowState.Closed)
                {
                    return RefuseAndRecord<CardSectionCloseOutcome, CardSectionCloseOutcome.AlreadyClosed>(
                        cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardSectionCloseOutcome.AlreadyClosed(filePath),
                        onToolFailure: static reason => new CardSectionCloseOutcome.ToolFailure(reason));
                }

                var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return new CardSectionCloseOutcome.LayoutMismatch(layoutFailure!.Reason);
                }

                var sectionDirectory = Path.GetDirectoryName(filePath)!;
                var candidatePaths = new List<string>();
                foreach (var (candidatePath, parseResult) in ReadAllCards(sectionDirectory))
                {
                    var corruptReason = parseResult.Match<string?>(onSuccess: static _ => null, onFailure: static failure => failure.Reason);
                    if (corruptReason is not null)
                    {
                        // Conservative by construction: an unreadable card's `section` field cannot
                        // be checked, so it cannot be ruled out as one of this section's own blocks
                        // (see CardCorrupt's own doc comment for the ArchiveChange precedent).
                        return new CardSectionCloseOutcome.CardCorrupt(candidatePath, corruptReason);
                    }

                    var parsedCard = parseResult.Match(onSuccess: static s => s.Card, onFailure: static _ => throw new InvalidOperationException("unreachable: corruptReason above already returned on failure."));
                    if (IsBlockCard(parsedCard) && string.Equals(parsedCard.Frontmatter.Section, card.Frontmatter.Id, StringComparison.Ordinal))
                    {
                        candidatePaths.Add(candidatePath);
                    }
                }

                var acquiredLocks = new List<CardLock>(candidatePaths.Count);
                try
                {
                    var deadline = DateTimeOffset.UtcNow + lockTimeout;
                    foreach (var candidatePath in candidatePaths)
                    {
                        var remaining = deadline - DateTimeOffset.UtcNow;
                        if (remaining < TimeSpan.Zero)
                        {
                            remaining = TimeSpan.Zero;
                        }

                        var lockResult = CardLock.Acquire(candidatePath, remaining);
                        var acquireFailure = lockResult.Match<CardSectionCloseOutcome?>(
                            onAcquired: acquired =>
                            {
                                acquiredLocks.Add(acquired.Lock);
                                return null;
                            },
                            onTimedOut: timedOut => new CardSectionCloseOutcome.ToolFailure(timedOut.Message));
                        if (acquireFailure is not null)
                        {
                            return acquireFailure;
                        }
                    }

                    var freshBlocks = new List<(string FilePath, CardFile Card)>(candidatePaths.Count);
                    foreach (var candidatePath in candidatePaths)
                    {
                        if (!File.Exists(candidatePath))
                        {
                            return new CardSectionCloseOutcome.CardNotFound(candidatePath);
                        }

                        var reread = ReadCard(candidatePath);
                        var rereadCard = reread.Match<CardFile?>(onSuccess: static s => s.Card, onFailure: static _ => null);
                        if (rereadCard is null)
                        {
                            var reason = reread.Match(onSuccess: static _ => string.Empty, onFailure: static failure => failure.Reason);
                            return new CardSectionCloseOutcome.CardCorrupt(candidatePath, reason);
                        }

                        freshBlocks.Add((candidatePath, rereadCard));
                    }

                    foreach (var (blockFilePath, blockCard) in freshBlocks)
                    {
                        var refusal = ValidateBlockForLanding(cardsRoot, blockCard, blockFilePath, changeName, actingRole, timestamp);
                        if (refusal is not null)
                        {
                            return refusal;
                        }
                    }

                    // process-enforcement: "Section close settles its obligations" (§9 block E,
                    // 9.4). Obligations are CardScope.Change-scoped, so a fresh pass over this same
                    // sectionDirectory finds every one owed by this section (RegisterCardFields.
                    // OwedBy naming its id) — conservative the same way the block scan above already
                    // is: an unreadable card anywhere in this directory refuses the whole close.
                    var openObligations = new List<(string Id, string Title)>();
                    foreach (var (obligationPath, obligationParseResult) in ReadAllCards(sectionDirectory))
                    {
                        var obligationCorruptReason = obligationParseResult.Match<string?>(onSuccess: static _ => null, onFailure: static failure => failure.Reason);
                        if (obligationCorruptReason is not null)
                        {
                            return new CardSectionCloseOutcome.CardCorrupt(obligationPath, obligationCorruptReason);
                        }

                        var obligationCandidate = obligationParseResult.Match(onSuccess: static s => s.Card, onFailure: static _ => throw new InvalidOperationException("unreachable: obligationCorruptReason above already returned on failure."));
                        if (!IsObligationCard(obligationCandidate) || !string.Equals(obligationCandidate.RegisterFields?.OwedBy, card.Frontmatter.Id, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        // The !TryParse half of this condition is unreachable: §12 block A's parse door
                        // (CardFileParser.ValidateStatus) never hands back an obligation-kind CardFile whose
                        // status does not parse against RegisterLifecycleStateWireFormat — a hand-edited bad
                        // status fails to parse at all and is already caught above as obligationCorruptReason,
                        // which returns CardCorrupt (a refusal) before this line is ever reached. That is the
                        // fail-open door this block closes: before the parse door existed, a bad status parsed
                        // successfully as "not open" here and was silently skipped, letting the close proceed
                        // as if the obligation had never existed.
                        if (!RegisterLifecycleStateWireFormat.TryParse(obligationCandidate.Frontmatter.Status, out var obligationState) || !ReferenceEquals(obligationState, RegisterLifecycleState.Open))
                        {
                            continue;
                        }

                        openObligations.Add((obligationCandidate.Frontmatter.Id, obligationCandidate.Frontmatter.Title));
                    }

                    if (openObligations.Count > 0)
                    {
                        return RefuseAndRecord<CardSectionCloseOutcome, CardSectionCloseOutcome.OpenObligations>(
                            cardsRoot, card, filePath, changeName, actingRole, timestamp,
                            new CardSectionCloseOutcome.OpenObligations(card.Frontmatter.Id, openObligations),
                            onToolFailure: static reason => new CardSectionCloseOutcome.ToolFailure(reason));
                    }

                    // process-enforcement: "Section close settles its questions" (§9 block E, 9.5).
                    // Questions are CardScope.Repository-scoped and live outside this section's own
                    // directory, so this reads every live record directory the same way
                    // RuleCitations.UncitedOpenRules already does — an unreadable card out there is
                    // skipped, not refused (Architect ruling: "resolution failures are conservative
                    // by omission", the same precedent FindBlockingOpenProductOwnerQuestion already
                    // established for a question lookup outside a card's own directory).
                    foreach (var recordDirectory in CardLayout.ResolveLiveRecordDirectories(cardsRoot))
                    {
                        if (!Directory.Exists(recordDirectory))
                        {
                            continue;
                        }

                        foreach (var (_, questionParseResult) in ReadAllCards(recordDirectory))
                        {
                            var questionCandidate = questionParseResult.Match<CardFile?>(onSuccess: static s => s.Card, onFailure: static _ => null);
                            if (questionCandidate is null || !IsQuestionCard(questionCandidate)
                                || !string.Equals(questionCandidate.Frontmatter.Section, card.Frontmatter.Id, StringComparison.Ordinal))
                            {
                                continue;
                            }

                            if (!QuestionStatusWireFormat.TryParse(questionCandidate.Frontmatter.Status, out var questionState) || !ReferenceEquals(questionState, QuestionStatus.Open))
                            {
                                continue;
                            }

                            return RefuseAndRecord<CardSectionCloseOutcome, CardSectionCloseOutcome.OpenUndeferredQuestion>(
                                cardsRoot, card, filePath, changeName, actingRole, timestamp,
                                new CardSectionCloseOutcome.OpenUndeferredQuestion(card.Frontmatter.Id, questionCandidate.Frontmatter.Id, questionCandidate.Frontmatter.Title),
                                onToolFailure: static reason => new CardSectionCloseOutcome.ToolFailure(reason));
                        }
                    }

                    // process-enforcement: "Section close settles its addressed threads" (§9 block
                    // E, 9.6). Role-agnostic — this close is not acting as any one role.
                    //
                    // Absolute — no age qualifier (architect ruling on the worker's own ❓: the
                    // requirement's purpose clause — "to keep this gate from becoming a formality
                    // discharged in bulk at the moment of closing" — names a close-time failure a
                    // close-time exemption cannot prevent; carving one out would let a section close
                    // over exactly the threads neglected longest, rewarding the neglect). Every live
                    // addressed thread refuses, aged or not — see <see cref="FindAgeingAddressedThreads"/>
                    // for the separate, non-refusing surfacing "section status" reads instead, during
                    // the section's life rather than at the gate.
                    var sectionThreadIds = CardCommentRouting.LiveAddressedThreadIds(card.Comments);
                    if (sectionThreadIds.Count > 0)
                    {
                        return RefuseAndRecord<CardSectionCloseOutcome, CardSectionCloseOutcome.UnresolvedAddressedThread>(
                            cardsRoot, card, filePath, changeName, actingRole, timestamp,
                            new CardSectionCloseOutcome.UnresolvedAddressedThread(card.Frontmatter.Id, filePath, sectionThreadIds),
                            onToolFailure: static reason => new CardSectionCloseOutcome.ToolFailure(reason));
                    }

                    foreach (var (blockFilePath, blockCard) in freshBlocks)
                    {
                        var blockThreadIds = CardCommentRouting.LiveAddressedThreadIds(blockCard.Comments);
                        if (blockThreadIds.Count > 0)
                        {
                            return RefuseAndRecord<CardSectionCloseOutcome, CardSectionCloseOutcome.UnresolvedAddressedThread>(
                                cardsRoot, blockCard, blockFilePath, changeName, actingRole, timestamp,
                                new CardSectionCloseOutcome.UnresolvedAddressedThread(blockCard.Frontmatter.Id, blockFilePath, blockThreadIds),
                                onToolFailure: static reason => new CardSectionCloseOutcome.ToolFailure(reason));
                        }
                    }

                    var landedBlocks = new List<CardFile>(freshBlocks.Count);
                    var landTransition = BlockFlowTransitions.LandTransition;
                    foreach (var (blockFilePath, blockCard) in freshBlocks)
                    {
                        // ValidateBlockForLanding above already proved this parses as a recognised
                        // BlockFlowState; a landed block is skipped rather than re-written (work-
                        // lifecycle: "a block already landed is skipped rather than refused").
                        BlockFlowStateWireFormat.TryParse(blockCard.Frontmatter.Status, out var blockState);
                        if (blockState == BlockFlowState.Landed)
                        {
                            landedBlocks.Add(blockCard);
                            continue;
                        }

                        var blockAnchored = AnchoredCardPath.TryCreate(cardsRoot, blockFilePath, blockCard.Frontmatter.Scope, changeName, out var blockLayoutFailure);
                        if (blockAnchored is null)
                        {
                            return new CardSectionCloseOutcome.LayoutMismatch(blockLayoutFailure!.Reason);
                        }

                        var entry = new CardBlockTransitionEntry(actingRole, landTransition.Name, landTransition.From, landTransition.To, timestamp, []);
                        var updatedBlock = blockCard with
                        {
                            Frontmatter = blockCard.Frontmatter with { Status = landTransition.To.ToWireString(), Updated = timestamp },
                            Transitions = [.. blockCard.Transitions, entry],
                        };

                        var blockWriteResult = AtomicWrite(blockAnchored, CardFileWriter.Serialize(updatedBlock));
                        var blockWriteFailure = blockWriteResult.Match<CardSectionCloseOutcome?>(
                            onSuccess: static _ => null,
                            onNotFound: notFound => new CardSectionCloseOutcome.CardNotFound(notFound.FilePath),
                            onAlreadyExists: alreadyExists => new CardSectionCloseOutcome.LayoutMismatch(
                                $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite."),
                            onLayoutMismatch: layoutMismatch => new CardSectionCloseOutcome.LayoutMismatch(layoutMismatch.Reason),
                            onCorrupt: corrupt => new CardSectionCloseOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason),
                            onToolFailure: toolFailure => new CardSectionCloseOutcome.ToolFailure(toolFailure.Reason),
                            onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
                        if (blockWriteFailure is not null)
                        {
                            return blockWriteFailure;
                        }

                        landedBlocks.Add(updatedBlock);
                    }

                    var updatedSection = card with
                    {
                        Frontmatter = card.Frontmatter with { Status = SectionFlowState.Closed.ToWireString(), Updated = timestamp },
                        SectionFields = card.SectionFields with { ClosedBy = actingRole, ClosedAt = timestamp },
                    };

                    var sectionWriteResult = AtomicWrite(anchored, CardFileWriter.Serialize(updatedSection));
                    return sectionWriteResult.Match<CardSectionCloseOutcome>(
                        onSuccess: _ => new CardSectionCloseOutcome.Closed(updatedSection, landedBlocks),
                        onNotFound: notFound => new CardSectionCloseOutcome.CardNotFound(notFound.FilePath),
                        onAlreadyExists: alreadyExists => new CardSectionCloseOutcome.LayoutMismatch(
                            $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite."),
                        onLayoutMismatch: layoutMismatch => new CardSectionCloseOutcome.LayoutMismatch(layoutMismatch.Reason),
                        onCorrupt: corrupt => new CardSectionCloseOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason),
                        onToolFailure: toolFailure => new CardSectionCloseOutcome.ToolFailure(toolFailure.Reason),
                        onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
                }
                finally
                {
                    foreach (var acquiredLock in acquiredLocks)
                    {
                        acquiredLock.Dispose();
                    }
                }
            },
            onFailure: failure =>
                new CardSectionCloseOutcome.CardCorrupt(filePath, failure.Reason));
    }

    /// <summary>
    /// process-enforcement's ageing-thread prompt (§9 block E, "Section close settles its
    /// addressed threads" — architect ruling on the worker's own ❓ about 9.6). The requirement's
    /// own purpose clause — "to keep this gate from becoming a formality discharged in bulk at the
    /// moment of closing" — names a <em>close-time</em> failure; a prompt that only ever fired at
    /// the close-time gate could not prevent it, so this is read from <c>section status</c> instead,
    /// an earlier surfacing during the section's life, to the role each comment is addressed to.
    /// It never refuses anything and is entirely separate from <see cref="CardSectionCloseOutcome.
    /// UnresolvedAddressedThread"/>, which fires on every live addressed thread regardless of
    /// whether it is also reported here.
    ///
    /// <para>
    /// Scans <paramref name="sectionDirectory"/> (the same directory <see cref="
    /// CloseSectionUnderExistingLock"/> scans for its own block candidates) for every <see
    /// cref="IsBlockCard"/> card whose own <see cref="CardFrontmatter.Section"/> names <paramref
    /// name="sectionId"/>, and applies <see cref="CardCommentRouting.AgeingAddressedThreadIds"/> to
    /// each. Read-only — no lock is taken, matching <see cref="FindBlockingOpenProductOwnerQuestion"/>'s
    /// own precedent for a read that decides nothing load-bearing on its own. An unreadable sibling
    /// card is skipped rather than refusing the read (this is a status surface, not a gate — an
    /// unrelated corrupt file must not make every other block's status unreadable). The section
    /// card itself is never swept: it carries no round, so nothing on it can ever age by this
    /// definition (<see cref="CardCommentRouting.AgeingAddressedThreadIds"/>'s own doc comment).
    /// </para>
    /// </summary>
    internal static IReadOnlyList<AgeingThread> FindAgeingAddressedThreads(string sectionDirectory, string sectionId)
    {
        var ageingThreads = new List<AgeingThread>();
        foreach (var (blockFilePath, parseResult) in ReadAllCards(sectionDirectory))
        {
            var blockCard = parseResult.Match<CardFile?>(onSuccess: static s => s.Card, onFailure: static _ => null);
            if (blockCard is null || !IsBlockCard(blockCard) || !string.Equals(blockCard.Frontmatter.Section, sectionId, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var ageingThreadId in CardCommentRouting.AgeingAddressedThreadIds(blockCard.Comments, blockCard.Transitions))
            {
                var ageingComment = blockCard.Comments.First(comment => comment.Id == ageingThreadId);
                // AgeingAddressedThreadIds only ever returns the id of a comment whose To is
                // non-null (that is exactly what "addressed" means here).
                ageingThreads.Add(new AgeingThread(blockCard.Frontmatter.Id, blockFilePath, ageingThreadId, ageingComment.To!));
            }
        }

        return ageingThreads;
    }

    /// <summary>
    /// The two §8a block A closing conditions applied to one block (work-lifecycle: "Closing a
    /// section SHALL refuse where any block in it is not `approved`, or where any block carries an
    /// expected gate whose recorded exit code is non-zero or absent"), checked in that order.
    /// Returns <see langword="null"/> when the block passes — including when it is already <see
    /// cref="BlockFlowState.Landed"/>, which is not re-validated at all (work-lifecycle: "a block
    /// already landed is skipped rather than refused" — it already satisfied these conditions the
    /// call that landed it).
    ///
    /// <para>
    /// <b>No `reviewed_state` comparison (§8a block A revision, Product Owner ruling: "`approved`
    /// is terminal").</b> An earlier version of this check compared each block's `reviewed_state`
    /// against a caller-supplied "current state", refusing a close when they disagreed. That check
    /// had no remedy: with `amendment-requested` cut (below), a block found stale by it could never
    /// be reopened — the refusal was unsatisfiable, not merely strict. work-lifecycle now says so
    /// explicitly: closing a section SHALL NOT compare `reviewed_state` against the repository at
    /// all. `reviewed_state` stays recorded as evidence of what a reviewer certified; the
    /// supervisor's whole-section review, not a per-block field comparison, is what catches a
    /// sibling's change touching an already-approved block's files.
    /// </para>
    ///
    /// <para>
    /// <b>§9 block E adds the carried 9.8 arm</b> (process-enforcement: "Work cannot proceed past a
    /// stop-and-ask" — the section-driven half). Every refusal-shaped case returned here is
    /// card-addressed against <paramref name="blockCard"/> (§9 block E ruling: "ask what the
    /// refusal asserts" — each of these is a fact about this block, not about the section
    /// attempting to close over it) and is recorded, via <see cref="RefuseAndRecord{TOutcome,
    /// TRefusal}"/>, under the lock <see cref="CloseSectionUnderExistingLock"/> already holds on
    /// this block by the time this runs — no new lock is taken here.
    /// </para>
    /// </summary>
    private static CardSectionCloseOutcome? ValidateBlockForLanding(
        string cardsRoot, CardFile blockCard, string blockFilePath, string? changeName, CardOwner actingRole, DateTimeOffset timestamp)
    {
        if (!IsBlockCard(blockCard))
        {
            // Filtered to IsBlockCard candidates before this is ever called — a defensive
            // unreachable guard, not a live code path (BlockFlowTransitions.Match's own discipline).
            throw new InvalidOperationException($"'{blockFilePath}' is not a block card; this method must only be called on IsBlockCard candidates.");
        }

        if (!BlockFlowStateWireFormat.TryParse(blockCard.Frontmatter.Status, out var state))
        {
            return new CardSectionCloseOutcome.CardCorrupt(
                blockFilePath, $"unrecognised status: '{blockCard.Frontmatter.Status}'. Recognised statuses: {BlockFlowStateWireFormat.RecognisedValues}.");
        }

        if (state == BlockFlowState.Landed)
        {
            // Already landed, so this call is not about to write it (work-lifecycle: "a block
            // already landed is skipped rather than refused") — nothing here mutates it, so the
            // round check does not apply (Architect ruling, §8a block D brief: the round check
            // binds writers, not this read-only skip).
            return null;
        }

        if (ReservedDerivedStateFieldKeyIn(blockCard) is { } reservedKey)
        {
            return RefuseAndRecord<CardSectionCloseOutcome, CardSectionCloseOutcome.HandEnteredDerivedState>(
                cardsRoot, blockCard, blockFilePath, changeName, actingRole, timestamp,
                new CardSectionCloseOutcome.HandEnteredDerivedState(blockFilePath, reservedKey),
                onToolFailure: static reason => new CardSectionCloseOutcome.ToolFailure(reason));
        }

        if (state != BlockFlowState.Approved)
        {
            return RefuseAndRecord<CardSectionCloseOutcome, CardSectionCloseOutcome.BlockNotApproved>(
                cardsRoot, blockCard, blockFilePath, changeName, actingRole, timestamp,
                new CardSectionCloseOutcome.BlockNotApproved(blockCard.Frontmatter.Id, blockFilePath, state),
                onToolFailure: static reason => new CardSectionCloseOutcome.ToolFailure(reason));
        }

        if (!RoundAgreesWithHistory(blockCard, out var storedRound, out var expectedRound))
        {
            return RefuseAndRecord<CardSectionCloseOutcome, CardSectionCloseOutcome.RoundDisagreesWithHistory>(
                cardsRoot, blockCard, blockFilePath, changeName, actingRole, timestamp,
                new CardSectionCloseOutcome.RoundDisagreesWithHistory(blockFilePath, storedRound, expectedRound),
                onToolFailure: static reason => new CardSectionCloseOutcome.ToolFailure(reason));
        }

        foreach (var label in blockCard.BlockFields.GateResults.Select(static result => result.Label).Distinct(StringComparer.Ordinal))
        {
            var status = blockCard.BlockFields.GateStatusOf(label);
            var gateRefusal = status.Match<CardSectionCloseOutcome?>(
                onAbsent: () => RefuseAndRecord<CardSectionCloseOutcome, CardSectionCloseOutcome.BlockGateAbsent>(
                    cardsRoot, blockCard, blockFilePath, changeName, actingRole, timestamp,
                    new CardSectionCloseOutcome.BlockGateAbsent(blockCard.Frontmatter.Id, blockFilePath, label),
                    onToolFailure: static reason => new CardSectionCloseOutcome.ToolFailure(reason)),
                onRecorded: exitCode => exitCode == 0
                    ? null
                    : RefuseAndRecord<CardSectionCloseOutcome, CardSectionCloseOutcome.BlockGateFailed>(
                        cardsRoot, blockCard, blockFilePath, changeName, actingRole, timestamp,
                        new CardSectionCloseOutcome.BlockGateFailed(blockCard.Frontmatter.Id, blockFilePath, label, exitCode),
                        onToolFailure: static reason => new CardSectionCloseOutcome.ToolFailure(reason)));
            if (gateRefusal is not null)
            {
                return gateRefusal;
            }
        }

        // process-enforcement: "Work cannot proceed past a stop-and-ask" (§9 block E, 9.8's carried
        // arm). Section-driven landing was the one forward motion this guard did not already reach
        // when block D shipped it for the generic transitions and 'approve' (§9 block D DEVLOG
        // note) — an approved block blocked on an open Product Owner question could otherwise still
        // reach 'landed' by its section closing.
        if (FindBlockingOpenProductOwnerQuestion(cardsRoot, blockCard) is { } blockingQuestion)
        {
            return RefuseAndRecord<CardSectionCloseOutcome, CardSectionCloseOutcome.BlockedByOpenProductOwnerQuestion>(
                cardsRoot, blockCard, blockFilePath, changeName, actingRole, timestamp,
                new CardSectionCloseOutcome.BlockedByOpenProductOwnerQuestion(blockCard.Frontmatter.Id, blockFilePath, blockingQuestion.QuestionId, blockingQuestion.Title),
                onToolFailure: static reason => new CardSectionCloseOutcome.ToolFailure(reason));
        }

        return null;
    }

    /// <summary>
    /// Creates a brand-new <paramref name="kind"/> card at <paramref name="filePath"/> (§7 block A):
    /// the four register kinds' creation verbs and <c>section create</c> — one card, no dual-lock
    /// complexity <see cref="RecordFinding"/> needs. <b>Scope is validated through
    /// <see cref="CardScopeRules.Validate"/> here, unconditionally</b> — every caller passes its own
    /// kind's scope (a caller-chosen value for <see cref="CardKind.Rule"/>, a fixed one for every
    /// other kind this method is called for), and this method never trusts a caller's fixed value
    /// as valid on its own say-so: it always asks the table, so a caller cannot restate — and
    /// silently drift from — the rule <see cref="CardScopeRules"/> already owns. Checked before any
    /// identity is allocated, so a refused scope never burns an identity number.
    /// <paramref name="initialStatus"/> is the wire text the caller's own lifecycle type already
    /// computed (<see cref="RegisterLifecycleStateWireFormat"/> for a register kind,
    /// <see cref="SectionFlowStateWireFormat"/> for a section) — this method does not choose it,
    /// the same "carries the vocabulary, not a second copy of it" discipline every wire-format type
    /// in this codebase already follows. <paramref name="section"/> is <see cref="CardFrontmatter.
    /// Section"/> — "the section a card was raised within" — left empty by every caller except
    /// <c>question create</c> (§9 block E ruling: a question's <em>scope</em> deliberately outlives
    /// any one section, but "which section raised it" is a different fact <c>question create</c>
    /// needed a way to record so 9.5's "raised in it" has something to check).
    /// </summary>
    internal static CardCreateOutcome CreateCard(
        string cardsRoot,
        string filePath,
        CardKind kind,
        CardScope scope,
        string title,
        string initialStatus,
        CardOwner actingRole,
        string body,
        RegisterCardFields? registerFields,
        DateTimeOffset timestamp,
        TimeSpan lockTimeout,
        string? changeName,
        string section = "",
        BlockCardFields? blockFields = null)
    {
        var scopeValidation = CardScopeRules.Validate(kind, scope);
        if (scopeValidation is CardScopeValidationResult.Refused refused)
        {
            return new CardCreateOutcome.ScopeRefused(refused.Reason);
        }

        var allocation = CardIdentityAllocator.Allocate(cardsRoot, kind, lockTimeout);
        var (id, allocationRefusal) = allocation.Match<(string? Id, CardCreateOutcome? Refusal)>(
            onAllocated: allocated => (allocated.Id, null),
            onFailed: failed => (null, new CardCreateOutcome.ToolFailure(failed.Reason)),
            onBorne: borne => (null, RecordIdentityAlreadyBorneRefusal(cardsRoot, kind, borne, actingRole, timestamp, lockTimeout)));
        if (allocationRefusal is not null)
        {
            return allocationRefusal;
        }

        var frontmatter = new CardFrontmatter(id!, kind, title, initialStatus, actingRole, scope, section, timestamp, timestamp);
        var cardFile = new CardFile(frontmatter, body, [], [], FindingFields: null, RegisterFields: registerFields, BlockFields: blockFields);

        var writeResult = WriteCard(cardsRoot, filePath, new NewCardFile(frontmatter, body, RegisterFields: registerFields, BlockFields: blockFields), lockTimeout, changeName);
        return writeResult.Match<CardCreateOutcome>(
            onSuccess: _ => new CardCreateOutcome.Created(cardFile),
            onNotFound: notFound => new CardCreateOutcome.ToolFailure(
                $"unexpected 'not found' writing a brand-new card at '{notFound.FilePath}'."),
            onAlreadyExists: alreadyExists => new CardCreateOutcome.AlreadyExists(alreadyExists.FilePath),
            onLayoutMismatch: layoutMismatch => new CardCreateOutcome.LayoutMismatch(layoutMismatch.Reason),
            onCorrupt: corrupt => new CardCreateOutcome.ToolFailure(
                $"unexpected corruption reported writing a brand-new card at '{corrupt.FilePath}': {corrupt.Reason}"),
            onToolFailure: toolFailure => new CardCreateOutcome.ToolFailure(toolFailure.Reason),
            onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
    }

    /// <summary>
    /// <see cref="CardIdentityAllocationResult.Borne"/>'s CLI-facing disposition (§13, card-model:
    /// "the system SHALL refuse to issue an identity that a card in the record already bears"):
    /// unlike <see cref="CardCreateOutcome"/>'s other three refusals, this one resolved a real card
    /// — just not the one being created — so it is recorded, against the first (ordinally sorted)
    /// card in <paramref name="borne"/>'s <see cref="CardIdentityAllocationResult.Borne.
    /// CardFilePaths"/>, under that card's own lock (never the counter's — the counter's lock was
    /// already released by <see cref="CardIdentityAllocator.Allocate"/> returning). The change name
    /// <see cref="RefuseAndRecord{TOutcome, TRefusal}"/> needs to anchor a change/section-scoped
    /// card is derived from the resolved file's own containing directory rather than threaded
    /// through from the caller, which only knows the change it is creating <em>into</em>, not the
    /// change the borne card happens to already live in.
    /// </summary>
    private static CardCreateOutcome RecordIdentityAlreadyBorneRefusal(
        string cardsRoot, CardKind kind, CardIdentityAllocationResult.Borne borne, CardOwner actingRole, DateTimeOffset timestamp, TimeSpan lockTimeout)
    {
        var refusal = new CardCreateOutcome.IdentityAlreadyBorne(kind, borne.Id, borne.CardFilePaths);
        var filePath = borne.CardFilePaths[0];

        var lockResult = CardLock.Acquire(filePath, lockTimeout);
        return lockResult.Match(
            onAcquired: acquired =>
            {
                using (acquired.Lock)
                {
                    var current = ReadCard(filePath);
                    return current.Match<CardCreateOutcome>(
                        onSuccess: success => RefuseAndRecord<CardCreateOutcome, CardCreateOutcome.IdentityAlreadyBorne>(
                            cardsRoot, success.Card, filePath, ChangeNameFromCardPath(filePath), actingRole, timestamp, refusal,
                            onToolFailure: static reason => new CardCreateOutcome.ToolFailure(reason)),
                        onFailure: _ => refusal);
                }
            },
            onTimedOut: timedOut => new CardCreateOutcome.ToolFailure(timedOut.Message));
    }

    /// <summary>
    /// The change name a card at <paramref name="filePath"/> would need to anchor under its own
    /// scope, read straight off the path rather than threaded through a caller that does not
    /// necessarily know it: <see cref="CardLayout.ChangesDirectory"/> and <see cref="CardLayout.
    /// ArchivedChangeDirectory"/> both end the path in <c>&lt;change-name&gt;/&lt;file&gt;.md</c>, so
    /// the containing directory's own name is the change name either way. Harmless when the card
    /// turns out to be capability- or repository-scoped — <see cref="CardLayout.DirectoryFor"/>
    /// ignores this value for those scopes — and, for an archived change, deliberately does not
    /// anchor at all: <see cref="AnchoredCardPath.TryCreate"/> resolves a change-scoped name to the
    /// <em>live</em> changes directory only, so a borne identity that turns out to live in an
    /// archived change reports without recording, the same "no anchor, no record" fallback every
    /// other <see cref="RefuseAndRecord{TOutcome, TRefusal}"/> caller already gets for free.
    /// </summary>
    private static string? ChangeNameFromCardPath(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        return string.IsNullOrEmpty(directory) ? null : Path.GetFileName(directory);
    }

    /// <summary>
    /// Discharges the register card at <paramref name="filePath"/> (register: "Register kinds have
    /// a two-state lifecycle", §7 block A) — reads the current card, and only if it is one of the
    /// four register kinds, its status parses as <see cref="RegisterLifecycleState"/>, and it is not
    /// already discharged, writes <c>status: discharged</c> plus <c>discharged_by</c>/
    /// <c>discharged_at</c> under the card's lock. Same "record the acting role and the time"
    /// discipline <see cref="CloseSection"/> already applies to a section's own close.
    /// </summary>
    internal static CardRegisterDischargeOutcome DischargeRegisterCard(
        string cardsRoot, string filePath, CardOwner actingRole, DateTimeOffset timestamp, TimeSpan lockTimeout, string? changeName = null) =>
        WithLock(
            filePath,
            lockTimeout,
            heldLock => DischargeRegisterCardUnderExistingLock(heldLock, cardsRoot, actingRole, timestamp, changeName),
            onTimedOut: timedOut => new CardRegisterDischargeOutcome.ToolFailure(timedOut.Message));

    /// <summary>
    /// The read-decide-write step of <see cref="DischargeRegisterCard"/>. Same structural lock
    /// precondition as every other <c>*UnderExistingLock</c> method on this type — the target is
    /// <see cref="CardLock.CardPath"/>, not a separately supplied <c>filePath</c>.
    /// </summary>
    internal static CardRegisterDischargeOutcome DischargeRegisterCardUnderExistingLock(
        CardLock heldLock, string cardsRoot, CardOwner actingRole, DateTimeOffset timestamp, string? changeName = null)
    {
        ArgumentNullException.ThrowIfNull(heldLock);
        var filePath = heldLock.CardPath;

        if (!File.Exists(filePath))
        {
            return new CardRegisterDischargeOutcome.CardNotFound(filePath);
        }

        var current = ReadCard(filePath);
        return current.Match<CardRegisterDischargeOutcome>(
            onSuccess: success =>
            {
                var card = success.Card;
                if (ReservedDerivedStateFieldKeyIn(card) is { } reservedKey)
                {
                    return RefuseAndRecord<CardRegisterDischargeOutcome, CardRegisterDischargeOutcome.HandEnteredDerivedState>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardRegisterDischargeOutcome.HandEnteredDerivedState(reservedKey),
                        static reason => new CardRegisterDischargeOutcome.ToolFailure(reason));
                }

                if (!IsRegisterCard(card))
                {
                    return RefuseAndRecord<CardRegisterDischargeOutcome, CardRegisterDischargeOutcome.NotARegisterCard>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardRegisterDischargeOutcome.NotARegisterCard(card.Frontmatter.Kind),
                        static reason => new CardRegisterDischargeOutcome.ToolFailure(reason));
                }

                // register: "SHALL NOT occupy flow states" — enforced at the parse door (§12 block
                // A): CardFileParser validates a register card's status against
                // RegisterLifecycleStateWireFormat before the card is ever constructed, and
                // IsRegisterCard above already confirmed this card's own kind, so this can only
                // ever succeed.
                if (!RegisterLifecycleStateWireFormat.TryParse(card.Frontmatter.Status, out var currentState))
                {
                    throw new InvalidOperationException("unreachable: a register card's status is validated at the parse door.");
                }

                if (currentState == RegisterLifecycleState.Discharged)
                {
                    return RefuseAndRecord<CardRegisterDischargeOutcome, CardRegisterDischargeOutcome.AlreadyDischarged>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardRegisterDischargeOutcome.AlreadyDischarged(filePath),
                        static reason => new CardRegisterDischargeOutcome.ToolFailure(reason));
                }

                var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return new CardRegisterDischargeOutcome.LayoutMismatch(layoutFailure!.Reason);
                }

                var updated = card with
                {
                    Frontmatter = card.Frontmatter with { Status = RegisterLifecycleState.Discharged.ToWireString(), Updated = timestamp },
                    RegisterFields = card.RegisterFields with { DischargedBy = actingRole, DischargedAt = timestamp },
                };

                var writeResult = AtomicWrite(anchored, CardFileWriter.Serialize(updated));
                return writeResult.Match<CardRegisterDischargeOutcome>(
                    onSuccess: _ => new CardRegisterDischargeOutcome.Discharged(updated),
                    onNotFound: notFound => new CardRegisterDischargeOutcome.CardNotFound(notFound.FilePath),
                    onAlreadyExists: alreadyExists => new CardRegisterDischargeOutcome.LayoutMismatch(
                        $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite."),
                    onLayoutMismatch: layoutMismatch => new CardRegisterDischargeOutcome.LayoutMismatch(layoutMismatch.Reason),
                    onCorrupt: corrupt => new CardRegisterDischargeOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason),
                    onToolFailure: toolFailure => new CardRegisterDischargeOutcome.ToolFailure(toolFailure.Reason),
                    onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
            },
            onFailure: failure =>
                new CardRegisterDischargeOutcome.CardCorrupt(filePath, failure.Reason));
    }

    /// <summary>
    /// Promotes a change-scoped rule to repository scope (§7 block E, register: "Promoting a
    /// change-scoped rule to repository scope SHALL move the same card, retaining its identity,
    /// text and thread"). <paramref name="filePath"/> is resolved by the caller through
    /// <see cref="CardIdentityResolver"/> (<c>rule promote --id</c>), never a caller-typed path —
    /// see <see cref="Callboard.Cli.CommandDispatcher.ParsedCommand.RulePromote"/>'s own doc
    /// comment.
    ///
    /// <para>
    /// <b>Moves the card; does not copy it.</b> No identity is allocated anywhere in this method —
    /// the card's own <c>id</c>, <c>body</c>, and every frontmatter field this method does not
    /// explicitly name survive verbatim, because the only "creation" of new content here is a
    /// single <c>with</c> expression that changes exactly <c>Scope</c> and <c>Updated</c> before
    /// the same <see cref="CardFile"/> is re-serialised. <b>Every existing comment is preserved,
    /// in order</b> (§7 remediation, blocker 3) — one attributed comment recording the acting role
    /// and the time is appended after them, the only way this method's write records who promoted
    /// the card, since promotion touches neither <see cref="RegisterCardFields.DischargedBy"/> (the
    /// rule stays open) nor any other existing attribution field.
    /// </para>
    ///
    /// <para>
    /// <b>Two steps, and the failure shape of each (§7 block E brief item 9: "do not claim
    /// atomicity you do not have").</b> Phase one is a plain <see cref="File.Move(string, string)"/>
    /// from the rule's current path to its new path under <see cref="CardLayout.RegisterDirectory"/>
    /// — same filesystem, so on this platform that is one <c>rename()</c> syscall, the same
    /// all-or-nothing guarantee <see cref="AtomicWrite"/> and <see cref="ArchiveChange"/>'s own
    /// directory move already document: it either lands whole (the file now fully exists at the new
    /// path, content untouched) or throws having moved nothing, never a half-moved file. A failure
    /// here is reported as <see cref="CardRulePromoteOutcome.ToolFailure"/> with the rule still live
    /// at its old path, unmodified. Phase two, once the move has landed, rewrites the frontmatter at
    /// the <em>new</em> location through the ordinary <see cref="AtomicWrite"/>/
    /// <see cref="AnchoredCardPath"/> path — the only way this content can legitimately be written,
    /// since <see cref="AnchoredCardPath.TryCreate"/> requires a file's directory to already match
    /// the scope being written, which is exactly why the move has to happen first: there is no way
    /// to write <c>scope: repository</c> while the file still lives in a change directory. If phase
    /// two fails, the rule is left half-promoted: it already lives under
    /// <see cref="CardLayout.RegisterDirectory"/>, but its own <c>scope</c> field still reads
    /// <c>change</c> until this method runs again — an accepted gap, stated rather than solved, the
    /// same class <see cref="ArchiveChange"/>'s own doc comment and <see cref="CardLock"/>'s already
    /// carry for other races in this codebase.
    /// </para>
    ///
    /// <para>
    /// <b>A retry self-heals that exact gap.</b> This method resolves its own target path from the
    /// card's current basename every time, so a second call against the same id after a phase-two
    /// failure finds the card <em>already</em> sitting at the target path (phase one has nothing
    /// left to do) and goes straight to phase two — the frontmatter rewrite that phase-two failure
    /// left undone. No special-casing is needed for this: it falls out of computing the target path
    /// fresh on every call rather than trusting wherever the caller's resolved
    /// <paramref name="filePath"/> happened to point.
    /// </para>
    /// </summary>
    internal static CardRulePromoteOutcome PromoteRule(
        string cardsRoot, string filePath, CardOwner actingRole, DateTimeOffset timestamp, TimeSpan lockTimeout, string? changeName = null) =>
        WithLock(
            filePath,
            lockTimeout,
            heldLock => PromoteRuleUnderExistingLock(heldLock, cardsRoot, actingRole, timestamp, changeName),
            onTimedOut: timedOut => new CardRulePromoteOutcome.ToolFailure(timedOut.Message));

    /// <summary>The read-decide-move-write step of <see cref="PromoteRule"/>. Same structural lock
    /// precondition as every other <c>*UnderExistingLock</c> method on this type.
    ///
    /// <para>
    /// <b><paramref name="changeName"/> exists only to anchor a refusal (§9 block A2 remediation).
    /// </b> A card that is still <see cref="CardScope.Change"/>-scoped — the common case promotion
    /// serves — cannot be anchored by <see cref="AnchoredCardPath.TryCreate"/> without one
    /// (<see cref="CardLayout.DirectoryFor"/> requires it for that scope); without
    /// <paramref name="changeName"/>, <see cref="CardRulePromoteOutcome.NotARuleCard"/> and
    /// <see cref="CardRulePromoteOutcome.TargetAlreadyExists"/> would report their refusal but
    /// never record it — reviewer/Architect
    /// ruling: "a refusal surface that records everywhere except the path most callers take is
    /// worse than one that records nowhere." The promotion move itself never uses this value — the
    /// move target is always <see cref="CardScope.Repository"/>, which needs no change name.
    /// </para>
    /// </summary>
    private static CardRulePromoteOutcome PromoteRuleUnderExistingLock(
        CardLock heldLock, string cardsRoot, CardOwner actingRole, DateTimeOffset timestamp, string? changeName)
    {
        ArgumentNullException.ThrowIfNull(heldLock);
        var originalFilePath = heldLock.CardPath;

        if (!File.Exists(originalFilePath))
        {
            return new CardRulePromoteOutcome.CardNotFound(originalFilePath);
        }

        var current = ReadCard(originalFilePath);
        return current.Match<CardRulePromoteOutcome>(
            onSuccess: success =>
            {
                var card = success.Card;
                if (ReservedDerivedStateFieldKeyIn(card) is { } reservedKey)
                {
                    return RefuseAndRecord<CardRulePromoteOutcome, CardRulePromoteOutcome.HandEnteredDerivedState>(cardsRoot, card, originalFilePath, changeName, actingRole, timestamp,
                        new CardRulePromoteOutcome.HandEnteredDerivedState(reservedKey),
                        static reason => new CardRulePromoteOutcome.ToolFailure(reason));
                }

                if (!IsRuleCard(card))
                {
                    return RefuseAndRecord<CardRulePromoteOutcome, CardRulePromoteOutcome.NotARuleCard>(cardsRoot, card, originalFilePath, changeName, actingRole, timestamp,
                        new CardRulePromoteOutcome.NotARuleCard(card.Frontmatter.Kind),
                        static reason => new CardRulePromoteOutcome.ToolFailure(reason));
                }

                // register: "SHALL NOT occupy flow states" — enforced at the parse door (§12 block
                // A): CardFileParser validates a register card's status against
                // RegisterLifecycleStateWireFormat before the card is ever constructed, and
                // IsRuleCard above already confirmed this card's own kind, so this can only ever
                // succeed.
                if (!RegisterLifecycleStateWireFormat.TryParse(card.Frontmatter.Status, out _))
                {
                    throw new InvalidOperationException("unreachable: a register card's status is validated at the parse door.");
                }

                // Promotion knows how to move exactly one scope pair: change -> repository.
                // AlreadyRepositoryScoped and NotChangeScoped are distinct cases (brief item 3:
                // "promoting an already-repository-scoped rule is a refusal too") rather than one
                // generic "wrong scope" answer, because a caller correcting the first fact learns
                // nothing about the second.
                var scopeRefusal = card.Frontmatter.Scope.Match<CardRulePromoteOutcome?>(
                    onSection: () => new CardRulePromoteOutcome.NotChangeScoped(CardScope.Section, originalFilePath),
                    onChange: static () => null,
                    onCapability: () => new CardRulePromoteOutcome.NotChangeScoped(CardScope.Capability, originalFilePath),
                    onRepository: () => new CardRulePromoteOutcome.AlreadyRepositoryScoped(originalFilePath));
                if (scopeRefusal is CardRulePromoteOutcome.AlreadyRepositoryScoped alreadyRepositoryScoped)
                {
                    return RefuseAndRecord<CardRulePromoteOutcome, CardRulePromoteOutcome.AlreadyRepositoryScoped>(cardsRoot, card, originalFilePath, changeName, actingRole, timestamp,
                        alreadyRepositoryScoped, static reason => new CardRulePromoteOutcome.ToolFailure(reason));
                }

                if (scopeRefusal is CardRulePromoteOutcome.NotChangeScoped notChangeScoped)
                {
                    return RefuseAndRecord<CardRulePromoteOutcome, CardRulePromoteOutcome.NotChangeScoped>(cardsRoot, card, originalFilePath, changeName, actingRole, timestamp,
                        notChangeScoped, static reason => new CardRulePromoteOutcome.ToolFailure(reason));
                }

                var registerDirectory = Path.GetFullPath(
                    Path.Combine(cardsRoot, CardLayout.RegisterDirectory.Replace('/', Path.DirectorySeparatorChar)));
                var targetFilePath = Path.Combine(registerDirectory, Path.GetFileName(originalFilePath));
                var normalizedOriginalFilePath = Path.GetFullPath(originalFilePath);

                // Phase one: the move. Skipped when the card is already sitting at its target path
                // (this method's own retry-self-heals-a-stalled-phase-two case above) — File.Move
                // with identical source and destination is not the operation this branch means.
                if (!string.Equals(normalizedOriginalFilePath, targetFilePath, StringComparison.Ordinal))
                {
                    if (File.Exists(targetFilePath))
                    {
                        return RefuseAndRecord<CardRulePromoteOutcome, CardRulePromoteOutcome.TargetAlreadyExists>(cardsRoot, card, originalFilePath, changeName, actingRole, timestamp,
                            new CardRulePromoteOutcome.TargetAlreadyExists(targetFilePath),
                            static reason => new CardRulePromoteOutcome.ToolFailure(reason));
                    }

                    Directory.CreateDirectory(registerDirectory);

                    try
                    {
                        File.Move(normalizedOriginalFilePath, targetFilePath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return new CardRulePromoteOutcome.ToolFailure(
                            $"could not move '{normalizedOriginalFilePath}' to '{targetFilePath}': {ex.Message}");
                    }
                }

                // Phase two: the frontmatter edit, now legitimately anchored — the file's directory
                // already matches CardScope.Repository, whether this call just moved it there or a
                // previous, phase-two-failed call already did.
                var anchored = AnchoredCardPath.TryCreate(cardsRoot, targetFilePath, CardScope.Repository, changeName: null, out var layoutFailure);
                if (anchored is null)
                {
                    return new CardRulePromoteOutcome.LayoutMismatch(layoutFailure!.Reason);
                }

                // §7 remediation, blocker 3: promotion is the one register mutation whose record
                // could not previously say who performed it — every sibling write records the
                // acting role and the time (DischargedBy/DischargedAt, the comment 7.12 appends),
                // and this one wrote neither. An appended comment, not a new RegisterCardFields
                // entry: promotion does not discharge the rule (it stays open) and does not
                // supersede anything, so neither existing attribution field fits the fact, and a
                // new frontmatter field would be the "naming more than you must" the brief warns
                // against — this is the one write already happening, so no second lock or second
                // AtomicWrite is needed to record it.
                var promotionComment = new CardComment(
                    Id: $"promote-{Guid.NewGuid():N}",
                    Author: actingRole,
                    Timestamp: timestamp,
                    Body: $"'{actingRole.ToWireString()}' promoted this rule from change to repository scope at " +
                        timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + ".",
                    ReplyTo: null,
                    To: null,
                    Resolves: null,
                    UnknownHeaderFields: []);

                var updated = card with
                {
                    Frontmatter = card.Frontmatter with { Scope = CardScope.Repository, Updated = timestamp },
                    Comments = [.. card.Comments, promotionComment],
                };

                var writeResult = AtomicWrite(anchored, CardFileWriter.Serialize(updated));
                return writeResult.Match<CardRulePromoteOutcome>(
                    onSuccess: _ => new CardRulePromoteOutcome.Promoted(updated, originalFilePath, targetFilePath),
                    onNotFound: notFound => new CardRulePromoteOutcome.CardNotFound(notFound.FilePath),
                    onAlreadyExists: alreadyExists => new CardRulePromoteOutcome.LayoutMismatch(
                        $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite."),
                    onLayoutMismatch: layoutMismatch => new CardRulePromoteOutcome.LayoutMismatch(layoutMismatch.Reason),
                    onCorrupt: corrupt => new CardRulePromoteOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason),
                    onToolFailure: toolFailure => new CardRulePromoteOutcome.ToolFailure(toolFailure.Reason),
                    onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
            },
            onFailure: failure => new CardRulePromoteOutcome.CardCorrupt(originalFilePath, failure.Reason));
    }

    /// <summary>
    /// Promotes a change-scoped obligation to repository scope (§9 block F, register: "Promotion
    /// SHALL NOT be limited to rules... An <c>obligation</c> that outlives the change it was raised
    /// in SHALL be promotable to a wider scope on the same terms — the same card, retaining its
    /// identity, text and thread — because an obligation whose owing section has closed must have
    /// somewhere to go other than a discharge that says it was met"). Exact mirror of
    /// <see cref="PromoteRule"/>'s move-then-rewrite mechanics, phase shapes and self-healing retry
    /// — see that method's own doc comment for the reasoning this reuses verbatim; only the kind
    /// check and refusal text differ. Kept as its own method, not a shared kind-parameterised one,
    /// for the same reason the sibling outcome unions are separate types: the two verbs' refusal
    /// vocabularies (<c>rule</c> vs. <c>obligation</c>) are different products even where the
    /// mechanics agree.
    /// </summary>
    internal static CardObligationPromoteOutcome PromoteObligation(
        string cardsRoot, string filePath, CardOwner actingRole, DateTimeOffset timestamp, TimeSpan lockTimeout, string? changeName = null) =>
        WithLock(
            filePath,
            lockTimeout,
            heldLock => PromoteObligationUnderExistingLock(heldLock, cardsRoot, actingRole, timestamp, changeName),
            onTimedOut: timedOut => new CardObligationPromoteOutcome.ToolFailure(timedOut.Message));

    /// <summary>The read-decide-move-write step of <see cref="PromoteObligation"/>. Same structural
    /// lock precondition as every other <c>*UnderExistingLock</c> method on this type, and the same
    /// <paramref name="changeName"/>-anchors-a-refusal reasoning <see cref="PromoteRuleUnderExistingLock"/>
    /// documents.</summary>
    private static CardObligationPromoteOutcome PromoteObligationUnderExistingLock(
        CardLock heldLock, string cardsRoot, CardOwner actingRole, DateTimeOffset timestamp, string? changeName)
    {
        ArgumentNullException.ThrowIfNull(heldLock);
        var originalFilePath = heldLock.CardPath;

        if (!File.Exists(originalFilePath))
        {
            return new CardObligationPromoteOutcome.CardNotFound(originalFilePath);
        }

        var current = ReadCard(originalFilePath);
        return current.Match<CardObligationPromoteOutcome>(
            onSuccess: success =>
            {
                var card = success.Card;
                if (ReservedDerivedStateFieldKeyIn(card) is { } reservedKey)
                {
                    return RefuseAndRecord<CardObligationPromoteOutcome, CardObligationPromoteOutcome.HandEnteredDerivedState>(cardsRoot, card, originalFilePath, changeName, actingRole, timestamp,
                        new CardObligationPromoteOutcome.HandEnteredDerivedState(reservedKey),
                        static reason => new CardObligationPromoteOutcome.ToolFailure(reason));
                }

                if (!IsObligationCard(card))
                {
                    return RefuseAndRecord<CardObligationPromoteOutcome, CardObligationPromoteOutcome.NotAnObligationCard>(cardsRoot, card, originalFilePath, changeName, actingRole, timestamp,
                        new CardObligationPromoteOutcome.NotAnObligationCard(card.Frontmatter.Kind),
                        static reason => new CardObligationPromoteOutcome.ToolFailure(reason));
                }

                // register: "SHALL NOT occupy flow states" — enforced at the parse door (§12 block
                // A): CardFileParser validates a register card's status against
                // RegisterLifecycleStateWireFormat before the card is ever constructed, and
                // IsObligationCard above already confirmed this card's own kind, so this can only
                // ever succeed.
                if (!RegisterLifecycleStateWireFormat.TryParse(card.Frontmatter.Status, out _))
                {
                    throw new InvalidOperationException("unreachable: a register card's status is validated at the parse door.");
                }

                var scopeRefusal = card.Frontmatter.Scope.Match<CardObligationPromoteOutcome?>(
                    onSection: () => new CardObligationPromoteOutcome.NotChangeScoped(CardScope.Section, originalFilePath),
                    onChange: static () => null,
                    onCapability: () => new CardObligationPromoteOutcome.NotChangeScoped(CardScope.Capability, originalFilePath),
                    onRepository: () => new CardObligationPromoteOutcome.AlreadyRepositoryScoped(originalFilePath));
                if (scopeRefusal is CardObligationPromoteOutcome.AlreadyRepositoryScoped alreadyRepositoryScoped)
                {
                    return RefuseAndRecord<CardObligationPromoteOutcome, CardObligationPromoteOutcome.AlreadyRepositoryScoped>(cardsRoot, card, originalFilePath, changeName, actingRole, timestamp,
                        alreadyRepositoryScoped, static reason => new CardObligationPromoteOutcome.ToolFailure(reason));
                }

                if (scopeRefusal is CardObligationPromoteOutcome.NotChangeScoped notChangeScoped)
                {
                    return RefuseAndRecord<CardObligationPromoteOutcome, CardObligationPromoteOutcome.NotChangeScoped>(cardsRoot, card, originalFilePath, changeName, actingRole, timestamp,
                        notChangeScoped, static reason => new CardObligationPromoteOutcome.ToolFailure(reason));
                }

                var registerDirectory = Path.GetFullPath(
                    Path.Combine(cardsRoot, CardLayout.RegisterDirectory.Replace('/', Path.DirectorySeparatorChar)));
                var targetFilePath = Path.Combine(registerDirectory, Path.GetFileName(originalFilePath));
                var normalizedOriginalFilePath = Path.GetFullPath(originalFilePath);

                if (!string.Equals(normalizedOriginalFilePath, targetFilePath, StringComparison.Ordinal))
                {
                    if (File.Exists(targetFilePath))
                    {
                        return RefuseAndRecord<CardObligationPromoteOutcome, CardObligationPromoteOutcome.TargetAlreadyExists>(cardsRoot, card, originalFilePath, changeName, actingRole, timestamp,
                            new CardObligationPromoteOutcome.TargetAlreadyExists(targetFilePath),
                            static reason => new CardObligationPromoteOutcome.ToolFailure(reason));
                    }

                    Directory.CreateDirectory(registerDirectory);

                    try
                    {
                        File.Move(normalizedOriginalFilePath, targetFilePath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return new CardObligationPromoteOutcome.ToolFailure(
                            $"could not move '{normalizedOriginalFilePath}' to '{targetFilePath}': {ex.Message}");
                    }
                }

                var anchored = AnchoredCardPath.TryCreate(cardsRoot, targetFilePath, CardScope.Repository, changeName: null, out var layoutFailure);
                if (anchored is null)
                {
                    return new CardObligationPromoteOutcome.LayoutMismatch(layoutFailure!.Reason);
                }

                var promotionComment = new CardComment(
                    Id: $"promote-{Guid.NewGuid():N}",
                    Author: actingRole,
                    Timestamp: timestamp,
                    Body: $"'{actingRole.ToWireString()}' promoted this obligation from change to repository scope at " +
                        timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + ".",
                    ReplyTo: null,
                    To: null,
                    Resolves: null,
                    UnknownHeaderFields: []);

                var updated = card with
                {
                    Frontmatter = card.Frontmatter with { Scope = CardScope.Repository, Updated = timestamp },
                    Comments = [.. card.Comments, promotionComment],
                };

                var writeResult = AtomicWrite(anchored, CardFileWriter.Serialize(updated));
                return writeResult.Match<CardObligationPromoteOutcome>(
                    onSuccess: _ => new CardObligationPromoteOutcome.Promoted(updated, originalFilePath, targetFilePath),
                    onNotFound: notFound => new CardObligationPromoteOutcome.CardNotFound(notFound.FilePath),
                    onAlreadyExists: alreadyExists => new CardObligationPromoteOutcome.LayoutMismatch(
                        $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite."),
                    onLayoutMismatch: layoutMismatch => new CardObligationPromoteOutcome.LayoutMismatch(layoutMismatch.Reason),
                    onCorrupt: corrupt => new CardObligationPromoteOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason),
                    onToolFailure: toolFailure => new CardObligationPromoteOutcome.ToolFailure(toolFailure.Reason),
                    onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
            },
            onFailure: failure => new CardObligationPromoteOutcome.CardCorrupt(originalFilePath, failure.Reason));
    }

    /// <summary>
    /// Declines an obligation with a recorded reason (§9 block F, register: "An obligation that
    /// will not be met SHALL be closable by declining it with a recorded reason, and the record
    /// SHALL distinguish that from an obligation that was discharged"). Same lifecycle transition
    /// (<c>open</c> → <c>discharged</c>) and the same two register-wide preconditions as
    /// <see cref="DischargeRegisterCard"/>, plus one more: <paramref name="reason"/> must be present.
    /// What makes this a decline rather than a discharge is <see cref="RegisterCardFields.
    /// DeclinedReason"/> alone — see that field's own doc comment for why this build does not add a
    /// third <see cref="RegisterLifecycleState"/> to say the same thing.
    /// </summary>
    internal static CardObligationDeclineOutcome DeclineObligation(
        string cardsRoot, string filePath, CardOwner actingRole, string reason, DateTimeOffset timestamp, TimeSpan lockTimeout, string? changeName = null) =>
        WithLock(
            filePath,
            lockTimeout,
            heldLock => DeclineObligationUnderExistingLock(heldLock, cardsRoot, actingRole, reason, timestamp, changeName),
            onTimedOut: timedOut => new CardObligationDeclineOutcome.ToolFailure(timedOut.Message));

    /// <summary>The read-decide-write step of <see cref="DeclineObligation"/>. Same structural lock
    /// precondition as every other <c>*UnderExistingLock</c> method on this type.</summary>
    internal static CardObligationDeclineOutcome DeclineObligationUnderExistingLock(
        CardLock heldLock, string cardsRoot, CardOwner actingRole, string reason, DateTimeOffset timestamp, string? changeName)
    {
        ArgumentNullException.ThrowIfNull(heldLock);
        var filePath = heldLock.CardPath;

        if (!File.Exists(filePath))
        {
            return new CardObligationDeclineOutcome.CardNotFound(filePath);
        }

        var current = ReadCard(filePath);
        return current.Match<CardObligationDeclineOutcome>(
            onSuccess: success =>
            {
                var card = success.Card;
                if (ReservedDerivedStateFieldKeyIn(card) is { } reservedKey)
                {
                    return RefuseAndRecord<CardObligationDeclineOutcome, CardObligationDeclineOutcome.HandEnteredDerivedState>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardObligationDeclineOutcome.HandEnteredDerivedState(reservedKey),
                        static r => new CardObligationDeclineOutcome.ToolFailure(r));
                }

                if (!IsObligationCard(card))
                {
                    return RefuseAndRecord<CardObligationDeclineOutcome, CardObligationDeclineOutcome.NotAnObligationCard>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardObligationDeclineOutcome.NotAnObligationCard(card.Frontmatter.Kind),
                        static r => new CardObligationDeclineOutcome.ToolFailure(r));
                }

                // register: "SHALL NOT occupy flow states" — enforced at the parse door (§12 block
                // A): CardFileParser validates a register card's status against
                // RegisterLifecycleStateWireFormat before the card is ever constructed, and
                // IsObligationCard above already confirmed this card's own kind, so this can only
                // ever succeed.
                if (!RegisterLifecycleStateWireFormat.TryParse(card.Frontmatter.Status, out var currentState))
                {
                    throw new InvalidOperationException("unreachable: a register card's status is validated at the parse door.");
                }

                if (currentState == RegisterLifecycleState.Discharged)
                {
                    return RefuseAndRecord<CardObligationDeclineOutcome, CardObligationDeclineOutcome.AlreadyDischarged>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardObligationDeclineOutcome.AlreadyDischarged(filePath),
                        static r => new CardObligationDeclineOutcome.ToolFailure(r));
                }

                // register: "Scenario: Declining requires a reason" — defended here as well as at
                // the CLI door (see CardObligationDeclineOutcome.ReasonRequired's own doc comment),
                // checked after the two shared register preconditions above so a caller missing a
                // reason against an already-discharged or wrong-kind card learns the more specific
                // fact first.
                if (string.IsNullOrWhiteSpace(reason))
                {
                    return RefuseAndRecord<CardObligationDeclineOutcome, CardObligationDeclineOutcome.ReasonRequired>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardObligationDeclineOutcome.ReasonRequired(filePath),
                        static r => new CardObligationDeclineOutcome.ToolFailure(r));
                }

                var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return new CardObligationDeclineOutcome.LayoutMismatch(layoutFailure!.Reason);
                }

                var updated = card with
                {
                    Frontmatter = card.Frontmatter with { Status = RegisterLifecycleState.Discharged.ToWireString(), Updated = timestamp },
                    RegisterFields = card.RegisterFields with { DischargedBy = actingRole, DischargedAt = timestamp, DeclinedReason = reason },
                };

                var writeResult = AtomicWrite(anchored, CardFileWriter.Serialize(updated));
                return writeResult.Match<CardObligationDeclineOutcome>(
                    onSuccess: _ => new CardObligationDeclineOutcome.Declined(updated),
                    onNotFound: notFound => new CardObligationDeclineOutcome.CardNotFound(notFound.FilePath),
                    onAlreadyExists: alreadyExists => new CardObligationDeclineOutcome.LayoutMismatch(
                        $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite."),
                    onLayoutMismatch: layoutMismatch => new CardObligationDeclineOutcome.LayoutMismatch(layoutMismatch.Reason),
                    onCorrupt: corrupt => new CardObligationDeclineOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason),
                    onToolFailure: toolFailure => new CardObligationDeclineOutcome.ToolFailure(toolFailure.Reason),
                    onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
            },
            onFailure: failure => new CardObligationDeclineOutcome.CardCorrupt(filePath, failure.Reason));
    }

    /// <summary>
    /// Resolves an addressed thread by appending a comment naming what it resolves (§9 remediation,
    /// round two — S4: give <c>9.6</c>'s "resolve … or decline with a recorded reason" and <c>9.3</c>'s
    /// "resolve the following thread(s)" a real verb). Backs both <c>comment resolve</c> and
    /// <c>comment decline --reason</c>, both always passing <paramref name="requireReason"/> <see
    /// langword="true"/> as of §10 block D (Product Owner ruling: "comment resolve requires a body")
    /// — the two verbs no longer differ on this parameter, only on whether <paramref name="body"/>
    /// is a narrative resolution or a declared reason. <see cref="CardCommentResolveOutcome.
    /// ReasonRequired"/> stays reachable from <c>comment resolve</c> as defence-in-depth even though
    /// <see cref="Cli.CommandParser.ParseCommentResolve"/> already refuses an empty body at the
    /// parse door, the same "required at the door, defended again on its own terms" shape <see
    /// cref="Cli.CommandParser.ParseCommentDecline"/>'s <c>--reason</c> already has. Never a
    /// mutation: <see cref="CardComment"/> offers no path to alter or remove the resolved comment,
    /// only to append a new one naming it via <see cref="CardComment.Resolves"/> — <see cref="
    /// DispositionNitUnderLocks"/>'s own disposition comment is the shape this reuses.
    /// </summary>
    internal static CardCommentResolveOutcome ResolveComment(
        string cardsRoot, string filePath, string commentId, CardOwner actingRole, string body, bool requireReason,
        DateTimeOffset timestamp, TimeSpan lockTimeout, string? changeName = null) =>
        WithLock(
            filePath,
            lockTimeout,
            heldLock => ResolveCommentUnderExistingLock(heldLock, cardsRoot, commentId, actingRole, body, requireReason, timestamp, changeName),
            onTimedOut: timedOut => new CardCommentResolveOutcome.ToolFailure(timedOut.Message));

    /// <summary>The read-decide-write step of <see cref="ResolveComment"/>. Same structural lock
    /// precondition as every other <c>*UnderExistingLock</c> method on this type.</summary>
    internal static CardCommentResolveOutcome ResolveCommentUnderExistingLock(
        CardLock heldLock, string cardsRoot, string commentId, CardOwner actingRole, string body, bool requireReason, DateTimeOffset timestamp, string? changeName)
    {
        ArgumentNullException.ThrowIfNull(heldLock);
        var filePath = heldLock.CardPath;

        if (!File.Exists(filePath))
        {
            return new CardCommentResolveOutcome.CardNotFound(filePath);
        }

        var current = ReadCard(filePath);
        return current.Match<CardCommentResolveOutcome>(
            onSuccess: success =>
            {
                var card = success.Card;

                if (ReservedDerivedStateFieldKeyIn(card) is { } reservedKey)
                {
                    return RefuseAndRecord<CardCommentResolveOutcome, CardCommentResolveOutcome.HandEnteredDerivedState>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardCommentResolveOutcome.HandEnteredDerivedState(reservedKey),
                        static r => new CardCommentResolveOutcome.ToolFailure(r));
                }

                var commentIndex = -1;
                for (var i = 0; i < card.Comments.Count; i++)
                {
                    if (string.Equals(card.Comments[i].Id, commentId, StringComparison.Ordinal))
                    {
                        commentIndex = i;
                        break;
                    }
                }

                if (commentIndex < 0)
                {
                    return RefuseAndRecord<CardCommentResolveOutcome, CardCommentResolveOutcome.CommentNotFound>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardCommentResolveOutcome.CommentNotFound(commentId),
                        static r => new CardCommentResolveOutcome.ToolFailure(r));
                }

                // process-enforcement: "A thread is disposed of only by its addressee or the
                // card's owner" (Product Owner ruling, §10). Checked as soon as the comment (and so
                // its addressee) is known — the same "no cost to checking after a successful
                // ReadCard" reasoning CardApprovalOutcome.RoleNotPermitted's own doc comment gives —
                // and before AlreadyResolved/ReasonRequired below: who may act is decided before
                // what state the thread is in.
                var threadAddressedTo = card.Comments[commentIndex].To;
                if (!CardCommentRouting.IsPermittedToDisposeThread(card.Frontmatter.Owner, threadAddressedTo, actingRole))
                {
                    return RefuseAndRecord<CardCommentResolveOutcome, CardCommentResolveOutcome.RoleNotPermitted>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardCommentResolveOutcome.RoleNotPermitted(actingRole, card.Frontmatter.Owner, threadAddressedTo),
                        static r => new CardCommentResolveOutcome.ToolFailure(r));
                }

                if (CardCommentRouting.IsResolved(card.Comments, commentIndex))
                {
                    return RefuseAndRecord<CardCommentResolveOutcome, CardCommentResolveOutcome.AlreadyResolved>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardCommentResolveOutcome.AlreadyResolved(commentId),
                        static r => new CardCommentResolveOutcome.ToolFailure(r));
                }

                // register: "Scenario: Declining requires a reason" — defended here as well as at
                // the CLI door (see CardCommentResolveOutcome.ReasonRequired's own doc comment),
                // checked after the three shared preconditions above so a caller missing a reason
                // against a comment that does not exist, is disposed of by the wrong role, or is
                // already resolved, learns the more specific fact first. As of §10 block D, both
                // 'comment resolve' and 'comment decline' always pass requireReason: true — the
                // parameter now only distinguishes "was one supplied", never "is one required".
                if (requireReason && string.IsNullOrWhiteSpace(body))
                {
                    return RefuseAndRecord<CardCommentResolveOutcome, CardCommentResolveOutcome.ReasonRequired>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardCommentResolveOutcome.ReasonRequired(filePath),
                        static r => new CardCommentResolveOutcome.ToolFailure(r));
                }

                var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return new CardCommentResolveOutcome.LayoutMismatch(layoutFailure!.Reason);
                }

                var resolvingComment = new CardComment(
                    Id: $"resolution-{Guid.NewGuid():N}", Author: actingRole, Timestamp: timestamp, Body: body,
                    ReplyTo: commentId, To: null, Resolves: commentId, UnknownHeaderFields: []);

                var updated = card with
                {
                    Frontmatter = card.Frontmatter with { Updated = timestamp },
                    Comments = [.. card.Comments, resolvingComment],
                };

                var writeResult = AtomicWrite(anchored, CardFileWriter.Serialize(updated));
                return writeResult.Match<CardCommentResolveOutcome>(
                    onSuccess: _ => new CardCommentResolveOutcome.Resolved(updated, resolvingComment),
                    onNotFound: notFound => new CardCommentResolveOutcome.CardNotFound(notFound.FilePath),
                    onAlreadyExists: alreadyExists => new CardCommentResolveOutcome.LayoutMismatch(
                        $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite."),
                    onLayoutMismatch: layoutMismatch => new CardCommentResolveOutcome.LayoutMismatch(layoutMismatch.Reason),
                    onCorrupt: corrupt => new CardCommentResolveOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason),
                    onToolFailure: toolFailure => new CardCommentResolveOutcome.ToolFailure(toolFailure.Reason),
                    onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
            },
            onFailure: failure => new CardCommentResolveOutcome.CardCorrupt(filePath, failure.Reason));
    }

    /// <summary>
    /// Promotes an addressed thread to a new <c>question</c> or <c>decision</c> card, resolving the
    /// thread on the original card in the same all-or-nothing write (§9 remediation, round two — S4:
    /// give <c>9.6</c>'s "promote to a 'question'" / "promote to a 'decision'" a real verb). Two-card
    /// write, reusing <see cref="RecordFinding"/>'s own discipline via the now-generalised <see
    /// cref="AcquireLocksAndRecord{TOutcome}"/> — the §8a supervisor's flag against a third divergent
    /// multi-card write shape in this type is exactly what a hand-copied fourth would have been.
    /// </summary>
    internal static CardCommentPromoteOutcome PromoteComment(
        string cardsRoot, string originalFilePath, string commentId, string raisedFilePath, CardKind toKind,
        string title, CardOwner actingRole, CardOwner? owedByRole, string body, string? changeName,
        DateTimeOffset timestamp, TimeSpan lockTimeout)
    {
        var (raisedId, allocationFailure) = AllocateIdentity(cardsRoot, toKind, lockTimeout);
        if (allocationFailure is not null)
        {
            return new CardCommentPromoteOutcome.ToolFailure(allocationFailure);
        }

        var raisedDirectory = Path.GetDirectoryName(raisedFilePath);
        if (string.IsNullOrEmpty(raisedDirectory))
        {
            return new CardCommentPromoteOutcome.RaisedCardLayoutMismatch($"'{raisedFilePath}' has no containing directory to write into.");
        }

        Directory.CreateDirectory(raisedDirectory);

        return AcquireLocksAndRecord(
            originalFilePath, raisedFilePath, lockTimeout,
            () => PromoteCommentUnderLocks(cardsRoot, originalFilePath, commentId, raisedFilePath, raisedId!, toKind, title, actingRole, owedByRole, body, changeName, timestamp),
            static reason => new CardCommentPromoteOutcome.ToolFailure(reason));
    }

    /// <summary>The locked step of <see cref="PromoteComment"/> — both the original card's and the
    /// raised card's locks are already held by the time this runs (<see cref="AcquireLocksAndRecord{TOutcome}"/>).
    /// Directory creation already happened in <see cref="PromoteComment"/>, before either lock was
    /// acquired.</summary>
    private static CardCommentPromoteOutcome PromoteCommentUnderLocks(
        string cardsRoot, string originalFilePath, string commentId, string raisedFilePath, string raisedId,
        CardKind toKind, string title, CardOwner actingRole, CardOwner? owedByRole, string body, string? changeName, DateTimeOffset timestamp)
    {
        if (!File.Exists(originalFilePath))
        {
            return new CardCommentPromoteOutcome.CardNotFound(originalFilePath);
        }

        var current = ReadCard(originalFilePath);
        return current.Match<CardCommentPromoteOutcome>(
            onSuccess: success =>
            {
                var card = success.Card;

                if (ReservedDerivedStateFieldKeyIn(card) is { } reservedKey)
                {
                    return RefuseAndRecord<CardCommentPromoteOutcome, CardCommentPromoteOutcome.HandEnteredDerivedState>(cardsRoot, card, originalFilePath, changeName, actingRole, timestamp,
                        new CardCommentPromoteOutcome.HandEnteredDerivedState(reservedKey),
                        static r => new CardCommentPromoteOutcome.ToolFailure(r));
                }

                var commentIndex = -1;
                for (var i = 0; i < card.Comments.Count; i++)
                {
                    if (string.Equals(card.Comments[i].Id, commentId, StringComparison.Ordinal))
                    {
                        commentIndex = i;
                        break;
                    }
                }

                if (commentIndex < 0)
                {
                    return RefuseAndRecord<CardCommentPromoteOutcome, CardCommentPromoteOutcome.CommentNotFound>(cardsRoot, card, originalFilePath, changeName, actingRole, timestamp,
                        new CardCommentPromoteOutcome.CommentNotFound(commentId),
                        static r => new CardCommentPromoteOutcome.ToolFailure(r));
                }

                // process-enforcement: "A thread is disposed of only by its addressee or the
                // card's owner" (Product Owner ruling, §10) — same check, same ordering, as
                // ResolveCommentUnderExistingLock's own (see its comment for the reasoning); the
                // two verbs share one policy.
                var threadAddressedTo = card.Comments[commentIndex].To;
                if (!CardCommentRouting.IsPermittedToDisposeThread(card.Frontmatter.Owner, threadAddressedTo, actingRole))
                {
                    return RefuseAndRecord<CardCommentPromoteOutcome, CardCommentPromoteOutcome.RoleNotPermitted>(cardsRoot, card, originalFilePath, changeName, actingRole, timestamp,
                        new CardCommentPromoteOutcome.RoleNotPermitted(actingRole, card.Frontmatter.Owner, threadAddressedTo),
                        static r => new CardCommentPromoteOutcome.ToolFailure(r));
                }

                if (CardCommentRouting.IsResolved(card.Comments, commentIndex))
                {
                    return RefuseAndRecord<CardCommentPromoteOutcome, CardCommentPromoteOutcome.AlreadyResolved>(cardsRoot, card, originalFilePath, changeName, actingRole, timestamp,
                        new CardCommentPromoteOutcome.AlreadyResolved(commentId),
                        static r => new CardCommentPromoteOutcome.ToolFailure(r));
                }

                var anchored = AnchoredCardPath.TryCreate(cardsRoot, originalFilePath, card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return new CardCommentPromoteOutcome.LayoutMismatch(layoutFailure!.Reason);
                }

                // The raised card's own scope is fixed by its kind (card-model: a 'question' is
                // Repository-scoped, a 'decision' is Capability-scoped — CardScopeRules), so unlike
                // the original comment's card, this write never needs changeName to anchor.
                var raisedScope = toKind == CardKind.Question ? CardScope.Repository : CardScope.Capability;
                var raisedAnchored = AnchoredCardPath.TryCreate(cardsRoot, raisedFilePath, raisedScope, changeName, out var raisedLayoutFailure);
                if (raisedAnchored is null)
                {
                    return new CardCommentPromoteOutcome.RaisedCardLayoutMismatch(raisedLayoutFailure!.Reason);
                }

                if (File.Exists(raisedFilePath))
                {
                    return new CardCommentPromoteOutcome.RaisedCardAlreadyExists(raisedFilePath);
                }

                // A promoted question is owned by whoever owes the answer (owedByRole, required at
                // the CLI door only for --to question — CommandParser.ParseCommentPromote); a
                // promoted decision is owned by the acting role, exactly what 'decision create'
                // already does. The '!' below is never reached for a decision: owedByRole is read
                // only in the Question arm.
                var raisedOwner = toKind == CardKind.Question ? owedByRole! : actingRole;
                var raisedFrontmatter = new CardFrontmatter(
                    raisedId, toKind, title, "open", raisedOwner, raisedScope, OwningSectionId(card.Frontmatter), timestamp, timestamp);
                var raisedBody = $"{body}\n\n(Promoted from a comment on {card.Frontmatter.Id}.)";
                var raisedCardFile = new CardFile(raisedFrontmatter, raisedBody, [], [], RegisterFields: RegisterCardFields.Empty);
                var serializedRaisedCard = CardFileWriter.Serialize(raisedCardFile);

                var raisedWriteResult = AtomicWrite(raisedAnchored, serializedRaisedCard);
                var raisedFailure = raisedWriteResult.Match<CardCommentPromoteOutcome?>(
                    onSuccess: static _ => null,
                    onNotFound: static notFound => new CardCommentPromoteOutcome.ToolFailure(
                        $"unexpected 'not found' writing a brand-new card at '{notFound.FilePath}'."),
                    onAlreadyExists: static alreadyExists => new CardCommentPromoteOutcome.RaisedCardAlreadyExists(alreadyExists.FilePath),
                    onLayoutMismatch: static layoutMismatch => new CardCommentPromoteOutcome.RaisedCardLayoutMismatch(layoutMismatch.Reason),
                    onCorrupt: static corrupt => new CardCommentPromoteOutcome.ToolFailure(
                        $"unexpected corruption reported writing a brand-new card at '{corrupt.FilePath}': {corrupt.Reason}"),
                    onToolFailure: static toolFailure => new CardCommentPromoteOutcome.ToolFailure(toolFailure.Reason),
                    onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
                if (raisedFailure is not null)
                {
                    return raisedFailure;
                }

                var resolvingComment = new CardComment(
                    Id: $"resolution-{Guid.NewGuid():N}", Author: actingRole, Timestamp: timestamp,
                    Body: $"Promoted to '{toKind.ToWireString()}' {raisedId}.",
                    ReplyTo: commentId, To: null, Resolves: commentId, UnknownHeaderFields: []);

                var updatedCard = card with
                {
                    Frontmatter = card.Frontmatter with { Updated = timestamp },
                    Comments = [.. card.Comments, resolvingComment],
                };

                var writeResult = AtomicWrite(anchored, CardFileWriter.Serialize(updatedCard));
                return writeResult.Match<CardCommentPromoteOutcome>(
                    onSuccess: _ => new CardCommentPromoteOutcome.Promoted(updatedCard, raisedCardFile),
                    onNotFound: notFound =>
                    {
                        RollbackRaisedCommentCard(raisedFilePath, serializedRaisedCard);
                        return new CardCommentPromoteOutcome.ToolFailure($"unexpected 'not found' writing to '{notFound.FilePath}'.");
                    },
                    onAlreadyExists: alreadyExists =>
                    {
                        RollbackRaisedCommentCard(raisedFilePath, serializedRaisedCard);
                        return new CardCommentPromoteOutcome.ToolFailure(
                            $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite.");
                    },
                    onLayoutMismatch: layoutMismatch =>
                    {
                        RollbackRaisedCommentCard(raisedFilePath, serializedRaisedCard);
                        return new CardCommentPromoteOutcome.LayoutMismatch(layoutMismatch.Reason);
                    },
                    onCorrupt: corrupt =>
                    {
                        RollbackRaisedCommentCard(raisedFilePath, serializedRaisedCard);
                        return new CardCommentPromoteOutcome.ToolFailure(
                            $"unexpected corruption reported writing to '{corrupt.FilePath}': {corrupt.Reason}");
                    },
                    onToolFailure: toolFailure =>
                    {
                        RollbackRaisedCommentCard(raisedFilePath, serializedRaisedCard);
                        return new CardCommentPromoteOutcome.ToolFailure(toolFailure.Reason);
                    },
                    onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
            },
            onFailure: failure => new CardCommentPromoteOutcome.CardCorrupt(originalFilePath, failure.Reason));
    }

    /// <summary>All-or-nothing's other half for <see cref="PromoteComment"/> — deletes the raised
    /// card <see cref="PromoteCommentUnderLocks"/> has already written, once the original card's own
    /// second write, tried afterward, fails for any reason. Compare-then-delete, not delete-by-path
    /// (the same discipline <see cref="RollbackRaisedCard"/> already established): the file is
    /// deleted only when its current content still matches <paramref name="raisedContent"/>, the
    /// exact bytes this call itself wrote — a mismatch is treated as a lost race, not an error.
    /// Best-effort otherwise.</summary>
    private static void RollbackRaisedCommentCard(string raisedFilePath, string raisedContent)
    {
        try
        {
            if (!File.Exists(raisedFilePath))
            {
                return;
            }

            var currentContent = File.ReadAllText(raisedFilePath, Utf8NoBom);
            if (string.Equals(currentContent, raisedContent, StringComparison.Ordinal))
            {
                File.Delete(raisedFilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Archives a change (§7 block D, register: "The register lives above the change" —
    /// "archiving a change SHALL act as a filter that closes its change-scoped cards and leaves
    /// cards of wider scope untouched"). <b>Nothing in transit</b>: every repository-scoped card
    /// (<c>rule</c>/<c>hazard</c>/<c>question</c> at <see cref="CardScope.Repository"/>) already
    /// lives in <see cref="CardLayout.RegisterDirectory"/>, and every capability-scoped
    /// <c>decision</c> already lives in <see cref="CardLayout.DecisionsDirectory"/> — neither is
    /// anywhere near <paramref name="changeName"/>'s own directory, so this method never opens,
    /// reads, or so much as enumerates either. Only the change-scoped directory itself
    /// (<see cref="CardLayout.ChangesDirectory"/>) is ever touched.
    ///
    /// <para>
    /// <b>"Closes its change-scoped cards" is settled here as: a scan, a refusal on any orphaned
    /// obligation, and otherwise a directory move — nothing is written (§9 block F).</b> An earlier
    /// version of this method silently discharged every open obligation in the directory on its way
    /// to moving it; process-enforcement's "Archive settles orphaned obligations" requirement
    /// replaced that with a refusal, because discharge asserts the work was <em>met</em>, and a gate
    /// whose only exit manufactures that assertion is worse than no gate. <b>Not every open
    /// obligation is orphaned</b> — one owed by a <c>section</c> card that is still open (no
    /// <see cref="SectionCardFields.ClosedBy"/>/<see cref="SectionCardFields.ClosedAt"/>) is not
    /// orphaned at all: 9.4 already refuses that section's own close while the obligation is open,
    /// so archiving ahead of that section closing is register's own "no carry-forward step"
    /// scenario — the obligation moves into the archive exactly as written, live at its own scope,
    /// the same as an open question does today. Only an obligation whose <see cref="RegisterCardFields.
    /// OwedBy"/> names a section that has already closed, or no section card in this directory at
    /// all, is <see cref="ChangeArchiveOutcome.OrphanedObligations"/> — the case
    /// process-enforcement's own scenario text names ("an open obligation whose owing section has
    /// closed").
    /// </para>
    ///
    /// <para>
    /// <b>One phase, once the scan clears.</b> The directory move is a single <see cref="
    /// Directory.Move(string, string)"/> — same filesystem, so on this platform that is one
    /// <c>rename()</c> syscall: it either lands whole or throws having moved nothing, never a
    /// half-moved directory (the same same-filesystem-rename assumption <see cref="AtomicWrite"/>
    /// already documents for a single file). A failure here is reported as
    /// <see cref="ChangeArchiveOutcome.ToolFailure"/> with the change still live and unmoved. Since
    /// this method no longer writes any card of its own, there is no longer a phase-one/phase-two
    /// split to fail partway through.
    /// </para>
    ///
    /// <para>
    /// <b>An unreadable file in the directory refuses rather than guesses (fail-closed, the same
    /// discipline <see cref="CardIdentityResolver"/> applies to a search that turns up no match).
    /// </b> A file this scan cannot parse might be an open obligation, or the section card that
    /// would settle whether one is orphaned — proceeding regardless would risk moving an orphaned
    /// obligation into the archive unclassified, so this reports <see cref="ChangeArchiveOutcome.
    /// CardsUnreadable"/> before touching anything.
    /// </para>
    ///
    /// <para>
    /// <b>Accepted race, stated rather than solved (the same class of accepted gap <see
    /// cref="CardLock"/>'s own doc comment records for stale-lock PID reuse):</b> nothing holds a
    /// directory-wide lock across the scan-then-move sequence, so a card written into the directory
    /// after the scan but before the move (a new <c>obligation create</c> racing this call) is not
    /// seen by the scan and is moved into the archive exactly as written, unclassified. That is a
    /// race between two independent writers acting on the same change at the same moment — not a
    /// corruption of either card — and closing it fully would need whole-directory locking
    /// machinery this block was not asked to build.
    /// </para>
    /// </summary>
    internal static ChangeArchiveOutcome ArchiveChange(
        string cardsRoot, string changeName, CardOwner actingRole, DateTimeOffset timestamp, TimeSpan lockTimeout)
    {
        string liveRelativeDirectory;
        try
        {
            liveRelativeDirectory = CardLayout.ChangesDirectory(changeName);
        }
        catch (ArgumentException ex)
        {
            return new ChangeArchiveOutcome.InvalidChangeName(ex.Message);
        }

        var liveDirectory = Path.GetFullPath(
            Path.Combine(cardsRoot, liveRelativeDirectory.Replace('/', Path.DirectorySeparatorChar)));

        if (!Directory.Exists(liveDirectory))
        {
            return new ChangeArchiveOutcome.ChangeNotFound(changeName);
        }

        var archivedRelativeDirectory = CardLayout.ArchivedChangeDirectory(changeName);
        var archivedDirectory = Path.GetFullPath(
            Path.Combine(cardsRoot, archivedRelativeDirectory.Replace('/', Path.DirectorySeparatorChar)));

        if (Directory.Exists(archivedDirectory))
        {
            return new ChangeArchiveOutcome.AlreadyArchived(changeName);
        }

        var unreadable = new List<string>();
        var openObligations = new List<(string Id, string Title, string? OwedBy)>();
        var sectionIds = new HashSet<string>(StringComparer.Ordinal);
        var closedSectionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (filePath, result) in ReadAllCards(liveDirectory))
        {
            result.Match<object?>(
                onSuccess: success =>
                {
                    var card = success.Card;
                    // TryParse failing here is unreachable: §12 block A's parse door
                    // (CardFileParser.ValidateStatus) never hands back an obligation-kind CardFile
                    // whose status does not parse against RegisterLifecycleStateWireFormat — a
                    // hand-edited bad status fails to parse at all, so `result` above is onFailure
                    // and this onSuccess branch is never entered for that card. Kept as a defensive
                    // check rather than an assert, matching the CardSectionClose obligation scan this
                    // mirrors, so `change archive` and `section close` agree on what counts as owed.
                    if (IsObligationCard(card)
                        && RegisterLifecycleStateWireFormat.TryParse(card.Frontmatter.Status, out var state)
                        && state == RegisterLifecycleState.Open)
                    {
                        openObligations.Add((card.Frontmatter.Id, card.Frontmatter.Title, card.RegisterFields?.OwedBy));
                    }
                    else if (IsSectionCard(card))
                    {
                        sectionIds.Add(card.Frontmatter.Id);
                        if (card.SectionFields.ClosedBy is not null)
                        {
                            closedSectionIds.Add(card.Frontmatter.Id);
                        }
                    }

                    return null;
                },
                onFailure: _ =>
                {
                    unreadable.Add(filePath);
                    return null;
                });
        }

        if (unreadable.Count > 0)
        {
            unreadable.Sort(StringComparer.Ordinal);
            return new ChangeArchiveOutcome.CardsUnreadable(unreadable);
        }

        // process-enforcement: "Archive settles orphaned obligations" — an obligation is orphaned
        // ("owed by no remaining section") when the section it names via OwedBy has already closed,
        // or names no section card present in this directory at all. An obligation owed by a
        // section that is present and still open is deliberately left out — see this method's own
        // doc comment for why that one carries into the archive untouched instead of refusing.
        var orphaned = openObligations
            .Where(obligation => obligation.OwedBy is null
                || !sectionIds.Contains(obligation.OwedBy)
                || closedSectionIds.Contains(obligation.OwedBy))
            .ToList();

        if (orphaned.Count > 0)
        {
            return new ChangeArchiveOutcome.OrphanedObligations(
                changeName, [.. orphaned.Select(o => (o.Id, o.Title))]);
        }

        try
        {
            // Directory.Move requires the destination's parent to already exist — creating
            // callboard/changes/archive/ itself is not part of what moves, so this is not the
            // same-filesystem-rename step the doc comment's atomicity guarantee is about.
            // CardLayout.ArchiveDirectory, not Path.GetDirectoryName(archivedDirectory): the latter
            // treats a trailing-separator path as already-a-directory-name and returns the same
            // directory rather than its parent, which would create archivedDirectory itself here.
            Directory.CreateDirectory(Path.Combine(cardsRoot, CardLayout.ArchiveDirectory.Replace('/', Path.DirectorySeparatorChar)));
            Directory.Move(liveDirectory, archivedDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ChangeArchiveOutcome.ToolFailure(
                $"could not move '{liveDirectory}' to '{archivedDirectory}': {ex.Message}");
        }

        return new ChangeArchiveOutcome.Archived(changeName, archivedDirectory);
    }

    /// <summary>
    /// Supersedes one <c>decision</c> card with another (§7 block C, register: "A decision MAY
    /// name the decision it supersedes and the decision that supersedes it"). A two-card write —
    /// both sides or neither — following the shape <see cref="RecordFinding"/> already established
    /// for a multi-card write under lock with rollback on failure, adapted for this call's own
    /// shape: two cards that both already exist (no allocation, no create-only check), rather than
    /// one brand-new card alongside an optional second one.
    ///
    /// <para>
    /// <b>Lock order: <see cref="StringComparer.Ordinal"/> over the two resolved file paths,
    /// deterministic and safe for a reason <see cref="AcquireLocksAndRecord"/>'s own doc comment
    /// says an earlier version of <em>that</em> method's ordinal-path ordering was not.</b> That
    /// method dropped path ordering because one of its two paths is caller-typed
    /// (<c>--blind-spot-file</c>) and can be spelled with different casing across two invocations
    /// naming the identical physical file, so an ordinal comparison over the raw strings could
    /// disagree with itself invocation to invocation. Neither path here has that problem: both
    /// <paramref name="supersedingFilePath"/> and <paramref name="supersededFilePath"/> are
    /// supplied by the caller (<see cref="Callboard.Cli.CommandDispatcher.RunDecisionSupersede"/>)
    /// already resolved through <see cref="CardIdentityResolver"/> — the same directory walk every
    /// time, over ids that never change once allocated — so any two invocations naming the same
    /// physical pair of decisions, regardless of which one is the "superseding" argument and which
    /// is "superseded", always resolve to the identical two path strings and therefore compute the
    /// identical order. No two invocations can ever disagree about which lock to take first, so no
    /// AB/BA cycle can form between them.
    /// </para>
    ///
    /// <para>
    /// <b>Self-supersession is refused before any lock is requested</b> — if the two paths are
    /// identical (the same id was named on both sides), locking the one path twice from the same
    /// call would not deadlock against a different invocation, it would hang this one against
    /// itself for the full timeout, since the second <see cref="CardLock.Acquire"/> would be
    /// waiting on a lock this same call already holds. Checked by path equality here, ahead of the
    /// id-based recheck under lock in <see cref="SupersedeDecisionUnderLocks"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Acyclic by construction, not by walking the chain (Architect's open question, §7 block C
    /// brief item 5).</b> <see cref="SupersedeDecisionUnderLocks"/> refuses the operation when
    /// either card is already discharged: the usual case (<see cref="CardDecisionSupersedeOutcome.
    /// SupersededAlreadyDischarged"/> — "superseding an already-discharged decision is a refusal,
    /// not a re-supersession", Architect ruling) <em>and</em> the case that closes the cycle
    /// (<see cref="CardDecisionSupersedeOutcome.SupersedingAlreadyDischarged"/> — a decision that
    /// has itself already been superseded cannot newly become another's successor). Proof this
    /// rules out every cycle, not merely the two-node case: forming a cycle of any length n
    /// requires every decision in it to, at some point, act as the successor for the next node in
    /// the cycle while still open, and later be discharged by being named as the predecessor's
    /// target. For the cycle to close, the last node's discharge (by the first node's own
    /// supersession of it) must have already happened before the first node itself can be named as
    /// a successor by the last node — but the first node discharging the last node happens
    /// <em>after</em> the last node would need to have already acted as a successor while open.
    /// Each "act while open" for node i must happen before node i is discharged by node i-1's own
    /// act; chasing that requirement all the way around an n-node cycle produces a "happens-before"
    /// relation from each act to the previous node's act that wraps back on itself — a cycle in a
    /// strict ordering, which is impossible. No runtime graph walk is needed because the two local
    /// checks (target not already discharged, acting card not already discharged) are exactly what
    /// makes that global ordering unsatisfiable.
    /// </para>
    /// </summary>
    /// <param name="supersedingFilePath">The already-existing decision naming what it supersedes —
    /// resolved by id, not typed by the caller as a fresh path.</param>
    /// <param name="supersededFilePath">The already-existing decision being superseded — resolved
    /// the same way.</param>
    internal static CardDecisionSupersedeOutcome SupersedeDecision(
        string cardsRoot, string supersedingFilePath, string supersededFilePath, CardOwner actingRole, DateTimeOffset timestamp, TimeSpan lockTimeout)
    {
        if (string.Equals(supersedingFilePath, supersededFilePath, StringComparison.Ordinal))
        {
            return new CardDecisionSupersedeOutcome.SelfSupersession(supersedingFilePath);
        }

        var deadline = DateTimeOffset.UtcNow + lockTimeout;
        var orderedPaths = string.CompareOrdinal(supersedingFilePath, supersededFilePath) < 0
            ? (First: supersedingFilePath, Second: supersededFilePath)
            : (First: supersededFilePath, Second: supersedingFilePath);

        var firstLockResult = CardLock.Acquire(orderedPaths.First, lockTimeout);
        return firstLockResult.Match<CardDecisionSupersedeOutcome>(
            onAcquired: firstAcquired =>
            {
                using (firstAcquired.Lock)
                {
                    var remaining = deadline - DateTimeOffset.UtcNow;
                    if (remaining < TimeSpan.Zero)
                    {
                        remaining = TimeSpan.Zero;
                    }

                    var secondLockResult = CardLock.Acquire(orderedPaths.Second, remaining);
                    return secondLockResult.Match<CardDecisionSupersedeOutcome>(
                        onAcquired: secondAcquired =>
                        {
                            using (secondAcquired.Lock)
                            {
                                return SupersedeDecisionUnderLocks(
                                    cardsRoot, supersedingFilePath, supersededFilePath, actingRole, timestamp);
                            }
                        },
                        onTimedOut: timedOut => new CardDecisionSupersedeOutcome.ToolFailure(timedOut.Message));
                }
            },
            onTimedOut: timedOut => new CardDecisionSupersedeOutcome.ToolFailure(timedOut.Message));
    }

    /// <summary>The locked step of <see cref="SupersedeDecision"/> — both cards' locks are already
    /// held by the time this runs. Re-reads both cards fresh (rather than trusting whatever
    /// <see cref="CardIdentityResolver"/> saw before either lock was acquired) so every check below
    /// answers against the record's current state, not a stale snapshot.</summary>
    private static CardDecisionSupersedeOutcome SupersedeDecisionUnderLocks(
        string cardsRoot, string supersedingFilePath, string supersededFilePath, CardOwner actingRole, DateTimeOffset timestamp)
    {
        if (!File.Exists(supersedingFilePath))
        {
            return new CardDecisionSupersedeOutcome.CardNotFound(supersedingFilePath);
        }

        if (!File.Exists(supersededFilePath))
        {
            return new CardDecisionSupersedeOutcome.CardNotFound(supersededFilePath);
        }

        var supersedingRead = ReadCard(supersedingFilePath);
        var supersedingCard = supersedingRead.Match<CardFile?>(onSuccess: static s => s.Card, onFailure: static _ => null);
        if (supersedingCard is null)
        {
            var reason = supersedingRead.Match(onSuccess: static _ => string.Empty, onFailure: static f => f.Reason);
            return new CardDecisionSupersedeOutcome.CardCorrupt(supersedingFilePath, reason);
        }

        var supersededRead = ReadCard(supersededFilePath);
        var supersededCard = supersededRead.Match<CardFile?>(onSuccess: static s => s.Card, onFailure: static _ => null);
        if (supersededCard is null)
        {
            var reason = supersededRead.Match(onSuccess: static _ => string.Empty, onFailure: static f => f.Reason);
            return new CardDecisionSupersedeOutcome.CardCorrupt(supersededFilePath, reason);
        }

        if (ReservedDerivedStateFieldKeyIn(supersedingCard) is { } supersedingReservedKey)
        {
            return RefuseAndRecord<CardDecisionSupersedeOutcome, CardDecisionSupersedeOutcome.HandEnteredDerivedState>(cardsRoot, supersedingCard, supersedingFilePath, changeName: null, actingRole, timestamp,
                new CardDecisionSupersedeOutcome.HandEnteredDerivedState(supersedingFilePath, supersedingReservedKey),
                static reason => new CardDecisionSupersedeOutcome.ToolFailure(reason));
        }

        if (ReservedDerivedStateFieldKeyIn(supersededCard) is { } supersededReservedKey)
        {
            return RefuseAndRecord<CardDecisionSupersedeOutcome, CardDecisionSupersedeOutcome.HandEnteredDerivedState>(cardsRoot, supersededCard, supersededFilePath, changeName: null, actingRole, timestamp,
                new CardDecisionSupersedeOutcome.HandEnteredDerivedState(supersededFilePath, supersededReservedKey),
                static reason => new CardDecisionSupersedeOutcome.ToolFailure(reason));
        }

        if (!IsDecisionCard(supersedingCard))
        {
            return RefuseAndRecord<CardDecisionSupersedeOutcome, CardDecisionSupersedeOutcome.NotADecisionCard>(cardsRoot, supersedingCard, supersedingFilePath, changeName: null, actingRole, timestamp,
                        new CardDecisionSupersedeOutcome.NotADecisionCard(supersedingFilePath, supersedingCard.Frontmatter.Kind),
                static reason => new CardDecisionSupersedeOutcome.ToolFailure(reason));
        }

        if (!IsDecisionCard(supersededCard))
        {
            return RefuseAndRecord<CardDecisionSupersedeOutcome, CardDecisionSupersedeOutcome.NotADecisionCard>(cardsRoot, supersededCard, supersededFilePath, changeName: null, actingRole, timestamp,
                        new CardDecisionSupersedeOutcome.NotADecisionCard(supersededFilePath, supersededCard.Frontmatter.Kind),
                static reason => new CardDecisionSupersedeOutcome.ToolFailure(reason));
        }

        if (string.Equals(supersedingCard.Frontmatter.Id, supersededCard.Frontmatter.Id, StringComparison.Ordinal))
        {
            // §9 block A2 remediation: both cards are resolved and both locks are held by this
            // point — unlike SupersedeDecision's own pre-lock path-string check, this is a
            // recordable refusal against a real card.
            return RefuseAndRecord<CardDecisionSupersedeOutcome, CardDecisionSupersedeOutcome.ResolvedSelfSupersession>(cardsRoot, supersedingCard, supersedingFilePath, changeName: null, actingRole, timestamp,
                new CardDecisionSupersedeOutcome.ResolvedSelfSupersession(supersedingCard.Frontmatter.Id),
                static reason => new CardDecisionSupersedeOutcome.ToolFailure(reason));
        }

        // register: "SHALL NOT occupy flow states" — enforced at the parse door (§12 block A):
        // CardFileParser validates a register card's status against RegisterLifecycleStateWireFormat
        // before the card is ever constructed, and IsDecisionCard above already confirmed both
        // cards' own kind, so these can only ever succeed.
        if (!RegisterLifecycleStateWireFormat.TryParse(supersedingCard.Frontmatter.Status, out var supersedingState))
        {
            throw new InvalidOperationException("unreachable: a register card's status is validated at the parse door.");
        }

        if (!RegisterLifecycleStateWireFormat.TryParse(supersededCard.Frontmatter.Status, out var supersededState))
        {
            throw new InvalidOperationException("unreachable: a register card's status is validated at the parse door.");
        }

        // Both sides must be open — the superseded side because supersession discharges it exactly
        // once (Architect ruling: reuse the state block A already shipped, do not re-supersede),
        // the superseding side because that is what rules out every cycle by construction — see
        // this method's own doc comment on SupersedeDecision for the proof.
        if (supersededState == RegisterLifecycleState.Discharged)
        {
            return RefuseAndRecord<CardDecisionSupersedeOutcome, CardDecisionSupersedeOutcome.SupersededAlreadyDischarged>(cardsRoot, supersededCard, supersededFilePath, changeName: null, actingRole, timestamp,
                        new CardDecisionSupersedeOutcome.SupersededAlreadyDischarged(supersededFilePath),
                static reason => new CardDecisionSupersedeOutcome.ToolFailure(reason));
        }

        if (supersedingState == RegisterLifecycleState.Discharged)
        {
            return RefuseAndRecord<CardDecisionSupersedeOutcome, CardDecisionSupersedeOutcome.SupersedingAlreadyDischarged>(cardsRoot, supersedingCard, supersedingFilePath, changeName: null, actingRole, timestamp,
                        new CardDecisionSupersedeOutcome.SupersedingAlreadyDischarged(supersedingFilePath),
                static reason => new CardDecisionSupersedeOutcome.ToolFailure(reason));
        }

        var supersedingAnchored = AnchoredCardPath.TryCreate(
            cardsRoot, supersedingFilePath, supersedingCard.Frontmatter.Scope, changeName: null, out var supersedingLayoutFailure);
        if (supersedingAnchored is null)
        {
            return new CardDecisionSupersedeOutcome.LayoutMismatch(supersedingLayoutFailure!.Reason);
        }

        var supersededAnchored = AnchoredCardPath.TryCreate(
            cardsRoot, supersededFilePath, supersededCard.Frontmatter.Scope, changeName: null, out var supersededLayoutFailure);
        if (supersededAnchored is null)
        {
            return new CardDecisionSupersedeOutcome.LayoutMismatch(supersededLayoutFailure!.Reason);
        }

        // The superseded card is written first, so a failure on the second write (the superseding
        // card) has something to roll back — the same "second write's failure is what makes
        // rollback reachable" ordering RecordFinding uses. Both locks are held for this whole
        // method's duration, so no third party can legitimately rewrite the superseded card between
        // these two writes; the restore below is a plain re-write of the bytes read at the top of
        // this method, not a compare-then-restore (unlike RecordFinding's delete-by-content, which
        // guards against a concurrent writer RecordFinding's own locking does not fully exclude —
        // here it does).
        var originalSupersededContent = File.ReadAllText(supersededFilePath, Utf8NoBom);

        var updatedSupersededCard = supersededCard with
        {
            Frontmatter = supersededCard.Frontmatter with { Status = RegisterLifecycleState.Discharged.ToWireString(), Updated = timestamp },
            RegisterFields = supersededCard.RegisterFields with
            {
                DischargedBy = actingRole,
                DischargedAt = timestamp,
                SupersededBy = supersedingCard.Frontmatter.Id,
            },
        };

        var supersededWriteResult = AtomicWrite(supersededAnchored, CardFileWriter.Serialize(updatedSupersededCard));
        var supersededFailure = supersededWriteResult.Match<CardDecisionSupersedeOutcome?>(
            onSuccess: static _ => null,
            onNotFound: static notFound => new CardDecisionSupersedeOutcome.CardNotFound(notFound.FilePath),
            onAlreadyExists: static alreadyExists => new CardDecisionSupersedeOutcome.LayoutMismatch(
                $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite."),
            onLayoutMismatch: static layoutMismatch => new CardDecisionSupersedeOutcome.LayoutMismatch(layoutMismatch.Reason),
            onCorrupt: static corrupt => new CardDecisionSupersedeOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason),
            onToolFailure: static toolFailure => new CardDecisionSupersedeOutcome.ToolFailure(toolFailure.Reason),
            onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
        if (supersededFailure is not null)
        {
            return supersededFailure;
        }

        var updatedSupersedingCard = supersedingCard with
        {
            Frontmatter = supersedingCard.Frontmatter with { Updated = timestamp },
            RegisterFields = supersedingCard.RegisterFields with { Supersedes = supersededCard.Frontmatter.Id },
        };

        var supersedingWriteResult = AtomicWrite(supersedingAnchored, CardFileWriter.Serialize(updatedSupersedingCard));
        return supersedingWriteResult.Match<CardDecisionSupersedeOutcome>(
            onSuccess: _ => new CardDecisionSupersedeOutcome.Superseded(updatedSupersedingCard, updatedSupersededCard),
            onNotFound: notFound =>
            {
                RestoreCardContent(supersededAnchored, originalSupersededContent);
                return new CardDecisionSupersedeOutcome.CardNotFound(notFound.FilePath);
            },
            onAlreadyExists: alreadyExists =>
            {
                RestoreCardContent(supersededAnchored, originalSupersededContent);
                return new CardDecisionSupersedeOutcome.LayoutMismatch(
                    $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite.");
            },
            onLayoutMismatch: layoutMismatch =>
            {
                RestoreCardContent(supersededAnchored, originalSupersededContent);
                return new CardDecisionSupersedeOutcome.LayoutMismatch(layoutMismatch.Reason);
            },
            onCorrupt: corrupt =>
            {
                RestoreCardContent(supersededAnchored, originalSupersededContent);
                return new CardDecisionSupersedeOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason);
            },
            onToolFailure: toolFailure =>
            {
                RestoreCardContent(supersededAnchored, originalSupersededContent);
                return new CardDecisionSupersedeOutcome.ToolFailure(toolFailure.Reason);
            },
            onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
    }

    /// <summary>All-or-nothing's other half for a multi-card write that has already written one or
    /// more cards and then hit a later failure: restores <paramref name="anchored"/> to the exact
    /// bytes it held before this call touched it. Named generically (not
    /// <c>RestoreSupersededCard</c>) because two callers share it — <see cref="
    /// SupersedeDecisionUnderLocks"/> (one card to restore) and <see cref="CompactRulesUnderLocks"/>
    /// (up to N, one per already-written absorbed rule). Best-effort, same disposition as
    /// <see cref="RollbackRaisedCard"/> — if the restore itself cannot complete, the caller already
    /// has a failure to act on and this is not the place to escalate a cleanup problem into a
    /// second, different one.</summary>
    private static void RestoreCardContent(AnchoredCardPath anchored, string originalContent)
    {
        try
        {
            AtomicWrite(anchored, originalContent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Compacts several rules into one family (§7 block F, register: "The system SHALL support
    /// compacting several rules into a family rule stating what they share. A family rule SHALL
    /// record the rules it absorbs, and every absorbed rule SHALL remain retrievable"). An
    /// N+1-card write — the family plus every absorbed rule — following the shape <see cref="
    /// SupersedeDecision"/> already established for a multi-card write under lock with
    /// content-based rollback, generalised from two cards to N+1. <paramref name="familyFilePath"/>
    /// and every entry of <paramref name="absorbedFilePaths"/> already exist and are resolved by
    /// the caller through <see cref="CardIdentityResolver"/> (<c>rule compact --id --absorbs</c>,
    /// or the <c>change archive --compact-family/--absorbs</c> hook), never a caller-typed path.
    ///
    /// <para>
    /// <b>Only the architect may call this successfully (§7 block F remediation, Architect ruling:
    /// "the constraint belongs to the operation, not to one entry point").</b> Register:
    /// "Compaction of change-scoped rules SHALL be performed by the architect at archive" — checked
    /// here, first, so every caller inherits identical enforcement rather than each call site
    /// re-implementing its own check that could drift from the other's. <see cref="
    /// Callboard.Cli.CommandDispatcher.RunChangeArchive"/>'s hook no longer checks the role itself
    /// for exactly this reason: this is the one and only place that decision is made.
    /// </para>
    ///
    /// <para>
    /// <b>This block restricts compaction to change-scoped rules, all within the same
    /// <paramref name="changeName"/>.</b> Register: "Compaction of repository-scoped rules SHALL be
    /// proposed by an agent and decided by the Product Owner" — that propose/decide flow is block
    /// G's (7.9), not built here, so nothing in this build may let repository-scoped rules be
    /// compacted directly. Every resolved card is checked <see cref="CardScope.Change"/> before any
    /// write, and every resolved path is then anchored (<see cref="AnchoredCardPath.TryCreate"/>)
    /// against <paramref name="changeName"/> — a repository-scoped rule refuses on the scope check,
    /// a change-scoped rule belonging to a *different* change fails the anchor; both surface as
    /// <see cref="CardRuleCompactOutcome.LayoutMismatch"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Deterministic lock order, safe for the same reason <see cref="SupersedeDecision"/>'s is.
    /// </b> Every one of the N+1 paths — the family's and every absorbed rule's — is supplied by
    /// the caller already resolved through <see cref="CardIdentityResolver"/>: the same directory
    /// walk every time, over ids that never change once allocated. Two invocations naming the same
    /// physical set of files, in whatever order the caller happened to list them, therefore always
    /// resolve to the identical set of path strings — so sorting that set with <see cref="
    /// StringComparer.Ordinal"/> and acquiring every lock in that order produces the identical
    /// sequence regardless of which invocation is doing the sorting. No two invocations can ever
    /// disagree about which lock to take next, so no cycle can form in the wait-for graph across
    /// any pair of concurrent invocations — the standard "every process acquires resources in one
    /// globally consistent total order" argument for deadlock-freedom, here with N+1 resources
    /// instead of two.
    /// </para>
    ///
    /// <para>
    /// <b>Self-absorption and duplicate members are refused before any lock is requested</b> —
    /// locking the same path twice within one call would not deadlock against a different
    /// invocation, it would hang this one against itself, since the second <see cref="
    /// CardLock.Acquire"/> would wait on a lock this same call already holds. Checked here by path
    /// equality, ahead of the id-based recheck under lock in <see cref="CompactRulesUnderLocks"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Acyclic by construction — the same argument as <see cref="SupersedeDecision"/>'s, and it
    /// covers a family absorbing a family too (§7 block F brief item 5, Architect's open
    /// question).</b> <see cref="CompactRulesUnderLocks"/> refuses when the family is already
    /// discharged (<see cref="CardRuleCompactOutcome.FamilyAlreadyDischarged"/>) or when an
    /// absorbed rule is already discharged (<see cref="CardRuleCompactOutcome.
    /// AbsorbedAlreadyDischarged"/>). Absorbing a member discharges that member but never the
    /// acting family — the same asymmetry <see cref="SupersedeDecision"/>'s "superseding"/
    /// "superseded" sides have. A cycle of any length n (family 1 absorbs a rule that is, or later
    /// becomes, family 2, ..., family n absorbs family 1) requires every node to, at some point,
    /// act as the absorbing family while still open, and later be discharged by being named in some
    /// other node's absorb set. For the cycle to close, the last node's absorption of the first
    /// node must be able to happen — but the first node can only have already acted as an absorbing
    /// family (a precondition for the cycle to have a first link at all) while it was still open,
    /// and the check above refuses the moment any node in the chain is discharged before it acts
    /// again. Chasing the "must have been open when it acted, discharged only after" requirement
    /// all the way around an n-node cycle produces a happens-before relation from each node's own
    /// act to the previous node's discharge of it that wraps back on itself — impossible, whether
    /// every node in the cycle is a plain rule, a family that has itself already absorbed others,
    /// or a mix of both. No runtime graph walk is needed, for the identical reason it was not
    /// needed for decisions: the two local open-checks are exactly what makes the global ordering
    /// unsatisfiable.
    /// </para>
    ///
    /// <para>
    /// <b>Failure guarantee, stated honestly.</b> Absorbed rules are written first, one at a time,
    /// in the order given; the family is written last. If an absorbed rule's write fails partway
    /// through the list, every absorbed rule already written in this call is restored to its
    /// pre-call bytes (<see cref="RestoreCardContent"/>) before the failure is returned, and the
    /// family and any not-yet-reached absorbed rules are left untouched — a real all-or-nothing for
    /// the whole set, not merely for one pair. If the family's own write then fails (every absorbed
    /// rule already landed), every absorbed rule is restored the same way. <b>Not claimed:
    /// retry-safety across a later, unrelated failure.</b> Unlike <see cref="ArchiveChange"/>'s
    /// idempotent obligation settlement, a retry of an already-<em>succeeded</em> compaction (for
    /// example, from <c>change archive --compact-family/--absorbs</c> when the archive move that
    /// follows it fails) will refuse — the absorbed rules are already discharged — rather than
    /// silently reapplying; recovering means omitting the compaction flags on retry, since
    /// compaction already landed. Documented, not solved, the same disposition <see cref="
    /// ArchiveChange"/>'s own doc comment gives its accepted directory-scan race.
    /// </para>
    /// </summary>
    /// <param name="familyFilePath">The already-existing rule that becomes the family, naming what
    /// it absorbs — resolved by id, not typed by the caller as a fresh path.</param>
    /// <param name="absorbedFilePaths">The already-existing rules being absorbed, in the order they
    /// will be recorded — resolved the same way. Never empty.</param>
    /// <param name="changeName">The change every one of the N+1 rules must belong to (this block's
    /// scope restriction — see this method's own doc comment).</param>
    internal static CardRuleCompactOutcome CompactRules(
        string cardsRoot, string familyFilePath, IReadOnlyList<string> absorbedFilePaths, string changeName,
        CardOwner actingRole, DateTimeOffset timestamp, TimeSpan lockTimeout)
    {
        // Checked first, ahead of every other check — role-not-permitted is a fact about whether
        // this call is allowed to happen at all, not about the shape of its arguments (Architect
        // ruling: "the constraint belongs to the operation, not to one entry point").
        var isArchitect = actingRole.Match(
            onArchitect: static () => true,
            onWorker: static () => false,
            onReviewer: static () => false,
            onSupervisor: static () => false,
            onProductOwner: static () => false);
        if (!isArchitect)
        {
            return new CardRuleCompactOutcome.RoleNotPermitted(actingRole, CardOwner.Architect);
        }

        if (absorbedFilePaths.Count == 0)
        {
            return new CardRuleCompactOutcome.EmptyAbsorbSet();
        }

        foreach (var absorbedFilePath in absorbedFilePaths)
        {
            if (string.Equals(familyFilePath, absorbedFilePath, StringComparison.Ordinal))
            {
                return new CardRuleCompactOutcome.SelfAbsorption(familyFilePath);
            }
        }

        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var absorbedFilePath in absorbedFilePaths)
        {
            if (!seenPaths.Add(absorbedFilePath))
            {
                return new CardRuleCompactOutcome.DuplicateAbsorbedRule(absorbedFilePath);
            }
        }

        var deadline = DateTimeOffset.UtcNow + lockTimeout;
        var orderedPaths = new List<string>(absorbedFilePaths.Count + 1) { familyFilePath };
        orderedPaths.AddRange(absorbedFilePaths);
        orderedPaths.Sort(StringComparer.Ordinal);

        var heldLocks = new List<CardLock>(orderedPaths.Count);
        try
        {
            foreach (var path in orderedPaths)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining < TimeSpan.Zero)
                {
                    remaining = TimeSpan.Zero;
                }

                var lockResult = CardLock.Acquire(path, remaining);
                var acquireFailure = lockResult.Match<CardRuleCompactOutcome?>(
                    onAcquired: acquired =>
                    {
                        heldLocks.Add(acquired.Lock);
                        return null;
                    },
                    onTimedOut: timedOut => new CardRuleCompactOutcome.ToolFailure(timedOut.Message));
                if (acquireFailure is not null)
                {
                    return acquireFailure;
                }
            }

            return CompactRulesUnderLocks(cardsRoot, familyFilePath, absorbedFilePaths, changeName, actingRole, timestamp);
        }
        finally
        {
            for (var i = heldLocks.Count - 1; i >= 0; i--)
            {
                heldLocks[i].Dispose();
            }
        }
    }

    /// <summary>The locked step of <see cref="CompactRules"/> — every one of the N+1 locks is
    /// already held by the time this runs. Re-reads every card fresh (rather than trusting whatever
    /// resolution saw before any lock was acquired) so every check below answers against the
    /// record's current state, not a stale snapshot.</summary>
    private static CardRuleCompactOutcome CompactRulesUnderLocks(
        string cardsRoot, string familyFilePath, IReadOnlyList<string> absorbedFilePaths, string changeName,
        CardOwner actingRole, DateTimeOffset timestamp)
    {
        var (familyRefusal, familyCard) = ReadOpenChangeScopedRule(cardsRoot, familyFilePath, changeName, isFamilySide: true, actingRole, timestamp);
        if (familyRefusal is not null)
        {
            return familyRefusal;
        }

        var familyAnchored = AnchoredCardPath.TryCreate(cardsRoot, familyFilePath, CardScope.Change, changeName, out var familyLayoutFailure);
        if (familyAnchored is null)
        {
            return new CardRuleCompactOutcome.LayoutMismatch(familyLayoutFailure!.Reason);
        }

        var absorbedCards = new CardFile[absorbedFilePaths.Count];
        var absorbedAnchors = new AnchoredCardPath[absorbedFilePaths.Count];
        var seenIds = new HashSet<string>(StringComparer.Ordinal) { familyCard!.Frontmatter.Id };

        for (var i = 0; i < absorbedFilePaths.Count; i++)
        {
            var (absorbedRefusal, absorbedCard) = ReadOpenChangeScopedRule(cardsRoot, absorbedFilePaths[i], changeName, isFamilySide: false, actingRole, timestamp);
            if (absorbedRefusal is not null)
            {
                return absorbedRefusal;
            }

            if (!seenIds.Add(absorbedCard!.Frontmatter.Id))
            {
                // §9 block A2 remediation: both cards are resolved and every lock is held by this
                // point, so — unlike the path-string checks in CompactRules that catch the common
                // case before any lock exists — this is a recordable refusal against a real card.
                return string.Equals(absorbedCard.Frontmatter.Id, familyCard.Frontmatter.Id, StringComparison.Ordinal)
                    ? RefuseAndRecord<CardRuleCompactOutcome, CardRuleCompactOutcome.ResolvedSelfAbsorption>(cardsRoot, absorbedCard, absorbedFilePaths[i], changeName, actingRole, timestamp,
                        new CardRuleCompactOutcome.ResolvedSelfAbsorption(absorbedCard.Frontmatter.Id),
                        static reason => new CardRuleCompactOutcome.ToolFailure(reason))
                    : RefuseAndRecord<CardRuleCompactOutcome, CardRuleCompactOutcome.ResolvedDuplicateAbsorbedRule>(cardsRoot, absorbedCard, absorbedFilePaths[i], changeName, actingRole, timestamp,
                        new CardRuleCompactOutcome.ResolvedDuplicateAbsorbedRule(absorbedCard.Frontmatter.Id),
                        static reason => new CardRuleCompactOutcome.ToolFailure(reason));
            }

            var absorbedAnchored = AnchoredCardPath.TryCreate(cardsRoot, absorbedFilePaths[i], CardScope.Change, changeName, out var absorbedLayoutFailure);
            if (absorbedAnchored is null)
            {
                return new CardRuleCompactOutcome.LayoutMismatch(absorbedLayoutFailure!.Reason);
            }

            absorbedCards[i] = absorbedCard;
            absorbedAnchors[i] = absorbedAnchored;
        }

        // Absorbed rules are written first, one at a time — see this method's own (via
        // CompactRules's doc comment) failure-guarantee statement: a failure on entry i rolls back
        // entries 0..i-1, leaves i.. and the family untouched.
        var originalContents = new string[absorbedCards.Length];
        var updatedAbsorbedCards = new CardFile[absorbedCards.Length];
        for (var i = 0; i < absorbedCards.Length; i++)
        {
            originalContents[i] = File.ReadAllText(absorbedFilePaths[i], Utf8NoBom);

            var updatedAbsorbed = absorbedCards[i] with
            {
                Frontmatter = absorbedCards[i].Frontmatter with { Status = RegisterLifecycleState.Discharged.ToWireString(), Updated = timestamp },
                RegisterFields = absorbedCards[i].RegisterFields with
                {
                    DischargedBy = actingRole,
                    DischargedAt = timestamp,
                    SupersededBy = familyCard.Frontmatter.Id,
                },
            };

            var writeResult = AtomicWrite(absorbedAnchors[i], CardFileWriter.Serialize(updatedAbsorbed));
            var writeFailure = writeResult.Match<CardRuleCompactOutcome?>(
                onSuccess: static _ => null,
                onNotFound: notFound => new CardRuleCompactOutcome.CardNotFound(notFound.FilePath),
                onAlreadyExists: alreadyExists => new CardRuleCompactOutcome.LayoutMismatch(
                    $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite."),
                onLayoutMismatch: layoutMismatch => new CardRuleCompactOutcome.LayoutMismatch(layoutMismatch.Reason),
                onCorrupt: corrupt => new CardRuleCompactOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason),
                onToolFailure: toolFailure => new CardRuleCompactOutcome.ToolFailure(toolFailure.Reason),
                onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));

            if (writeFailure is not null)
            {
                for (var j = 0; j < i; j++)
                {
                    RestoreCardContent(absorbedAnchors[j], originalContents[j]);
                }

                return writeFailure;
            }

            updatedAbsorbedCards[i] = updatedAbsorbed;
        }

        var updatedFamilyCard = familyCard with
        {
            Frontmatter = familyCard.Frontmatter with { Updated = timestamp },
            RegisterFields = familyCard.RegisterFields with
            {
                Absorbs = updatedAbsorbedCards.Select(static c => c.Frontmatter.Id).ToImmutableArray(),
            },
        };

        var familyWriteResult = AtomicWrite(familyAnchored, CardFileWriter.Serialize(updatedFamilyCard));
        return familyWriteResult.Match<CardRuleCompactOutcome>(
            onSuccess: _ => new CardRuleCompactOutcome.Compacted(updatedFamilyCard, updatedAbsorbedCards),
            onNotFound: notFound =>
            {
                RestoreAllAbsorbed(absorbedAnchors, originalContents);
                return new CardRuleCompactOutcome.CardNotFound(notFound.FilePath);
            },
            onAlreadyExists: alreadyExists =>
            {
                RestoreAllAbsorbed(absorbedAnchors, originalContents);
                return new CardRuleCompactOutcome.LayoutMismatch(
                    $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite.");
            },
            onLayoutMismatch: layoutMismatch =>
            {
                RestoreAllAbsorbed(absorbedAnchors, originalContents);
                return new CardRuleCompactOutcome.LayoutMismatch(layoutMismatch.Reason);
            },
            onCorrupt: corrupt =>
            {
                RestoreAllAbsorbed(absorbedAnchors, originalContents);
                return new CardRuleCompactOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason);
            },
            onToolFailure: toolFailure =>
            {
                RestoreAllAbsorbed(absorbedAnchors, originalContents);
                return new CardRuleCompactOutcome.ToolFailure(toolFailure.Reason);
            },
            onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
    }

    /// <summary>All-or-nothing's other half for <see cref="CompactRulesUnderLocks"/>'s own final
    /// step: once the family's write fails, restores every absorbed rule already written in this
    /// call back to its pre-call bytes, the same content-based restore <see cref="RestoreCardContent"/>
    /// gives a single card.</summary>
    private static void RestoreAllAbsorbed(IReadOnlyList<AnchoredCardPath> absorbedAnchors, IReadOnlyList<string> originalContents)
    {
        for (var i = 0; i < absorbedAnchors.Count; i++)
        {
            RestoreCardContent(absorbedAnchors[i], originalContents[i]);
        }
    }

    /// <summary>Reads <paramref name="filePath"/> fresh and confirms it is an open, change-scoped
    /// <c>rule</c> card — the checks <see cref="CompactRulesUnderLocks"/> applies identically to
    /// the family side and every absorbed side, differing only in which refusal an already-
    /// discharged card earns (<paramref name="isFamilySide"/> picks <see cref="
    /// CardRuleCompactOutcome.FamilyAlreadyDischarged"/> vs. <see cref="CardRuleCompactOutcome.
    /// AbsorbedAlreadyDischarged"/> — the same "acting side"/"target side" distinction <see cref="
    /// SupersedeDecision"/> draws with two separate already-discharged cases). The scope check here
    /// is this block's own restriction to change-scoped compaction (see <see cref="CompactRules"/>'s
    /// doc comment) — a repository-scoped rule is refused before the anchor check even runs, since
    /// <see cref="AnchoredCardPath.TryCreate"/> alone cannot distinguish "the wrong change" from
    /// "not change-scoped at all".</summary>
    private static (CardRuleCompactOutcome? Refusal, CardFile? Card) ReadOpenChangeScopedRule(
        string cardsRoot, string filePath, string changeName, bool isFamilySide, CardOwner actingRole, DateTimeOffset timestamp)
    {
        if (!File.Exists(filePath))
        {
            return (new CardRuleCompactOutcome.CardNotFound(filePath), null);
        }

        var read = ReadCard(filePath);
        var card = read.Match<CardFile?>(onSuccess: static s => s.Card, onFailure: static _ => null);
        if (card is null)
        {
            var reason = read.Match(onSuccess: static _ => string.Empty, onFailure: static f => f.Reason);
            return (new CardRuleCompactOutcome.CardCorrupt(filePath, reason), null);
        }

        if (ReservedDerivedStateFieldKeyIn(card) is { } reservedKey)
        {
            return (RefuseAndRecord<CardRuleCompactOutcome, CardRuleCompactOutcome.HandEnteredDerivedState>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardRuleCompactOutcome.HandEnteredDerivedState(filePath, reservedKey),
                static reason => new CardRuleCompactOutcome.ToolFailure(reason)), null);
        }

        if (!IsRuleCard(card))
        {
            return (RefuseAndRecord<CardRuleCompactOutcome, CardRuleCompactOutcome.NotARuleCard>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardRuleCompactOutcome.NotARuleCard(filePath, card.Frontmatter.Kind),
                static reason => new CardRuleCompactOutcome.ToolFailure(reason)), null);
        }

        // register: "SHALL NOT occupy flow states" — enforced at the parse door (§12 block A):
        // CardFileParser validates a register card's status against RegisterLifecycleStateWireFormat
        // before the card is ever constructed, and IsRuleCard above already confirmed this card's
        // own kind, so this can only ever succeed.
        if (!RegisterLifecycleStateWireFormat.TryParse(card.Frontmatter.Status, out var state))
        {
            throw new InvalidOperationException("unreachable: a register card's status is validated at the parse door.");
        }

        if (state == RegisterLifecycleState.Discharged)
        {
            return isFamilySide
                ? (RefuseAndRecord<CardRuleCompactOutcome, CardRuleCompactOutcome.FamilyAlreadyDischarged>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardRuleCompactOutcome.FamilyAlreadyDischarged(filePath),
                    static reason => new CardRuleCompactOutcome.ToolFailure(reason)), null)
                : (RefuseAndRecord<CardRuleCompactOutcome, CardRuleCompactOutcome.AbsorbedAlreadyDischarged>(cardsRoot, card, filePath, changeName, actingRole, timestamp,
                        new CardRuleCompactOutcome.AbsorbedAlreadyDischarged(filePath),
                    static reason => new CardRuleCompactOutcome.ToolFailure(reason)), null);
        }

        var isChangeScoped = card.Frontmatter.Scope.Match(
            onSection: static () => false,
            onChange: static () => true,
            onCapability: static () => false,
            onRepository: static () => false);
        if (!isChangeScoped)
        {
            return (new CardRuleCompactOutcome.LayoutMismatch(
                $"'{filePath}' is '{card.Frontmatter.Scope.ToWireString()}'-scoped; compaction in this build only " +
                $"applies to 'change'-scoped rules within '{changeName}' (repository-scoped compaction is proposed " +
                "and decided by the Product Owner, not applied directly)."), null);
        }

        return (null, card);
    }

    /// <summary>
    /// Records a clean finding (findings: "Clean findings are cards") and, when
    /// <paramref name="raiseRequest"/> is supplied, the <c>obligation</c> or <c>hazard</c> its
    /// declared blind spot is raised as — in the same operation (§6 block B Architect ruling: "one
    /// command, two cards ... all-or-nothing, each referencing the other"). The finding references
    /// the raised card through its own <see cref="FindingBlindSpotDeclaration.RaisedAs"/>; the
    /// raised card references the finding by id in its own body — see this method's body for
    /// exactly where each is set.
    ///
    /// <para>
    /// <b>Two <see cref="CardLock"/>s, not one (§6 block B remediation, reviewer blocker 1).</b> The
    /// brief that opened this block claimed one lock sufficed because the raised card's path "comes
    /// from the allocated identity" — that reasoning was wrong: <see cref="FindingBlindSpotRaiseRequest.
    /// FilePath"/> is caller-supplied (<c>--blind-spot-file</c>), unrelated to the allocated id,
    /// which appears only inside the file's content, not its path. Two concurrent invocations can
    /// name the identical <c>--blind-spot-file</c> path, and the reviewer reproduced exactly that
    /// against the unlocked version of this method: one invocation's raised-card write landed, then
    /// a second invocation's own write silently overwrote it, and the first invocation's subsequent
    /// rollback then deleted the second invocation's card out from under it — the same class of hole
    /// card-model 4.5 closed by making <see cref="CardLock.CardPath"/> the only source of the path a
    /// write acts on ("a lock cannot vouch for a file it does not name"). Every card this method
    /// writes now has its own lock held for the write's whole duration — see
    /// <see cref="AcquireLocksAndRecord"/>.
    /// </para>
    ///
    /// <para>
    /// <b>No lock ordering at all (§6 block B fifth remediation, reviewer's "cross-invocation lock
    /// ordering" finding).</b> An earlier version of this method decided a deterministic
    /// <see cref="StringComparer.Ordinal"/> order over the two paths so two concurrent invocations
    /// naming the same pair would request their locks in the same sequence. That broke: two
    /// invocations naming the identical <em>pair of physical files</em> but spelling them with
    /// different casing can each compute a <em>different</em> ordinal order for the pair — a
    /// genuine AB/BA deadlock across invocations, since an ordinal order over path strings is not
    /// evidence about file identity, the same lesson <see cref="CardLock.CurrentlyNames"/> already
    /// carries for the single-invocation case. This method no longer orders anything: see
    /// <see cref="AcquireLocksAndRecord"/>'s own doc comment for the acquire/probe/release-and-retry
    /// shape that makes an ordering unnecessary — no call ever blocks while holding a resource, so
    /// no two invocations can disagree about an order that no longer exists.
    /// </para>
    ///
    /// <para>
    /// <b>Directories created before any lock is acquired (§6 block B remediation, reviewer
    /// blocker 3).</b> <see cref="CardLock.Acquire"/>'s first step creates a <c>.lock</c> file beside
    /// the target, which requires the target's directory to already exist — the same reasoning
    /// <see cref="WriteCard"/>'s own doc comment states for doing this ahead of its own lock
    /// acquisition, and the ordering this method failed to follow until this remediation, which made
    /// the verb's primary use case — the first finding a section ever raises, the first obligation a
    /// change ever raises, where neither directory exists yet — spin for the full lock timeout and
    /// report a misleading <c>tool-failure</c>. Both the finding's and (when present) the raised
    /// card's directory are created here, before <see cref="AcquireLocksAndRecord"/> is ever called.
    /// </para>
    ///
    /// <para>
    /// <b>All-or-nothing, by write order (§6 block B Architect ruling: "if the second write fails,
    /// neither card is left behind").</b> The raised card, if any, is written <em>first</em>, then
    /// the finding — and the finding's own create-only existence check runs only <em>after</em> the
    /// raised card has already landed on disk, not before, so a pre-occupied
    /// <paramref name="findingFilePath"/> reaches the rollback path for real rather than short-
    /// circuiting before the raised card is ever written. If the finding's write then fails for any
    /// reason, the raised card that was just written is deleted before this method returns — by
    /// content, not by path (§6 block B remediation, reviewer blocker 2): <see cref="RollbackRaisedCard"/>
    /// only unlinks the file when its current content still matches what this call itself wrote, the
    /// same compare-then-delete discipline <see cref="CardLock.Dispose"/> already applies in this
    /// codebase for exactly this reason.
    /// </para>
    /// </summary>
    /// <param name="section">The section this finding was raised within — the same id a raised
    /// obligation's own <c>owed_by</c> is set from (§7 block C), since a raised obligation is owed
    /// to exactly the section that raised it.</param>
    /// <param name="changeName">Required for the finding (always <see cref="CardScope.Section"/>-
    /// scoped) and for an obligation (<see cref="CardScope.Change"/>-scoped) when one is raised;
    /// ignored for a hazard (<see cref="CardScope.Repository"/>-scoped).</param>
    internal static CardFindingRecordOutcome RecordFinding(
        string cardsRoot,
        string findingFilePath,
        string title,
        CardOwner actingRole,
        string section,
        string body,
        string? instrument,
        FindingExtent extent,
        string? verifiedAt,
        FindingBlindSpotRaiseRequest? raiseRequest,
        FindingDisposition disposition,
        DateTimeOffset timestamp,
        TimeSpan lockTimeout,
        string changeName)
    {
        var (findingId, findingAllocationFailure) = AllocateIdentity(cardsRoot, CardKind.Finding, lockTimeout);
        if (findingAllocationFailure is not null)
        {
            return new CardFindingRecordOutcome.ToolFailure(findingAllocationFailure);
        }

        string? raisedId = null;
        if (raiseRequest is not null)
        {
            var (allocatedRaisedId, raisedAllocationFailure) = AllocateIdentity(cardsRoot, raiseRequest.Kind, lockTimeout);
            if (raisedAllocationFailure is not null)
            {
                return new CardFindingRecordOutcome.ToolFailure(raisedAllocationFailure);
            }

            raisedId = allocatedRaisedId;
        }

        var blindSpot = raiseRequest is null
            ? FindingBlindSpotDeclaration.None
            : FindingBlindSpotDeclaration.RaisedAs(raisedId!);

        var findingFrontmatter = new CardFrontmatter(
            findingId!, CardKind.Finding, title, "open", actingRole, CardScope.Section, section, timestamp, timestamp);

        // §6 block C ruling: "At record time the tool fingerprints the files/ranges the declared
        // extent covers and stores that alongside verified_at." FindingExtentFingerprint.Compute
        // itself decides whether there is anything to fingerprint (only an Explicit extent has a
        // file set) — this call site does not need to branch on the extent's own form.
        //
        // Deliberately outside AcquireLocksAndRecord's lock, below (reviewer finding, §6 block C
        // review round 1 — corrected here after an earlier DEVLOG post inaccurately claimed this
        // read happened "inside the same locked write"). The CardLock a finding record acquires
        // protects the finding's (and any raised card's) own file — it says nothing about, and
        // cannot say anything about, the arbitrary source files an Explicit extent names, so there
        // is no lock to hold this read under in the first place. This is sound, not merely
        // unguarded: if the extent's declared content changes in the window between this read and
        // the write below, the fingerprint stored is of the *older* content, so a later `finding
        // status` comparison reports the extent stale — the safe direction this section's own
        // "never under-report" rule requires. A second `finding record` racing the same extent's
        // source files would carry the identical TOCTOU exposure regardless of which side of the
        // lock this read sat on, since the lock never covers those files either way.
        var extentFingerprint = FindingExtentFingerprint.Compute(extent, cardsRoot);
        var findingFields = new FindingCardFields(instrument, extent, verifiedAt, blindSpot, extentFingerprint, disposition);

        // Both directories exist before either lock is requested (reviewer blocker 3) — WriteCard's
        // own doc comment states why: a lock file is created beside the target, which needs the
        // target's directory to already exist, or the whole lock-acquire loop retries a create that
        // can never succeed.
        var findingDirectory = Path.GetDirectoryName(findingFilePath);
        if (string.IsNullOrEmpty(findingDirectory))
        {
            return new CardFindingRecordOutcome.FindingLayoutMismatch($"'{findingFilePath}' has no containing directory to write into.");
        }

        if (raiseRequest is not null)
        {
            var raisedDirectory = Path.GetDirectoryName(raiseRequest.FilePath);
            if (string.IsNullOrEmpty(raisedDirectory))
            {
                return new CardFindingRecordOutcome.BlindSpotLayoutMismatch($"'{raiseRequest.FilePath}' has no containing directory to write into.");
            }

            Directory.CreateDirectory(raisedDirectory);
        }

        Directory.CreateDirectory(findingDirectory);

        return AcquireLocksAndRecord(
            findingFilePath, raiseRequest?.FilePath, lockTimeout,
            () => RecordFindingUnderLocks(cardsRoot, findingFilePath, findingFrontmatter, body, findingFields, raiseRequest, raisedId, changeName),
            static reason => new CardFindingRecordOutcome.ToolFailure(reason));
    }

    /// <summary>
    /// Acquires the lock(s) a two-card write needs — one for <paramref name="firstFilePath"/>,
    /// and, when <paramref name="raisedFilePath"/> is not <see langword="null"/> and not the same
    /// file, a second for it — then runs <paramref name="action"/> with both held. Generalised over
    /// <typeparamref name="TOutcome"/> (§9 remediation, round two — S4) so <see cref="PromoteComment"/>
    /// can share this exact discipline rather than <see cref="RecordFinding"/>'s own copy of it —
    /// the §8a supervisor's flag against a third divergent multi-card write shape in this type is
    /// exactly what a fourth, hand-copied acquire/probe/retry loop would have been.
    /// <paramref name="onToolFailure"/> lets each caller mint its own union's <c>ToolFailure</c> case.
    ///
    /// <para>
    /// <b>No ordering, by design (§6 block B fifth remediation, reviewer's "cross-invocation lock
    /// ordering" finding).</b> An earlier version of this method decided which lock to acquire
    /// first by comparing <paramref name="firstFilePath"/> and <paramref name="raisedFilePath"/>
    /// with <see cref="StringComparer.Ordinal"/>, on the reasoning that two different calls would
    /// always agree on the order regardless of the filesystem. That reasoning already broke once,
    /// for the single-invocation "same file, different spelling" case (block B's fourth
    /// remediation) — and it breaks again here for the same underlying reason, one call earlier:
    /// two concurrent invocations that name the identical <em>pair</em> of physical files but spell
    /// them with different casing can each compute a <em>different</em> ordinal order for that
    /// pair, purely from how each one happened to spell its own paths — invocation 1 might lock
    /// physical file Z first (wanting A second) while invocation 2, spelling the same two files
    /// differently, locks physical file A first (wanting Z second): a genuine AB/BA deadlock,
    /// bounded only by <paramref name="lockTimeout"/>. A path string is not evidence of file
    /// identity — <see cref="CardLock.CurrentlyNames"/> already established that for the single-
    /// invocation case — and an ordering built from those same strings inherits the same defect one
    /// level up. There is no canonical form of a not-yet-existing file's name to fall back to
    /// either: what a doubled separator or a case variant resolves to is exactly the fact the
    /// volume itself decides, not something <c>Path.GetFullPath</c> or any other pure-string
    /// function can predict in advance.
    /// </para>
    ///
    /// <para>
    /// <b>The shape: acquire, probe, release-and-retry — never hold while waiting.</b> This method
    /// always attempts <paramref name="firstFilePath"/>'s lock first, <em>blocking</em> for
    /// whatever remains of <paramref name="lockTimeout"/>. Once held, if <paramref name="
    /// raisedFilePath"/> is <see langword="null"/> or names the identical file (<see cref="
    /// CardLock.CurrentlyNames"/> — the same evidence-based check the fourth remediation
    /// introduced, still doing its original job here), <paramref name="action"/> runs with the one
    /// lock. Otherwise it attempts <paramref name="raisedFilePath"/>'s lock with <b>no wait at
    /// all</b> (<see cref="CardLock.Acquire"/> with <see cref="TimeSpan.Zero"/>): if that succeeds,
    /// <paramref name="action"/> runs with both held; if it does not, the finding's lock is
    /// released immediately (never held past this point) and, after a short jittered backoff, the
    /// whole pair is retried from the top — bounded by the same overall <paramref name="
    /// lockTimeout"/> deadline, tracked across every retry, not reset per attempt. <b>No global
    /// order is needed because no call ever blocks while holding a resource</b> — the only wait in
    /// this method is the very first lock, held by nobody yet when the wait begins, so no cycle of
    /// "holds X, blocked on Y" can form between two invocations regardless of how each one spells
    /// its own paths or which physical files those paths turn out to name. Demonstrated, not just
    /// argued: <c>CardFindingRecordConcurrencyTests</c>' new
    /// <c>WhenTheRaisedLockIsUnavailable_TheFindingLockIsReleasedBetweenRetries_NotHeldWhileWaiting</c>
    /// proves this directly — while a real call is stuck retrying because the raised lock is held
    /// elsewhere, the finding lock is independently, successfully acquired and released by a third
    /// party from outside the call, which could not happen if this method held it across the wait.
    /// </para>
    ///
    /// <para>
    /// <b>The final refusal is honest, not "held by pid &lt;its own pid&gt;".</b> If the retry
    /// budget is exhausted, the message names what actually happened — repeated contention with
    /// another writer over the raised card's path — rather than reporting a lock timeout on a
    /// resource this call was never actually blocked holding.
    /// </para>
    /// </summary>
    private static TOutcome AcquireLocksAndRecord<TOutcome>(
        string firstFilePath, string? raisedFilePath, TimeSpan lockTimeout, Func<TOutcome> action, Func<string, TOutcome> onToolFailure)
        where TOutcome : class
    {
        var deadline = DateTimeOffset.UtcNow + lockTimeout;

        while (true)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return onToolFailure(
                    $"could not acquire both locks this two-card write needs within {lockTimeout.TotalSeconds:0.###}s — " +
                    $"repeatedly lost the race for '{raisedFilePath}' against another concurrent write; retry.");
            }

            var firstLockResult = CardLock.Acquire(firstFilePath, remaining);
            var outcome = firstLockResult.Match<TOutcome?>(
                onAcquired: firstAcquired =>
                {
                    using (firstAcquired.Lock)
                    {
                        if (raisedFilePath is null || firstAcquired.Lock.CurrentlyNames(raisedFilePath))
                        {
                            return action();
                        }

                        // No wait at all: this is the "probe" half of acquire-probe-release-retry.
                        // A miss releases firstFilePath's lock (the using block above) and falls
                        // through to the retry below — it never blocks while still holding it.
                        var secondLockResult = CardLock.Acquire(raisedFilePath, TimeSpan.Zero);
                        return secondLockResult.Match<TOutcome?>(
                            onAcquired: secondAcquired =>
                            {
                                using (secondAcquired.Lock)
                                {
                                    return action();
                                }
                            },
                            onTimedOut: static _ => null);
                    }
                },
                // A genuine timeout on the first lock is a real, external hold — the ordinary
                // honest message applies unchanged, naming that lock's actual holder.
                onTimedOut: timedOut => onToolFailure(timedOut.Message));

            if (outcome is not null)
            {
                return outcome;
            }

            // Jittered backoff before the whole pair is retried — same shape CardLock's own retry
            // loop uses and for the same reason: a fixed delay lets every loser wake in lockstep and
            // collide again.
            Thread.Sleep(TimeSpan.FromMilliseconds(5 + Random.Shared.Next(0, 15)));
        }
    }

    /// <summary>The locked step of <see cref="RecordFinding"/> — both the finding's and (when
    /// present) the raised card's locks are already held by the time this runs
    /// (<see cref="AcquireLocksAndRecord"/>). Directory creation already happened in
    /// <see cref="RecordFinding"/>, before either lock was acquired.</summary>
    private static CardFindingRecordOutcome RecordFindingUnderLocks(
        string cardsRoot,
        string findingFilePath,
        CardFrontmatter findingFrontmatter,
        string findingBody,
        FindingCardFields findingFields,
        FindingBlindSpotRaiseRequest? raiseRequest,
        string? raisedId,
        string changeName)
    {
        // Deliberately not checked here, ahead of the raised card's own write below: the finding's
        // existence is what this method's doc comment calls the "second write" — checked
        // immediately before the finding's own AtomicWrite call, after the raised card (if any) has
        // already landed on disk, which is what makes the rollback path below reachable at all
        // rather than dead code a pre-check would make unnecessary in every ordinary run.
        string? raisedContent = null;
        CardFile? raisedCard = null;
        if (raiseRequest is not null)
        {
            var raisedScope = ScopeForRaisedCard(raiseRequest.Kind);
            var raisedAnchored = AnchoredCardPath.TryCreate(cardsRoot, raiseRequest.FilePath, raisedScope, changeName, out var raisedLayoutFailure);
            if (raisedAnchored is null)
            {
                return new CardFindingRecordOutcome.BlindSpotLayoutMismatch(raisedLayoutFailure!.Reason);
            }

            if (File.Exists(raiseRequest.FilePath))
            {
                return new CardFindingRecordOutcome.BlindSpotCardAlreadyExists(raiseRequest.FilePath);
            }

            var raisedFrontmatter = new CardFrontmatter(
                raisedId!, raiseRequest.Kind, raiseRequest.Title, "open", findingFrontmatter.Owner,
                raisedScope, OwningSectionId(findingFrontmatter), findingFrontmatter.Created, findingFrontmatter.Created);

            // The raised card's half of "each referencing the other" (§6 block B brief) — the
            // finding's own reference is FindingCardFields.BlindSpot.RaisedAs(raisedId), set by the
            // caller of this method. No new frontmatter key is minted for the finding→raised-card
            // direction: that stays a body-text reference only (§7 block C brief: "do not invent a
            // structured finding→obligation back-reference — that belongs with earned_from").
            //
            // The other direction — a raised obligation's own owed_by — is §7 block C's, and is set
            // here: findingFrontmatter.Section is already a validated section card id by the time
            // this runs (RunFindingRecord's own ValidateSection call, ahead of RecordFinding), the
            // same id CommandDispatcher.RunObligationCreate would otherwise require a caller to
            // spell out via --section. A raised obligation is owed to exactly the section that
            // raised it, so this is the one case where that id is already in hand rather than
            // supplied — "give that obligation a real owed_by like any other" (Architect ruling),
            // not a free-text label, and not a second, hand-typed --section a caller could get
            // wrong. A raised hazard carries no owed_by (register: only an obligation is owed to a
            // section) — the ternary is exhaustive over the only two kinds
            // FindingBlindSpotRaiseRequest's own constructor ever allows.
            var raisedIsObligation = raiseRequest.Kind.Match(
                onBlock: static () => false,
                onQuestion: static () => false,
                onFinding: static () => false,
                onObligation: static () => true,
                onRule: static () => false,
                onHazard: static () => false,
                onDecision: static () => false,
                onSection: static () => false);
            var raisedRegisterFields = raisedIsObligation
                ? new RegisterCardFields(null, null, null, null, OwedBy: OwningSectionId(findingFrontmatter))
                : RegisterCardFields.Empty;
            var raisedBody =
                $"Raised from finding {findingFrontmatter.Id} — a blind spot declared while recording a clean result.\n\n{raiseRequest.Body}";
            var raisedCardFile = new CardFile(raisedFrontmatter, raisedBody, [], [], RegisterFields: raisedRegisterFields);
            var serializedRaisedCard = CardFileWriter.Serialize(raisedCardFile);

            var raisedWriteResult = AtomicWrite(raisedAnchored, serializedRaisedCard);
            var raisedFailure = raisedWriteResult.Match<CardFindingRecordOutcome?>(
                onSuccess: static _ => null,
                onNotFound: static notFound => new CardFindingRecordOutcome.ToolFailure(
                    $"unexpected 'not found' writing a brand-new card at '{notFound.FilePath}'."),
                onAlreadyExists: static alreadyExists => new CardFindingRecordOutcome.BlindSpotCardAlreadyExists(alreadyExists.FilePath),
                onLayoutMismatch: static layoutMismatch => new CardFindingRecordOutcome.BlindSpotLayoutMismatch(layoutMismatch.Reason),
                onCorrupt: static corrupt => new CardFindingRecordOutcome.ToolFailure(
                    $"unexpected corruption reported writing a brand-new card at '{corrupt.FilePath}': {corrupt.Reason}"),
                onToolFailure: static toolFailure => new CardFindingRecordOutcome.ToolFailure(toolFailure.Reason),
                onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
            if (raisedFailure is not null)
            {
                return raisedFailure;
            }

            raisedCard = raisedCardFile;
            raisedContent = serializedRaisedCard;
        }

        var findingAnchored = AnchoredCardPath.TryCreate(cardsRoot, findingFilePath, findingFrontmatter.Scope, changeName, out var findingLayoutFailure);
        if (findingAnchored is null)
        {
            RollbackRaisedCard(raiseRequest, raisedContent);
            return new CardFindingRecordOutcome.FindingLayoutMismatch(findingLayoutFailure!.Reason);
        }

        // The finding's own create-only check — same shape as WriteCard's own ternary, deliberately
        // placed here rather than earlier in this method: AtomicWrite itself always overwrites
        // unconditionally (File.Move(overwrite: true)), so this is the one place anything checks
        // whether a card already sits at findingFilePath. Running it only now, after the raised
        // card (if any) has already been written, is what makes a genuine "first write succeeded,
        // second failed" case reachable — pre-occupy findingFilePath before calling this method and
        // the raised card write above still runs and lands on disk before this check ever sees the
        // conflict.
        if (File.Exists(findingFilePath))
        {
            RollbackRaisedCard(raiseRequest, raisedContent);
            return new CardFindingRecordOutcome.FindingAlreadyExists(findingFilePath);
        }

        var findingCardFile = new CardFile(findingFrontmatter, findingBody, [], [], FindingFields: findingFields);
        var findingWriteResult = AtomicWrite(findingAnchored, CardFileWriter.Serialize(findingCardFile));

        return findingWriteResult.Match<CardFindingRecordOutcome>(
            onSuccess: _ => new CardFindingRecordOutcome.Recorded(findingCardFile, raisedCard),
            onNotFound: notFound =>
            {
                RollbackRaisedCard(raiseRequest, raisedContent);
                return new CardFindingRecordOutcome.ToolFailure($"unexpected 'not found' writing a brand-new card at '{notFound.FilePath}'.");
            },
            onAlreadyExists: alreadyExists =>
            {
                RollbackRaisedCard(raiseRequest, raisedContent);
                return new CardFindingRecordOutcome.FindingAlreadyExists(alreadyExists.FilePath);
            },
            onLayoutMismatch: layoutMismatch =>
            {
                RollbackRaisedCard(raiseRequest, raisedContent);
                return new CardFindingRecordOutcome.FindingLayoutMismatch(layoutMismatch.Reason);
            },
            onCorrupt: corrupt =>
            {
                RollbackRaisedCard(raiseRequest, raisedContent);
                return new CardFindingRecordOutcome.ToolFailure(
                    $"unexpected corruption reported writing a brand-new card at '{corrupt.FilePath}': {corrupt.Reason}");
            },
            onToolFailure: toolFailure =>
            {
                RollbackRaisedCard(raiseRequest, raisedContent);
                return new CardFindingRecordOutcome.ToolFailure(toolFailure.Reason);
            },
            onRoundDisagreesWithHistory: static _ => throw new InvalidOperationException("unreachable: RoundAgreesWithHistory is checked, and refuses, before AtomicWrite is ever reached for a block card."),
                    onHandEnteredDerivedState: static _ => throw new InvalidOperationException("unreachable: AtomicWrite never returns this case; a reserved derived-state field is refused before this point."));
    }

    /// <summary>
    /// All-or-nothing's other half: deletes the raised card <see cref="RecordFindingUnderLocks"/>
    /// has already written, once the finding's own write, tried afterward, fails for any reason.
    /// <b>Compare-then-delete, not delete-by-path (§6 block B remediation, reviewer blocker 2).</b>
    /// <see cref="CardLock.Dispose"/> already establishes this discipline in this same codebase —
    /// "releasing must mean unlink the file this instance itself created, not unlink whatever
    /// currently sits at this path" — because a blind delete-by-path can remove content this call
    /// never wrote if something else has since legitimately taken over the path. This is the same
    /// shape: the file is deleted only when its current content still matches
    /// <paramref name="raisedContent"/>, the exact bytes this call itself wrote to
    /// <paramref name="raiseRequest"/>'s own path; a mismatch is treated as a lost race, not an
    /// error. With both cards' paths now locked for this call's whole duration
    /// (<see cref="AcquireLocksAndRecord"/>), no other <c>finding record</c> invocation can actually
    /// produce that mismatch any more — but the guard costs nothing and matches the standing
    /// discipline regardless. Best-effort otherwise, same disposition as <see cref="CardLock.Dispose"/>'s
    /// own release — if the delete itself cannot complete, the caller already has a failure to act
    /// on and this is not the place to escalate a cleanup problem into a second, different one.
    /// </summary>
    private static void RollbackRaisedCard(FindingBlindSpotRaiseRequest? raiseRequest, string? raisedContent)
    {
        if (raiseRequest is null || raisedContent is null)
        {
            return;
        }

        try
        {
            if (!File.Exists(raiseRequest.FilePath))
            {
                return;
            }

            var currentContent = File.ReadAllText(raiseRequest.FilePath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (string.Equals(currentContent, raisedContent, StringComparison.Ordinal))
            {
                File.Delete(raiseRequest.FilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }


    /// <summary>
    /// The fixed scope a blind spot's raised card takes by kind — <see cref="CardScope.Change"/> for
    /// an <see cref="CardKind.Obligation"/>, <see cref="CardScope.Repository"/> for a
    /// <see cref="CardKind.Hazard"/> — exactly what <see cref="CardScopeRules.Validate"/> already
    /// requires for those two kinds (<see cref="CardFindingRecordScopeAgreementTests"/> in the test
    /// project asserts the two never drift apart), restated here as a direct function rather than a
    /// second call through the refusal-shaped <see cref="CardScopeValidationResult"/> because this
    /// caller never lets a caller choose the scope — there is nothing here for that type's refusal
    /// case to ever report. <see cref="FindingBlindSpotRaiseRequest"/>'s own constructor is what
    /// makes every other <see cref="CardKind"/> unreachable at this call site; the remaining five
    /// arms below exist only so this switch stays exhaustive over the closed union, not because any
    /// of them can run.
    /// </summary>
    private static CardScope ScopeForRaisedCard(CardKind raisedKind) => raisedKind.Match(
        onBlock: static () => throw new InvalidOperationException("unreachable: FindingBlindSpotRaiseRequest only ever carries Obligation or Hazard."),
        onQuestion: static () => throw new InvalidOperationException("unreachable: FindingBlindSpotRaiseRequest only ever carries Obligation or Hazard."),
        onFinding: static () => throw new InvalidOperationException("unreachable: FindingBlindSpotRaiseRequest only ever carries Obligation or Hazard."),
        onObligation: static () => CardScope.Change,
        onRule: static () => throw new InvalidOperationException("unreachable: FindingBlindSpotRaiseRequest only ever carries Obligation or Hazard."),
        onHazard: static () => CardScope.Repository,
        onDecision: static () => throw new InvalidOperationException("unreachable: FindingBlindSpotRaiseRequest only ever carries Obligation or Hazard."),
        onSection: static () => throw new InvalidOperationException("unreachable: FindingBlindSpotRaiseRequest only ever carries Obligation or Hazard."));

    /// <summary>
    /// Shared by every caller below except <see cref="CreateCard"/> (§13: <see cref="CreateCard"/>
    /// needs the full <see cref="CardIdentityAllocationResult.Borne"/> shape, to record its refusal
    /// against the card already bearing the identity — none of these five callers build a
    /// <see cref="ICardRefusalReason"/>-shaped outcome for that case, so a borne identity collapses
    /// into the same flat <c>Failure</c> string a lock timeout or an unverifiable counter already
    /// does here. Still fail-closed — none of these five can hand out a reissued identity either —
    /// just without the dedicated recording <see cref="CreateCard"/> gets.
    /// </summary>
    private static (string? Id, string? Failure) AllocateIdentity(string cardsRoot, CardKind kind, TimeSpan lockTimeout) =>
        CardIdentityAllocator.Allocate(cardsRoot, kind, lockTimeout).Match(
            onAllocated: allocated => ((string?)allocated.Id, (string?)null),
            onFailed: failed => ((string?)null, (string?)failed.Reason),
            onBorne: borne => ((string?)null, (string?)
                $"identity '{borne.Id}' is already borne by the record ({string.Join(", ", borne.CardFilePaths)}); " +
                "run 'index rebuild' to reconcile the identity counter."));

    /// <summary>Shared by <see cref="ApplyBlockTransitionUnderExistingLock"/>,
    /// <see cref="RecordGateResultUnderExistingLock"/>, <see cref="UpdateBlockedByUnderExistingLock"/>
    /// and <see cref="RecordApprovalUnderExistingLock"/> — the one place "is this card's kind block"
    /// is decided, over the closed <see cref="CardKind"/> union, so the four verbs cannot drift on
    /// what counts as a block card. <see langword="internal"/> (§8 block A, same reason
    /// <see cref="IsSectionCard"/> already went <see langword="internal"/> in §5 remediation) so
    /// <see cref="Callboard.Cli.CommandDispatcher.RunBlockApprove"/> can pass this to
    /// <see cref="Callboard.Cli.CommandDispatcher.ResolveCardReference"/> instead of re-implementing
    /// the same eight-arm match a second time.</summary>
    /// <summary>work-lifecycle: "Stored round agrees with the transition history" (8a.17) — the
    /// count this checks a block card's stored <see cref="BlockCardFields.Round"/> (unset reading
    /// as round 1, the same default every increment site applies) against. Reads
    /// <see cref="BlockFlowTransitions.RoundIncrementingTransitionNames"/> rather than restating the
    /// three-name set here, so a transition added to that table later is picked up without this
    /// method changing.</summary>
    private static int CountRoundIncrementingTransitions(IReadOnlyList<CardBlockTransitionEntry> transitions)
    {
        var roundIncrementingNames = BlockFlowTransitions.RoundIncrementingTransitionNames;
        var count = 0;
        foreach (var entry in transitions)
        {
            foreach (var name in roundIncrementingNames)
            {
                if (string.Equals(entry.Name, name, StringComparison.Ordinal))
                {
                    count++;
                    break;
                }
            }
        }

        return count;
    }

    /// <summary>The one check every writer that mutates a block card applies before touching it
    /// (work-lifecycle: "Stored round agrees with the transition history", 8a.17; Architect ruling,
    /// §8a block D brief — "act on that card" covers every writer that mutates a block card; reads
    /// are unaffected, since a card the tool refuses to describe is one nobody can diagnose).
    /// <see langword="false"/> when <paramref name="card"/>'s stored <c>round</c> (unset reading as
    /// round 1, <see cref="RecordApprovalUnderExistingLock"/>'s own comment for why that default is
    /// shared) does not equal one plus <see cref="CountRoundIncrementingTransitions"/> of its own
    /// <see cref="CardFile.Transitions"/> — with both figures out so the caller can name them without
    /// recomputing either, and without this method reconciling or altering the disagreement itself.
    /// Callers of this method are expected to have already established <paramref name="card"/> is a
    /// block card (<see cref="IsBlockCard"/>) — it is meaningless, not merely unchecked, against any
    /// other kind, since only a block card carries <see cref="CardFile.BlockFields"/> or
    /// <see cref="CardFile.Transitions"/>.</summary>
    private static bool RoundAgreesWithHistory(CardFile card, out int storedRound, out int expectedRound)
    {
        storedRound = card.BlockFields.Round ?? 1;
        expectedRound = 1 + CountRoundIncrementingTransitions(card.Transitions);
        return storedRound == expectedRound;
    }

    internal static bool IsBlockCard(CardFile card) => card.Frontmatter.Kind.Match(
        onBlock: static () => true,
        onQuestion: static () => false,
        onFinding: static () => false,
        onObligation: static () => false,
        onRule: static () => false,
        onHazard: static () => false,
        onDecision: static () => false,
        onSection: static () => false);

    /// <summary>The <see cref="IsBlockCard"/> counterpart for <see cref="CardKind.Section"/>
    /// (§5 block E), shared by <see cref="RecordSectionVerdictUnderExistingLock"/> and
    /// <see cref="CloseSectionUnderExistingLock"/> so the two verbs cannot drift on what counts as
    /// a section card. <see langword="internal"/> (§5 remediation, DEVLOG §5 finding N7) so
    /// <see cref="Callboard.Cli.CommandDispatcher.RunSectionStatus"/> can share this predicate too,
    /// instead of re-implementing the same eight-arm match a second time.</summary>
    internal static bool IsSectionCard(CardFile card) => card.Frontmatter.Kind.Match(
        onBlock: static () => false,
        onQuestion: static () => false,
        onFinding: static () => false,
        onObligation: static () => false,
        onRule: static () => false,
        onHazard: static () => false,
        onDecision: static () => false,
        onSection: static () => true);

    /// <summary>Answers "which section does this card belong to?" for an arbitrary card
    /// (§9 remediation round three, F2). <see cref="CardFrontmatter.Section"/> is empty on a
    /// <b>section</b> card — a section does not name itself as its own owner — so a caller that
    /// wants the owning section of a card whose kind it has not already established must not read
    /// <see cref="CardFrontmatter.Section"/> directly: for a section card the owning section is the
    /// card's own <see cref="CardFrontmatter.Id"/>, and for every other kind it is
    /// <see cref="CardFrontmatter.Section"/> as stored. Routed through <see cref="CardKind.Match"/>,
    /// the same idiom as <see cref="IsSectionCard"/> and its siblings, so a new kind is a compile
    /// error here rather than a silently wrong answer.
    ///
    /// This is deliberately <b>not</b> a stored value — <c>Id</c> and <c>Section</c> on a section
    /// card would be two fields obliged to agree, the exact shape 8a.17's round-agrees-with-history
    /// refusal exists to catch. Do not special-case section cards at a call site instead of calling
    /// this; do not write <c>section</c> onto a section card's frontmatter to make this
    /// unnecessary.
    ///
    /// Not for a call site that means something else — a <b>block</b> or <b>question</b> card's own
    /// <see cref="CardFrontmatter.Section"/> matched against a <b>section</b> card's own
    /// <see cref="CardFrontmatter.Id"/> (the section-scan join every section-scoped verb performs)
    /// reads <see cref="CardFrontmatter.Section"/> directly and must keep doing so — passing the
    /// section card itself through this accessor there would be a no-op at best and confusing at
    /// worst.</summary>
    internal static string OwningSectionId(CardFrontmatter frontmatter) => frontmatter.Kind.Match(
        onBlock: () => frontmatter.Section,
        onQuestion: () => frontmatter.Section,
        onFinding: () => frontmatter.Section,
        onObligation: () => frontmatter.Section,
        onRule: () => frontmatter.Section,
        onHazard: () => frontmatter.Section,
        onDecision: () => frontmatter.Section,
        onSection: () => frontmatter.Id);

    /// <summary>The <see cref="IsBlockCard"/>/<see cref="IsSectionCard"/> counterpart for
    /// <see cref="CardKind.Question"/> (§9 block D) — shared by <see cref="AnswerQuestionUnderExistingLock"/>,
    /// <see cref="DeferQuestionUnderExistingLock"/> and the <c>blocked_by</c>-resolution check
    /// <see cref="FindBlockingOpenProductOwnerQuestion"/> applies against every id a block card
    /// names, so none of the three re-implements the same eight-arm match independently.</summary>
    internal static bool IsQuestionCard(CardFile card) => card.Frontmatter.Kind.Match(
        onBlock: static () => false,
        onQuestion: static () => true,
        onFinding: static () => false,
        onObligation: static () => false,
        onRule: static () => false,
        onHazard: static () => false,
        onDecision: static () => false,
        onSection: static () => false);

    /// <summary>The <see cref="IsBlockCard"/>/<see cref="IsSectionCard"/> counterpart for
    /// <see cref="CardKind.Finding"/> (§6 block C), <see langword="internal"/> for the same reason
    /// <see cref="IsSectionCard"/> is — so <see cref="Callboard.Cli.CommandDispatcher.
    /// RunFindingStatus"/> shares this predicate rather than re-implementing the same eight-arm
    /// match a third time.</summary>
    internal static bool IsFindingCard(CardFile card) => card.Frontmatter.Kind.Match(
        onBlock: static () => false,
        onQuestion: static () => false,
        onFinding: static () => true,
        onObligation: static () => false,
        onRule: static () => false,
        onHazard: static () => false,
        onDecision: static () => false,
        onSection: static () => false);

    /// <summary>The <see cref="IsBlockCard"/>/<see cref="IsSectionCard"/>/<see cref="IsFindingCard"/>
    /// counterpart for the four register kinds (§7 block A), <see langword="internal"/> for the
    /// same reason <see cref="IsSectionCard"/> is — so <see cref="Callboard.Cli.CommandDispatcher"/>
    /// shares this predicate rather than re-implementing the same eight-arm match a fourth
    /// time.</summary>
    internal static bool IsRegisterCard(CardFile card) => card.Frontmatter.Kind.Match(
        onBlock: static () => false,
        onQuestion: static () => false,
        onFinding: static () => false,
        onObligation: static () => true,
        onRule: static () => true,
        onHazard: static () => true,
        onDecision: static () => true,
        onSection: static () => false);

    /// <summary>The <see cref="IsRegisterCard"/> counterpart narrowed to exactly
    /// <see cref="CardKind.Decision"/> (§7 block C) — shared by <see cref="Callboard.Cli.
    /// CommandDispatcher"/>'s <c>--supersedes</c> resolution and
    /// <see cref="SupersedeDecisionUnderLocks"/>, so neither re-implements the same eight-arm match
    /// a fifth time.</summary>
    internal static bool IsDecisionCard(CardFile card) => card.Frontmatter.Kind.Match(
        onBlock: static () => false,
        onQuestion: static () => false,
        onFinding: static () => false,
        onObligation: static () => false,
        onRule: static () => false,
        onHazard: static () => false,
        onDecision: static () => true,
        onSection: static () => false);

    /// <summary>The <see cref="IsRegisterCard"/> counterpart narrowed to exactly
    /// <see cref="CardKind.Obligation"/> (§7 block D) — shared by <see cref="ArchiveChange"/>, which
    /// needs to find exactly the change-scoped cards register's "closes its change-scoped cards"
    /// names, not every register kind a change directory might otherwise hold.</summary>
    internal static bool IsObligationCard(CardFile card) => card.Frontmatter.Kind.Match(
        onBlock: static () => false,
        onQuestion: static () => false,
        onFinding: static () => false,
        onObligation: static () => true,
        onRule: static () => false,
        onHazard: static () => false,
        onDecision: static () => false,
        onSection: static () => false);

    /// <summary>The <see cref="IsRegisterCard"/> counterpart narrowed to exactly
    /// <see cref="CardKind.Rule"/> (§7 block E) — shared by <see cref="Callboard.Cli.
    /// CommandDispatcher"/>'s <c>rule promote</c> resolution and <see cref="PromoteRule"/> itself, so
    /// neither re-implements the same eight-arm match a sixth time.</summary>
    internal static bool IsRuleCard(CardFile card) => card.Frontmatter.Kind.Match(
        onBlock: static () => false,
        onQuestion: static () => false,
        onFinding: static () => false,
        onObligation: static () => false,
        onRule: static () => true,
        onHazard: static () => false,
        onDecision: static () => false,
        onSection: static () => false);

    /// <summary>The <see cref="IsRegisterCard"/> counterpart narrowed to exactly
    /// <see cref="CardKind.Hazard"/> (§10 block A) — <see cref="Cards.WorkingContextAssembler"/>'s
    /// register-scan shares this predicate rather than re-implementing the same eight-arm match a
    /// seventh time.</summary>
    internal static bool IsHazardCard(CardFile card) => card.Frontmatter.Kind.Match(
        onBlock: static () => false,
        onQuestion: static () => false,
        onFinding: static () => false,
        onObligation: static () => false,
        onRule: static () => false,
        onHazard: static () => true,
        onDecision: static () => false,
        onSection: static () => false);

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

    private static CardWriteResult WithLock(string filePath, TimeSpan lockTimeout, Func<CardLock, CardWriteResult> action) =>
        WithLock(filePath, lockTimeout, action, onTimedOut: static timedOut => new CardWriteResult.ToolFailure(timedOut.Message));

    /// <summary>
    /// The lock-acquire-then-run shape every locked write on this type shares, generalised over
    /// its result type so <see cref="ApplyBlockTransition"/> — whose failure needs to be a
    /// <see cref="CardBlockTransitionOutcome"/>, not a <see cref="CardWriteResult"/>, so the CLI
    /// boundary can mint distinct refusal codes — reuses the same acquire/dispose/timeout logic as
    /// the <see cref="CardWriteResult"/>-returning overload above, rather than a second
    /// hand-copied implementation the two could drift apart from.
    /// </summary>
    private static TResult WithLock<TResult>(
        string filePath, TimeSpan lockTimeout, Func<CardLock, TResult> action, Func<CardLockResult.TimedOut, TResult> onTimedOut)
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
            onTimedOut: onTimedOut);
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
            return new CardWriteResult.LayoutMismatch($"'{filePath}' has no containing directory to write into.");
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
            return new CardWriteResult.ToolFailure($"could not write '{filePath}': {ex.Message}");
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
