using System.Reflection;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 4.8 — proves the spec's "an appended comment cannot be edited or deleted" the strongest way
/// available: there is no operation to test.
///
/// <para>
/// <b>Round 1 was wrong, and the correction is the point.</b> The first version of this file
/// claimed that outright while <see cref="CardStore.WriteCard"/> still fully replaced a card —
/// the reviewer proved it false by writing a probe: a card holding one comment, then
/// <c>WriteCard</c> called again on the same path with an empty <c>Comments</c> list, and the
/// comment was gone on the next read. The reflection test below filtered
/// <see cref="CardStore"/>'s methods to those whose <em>name</em> contained <c>"Comment"</c> —
/// <c>WriteCard</c> does not, so the filter never saw it. <b>A test that enumerates a surface by
/// name proves only what its filter admits, not what the type exposes.</b> The fix applied is not
/// a narrower claim here — it is making <see cref="CardStore.WriteCard"/> itself create-only
/// (refuses under the lock when the target path already exists), so full replacement is no
/// longer reachable through this type at all. With that gap closed, "there is no operation to
/// test" is true again, for real this time, and is checked two ways below: a complete method
/// inventory (not a name filter) and a direct behavioural regression reproducing the reviewer's
/// exact probe.
/// </para>
///
/// <para>
/// <b>The mistake, written and confirmed not to compile (§3's standard):</b>
/// <code>
/// var comment = new CardComment(...);
/// comment.Body = "changed";                 // CS8852: Body is an init-only property
/// CardStore.EditComment(root, path, ...);   // CS0117: CardStore has no such member
/// </code>
/// Verified against this build by adding exactly this code to a scratch test file, running
/// <c>dotnet build</c>, and confirming both errors, then discarding the file — the same evidence
/// standard O-1 and O-2 closed on (block B: "Prove that by writing the mistake and showing it
/// does not compile"). Still true after the create-only fix; unaffected by it.
/// </para>
///
/// <para>
/// <b>The honest limit (named, not closed):</b> a card is a git-committed Markdown file humans
/// are expected to hand-edit (ADR-0003, "legible without the tool") — <c>callboard</c> cannot
/// refuse a text editor. What these tests guarantee is narrower: <c>callboard</c> itself never
/// rewrites or drops an existing comment. A human editing the file directly is guarded only by
/// git history, not by this tool, and nothing in this block (or any later one) should be read as
/// closing that gap.
/// </para>
///
/// <para>
/// <b>What is still owed:</b> the spec's refusal-shaped sentence — "the system refuses and
/// states that corrections are appended" — has no verb to refuse through in §4; there is no
/// operation that attempts an edit for a verb to refuse. §9 owns the closed refusal-code set, so
/// no code is minted here. Whichever later section wires a comment-editing verb (if one is ever
/// proposed) owes that scenario's refusal message; this block deliberately mints none.
/// </para>
/// </summary>
public sealed class CardCommentImmutabilityTests
{
    [Fact]
    public void CardComment_EveryProperty_IsInitOnlyOrGetOnly_NoneAreFreelySettable()
    {
        foreach (var property in typeof(CardComment).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var setter = property.GetSetMethod(nonPublic: false);
            if (setter is null)
            {
                continue;
            }

            var isInitOnly = setter.ReturnParameter
                .GetRequiredCustomModifiers()
                .Any(modifier => modifier == typeof(System.Runtime.CompilerServices.IsExternalInit));

            Assert.True(
                isInitOnly,
                $"CardComment.{property.Name} has a plain settable setter — a comment must be constructible only, never mutable after the fact.");
        }
    }

    /// <summary>
    /// The fix for the round-1 finding: not a narrower filter, a complete inventory. Every static
    /// method <see cref="CardStore"/> declares is named here, with no <c>Where</c> clause standing
    /// between the reflected surface and the assertion — a future member fails this test simply by
    /// existing, which is what forces it to be read and accounted for (in the comment beside its
    /// name below) rather than silently passing because a pattern didn't match it, the way
    /// <c>"WriteCard".Contains("Comment")</c> silently didn't.
    /// </summary>
    [Fact]
    public void CardStore_EntireStaticMethodSurface_IsExplicitlyAccountedFor()
    {
        var actualMembers = typeof(CardStore)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var expectedMembers = new[]
        {
            "AddBlockedBy",                         // §5 block D: read-modify-write on BlockFields.BlockedBy only; never touches Frontmatter.Status or Comments
            "AddBlockedByUnderExistingLock",        // same, lock already held
            "AllocateIdentity",                     // §6 block B: thin Match wrapper over CardIdentityAllocator.Allocate; never touches a CardFile's Comments
            "AnswerQuestion",                       // §9 block D: acquires the question's own lock and delegates to AnswerQuestionUnderExistingLock; never touches a CardFile itself
            "AnswerQuestionUnderExistingLock",      // §9 block D: read-decide-write on Frontmatter.Status/QuestionFields.AnsweredBy/AnsweredAt/AnswerDecisionId/AnswerInline only; never touches Comments
            "AppendComment",                       // read-modify-write: reads, appends one comment, writes — never drops one
            "AppendCommentUnderExistingLock",       // same, lock already held by the caller
            "ApplyBlockTransition",                 // §5 block C: read-modify-write on Frontmatter.Status/BlockFields/Transitions only; Comments passes through the `card with { ... }` unchanged
            "ApplyBlockTransitionUnderExistingLock", // same, lock already held
            "ArchiveChange",                        // §7 block D: settles open obligations via DischargeRegisterCard (unchanged), then Directory.Move's the whole change directory — never opens, reads, or rewrites a repository- or capability-scoped card, and never touches Comments on any card it does settle
            "AtomicWrite",                          // the shared byte-writer every path above funnels through; it writes whatever CardFileWriter.Serialize(card) produces for the CardFile each of those paths built — none of them builds one with a truncated Comments list
            "CloseSection",                         // §5 block E: read-decide-write on Frontmatter.Status/SectionFields.ClosedBy/ClosedAt only; never touches Comments
            "CloseSectionUnderExistingLock",        // same, lock already held
            "CompactRules",                         // §7 block F: dedupes/self-checks on paths, then acquires N+1 locks in ordinal path order and delegates to CompactRulesUnderLocks — never touches a CardFile itself
            "CompactRulesUnderLocks",               // §7 block F: read-decide-write on Frontmatter.Status/RegisterFields.SupersededBy/DischargedBy/DischargedAt for every absorbed rule, and RegisterFields.Absorbs for the family only; never touches Comments on any of them
            "CountRoundIncrementingTransitions",     // 8a.17: pure count over a block card's Transitions against BlockFlowTransitions.RoundIncrementingTransitionNames; never touches a CardFile
            "CreateCard",                           // §7 block A: allocates an identity, validates scope, writes one brand-new card via WriteCard — never touches an existing card's Comments, it only ever creates
            "DeclineObligation",                    // §9 block F: acquires the obligation's own lock and delegates to DeclineObligationUnderExistingLock; never touches a CardFile itself
            "DeclineObligationUnderExistingLock",   // §9 block F: read-decide-write on Frontmatter.Status/RegisterFields.DischargedBy/DischargedAt/DeclinedReason only; never touches Comments
            "DeferQuestion",                        // §9 block D: acquires the question's own lock and delegates to DeferQuestionUnderExistingLock; never touches a CardFile itself
            "DeferQuestionUnderExistingLock",        // §9 block D: read-decide-write on Frontmatter.Status/QuestionFields.DeferredBy/DeferredAt/DeferredTarget only; never touches Comments
            "DischargeRegisterCard",                // §7 block A: read-decide-write on Frontmatter.Status/RegisterFields.DischargedBy/DischargedAt only; never touches Comments
            "DischargeRegisterCardUnderExistingLock", // same, lock already held
            "DispositionNit",                       // §8 block B: role check, allocates the raised card's identity (defer/decline), then acquires the block's lock (and, for defer/decline, the raised card's) and delegates to DispositionNitUnderLocks; never touches a CardFile itself
            "DispositionNitUnderLocks",              // §8 block B: appends one disposition comment (never edits or drops the nit comment it resolves), and — for defer/decline — create-only writes a second card via AtomicWrite; for fix-before-land, read-decide-write on Frontmatter.Status/BlockFields.Round/Transitions only when the edge actually applies
            "FindAgeingAddressedThreads",           // §9 block E: read-only scan of a section's own directory for its blocks' live addressed threads that have survived a round boundary (CardCommentRouting.AgeingAddressedThreadIds) — feeds 'section status', never touches a CardFile
            "FindBlockingOpenProductOwnerQuestion",  // §9 block D: read-only resolution over a block card's own BlockedBy ids via CardIdentityResolver, looking for an open product-owner question; never touches a CardFile
            "IsApprovingRole",                      // §8 block A: pure predicate over CardOwner (reviewer/supervisor only), shared by RecordApproval; never touches a CardFile
            "IsArchitectRole",                       // §8 block B: pure predicate over CardOwner (architect only), shared by DispositionNit; never touches a CardFile
            "IsAuthorisingRole",                     // §8a block C: pure predicate over CardOwner (product-owner only), shared by RecordSectionAuthorisation; never touches a CardFile
            "IsBlockCard",                          // pure predicate over CardFrontmatter.Kind, shared by ApplyBlockTransition/RecordGateResult/AddBlockedBy/RemoveBlockedBy/RecordApproval; never touches a CardFile's Comments
            "IsDecisionCard",                       // §7 block C: the IsRegisterCard counterpart narrowed to CardKind.Decision, shared by CommandDispatcher's --supersedes resolution and SupersedeDecisionUnderLocks; never touches a CardFile's Comments
            "IsFindingCard",                        // §6 block C: the IsBlockCard/IsSectionCard counterpart for CardKind.Finding, shared with CommandDispatcher.RunFindingStatus; never touches a CardFile's Comments
            "IsObligationCard",                     // §7 block D: the IsRegisterCard counterpart narrowed to CardKind.Obligation, shared by ArchiveChange's obligation scan; never touches a CardFile's Comments
            "IsQuestionCard",                       // §9 block D: the IsBlockCard/IsSectionCard counterpart for CardKind.Question, shared by AnswerQuestion/DeferQuestion/FindBlockingOpenProductOwnerQuestion; never touches a CardFile's Comments
            "IsRegisterCard",                       // §7 block A: the IsBlockCard/IsSectionCard/IsFindingCard counterpart for the four register kinds; never touches a CardFile's Comments
            "IsRuleCard",                            // §7 block E: the IsRegisterCard counterpart narrowed to CardKind.Rule, shared by CommandDispatcher's `rule promote` resolution and PromoteRuleUnderExistingLock; never touches a CardFile's Comments
            "IsSectionCard",                        // §5 block E: the IsBlockCard counterpart for CardKind.Section, shared by RecordSectionVerdict/CloseSection; never touches a CardFile's Comments
            "PromoteObligation",                    // §9 block F: acquires the obligation's own lock, then delegates to PromoteObligationUnderExistingLock; never touches a CardFile itself
            "PromoteObligationUnderExistingLock",   // §9 block F: exact mirror of PromoteRuleUnderExistingLock generalised to obligation — File.Move's the card to callboard/register/, then read-decide-write on Frontmatter.Scope/Updated and one appended attribution comment; appends, never edits or drops an existing comment
            "PromoteRule",                          // §7 block E: acquires the rule's own lock, then delegates to PromoteRuleUnderExistingLock; never touches a CardFile itself
            "PromoteRuleUnderExistingLock",         // §7 block E, remediation blocker 3: File.Move's the card to callboard/register/, then read-decide-write on Frontmatter.Scope/Updated and one appended attribution comment via the ordinary AnchoredCardPath/AtomicWrite path — appends, never edits or drops an existing comment
            "RaiseNit",                              // §8 remediation: acquires the block's lock and delegates to RaiseNitUnderExistingLock; never touches a CardFile itself
            "RaiseNitUnderExistingLock",             // §8 remediation: read-decide-write — refuses unless the card is in-review, then appends exactly one nit comment via `card with { Comments = [.. card.Comments, comment] }`; never edits or drops an existing comment
            "ReadAllCards",                         // read-only
            "ReadCard",                             // read-only
            "ReadOpenChangeScopedRule",             // §7 block F: read-only check (rule kind, open lifecycle state, change-scoped) shared by CompactRulesUnderLocks for both the family and every absorbed side; never touches a CardFile's Comments, and never writes
            "AcquireLocksAndRecord",                // §6 block B remediation: acquires one or two CardLocks in ordinal path order and runs the record step under them; never touches a CardFile
            "RecordFinding",                        // §6 block B: allocates identities, pre-creates directories, then delegates to AcquireLocksAndRecord/RecordFindingUnderLocks — never touches an existing card's Comments, it only ever creates brand-new cards with an empty comment list
            "RecordFindingUnderLocks",              // same: writes the raised card (if any) then the finding, both create-only via AtomicWrite, rolling the raised card back (by content, not by path) on the finding's own write failing — never an edit or drop of an existing comment
            "RecordApproval",                       // §8 block A: role check, then acquires the block's lock and delegates to RecordApprovalUnderExistingLock; never touches a CardFile itself
            "RecordApprovalUnderExistingLock",      // §8 block A: read-decide-write on Frontmatter.Status/BlockFields.ReviewedState/Transitions/Claims/Limits only, in one write with the transition; never touches Comments
            "RecordGateResult",                     // §5 block D: read-modify-write on BlockFields.GateResults only; never touches Frontmatter.Status or Comments — see the structural argument in GateStatus's doc comment
            "RecordGateResultUnderExistingLock",    // same, lock already held
            "RecordSectionAuthorisation",            // §8a block C: role check, then acquires the section's lock and delegates to RecordSectionAuthorisationUnderExistingLock; never touches a CardFile itself
            "RecordSectionAuthorisationUnderExistingLock", // §8a block C: role check first, then append-only write on SectionFields.Authorisations only; never touches Frontmatter.Status or Comments
            "RecordSectionVerdict",                 // §5 block E: append-only write on SectionFields.Verdicts only; never touches Frontmatter.Status or Comments
            "RecordSectionVerdictUnderExistingLock", // same, lock already held
            "RefuseAndRecord",                       // §9 block A: appends exactly one CardRefusalEntry to Refusals (never Comments) and writes it back under the caller's own held lock; called only from a refusal branch, never touches an existing comment
            "RefuseAndRecord",                       // §9 block A2: the outcome-union-generic overload the register/rules families share — same contract, same lock precondition, never touches Comments
            "RemoveBlockedBy",                      // §5 block D: read-modify-write on BlockFields.BlockedBy only, the "clearing what blocked it" half of "Blocked is derived, not stored"
            "RemoveBlockedByUnderExistingLock",     // same, lock already held
            "RestoreAllAbsorbed",                   // §7 block F: loops RestoreCardContent over every absorbed rule already written in a CompactRulesUnderLocks call when the family's own write then fails; touches only what RestoreCardContent itself touches
            "RoundAgreesWithHistory",               // 8a.17: pure predicate — a block card's stored round equals one plus CountRoundIncrementingTransitions of its own Transitions; every writer that mutates a block card checks this before writing, never touches a CardFile itself
            "RestoreCardContent",                   // §7 block C/F (renamed from RestoreSupersededCard when block F's own multi-card write needed the identical restore): best-effort re-write of a card's pre-mutation bytes when a later write in the same multi-card operation fails — never touches an existing card's Comments, it restores exactly what it read
            "RollbackRaisedCard",                   // §6 block B: best-effort delete of a just-written, brand-new raised card when the finding's own write then fails — never touches an existing card at all
            "RollbackRaisedNitCard",                // §8 block B: the same compare-then-delete rollback applied to DispositionNit's own raised card — never touches an existing card at all
            "ScopeForRaisedCard",                   // §6 block B: pure function from CardKind to its fixed CardScope; never touches a CardFile
            "SectionRemediationBoundState",          // §8a block C remediation: pure derivation over SectionCardFields.Verdicts/Authorisations, shared by RecordSectionVerdictUnderExistingLock and RecordSectionAuthorisationUnderExistingLock; never touches a CardFile
            "SupersedeDecision",                    // §7 block C: acquires both decisions' locks in a deterministic id-derived path order, then delegates to SupersedeDecisionUnderLocks — never touches a CardFile itself
            "SupersedeDecisionUnderLocks",          // §7 block C: read-decide-write on Frontmatter.Status/RegisterFields.Supersedes/SupersededBy/DischargedBy/DischargedAt for both cards only; never touches Comments on either
            "TransferOwnership",                    // read-modify-write: overwrites Owner/Handovers only; Comments passes through the `success.Card with { ... }` unchanged
            "TransferOwnershipUnderExistingLock",   // same, lock already held
            "UpdateBlockedByUnderExistingLock",     // §5 block D: the read-decide-write shape AddBlockedByUnderExistingLock/RemoveBlockedByUnderExistingLock share; never touches Frontmatter.Status or Comments
            "ValidateBlockForLanding",              // §8a block A: the three closing-condition checks CloseSectionUnderExistingLock applies to one block; read-only, returns a refusal or null, never touches a CardFile
            "WithLock",                             // lock-acquisition plumbing (CardWriteResult overload); never touches a CardFile
            "WithLock",                             // §5 block C: the same plumbing generalised over TResult so ApplyBlockTransition can return a CardBlockTransitionOutcome; never touches a CardFile — two overloads, two entries, same method name
            "WriteCard",                            // create-only (this fix) — see WriteCard_RefusesToOverwriteAnExistingCard_SoItCannotDropAComment below for the direct proof
        };

        Assert.Equal(expectedMembers.OrderBy(name => name, StringComparer.Ordinal), actualMembers);
    }

    /// <summary>
    /// The reviewer's exact probe, reproduced as a permanent regression test: a card is created and
    /// then has one comment appended; a second <see cref="CardStore.WriteCard"/> call on the same
    /// path must now be refused — and the original comment must still read back afterwards, proving
    /// the refusal actually prevented the drop rather than merely reporting a failure while the file
    /// underneath still changed. (§4 remediation R3 narrowed <see cref="CardStore.WriteCard"/>'s
    /// input further, to <see cref="NewCardFile"/>, which cannot carry a comment list at all — so
    /// the original probe's shape, a second <c>CardFile</c> with an emptied <c>Comments</c>, is no
    /// longer expressible as a call; create-only refusal is what is left to prove.)
    /// </summary>
    [Fact]
    public void WriteCard_RefusesToOverwriteAnExistingCard_SoItCannotDropAComment()
    {
        var root = Path.Combine(Path.GetTempPath(), "callboard-create-only-tests-" + Guid.NewGuid().ToString("N"));
        const string changeName = "establish-callboard";
        var directory = Path.Combine(root, CardLayout.ChangesDirectory(changeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "b-0001.md");

        try
        {
            var timestamp = new DateTimeOffset(2026, 8, 22, 9, 0, 0, TimeSpan.Zero);
            var frontmatter = new CardFrontmatter(
                "B-0001", CardKind.Block, "Title", "open", CardOwner.Worker, CardScope.Change, "4", timestamp, timestamp);
            var comment = new CardComment("C-0001", CardOwner.Worker, timestamp, "Do not drop me.", null, null, null, []);

            var created = CardStore.WriteCard(root, path, new NewCardFile(frontmatter, "Body."), TimeSpan.FromSeconds(5), changeName);
            AssertSuccess(created);
            AssertSuccess(CardStore.AppendComment(root, path, comment, TimeSpan.FromSeconds(5), changeName));

            // The reviewer's probe: the same production entry point, same path, a second create
            // attempt over a card that already carries a comment.
            var replacement = new NewCardFile(frontmatter, "Body.");
            var replaceResult = CardStore.WriteCard(root, path, replacement, TimeSpan.FromSeconds(5), changeName);

            AssertFailure(replaceResult);

            var readBack = CardStore.ReadCard(path);
            var card = AssertParseSuccess(readBack);
            Assert.Equal(comment, Assert.Single(card.Comments));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void AssertSuccess(CardWriteResult result) =>
        result.Match<object?>(
            onSuccess: static _ => null,
            onNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected write success, got NotFound: '{notFound.FilePath}'"),
            onAlreadyExists: alreadyExists => throw new Xunit.Sdk.XunitException($"expected write success, got AlreadyExists: '{alreadyExists.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected write success, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected write success, got Corrupt: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected write success, got ToolFailure: {toolFailure.Reason}"),
            onRoundDisagreesWithHistory: disagreement => throw new Xunit.Sdk.XunitException($"expected write success, got RoundDisagreesWithHistory: (stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound})"));

    private static void AssertFailure(CardWriteResult result) =>
        result.Match<object?>(
            onSuccess: static _ => throw new Xunit.Sdk.XunitException("expected write failure (create-only refusal), got success"),
            onNotFound: static _ => null,
            onAlreadyExists: static _ => null,
            onLayoutMismatch: static _ => null,
            onCorrupt: static _ => null,
            onToolFailure: static _ => null,
            onRoundDisagreesWithHistory: static disagreement => null);

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
