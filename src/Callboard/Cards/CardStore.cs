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
                : AtomicWrite(anchored, CardFileWriter.Serialize(new CardFile(card.Frontmatter, card.Body, [], []))));
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
    /// legal from its current status by reading <see cref="BlockFlowTransitions.AvailableFrom"/>
    /// (never a second hand-maintained list), and only if it is legal writes the new status, the
    /// possibly-newly-recorded <c>base</c>, the possibly-incremented <c>round</c>, and an appended
    /// <see cref="CardBlockTransitionEntry"/> — all under the card's lock, so the refusal and the
    /// write share one read (obligation O-3: a refusal must prevent the side effect it refuses,
    /// not merely follow it).
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
                if (!IsBlockCard(card))
                {
                    return new CardBlockTransitionOutcome.NotABlockCard(card.Frontmatter.Kind);
                }

                if (!BlockFlowStateWireFormat.TryParse(card.Frontmatter.Status, out var currentState))
                {
                    return new CardBlockTransitionOutcome.CardCorrupt(
                        filePath, $"unrecognised status: '{card.Frontmatter.Status}'. Recognised statuses: {BlockFlowStateWireFormat.RecognisedValues}.");
                }

                var available = BlockFlowTransitions.AvailableFrom(currentState);
                var transition = available.FirstOrDefault(candidate => string.Equals(candidate.Name, transitionName, StringComparison.Ordinal));
                if (transition is null)
                {
                    return new CardBlockTransitionOutcome.UndefinedTransition(currentState, available);
                }

                var recordedBase = card.BlockFields.Base;
                if (recordedBase is not null && baseCommit is not null && !string.Equals(recordedBase, baseCommit, StringComparison.Ordinal))
                {
                    return new CardBlockTransitionOutcome.BaseImmutable(recordedBase, baseCommit);
                }

                var effectiveBase = recordedBase ?? baseCommit;
                if (transition.To == BlockFlowState.Briefed && effectiveBase is null)
                {
                    return new CardBlockTransitionOutcome.BaseNotRecorded();
                }

                var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return new CardBlockTransitionOutcome.LayoutMismatch(layoutFailure!.Reason);
                }

                // "changes-requested" is work-lifecycle's own named increment; any other
                // transition that lands the card on Briefed for the first time (the initial
                // "brief") starts the round at 1 rather than leaving it unset.
                var round = card.BlockFields.Round;
                if (string.Equals(transition.Name, "changes-requested", StringComparison.Ordinal))
                {
                    round = (round ?? 0) + 1;
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
                    onToolFailure: toolFailure => new CardBlockTransitionOutcome.ToolFailure(toolFailure.Reason));
            },
            onFailure: failure =>
                new CardBlockTransitionOutcome.CardCorrupt(filePath, failure.Reason));
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
                if (!IsBlockCard(card))
                {
                    return new CardGateResultOutcome.NotABlockCard(card.Frontmatter.Kind);
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
                    onToolFailure: toolFailure => new CardGateResultOutcome.ToolFailure(toolFailure.Reason));
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

    /// <summary>The read-decide-write step of <see cref="AddBlockedBy"/>.</summary>
    internal static CardBlockedByOutcome AddBlockedByUnderExistingLock(
        CardLock heldLock, string cardsRoot, string blockingCardId, CardOwner actingRole, DateTimeOffset timestamp, string? changeName = null) =>
        UpdateBlockedByUnderExistingLock(
            heldLock, cardsRoot, blockingCardId, actingRole, timestamp, changeName,
            apply: (current, id) => current.Contains(id, StringComparer.Ordinal)
                ? (Updated: false, Result: current)
                : (Updated: true, Result: current.Add(id)),
            onNoChange: id => new CardBlockedByOutcome.AlreadyBlockedBy(id));

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
            onNoChange: id => new CardBlockedByOutcome.NotBlockedBy(id));

    /// <summary>
    /// The read-decide-write shape <see cref="AddBlockedByUnderExistingLock"/> and
    /// <see cref="RemoveBlockedByUnderExistingLock"/> share — same structural lock precondition as
    /// every other <c>*UnderExistingLock</c> method — differing only in how
    /// <paramref name="apply"/> decides the new <c>blocked_by</c> set from the current one and what
    /// <paramref name="onNoChange"/> refuses with when nothing needed to change.
    /// </summary>
    private static CardBlockedByOutcome UpdateBlockedByUnderExistingLock(
        CardLock heldLock,
        string cardsRoot,
        string blockingCardId,
        CardOwner actingRole,
        DateTimeOffset timestamp,
        string? changeName,
        Func<ImmutableArray<string>, string, (bool Updated, ImmutableArray<string> Result)> apply,
        Func<string, CardBlockedByOutcome> onNoChange)
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
                if (!IsBlockCard(card))
                {
                    return new CardBlockedByOutcome.NotABlockCard(card.Frontmatter.Kind);
                }

                var (updated, newBlockedBy) = apply(card.BlockFields.BlockedBy, blockingCardId);
                if (!updated)
                {
                    return onNoChange(blockingCardId);
                }

                var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return new CardBlockedByOutcome.LayoutMismatch(layoutFailure!.Reason);
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
                    onToolFailure: toolFailure => new CardBlockedByOutcome.ToolFailure(toolFailure.Reason));
            },
            onFailure: failure =>
                new CardBlockedByOutcome.CardCorrupt(filePath, failure.Reason));
    }

    /// <summary>
    /// Appends one supervisor verdict to the section card at <paramref name="filePath"/>
    /// (work-lifecycle: "Sections are entities" — "the verdict, the range and the acting role are
    /// recorded against that section entity", §5 block E) — reads the current card, and only if it
    /// is a section card, appends the entry under the card's lock, the same read-decide-write shape
    /// <see cref="ApplyBlockTransition"/> established. A second verdict for the same section is a
    /// second entry, not an upsert — see <see cref="SectionVerdictEntry"/>'s own doc comment for
    /// why (unlike <see cref="RecordGateResult"/>'s label-keyed upsert).
    /// </summary>
    internal static CardSectionVerdictOutcome RecordSectionVerdict(
        string cardsRoot, string filePath, SectionVerdict verdict, string rangeFrom, string rangeTo, CardOwner actingRole, DateTimeOffset timestamp, TimeSpan lockTimeout, string? changeName = null) =>
        WithLock(
            filePath,
            lockTimeout,
            heldLock => RecordSectionVerdictUnderExistingLock(heldLock, cardsRoot, verdict, rangeFrom, rangeTo, actingRole, timestamp, changeName),
            onTimedOut: timedOut => new CardSectionVerdictOutcome.ToolFailure(timedOut.Message));

    /// <summary>
    /// The read-decide-write step of <see cref="RecordSectionVerdict"/>. Same structural lock
    /// precondition as every other <c>*UnderExistingLock</c> method on this type — the target is
    /// <see cref="CardLock.CardPath"/>, not a separately supplied <c>filePath</c>.
    /// </summary>
    internal static CardSectionVerdictOutcome RecordSectionVerdictUnderExistingLock(
        CardLock heldLock, string cardsRoot, SectionVerdict verdict, string rangeFrom, string rangeTo, CardOwner actingRole, DateTimeOffset timestamp, string? changeName = null)
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
                if (!IsSectionCard(card))
                {
                    return new CardSectionVerdictOutcome.NotASectionCard(card.Frontmatter.Kind);
                }

                var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return new CardSectionVerdictOutcome.LayoutMismatch(layoutFailure!.Reason);
                }

                var entry = new SectionVerdictEntry(actingRole, verdict, rangeFrom, rangeTo, timestamp, []);
                var updated = card with
                {
                    Frontmatter = card.Frontmatter with { Updated = timestamp },
                    SectionFields = card.SectionFields with { Verdicts = [.. card.SectionFields.Verdicts, entry] },
                };

                var writeResult = AtomicWrite(anchored, CardFileWriter.Serialize(updated));
                return writeResult.Match<CardSectionVerdictOutcome>(
                    onSuccess: _ => new CardSectionVerdictOutcome.Recorded(updated, entry),
                    onNotFound: notFound => new CardSectionVerdictOutcome.CardNotFound(notFound.FilePath),
                    onAlreadyExists: alreadyExists => new CardSectionVerdictOutcome.LayoutMismatch(
                        $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite."),
                    onLayoutMismatch: layoutMismatch => new CardSectionVerdictOutcome.LayoutMismatch(layoutMismatch.Reason),
                    onCorrupt: corrupt => new CardSectionVerdictOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason),
                    onToolFailure: toolFailure => new CardSectionVerdictOutcome.ToolFailure(toolFailure.Reason));
            },
            onFailure: failure =>
                new CardSectionVerdictOutcome.CardCorrupt(filePath, failure.Reason));
    }

    /// <summary>
    /// Closes the section card at <paramref name="filePath"/> (work-lifecycle: "Sections are
    /// entities" — "closing it SHALL record the acting role and the time", §5 block E) — reads the
    /// current card, and only if it is a section card not already closed, writes
    /// <c>status: closed</c> plus <c>closed_by</c>/<c>closed_at</c> under the card's lock. Whether a
    /// section is <em>permitted</em> to close (§9: open obligations, undeferred questions,
    /// unresolved threads) is not decided here — see <see cref="CardSectionCloseOutcome"/>'s own
    /// doc comment.
    /// </summary>
    internal static CardSectionCloseOutcome CloseSection(
        string cardsRoot, string filePath, CardOwner actingRole, DateTimeOffset timestamp, TimeSpan lockTimeout, string? changeName = null) =>
        WithLock(
            filePath,
            lockTimeout,
            heldLock => CloseSectionUnderExistingLock(heldLock, cardsRoot, actingRole, timestamp, changeName),
            onTimedOut: timedOut => new CardSectionCloseOutcome.ToolFailure(timedOut.Message));

    /// <summary>
    /// The read-decide-write step of <see cref="CloseSection"/>. Same structural lock precondition
    /// as every other <c>*UnderExistingLock</c> method on this type — the target is
    /// <see cref="CardLock.CardPath"/>, not a separately supplied <c>filePath</c>.
    /// </summary>
    internal static CardSectionCloseOutcome CloseSectionUnderExistingLock(
        CardLock heldLock, string cardsRoot, CardOwner actingRole, DateTimeOffset timestamp, string? changeName = null)
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
                if (!IsSectionCard(card))
                {
                    return new CardSectionCloseOutcome.NotASectionCard(card.Frontmatter.Kind);
                }

                if (!SectionFlowStateWireFormat.TryParse(card.Frontmatter.Status, out var currentState))
                {
                    return new CardSectionCloseOutcome.CardCorrupt(
                        filePath, $"unrecognised status: '{card.Frontmatter.Status}'. Recognised statuses: {SectionFlowStateWireFormat.RecognisedValues}.");
                }

                if (currentState == SectionFlowState.Closed)
                {
                    return new CardSectionCloseOutcome.AlreadyClosed(filePath);
                }

                var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return new CardSectionCloseOutcome.LayoutMismatch(layoutFailure!.Reason);
                }

                var updated = card with
                {
                    Frontmatter = card.Frontmatter with { Status = SectionFlowState.Closed.ToWireString(), Updated = timestamp },
                    SectionFields = card.SectionFields with { ClosedBy = actingRole, ClosedAt = timestamp },
                };

                var writeResult = AtomicWrite(anchored, CardFileWriter.Serialize(updated));
                return writeResult.Match<CardSectionCloseOutcome>(
                    onSuccess: _ => new CardSectionCloseOutcome.Closed(updated),
                    onNotFound: notFound => new CardSectionCloseOutcome.CardNotFound(notFound.FilePath),
                    onAlreadyExists: alreadyExists => new CardSectionCloseOutcome.LayoutMismatch(
                        $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite."),
                    onLayoutMismatch: layoutMismatch => new CardSectionCloseOutcome.LayoutMismatch(layoutMismatch.Reason),
                    onCorrupt: corrupt => new CardSectionCloseOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason),
                    onToolFailure: toolFailure => new CardSectionCloseOutcome.ToolFailure(toolFailure.Reason));
            },
            onFailure: failure =>
                new CardSectionCloseOutcome.CardCorrupt(filePath, failure.Reason));
    }

    /// <summary>Shared by <see cref="ApplyBlockTransitionUnderExistingLock"/>,
    /// <see cref="RecordGateResultUnderExistingLock"/> and <see cref="UpdateBlockedByUnderExistingLock"/>
    /// — the one place "is this card's kind block" is decided, over the closed
    /// <see cref="CardKind"/> union, so the three verbs cannot drift on what counts as a block
    /// card.</summary>
    private static bool IsBlockCard(CardFile card) => card.Frontmatter.Kind.Match(
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
