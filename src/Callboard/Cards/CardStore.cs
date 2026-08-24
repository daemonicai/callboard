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
                : AtomicWrite(anchored, CardFileWriter.Serialize(new CardFile(card.Frontmatter, card.Body, [], [], FindingFields: card.FindingFields, RegisterFields: card.RegisterFields))));
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

                // review-certification: "Undispositioned nits block the verdict" (§8 block B) —
                // every transition that leaves in-review is bound, not just changes-requested;
                // approve carries its own copy of this check (CardApprovalOutcome.
                // UndispositionedNits) since it never reaches this table (approve-via-transition-
                // refused), and fix-before-land never reaches this method at all (refused at parse).
                if (transition.From == BlockFlowState.InReview)
                {
                    var liveNitIds = CardCommentRouting.LiveUndispositionedNitIds(card.Comments);
                    if (liveNitIds.Count > 0)
                    {
                        return new CardBlockTransitionOutcome.UndispositionedNits(liveNitIds);
                    }
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
    /// <see langword="true"/> for the two roles review-certification's "Approval is role-bounded"
    /// permits to record an <c>approve</c> verdict or a recertification — <c>reviewer</c> and
    /// <c>supervisor</c>. §8 block A ships this half of 8.13's enforcement (approval's own role
    /// check); block C's <c>recertify</c> is expected to call this same predicate rather than
    /// re-deriving the same two-role fact a second way.
    /// </summary>
    internal static bool IsApprovingRole(CardOwner role) => role.Match(
        onArchitect: static () => false,
        onWorker: static () => false,
        onReviewer: static () => true,
        onSupervisor: static () => true,
        onProductOwner: static () => false);

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
    /// <see cref="CardLock.CardPath"/>, not a separately supplied <c>filePath</c>. The role check is
    /// the very first thing decided, ahead of even <see cref="File.Exists(string)"/> — the same
    /// ordering <see cref="CompactRules"/>'s own doc comment justifies: "role-not-permitted is a fact
    /// about whether this call is allowed to happen at all, not about the shape of its arguments."
    /// Everything else is validated — kind, current state, layout — before the one
    /// <see cref="AtomicWrite"/> call that lands claims, limits, <c>reviewed_state</c> and the
    /// transition together.
    /// </summary>
    internal static CardApprovalOutcome RecordApprovalUnderExistingLock(
        CardLock heldLock, string cardsRoot, string reviewedState, IReadOnlyList<string> claimTexts, IReadOnlyList<string> limitTexts,
        CardOwner actingRole, DateTimeOffset timestamp, string? changeName = null)
    {
        ArgumentNullException.ThrowIfNull(heldLock);
        var filePath = heldLock.CardPath;

        if (!IsApprovingRole(actingRole))
        {
            return new CardApprovalOutcome.RoleNotPermitted(actingRole);
        }

        if (!File.Exists(filePath))
        {
            return new CardApprovalOutcome.CardNotFound(filePath);
        }

        var current = ReadCard(filePath);
        return current.Match<CardApprovalOutcome>(
            onSuccess: success =>
            {
                var card = success.Card;
                if (!IsBlockCard(card))
                {
                    return new CardApprovalOutcome.NotABlockCard(card.Frontmatter.Kind);
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
                    return new CardApprovalOutcome.UndefinedTransition(currentState, available);
                }

                // review-certification: "Undispositioned nits block the verdict" (§8 block B) — an
                // approve is one of the transitions that moves a block out of in-review, so it is
                // bound by the same requirement ApplyBlockTransitionUnderExistingLock's own
                // changes-requested arm checks below.
                var liveNitIds = CardCommentRouting.LiveUndispositionedNitIds(card.Comments);
                if (liveNitIds.Count > 0)
                {
                    return new CardApprovalOutcome.UndispositionedNits(liveNitIds);
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
                    onToolFailure: toolFailure => new CardApprovalOutcome.ToolFailure(toolFailure.Reason));
            },
            onFailure: failure =>
                new CardApprovalOutcome.CardCorrupt(filePath, failure.Reason));
    }

    /// <summary>
    /// Records one <c>block recertify</c> call (review-certification: "Recertification re-asserts
    /// an existing claim set", §8 block C) — the one door to re-stamping <c>reviewed_state</c> on
    /// an already-<c>approved</c> block, or to refusing it back to <c>briefed</c> claim-by-claim.
    /// Same lock/role/read-decide-write shape as <see cref="RecordApproval"/>.
    /// </summary>
    internal static CardRecertificationOutcome RecordRecertification(
        string cardsRoot, string filePath, string amendedState, IReadOnlyList<string> assertedClaimIds, IReadOnlyList<string> refusedClaimIds,
        CardOwner actingRole, DateTimeOffset timestamp, TimeSpan lockTimeout, string? changeName = null) =>
        WithLock(
            filePath,
            lockTimeout,
            heldLock => RecordRecertificationUnderExistingLock(heldLock, cardsRoot, amendedState, assertedClaimIds, refusedClaimIds, actingRole, timestamp, changeName),
            onTimedOut: timedOut => new CardRecertificationOutcome.ToolFailure(timedOut.Message));

    /// <summary>
    /// The read-decide-write step of <see cref="RecordRecertification"/>. Same structural lock
    /// precondition as every other <c>*UnderExistingLock</c> method on this type — the target is
    /// <see cref="CardLock.CardPath"/>, not a separately supplied <c>filePath</c>. Role checked
    /// first, ahead of <see cref="File.Exists(string)"/>, the same ordering
    /// <see cref="RecordApprovalUnderExistingLock"/> already uses (<see cref="IsApprovingRole"/> —
    /// 8.13's recertification half, reused rather than re-derived).
    ///
    /// <para>
    /// <b>"Since the current approval" (8.10) is derived, not stored.</b> The bound attaches to the
    /// <em>approval</em>, not the card (Architect ruling: a block recertified, sent back to
    /// <c>briefed</c>, rebuilt and approved again is a new approval and gets a fresh
    /// recertification). Rather than a raw boolean field that would need its own reset logic on
    /// every fresh <c>approve</c> — exactly the shape that has already produced two defects in this
    /// section (§8 block B's own remediation, twice) — this scans the record: the current
    /// approval's start is the timestamp of the most recent <see cref="CardBlockTransitionEntry"/>
    /// named <c>approve</c> (<see cref="BlockFlowTransitions.AvailableFrom"/>'s only edge into
    /// <see cref="BlockFlowState.Approved"/>), and <see cref="CardCommentRouting.
    /// HasRecertification"/> is asked only about comments at or after that timestamp — the same
    /// round-boundary idiom §8 block B's remediation established for <see cref="CardCommentRouting.
    /// HasFixBeforeLandDisposition"/>. A recertification from a superseded, earlier approval never
    /// counts against a later one.
    /// </para>
    ///
    /// <para>
    /// <b>Every claim must receive an outcome (Architect ruling).</b> The current approval's whole
    /// claim set is <c>card.Claims</c> whose <see cref="CardApprovalClaim.Round"/> equals the
    /// card's current <see cref="BlockCardFields.Round"/> — the same round-scoping
    /// <see cref="RecordApprovalUnderExistingLock"/> stamps a claim with in the first place, so it
    /// stays exactly the approval's claim set for as long as the card remains <c>approved</c> (no
    /// other transition touches <c>Round</c> while <c>approved</c>). A caller-named id outside that
    /// set is refused (<see cref="CardRecertificationOutcome.UnknownClaimIds"/>); a claim in that
    /// set named by neither <c>--assert</c> nor <c>--refuse</c> is refused
    /// (<see cref="CardRecertificationOutcome.MissingClaimOutcomes"/>) — both checked, and both
    /// refuse, before anything is written.
    /// </para>
    /// </summary>
    internal static CardRecertificationOutcome RecordRecertificationUnderExistingLock(
        CardLock heldLock, string cardsRoot, string amendedState, IReadOnlyList<string> assertedClaimIds, IReadOnlyList<string> refusedClaimIds,
        CardOwner actingRole, DateTimeOffset timestamp, string? changeName = null)
    {
        ArgumentNullException.ThrowIfNull(heldLock);
        var filePath = heldLock.CardPath;

        if (!IsApprovingRole(actingRole))
        {
            return new CardRecertificationOutcome.RoleNotPermitted(actingRole);
        }

        if (!File.Exists(filePath))
        {
            return new CardRecertificationOutcome.CardNotFound(filePath);
        }

        var current = ReadCard(filePath);
        return current.Match<CardRecertificationOutcome>(
            onSuccess: success =>
            {
                var card = success.Card;
                if (!IsBlockCard(card))
                {
                    return new CardRecertificationOutcome.NotABlockCard(card.Frontmatter.Kind);
                }

                if (!BlockFlowStateWireFormat.TryParse(card.Frontmatter.Status, out var currentState))
                {
                    return new CardRecertificationOutcome.CardCorrupt(
                        filePath, $"unrecognised status: '{card.Frontmatter.Status}'. Recognised statuses: {BlockFlowStateWireFormat.RecognisedValues}.");
                }

                if (currentState != BlockFlowState.Approved)
                {
                    return new CardRecertificationOutcome.NotApproved(currentState);
                }

                var approvedAt = DateTimeOffset.MinValue;
                for (var i = card.Transitions.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(card.Transitions[i].Name, "approve", StringComparison.Ordinal))
                    {
                        approvedAt = card.Transitions[i].Timestamp;
                        break;
                    }
                }

                var commentsSinceApproval = card.Comments.Where(c => c.Timestamp >= approvedAt).ToList();
                if (CardCommentRouting.HasRecertification(commentsSinceApproval))
                {
                    return new CardRecertificationOutcome.AlreadyRecertified();
                }

                var currentRound = card.BlockFields.Round ?? 1;
                var currentClaimIds = new HashSet<string>(
                    card.Claims.Where(claim => claim.Round == currentRound).Select(claim => claim.Id), StringComparer.Ordinal);

                var namedIds = new HashSet<string>(StringComparer.Ordinal);
                var unknownIds = new List<string>();
                foreach (var id in assertedClaimIds.Concat(refusedClaimIds))
                {
                    namedIds.Add(id);
                    if (!currentClaimIds.Contains(id))
                    {
                        unknownIds.Add(id);
                    }
                }

                if (unknownIds.Count > 0)
                {
                    return new CardRecertificationOutcome.UnknownClaimIds(unknownIds);
                }

                var missingIds = currentClaimIds.Where(id => !namedIds.Contains(id)).ToList();
                if (missingIds.Count > 0)
                {
                    return new CardRecertificationOutcome.MissingClaimOutcomes(missingIds);
                }

                var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return new CardRecertificationOutcome.LayoutMismatch(layoutFailure!.Reason);
                }

                var recordComment = new CardComment(
                    Id: $"recertification-{Guid.NewGuid():N}", Author: actingRole, Timestamp: timestamp,
                    Body: BuildRecertificationBody(amendedState, assertedClaimIds, refusedClaimIds),
                    ReplyTo: null, To: null, Resolves: null, UnknownHeaderFields: [], IsRecertification: true);

                CardFile updated;
                if (refusedClaimIds.Count == 0)
                {
                    // review-certification: "A successful recertification SHALL re-stamp
                    // reviewed_state to the amended state" — round does not move (Architect
                    // ruling), and status stays approved, so nothing else on the card changes.
                    updated = card with
                    {
                        Frontmatter = card.Frontmatter with { Updated = timestamp },
                        BlockFields = card.BlockFields with { ReviewedState = amendedState },
                        Comments = [.. card.Comments, recordComment],
                    };
                }
                else
                {
                    var transition = BlockFlowTransitions.AvailableFrom(currentState)
                        .First(candidate => string.Equals(candidate.Name, "recertification-refused", StringComparison.Ordinal));
                    var entry = new CardBlockTransitionEntry(actingRole, transition.Name, transition.From, transition.To, timestamp, []);
                    updated = card with
                    {
                        Frontmatter = card.Frontmatter with { Status = transition.To.ToWireString(), Updated = timestamp },
                        BlockFields = card.BlockFields with { Round = (card.BlockFields.Round ?? 0) + 1 },
                        Transitions = [.. card.Transitions, entry],
                        Comments = [.. card.Comments, recordComment],
                    };
                }

                var writeResult = AtomicWrite(anchored, CardFileWriter.Serialize(updated));
                return writeResult.Match<CardRecertificationOutcome>(
                    onSuccess: _ => refusedClaimIds.Count == 0
                        ? new CardRecertificationOutcome.Recertified(updated, assertedClaimIds)
                        : new CardRecertificationOutcome.ClaimsRefused(updated, assertedClaimIds, refusedClaimIds),
                    onNotFound: notFound => new CardRecertificationOutcome.CardNotFound(notFound.FilePath),
                    onAlreadyExists: alreadyExists => new CardRecertificationOutcome.LayoutMismatch(
                        $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite."),
                    onLayoutMismatch: layoutMismatch => new CardRecertificationOutcome.LayoutMismatch(layoutMismatch.Reason),
                    onCorrupt: corrupt => new CardRecertificationOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason),
                    onToolFailure: toolFailure => new CardRecertificationOutcome.ToolFailure(toolFailure.Reason));
            },
            onFailure: failure =>
                new CardRecertificationOutcome.CardCorrupt(filePath, failure.Reason));
    }

    /// <summary>
    /// Records one <c>block amendment-requested</c> call (§8 block C remediation, work-lifecycle:
    /// "`amendment-requested` is the architect deliberately reopening an approved block for a
    /// further amendment") — the one door to the <c>approved → briefed</c> edge of that name.
    /// Role-bounded to <c>architect</c> (<see cref="IsArchitectRole"/>, the same predicate
    /// <see cref="DispositionNit"/> already uses), unlike <see cref="ApplyBlockTransition"/>'s
    /// generic path, which never restricts who may apply a named edge — this verb specifically
    /// exists to be a deliberate architect act, not a fact any role may record.
    /// </summary>
    internal static CardAmendmentRequestOutcome RecordAmendmentRequest(
        string cardsRoot, string filePath, CardOwner actingRole, DateTimeOffset timestamp, TimeSpan lockTimeout, string? changeName = null) =>
        WithLock(
            filePath,
            lockTimeout,
            heldLock => RecordAmendmentRequestUnderExistingLock(heldLock, cardsRoot, actingRole, timestamp, changeName),
            onTimedOut: timedOut => new CardAmendmentRequestOutcome.ToolFailure(timedOut.Message));

    /// <summary>
    /// The read-decide-write step of <see cref="RecordAmendmentRequest"/>. Same structural lock
    /// precondition as every other <c>*UnderExistingLock</c> method on this type — the target is
    /// <see cref="CardLock.CardPath"/>, not a separately supplied <c>filePath</c>. Role checked
    /// first, ahead of <see cref="File.Exists(string)"/>, the same ordering <see cref="
    /// RecordApprovalUnderExistingLock"/> and <see cref="RecordRecertificationUnderExistingLock"/>
    /// already use.
    /// </summary>
    internal static CardAmendmentRequestOutcome RecordAmendmentRequestUnderExistingLock(
        CardLock heldLock, string cardsRoot, CardOwner actingRole, DateTimeOffset timestamp, string? changeName = null)
    {
        ArgumentNullException.ThrowIfNull(heldLock);
        var filePath = heldLock.CardPath;

        if (!IsArchitectRole(actingRole))
        {
            return new CardAmendmentRequestOutcome.RoleNotPermitted(actingRole);
        }

        if (!File.Exists(filePath))
        {
            return new CardAmendmentRequestOutcome.CardNotFound(filePath);
        }

        var current = ReadCard(filePath);
        return current.Match<CardAmendmentRequestOutcome>(
            onSuccess: success =>
            {
                var card = success.Card;
                if (!IsBlockCard(card))
                {
                    return new CardAmendmentRequestOutcome.NotABlockCard(card.Frontmatter.Kind);
                }

                if (!BlockFlowStateWireFormat.TryParse(card.Frontmatter.Status, out var currentState))
                {
                    return new CardAmendmentRequestOutcome.CardCorrupt(
                        filePath, $"unrecognised status: '{card.Frontmatter.Status}'. Recognised statuses: {BlockFlowStateWireFormat.RecognisedValues}.");
                }

                var available = BlockFlowTransitions.AvailableFrom(currentState);
                var transition = available.FirstOrDefault(candidate => string.Equals(candidate.Name, "amendment-requested", StringComparison.Ordinal));
                if (transition is null)
                {
                    return new CardAmendmentRequestOutcome.UndefinedTransition(currentState, available);
                }

                var anchored = AnchoredCardPath.TryCreate(cardsRoot, filePath, card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return new CardAmendmentRequestOutcome.LayoutMismatch(layoutFailure!.Reason);
                }

                var entry = new CardBlockTransitionEntry(actingRole, transition.Name, transition.From, transition.To, timestamp, []);
                var updated = card with
                {
                    Frontmatter = card.Frontmatter with { Status = transition.To.ToWireString(), Updated = timestamp },
                    BlockFields = card.BlockFields with { Round = (card.BlockFields.Round ?? 0) + 1 },
                    Transitions = [.. card.Transitions, entry],
                };

                var writeResult = AtomicWrite(anchored, CardFileWriter.Serialize(updated));
                return writeResult.Match<CardAmendmentRequestOutcome>(
                    onSuccess: _ => new CardAmendmentRequestOutcome.Requested(updated),
                    onNotFound: notFound => new CardAmendmentRequestOutcome.CardNotFound(notFound.FilePath),
                    onAlreadyExists: alreadyExists => new CardAmendmentRequestOutcome.LayoutMismatch(
                        $"'{alreadyExists.FilePath}' unexpectedly reported as already existing during a targeted rewrite."),
                    onLayoutMismatch: layoutMismatch => new CardAmendmentRequestOutcome.LayoutMismatch(layoutMismatch.Reason),
                    onCorrupt: corrupt => new CardAmendmentRequestOutcome.CardCorrupt(corrupt.FilePath, corrupt.Reason),
                    onToolFailure: toolFailure => new CardAmendmentRequestOutcome.ToolFailure(toolFailure.Reason));
            },
            onFailure: failure =>
                new CardAmendmentRequestOutcome.CardCorrupt(filePath, failure.Reason));
    }

    /// <summary>Free-form prose recorded on the recertification-record comment
    /// (<see cref="CardComment.IsRecertification"/>) — human-readable context for a card read
    /// without the tool (ADR-0003); nothing re-parses this back. The structural facts (which claims,
    /// which outcome, whether the block moved) live in <see cref="CardRecertificationOutcome"/>'s
    /// own cases and the CLI response, not in this text.</summary>
    private static string BuildRecertificationBody(string amendedState, IReadOnlyList<string> assertedClaimIds, IReadOnlyList<string> refusedClaimIds)
    {
        var assertedText = assertedClaimIds.Count == 0 ? "none" : string.Join(", ", assertedClaimIds);
        if (refusedClaimIds.Count == 0)
        {
            return $"Recertified against '{amendedState}'. Asserted claim(s): {assertedText}.";
        }

        var refusedText = string.Join(", ", refusedClaimIds);
        return $"Recertification against '{amendedState}' refused. Asserted claim(s): {assertedText}. Refused claim(s): {refusedText}.";
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
                if (!IsBlockCard(card))
                {
                    return new CardNitDispositionOutcome.NotABlockCard(card.Frontmatter.Kind);
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
                    return new CardNitDispositionOutcome.NitNotFound(nitId);
                }

                if (CardCommentRouting.IsNitDispositioned(card.Comments, nitIndex))
                {
                    return new CardNitDispositionOutcome.AlreadyDispositioned(nitId);
                }

                var anchored = AnchoredCardPath.TryCreate(cardsRoot, nitFilePath, card.Frontmatter.Scope, changeName, out var layoutFailure);
                if (anchored is null)
                {
                    return new CardNitDispositionOutcome.LayoutMismatch(layoutFailure!.Reason);
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
                        return new CardNitDispositionOutcome.RaisedCardAlreadyExists(raiseRequest.FilePath);
                    }

                    var raisedFrontmatter = new CardFrontmatter(
                        raisedId!, raiseRequest.Kind, raiseRequest.Title, "open", actingRole, raisedScope, card.Frontmatter.Section, timestamp, timestamp);

                    // An obligation raised from a declined-or-deferred nit is owed to the same
                    // section the block itself belongs to — the same "give it a real owed_by, not a
                    // free-text label" ruling RecordFinding's own blind-spot obligation already
                    // applies (§7 block C). A decision carries no owed_by at all.
                    var raisedRegisterFields = raiseRequest.Kind == CardKind.Obligation
                        ? new RegisterCardFields(null, null, null, null, OwedBy: card.Frontmatter.Section)
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
                        onToolFailure: static toolFailure => new CardNitDispositionOutcome.ToolFailure(toolFailure.Reason));
                    if (raisedFailure is not null)
                    {
                        return raisedFailure;
                    }

                    raisedContent = serializedRaisedCard;
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
                            var transition = BlockFlowTransitions.AvailableFrom(currentState)
                                .First(candidate => string.Equals(candidate.Name, "fix-before-land", StringComparison.Ordinal));
                            updatedFrontmatter = updatedFrontmatter with { Status = transition.To.ToWireString() };
                            updatedBlockFields = updatedBlockFields with { Round = (updatedBlockFields.Round ?? 0) + 1 };
                            updatedTransitions = [.. updatedTransitions, new CardBlockTransitionEntry(actingRole, transition.Name, transition.From, transition.To, timestamp, [])];
                            transitioned = true;
                        }
                    }
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
                    });
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
    /// in this codebase already follows.
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
        string? changeName)
    {
        var scopeValidation = CardScopeRules.Validate(kind, scope);
        if (scopeValidation is CardScopeValidationResult.Refused refused)
        {
            return new CardCreateOutcome.ScopeRefused(refused.Reason);
        }

        var (id, allocationFailure) = AllocateIdentity(cardsRoot, kind, lockTimeout);
        if (allocationFailure is not null)
        {
            return new CardCreateOutcome.ToolFailure(allocationFailure);
        }

        var frontmatter = new CardFrontmatter(id!, kind, title, initialStatus, actingRole, scope, string.Empty, timestamp, timestamp);
        var cardFile = new CardFile(frontmatter, body, [], [], FindingFields: null, RegisterFields: registerFields);

        var writeResult = WriteCard(cardsRoot, filePath, new NewCardFile(frontmatter, body, RegisterFields: registerFields), lockTimeout, changeName);
        return writeResult.Match<CardCreateOutcome>(
            onSuccess: _ => new CardCreateOutcome.Created(cardFile),
            onNotFound: notFound => new CardCreateOutcome.ToolFailure(
                $"unexpected 'not found' writing a brand-new card at '{notFound.FilePath}'."),
            onAlreadyExists: alreadyExists => new CardCreateOutcome.AlreadyExists(alreadyExists.FilePath),
            onLayoutMismatch: layoutMismatch => new CardCreateOutcome.LayoutMismatch(layoutMismatch.Reason),
            onCorrupt: corrupt => new CardCreateOutcome.ToolFailure(
                $"unexpected corruption reported writing a brand-new card at '{corrupt.FilePath}': {corrupt.Reason}"),
            onToolFailure: toolFailure => new CardCreateOutcome.ToolFailure(toolFailure.Reason));
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
                if (!IsRegisterCard(card))
                {
                    return new CardRegisterDischargeOutcome.NotARegisterCard(card.Frontmatter.Kind);
                }

                // register: "SHALL NOT occupy flow states" — a real, exercised refusal, not merely
                // a documented intention. See RegisterLifecycleState's own doc comment.
                if (!RegisterLifecycleStateWireFormat.TryParse(card.Frontmatter.Status, out var currentState))
                {
                    return new CardRegisterDischargeOutcome.InvalidStatus(filePath, card.Frontmatter.Status);
                }

                if (currentState == RegisterLifecycleState.Discharged)
                {
                    return new CardRegisterDischargeOutcome.AlreadyDischarged(filePath);
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
                    onToolFailure: toolFailure => new CardRegisterDischargeOutcome.ToolFailure(toolFailure.Reason));
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
        string cardsRoot, string filePath, CardOwner actingRole, DateTimeOffset timestamp, TimeSpan lockTimeout) =>
        WithLock(
            filePath,
            lockTimeout,
            heldLock => PromoteRuleUnderExistingLock(heldLock, cardsRoot, actingRole, timestamp),
            onTimedOut: timedOut => new CardRulePromoteOutcome.ToolFailure(timedOut.Message));

    /// <summary>The read-decide-move-write step of <see cref="PromoteRule"/>. Same structural lock
    /// precondition as every other <c>*UnderExistingLock</c> method on this type.</summary>
    private static CardRulePromoteOutcome PromoteRuleUnderExistingLock(
        CardLock heldLock, string cardsRoot, CardOwner actingRole, DateTimeOffset timestamp)
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
                if (!IsRuleCard(card))
                {
                    return new CardRulePromoteOutcome.NotARuleCard(card.Frontmatter.Kind);
                }

                // register: "SHALL NOT occupy flow states" — the same exercised refusal every
                // other register mutation in this codebase already enforces.
                if (!RegisterLifecycleStateWireFormat.TryParse(card.Frontmatter.Status, out _))
                {
                    return new CardRulePromoteOutcome.InvalidStatus(originalFilePath, card.Frontmatter.Status);
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
                if (scopeRefusal is not null)
                {
                    return scopeRefusal;
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
                        return new CardRulePromoteOutcome.TargetAlreadyExists(targetFilePath);
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
                    onToolFailure: toolFailure => new CardRulePromoteOutcome.ToolFailure(toolFailure.Reason));
            },
            onFailure: failure => new CardRulePromoteOutcome.CardCorrupt(originalFilePath, failure.Reason));
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
    /// <b>"Closes its change-scoped cards" is settled here as: every <c>open</c> obligation in the
    /// directory becomes <c>discharged</c>, and nothing else.</b> Register's own scenario text
    /// names only obligations ("its change-scoped obligations are settled"); a block or section
    /// still short of its own flow-state close is left exactly as it is — whether a section <em>
    /// may</em> close given open obligations or undeferred questions is §9's refusal, not this
    /// verb's, and archiving is not required to force one first (register: "SHALL NOT require a
    /// carry-forward step"). Settling reuses <see cref="DischargeRegisterCard"/> unchanged, one
    /// call per open obligation, under that card's own per-card lock — no new write mechanism.
    /// </para>
    ///
    /// <para>
    /// <b>Two-phase, and the failure shape of each phase.</b> Phase one settles every open
    /// obligation while the directory is still live, each write independently atomic (temp file
    /// then rename, under its own lock) the same as any other card write. If phase one fails
    /// partway — a lock timeout on the third of five obligations, say — this method stops and
    /// reports <see cref="ChangeArchiveOutcome.ToolFailure"/> without ever attempting the move: the
    /// change is left live, with whichever obligations already settled staying settled (discharge
    /// is idempotent — a re-run skips them, <see cref="CardRegisterDischargeOutcome.
    /// AlreadyDischarged"/>), which is a valid, re-archivable state, not a corrupt one. Phase two,
    /// once every obligation is known settled, is a single <see cref="Directory.Move(string,
    /// string)"/> of the whole directory — same filesystem, so on this platform that is one
    /// <c>rename()</c> syscall: it either lands whole or throws having moved nothing, never a
    /// half-moved directory (the same same-filesystem-rename assumption <see cref="AtomicWrite"/>
    /// already documents for a single file). A failure here is reported as
    /// <see cref="ChangeArchiveOutcome.ToolFailure"/> with the change still live and unmoved.
    /// </para>
    ///
    /// <para>
    /// <b>An unreadable file in the directory refuses rather than guesses (fail-closed, the same
    /// discipline <see cref="CardIdentityResolver"/> applies to a search that turns up no match).
    /// </b> A file this scan cannot parse might be an open obligation this verb is supposed to
    /// settle before the move — proceeding regardless would risk moving an unsettled obligation
    /// into the archive unnoticed, so this reports <see cref="ChangeArchiveOutcome.CardsUnreadable"/>
    /// before touching anything.
    /// </para>
    ///
    /// <para>
    /// <b>Accepted race, stated rather than solved (the same class of accepted gap <see
    /// cref="CardLock"/>'s own doc comment records for stale-lock PID reuse):</b> nothing holds a
    /// directory-wide lock across the scan-then-settle-then-move sequence, so a card written into
    /// the directory after the scan but before the move (a new <c>obligation create</c> racing
    /// this call) is not seen by phase one and is moved into the archive exactly as written,
    /// still <c>open</c>. That is a race between two independent writers acting on the same change
    /// at the same moment — not a corruption of either card — and closing it fully would need
    /// whole-directory locking machinery this block was not asked to build.
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
        var openObligationPaths = new List<string>();
        foreach (var (filePath, result) in ReadAllCards(liveDirectory))
        {
            result.Match<object?>(
                onSuccess: success =>
                {
                    if (IsObligationCard(success.Card)
                        && RegisterLifecycleStateWireFormat.TryParse(success.Card.Frontmatter.Status, out var state)
                        && state == RegisterLifecycleState.Open)
                    {
                        openObligationPaths.Add(filePath);
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

        var settledIds = new List<string>();
        foreach (var filePath in openObligationPaths)
        {
            var dischargeOutcome = DischargeRegisterCard(cardsRoot, filePath, actingRole, timestamp, lockTimeout, changeName);
            var settled = dischargeOutcome.Match<string?>(
                onDischarged: discharged => discharged.Card.Frontmatter.Id,
                onAlreadyDischarged: static _ => null,
                onInvalidStatus: static _ => null,
                onNotARegisterCard: static _ => null,
                onCardNotFound: static _ => null,
                onLayoutMismatch: static _ => null,
                onCardCorrupt: static _ => null,
                onToolFailure: static _ => null);

            if (settled is not null)
            {
                settledIds.Add(settled);
                continue;
            }

            // Every candidate in openObligationPaths was just proven, in this same scan, to be an
            // obligation card with status "open" — anything other than Discharged coming back now
            // is the accepted race this method's own doc comment names (a concurrent write to the
            // same card between the scan and this call), reported as tool-failure rather than
            // guessed at.
            return new ChangeArchiveOutcome.ToolFailure(
                $"could not settle obligation '{filePath}' before archiving '{changeName}': " +
                DescribeUnexpectedDischargeOutcome(dischargeOutcome));
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

        return new ChangeArchiveOutcome.Archived(changeName, archivedDirectory, settledIds);
    }

    /// <summary>The human-readable half of <see cref="ArchiveChange"/>'s tool-failure message for
    /// the accepted-race branch — named once here rather than inlined, so the reason text stays in
    /// one place regardless of which of the five non-<c>Discharged</c> outcomes actually
    /// occurred.</summary>
    private static string DescribeUnexpectedDischargeOutcome(CardRegisterDischargeOutcome outcome) => outcome.Match(
        onDischarged: static _ => throw new InvalidOperationException("Discharged is handled by the caller before this is reached."),
        onAlreadyDischarged: static already => $"'{already.FilePath}' was already discharged by a concurrent write.",
        onInvalidStatus: static invalidStatus => $"'{invalidStatus.FilePath}' now carries an unrecognised status '{invalidStatus.Status}'.",
        onNotARegisterCard: static notARegisterCard => $"resolved to a '{notARegisterCard.Kind.ToWireString()}' card, not a register card.",
        onCardNotFound: static notFound => $"'{notFound.FilePath}' no longer exists.",
        onLayoutMismatch: static layoutMismatch => layoutMismatch.Reason,
        onCardCorrupt: static corrupt => $"'{corrupt.FilePath}' could not be read: {corrupt.Reason}",
        onToolFailure: static toolFailure => toolFailure.Reason);

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

        if (!IsDecisionCard(supersedingCard))
        {
            return new CardDecisionSupersedeOutcome.NotADecisionCard(supersedingFilePath, supersedingCard.Frontmatter.Kind);
        }

        if (!IsDecisionCard(supersededCard))
        {
            return new CardDecisionSupersedeOutcome.NotADecisionCard(supersededFilePath, supersededCard.Frontmatter.Kind);
        }

        if (string.Equals(supersedingCard.Frontmatter.Id, supersededCard.Frontmatter.Id, StringComparison.Ordinal))
        {
            return new CardDecisionSupersedeOutcome.SelfSupersession(supersedingCard.Frontmatter.Id);
        }

        if (!RegisterLifecycleStateWireFormat.TryParse(supersedingCard.Frontmatter.Status, out var supersedingState))
        {
            return new CardDecisionSupersedeOutcome.InvalidStatus(supersedingFilePath, supersedingCard.Frontmatter.Status);
        }

        if (!RegisterLifecycleStateWireFormat.TryParse(supersededCard.Frontmatter.Status, out var supersededState))
        {
            return new CardDecisionSupersedeOutcome.InvalidStatus(supersededFilePath, supersededCard.Frontmatter.Status);
        }

        // Both sides must be open — the superseded side because supersession discharges it exactly
        // once (Architect ruling: reuse the state block A already shipped, do not re-supersede),
        // the superseding side because that is what rules out every cycle by construction — see
        // this method's own doc comment on SupersedeDecision for the proof.
        if (supersededState == RegisterLifecycleState.Discharged)
        {
            return new CardDecisionSupersedeOutcome.SupersededAlreadyDischarged(supersededFilePath);
        }

        if (supersedingState == RegisterLifecycleState.Discharged)
        {
            return new CardDecisionSupersedeOutcome.SupersedingAlreadyDischarged(supersedingFilePath);
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
            onToolFailure: static toolFailure => new CardDecisionSupersedeOutcome.ToolFailure(toolFailure.Reason));
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
            });
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
        var (familyRefusal, familyCard) = ReadOpenChangeScopedRule(familyFilePath, changeName, isFamilySide: true);
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
            var (absorbedRefusal, absorbedCard) = ReadOpenChangeScopedRule(absorbedFilePaths[i], changeName, isFamilySide: false);
            if (absorbedRefusal is not null)
            {
                return absorbedRefusal;
            }

            if (!seenIds.Add(absorbedCard!.Frontmatter.Id))
            {
                return string.Equals(absorbedCard.Frontmatter.Id, familyCard.Frontmatter.Id, StringComparison.Ordinal)
                    ? new CardRuleCompactOutcome.SelfAbsorption(absorbedCard.Frontmatter.Id)
                    : new CardRuleCompactOutcome.DuplicateAbsorbedRule(absorbedCard.Frontmatter.Id);
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
                onToolFailure: toolFailure => new CardRuleCompactOutcome.ToolFailure(toolFailure.Reason));

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
            });
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
        string filePath, string changeName, bool isFamilySide)
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

        if (!IsRuleCard(card))
        {
            return (new CardRuleCompactOutcome.NotARuleCard(filePath, card.Frontmatter.Kind), null);
        }

        if (!RegisterLifecycleStateWireFormat.TryParse(card.Frontmatter.Status, out var state))
        {
            return (new CardRuleCompactOutcome.InvalidStatus(filePath, card.Frontmatter.Status), null);
        }

        if (state == RegisterLifecycleState.Discharged)
        {
            return isFamilySide
                ? (new CardRuleCompactOutcome.FamilyAlreadyDischarged(filePath), null)
                : (new CardRuleCompactOutcome.AbsorbedAlreadyDischarged(filePath), null);
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
            () => RecordFindingUnderLocks(cardsRoot, findingFilePath, findingFrontmatter, body, findingFields, raiseRequest, raisedId, changeName));
    }

    /// <summary>
    /// Acquires the lock(s) <see cref="RecordFinding"/> needs — one for <paramref name="findingFilePath"/>,
    /// and, when <paramref name="raisedFilePath"/> is not <see langword="null"/> and not the same
    /// file, a second for it — then runs <paramref name="action"/> with both held.
    ///
    /// <para>
    /// <b>No ordering, by design (§6 block B fifth remediation, reviewer's "cross-invocation lock
    /// ordering" finding).</b> An earlier version of this method decided which lock to acquire
    /// first by comparing <paramref name="findingFilePath"/> and <paramref name="raisedFilePath"/>
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
    /// always attempts <paramref name="findingFilePath"/>'s lock first, <em>blocking</em> for
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
    private static CardFindingRecordOutcome AcquireLocksAndRecord(
        string findingFilePath, string? raisedFilePath, TimeSpan lockTimeout, Func<CardFindingRecordOutcome> action)
    {
        var deadline = DateTimeOffset.UtcNow + lockTimeout;

        while (true)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return new CardFindingRecordOutcome.ToolFailure(
                    $"could not acquire both locks 'finding record' needs within {lockTimeout.TotalSeconds:0.###}s — " +
                    $"repeatedly lost the race for '{raisedFilePath}' against another concurrent write; retry.");
            }

            var firstLockResult = CardLock.Acquire(findingFilePath, remaining);
            var outcome = firstLockResult.Match<CardFindingRecordOutcome?>(
                onAcquired: firstAcquired =>
                {
                    using (firstAcquired.Lock)
                    {
                        if (raisedFilePath is null || firstAcquired.Lock.CurrentlyNames(raisedFilePath))
                        {
                            return action();
                        }

                        // No wait at all: this is the "probe" half of acquire-probe-release-retry.
                        // A miss releases findingFilePath's lock (the using block above) and falls
                        // through to the retry below — it never blocks while still holding it.
                        var secondLockResult = CardLock.Acquire(raisedFilePath, TimeSpan.Zero);
                        return secondLockResult.Match<CardFindingRecordOutcome?>(
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
                // A genuine timeout on the finding's own lock is a real, external hold — the
                // ordinary honest message applies unchanged, naming that lock's actual holder.
                onTimedOut: timedOut => new CardFindingRecordOutcome.ToolFailure(timedOut.Message));

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
                raisedScope, findingFrontmatter.Section, findingFrontmatter.Created, findingFrontmatter.Created);

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
            // spell out via --owed-by. A raised obligation is owed to exactly the section that
            // raised it, so this is the one case where that id is already in hand rather than
            // supplied — "give that obligation a real owed_by like any other" (Architect ruling),
            // not a free-text label, and not a second, hand-typed --owed-by a caller could get
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
                ? new RegisterCardFields(null, null, null, null, OwedBy: findingFrontmatter.Section)
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
                onToolFailure: static toolFailure => new CardFindingRecordOutcome.ToolFailure(toolFailure.Reason));
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
            });
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

    private static (string? Id, string? Failure) AllocateIdentity(string cardsRoot, CardKind kind, TimeSpan lockTimeout) =>
        CardIdentityAllocator.Allocate(cardsRoot, kind, lockTimeout).Match(
            onAllocated: allocated => ((string?)allocated.Id, (string?)null),
            onFailed: failed => ((string?)null, (string?)failed.Reason));

    /// <summary>Shared by <see cref="ApplyBlockTransitionUnderExistingLock"/>,
    /// <see cref="RecordGateResultUnderExistingLock"/>, <see cref="UpdateBlockedByUnderExistingLock"/>
    /// and <see cref="RecordApprovalUnderExistingLock"/> — the one place "is this card's kind block"
    /// is decided, over the closed <see cref="CardKind"/> union, so the four verbs cannot drift on
    /// what counts as a block card. <see langword="internal"/> (§8 block A, same reason
    /// <see cref="IsSectionCard"/> already went <see langword="internal"/> in §5 remediation) so
    /// <see cref="Callboard.Cli.CommandDispatcher.RunBlockApprove"/> can pass this to
    /// <see cref="Callboard.Cli.CommandDispatcher.ResolveCardReference"/> instead of re-implementing
    /// the same eight-arm match a second time.</summary>
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
    /// CommandDispatcher"/>'s <c>--owed-by</c>/<c>--supersedes</c> resolution and
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
