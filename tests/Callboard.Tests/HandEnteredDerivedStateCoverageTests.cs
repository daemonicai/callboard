using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// §10 block C remediation (reviewer finding on the original block C submission): the reserved-
/// derived-state guard (working-context, "No figure SHALL be hand-entered anywhere in the system")
/// is wired ahead of every <c>AtomicWrite</c> call site in <c>CardStore.cs</c> that reads and
/// rewrites an <em>existing</em> card — not just the two generic <c>AppendComment</c>/
/// <c>TransferOwnership</c> surfaces the original submission covered. This file is the coverage-gate
/// evidence for the nineteen additional cases: one test per outcome type, each proving the refusal
/// both fires and records against the card, per <c>RefusalCoverageGateTests</c>'s own standard.
///
/// <para>
/// <b>Three write paths carry no <c>HandEnteredDerivedState</c> case at all, deliberately, not as
/// an <c>Exclusions</c> entry (there is no case for the gate to ask about):</b>
/// <see cref="CardCreateOutcome"/> (<c>CardStore.WriteCard</c>), <see cref="CardFindingRecordOutcome"/>
/// (<c>CardStore.RecordFinding</c>) and <see cref="ChangeArchiveOutcome"/> (a pure directory move,
/// never a content rewrite) only ever construct a brand-new card — a fresh <see cref="NewCardFile"/>
/// has no <see cref="CardFile.UnknownFrontmatterFields"/> parameter at all, so none of the three can
/// structurally ever encounter a hand-entered reserved key. Adding an unreachable case to name a
/// guard that can never fire there would be the union lying about what it can produce.
/// </para>
/// </summary>
public sealed class HandEnteredDerivedStateCoverageTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";
    private const string ReservedKey = "next_step";
    private const string ReservedValue = "ship it";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-handentered-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _changesDirectory;
    private readonly string _registerDirectory;
    private readonly string _decisionsDirectory;

    public HandEnteredDerivedStateCoverageTests()
    {
        _changesDirectory = Path.Combine(_root, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(_changesDirectory);
        _registerDirectory = Path.Combine(_root, CardLayout.RegisterDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(_registerDirectory);
        _decisionsDirectory = Path.Combine(_root, CardLayout.DecisionsDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(_decisionsDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void RaiseNit_CardCarryingAReservedKey_Refuses_AndRecords()
    {
        var path = WriteTaintedCard(_changesDirectory, "b-0001", "B-0001", CardKind.Block, CardOwner.Worker);
        var comment = new CardComment("C-0001", CardOwner.Reviewer, Now, "Fix this.", null, CardOwner.Architect, null, [], IsNit: true);

        var outcome = CardStore.RaiseNit(_root, path, comment, TimeSpan.FromSeconds(5), ChangeName);

        var handEntered = Assert.IsType<CardNitRaiseOutcome.HandEnteredDerivedState>(outcome);
        Assert.Equal(ReservedKey, handEntered.Key);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Reviewer, refusal.By);
        Assert.Equal(handEntered.RefusingRule, refusal.Rule);
        Assert.Equal(handEntered.Remedy, refusal.Remedy);
        Assert.Equal((ReservedKey, ReservedValue), Assert.Single(read.UnknownFrontmatterFields));
    }

    [Fact]
    public void ApplyBlockTransition_CardCarryingAReservedKey_Refuses_AndRecords()
    {
        var path = WriteTaintedCard(_changesDirectory, "b-0002", "B-0002", CardKind.Block, CardOwner.Worker);

        var outcome = CardStore.ApplyBlockTransition(_root, path, "claim", CardOwner.Worker, Now, baseCommit: null, TimeSpan.FromSeconds(5), ChangeName);

        var handEntered = Assert.IsType<CardBlockTransitionOutcome.HandEnteredDerivedState>(outcome);
        Assert.Equal(ReservedKey, handEntered.Key);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Worker, refusal.By);
        Assert.Equal(handEntered.RefusingRule, refusal.Rule);
        Assert.Equal(handEntered.Remedy, refusal.Remedy);
        Assert.Equal((ReservedKey, ReservedValue), Assert.Single(read.UnknownFrontmatterFields));
    }

    [Fact]
    public void RecordApproval_CardCarryingAReservedKey_Refuses_AndRecords()
    {
        var path = WriteTaintedCard(_changesDirectory, "b-0003", "B-0003", CardKind.Block, CardOwner.Worker);

        var outcome = CardStore.RecordApproval(_root, path, "reviewed-state", [], [], CardOwner.Reviewer, Now, TimeSpan.FromSeconds(5), ChangeName);

        var handEntered = Assert.IsType<CardApprovalOutcome.HandEnteredDerivedState>(outcome);
        Assert.Equal(ReservedKey, handEntered.Key);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Reviewer, refusal.By);
        Assert.Equal(handEntered.RefusingRule, refusal.Rule);
        Assert.Equal(handEntered.Remedy, refusal.Remedy);
        Assert.Equal((ReservedKey, ReservedValue), Assert.Single(read.UnknownFrontmatterFields));
    }

    [Fact]
    public void DispositionNit_CardCarryingAReservedKey_Refuses_AndRecords()
    {
        var path = WriteTaintedCard(_changesDirectory, "b-0004", "B-0004", CardKind.Block, CardOwner.Worker);

        var outcome = CardStore.DispositionNit(
            _root, path, "some-nit-id", NitDisposition.FixBeforeLand, "Done.", CardOwner.Architect, Now, TimeSpan.FromSeconds(5), ChangeName, raiseRequest: null);

        var handEntered = Assert.IsType<CardNitDispositionOutcome.HandEnteredDerivedState>(outcome);
        Assert.Equal(ReservedKey, handEntered.Key);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, refusal.By);
        Assert.Equal(handEntered.RefusingRule, refusal.Rule);
        Assert.Equal(handEntered.Remedy, refusal.Remedy);
        Assert.Equal((ReservedKey, ReservedValue), Assert.Single(read.UnknownFrontmatterFields));
    }

    [Fact]
    public void RecordGateResult_CardCarryingAReservedKey_Refuses_AndRecords()
    {
        var path = WriteTaintedCard(_changesDirectory, "b-0005", "B-0005", CardKind.Block, CardOwner.Worker);

        var outcome = CardStore.RecordGateResult(_root, path, "build", 0, CardOwner.Worker, Now, TimeSpan.FromSeconds(5), ChangeName);

        var handEntered = Assert.IsType<CardGateResultOutcome.HandEnteredDerivedState>(outcome);
        Assert.Equal(ReservedKey, handEntered.Key);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Worker, refusal.By);
        Assert.Equal(handEntered.RefusingRule, refusal.Rule);
        Assert.Equal(handEntered.Remedy, refusal.Remedy);
        Assert.Equal((ReservedKey, ReservedValue), Assert.Single(read.UnknownFrontmatterFields));
    }

    [Fact]
    public void AddBlockedBy_CardCarryingAReservedKey_Refuses_AndRecords()
    {
        var path = WriteTaintedCard(_changesDirectory, "b-0006", "B-0006", CardKind.Block, CardOwner.Worker);

        var outcome = CardStore.AddBlockedBy(_root, path, "Q-0001", CardOwner.Worker, Now, TimeSpan.FromSeconds(5), ChangeName);

        var handEntered = Assert.IsType<CardBlockedByOutcome.HandEnteredDerivedState>(outcome);
        Assert.Equal(ReservedKey, handEntered.Key);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Worker, refusal.By);
        Assert.Equal(handEntered.RefusingRule, refusal.Rule);
        Assert.Equal(handEntered.Remedy, refusal.Remedy);
        Assert.Equal((ReservedKey, ReservedValue), Assert.Single(read.UnknownFrontmatterFields));
    }

    [Fact]
    public void AnswerQuestion_CardCarryingAReservedKey_Refuses_AndRecords()
    {
        var path = WriteTaintedCard(_registerDirectory, "q-0001", "Q-0001", CardKind.Question, CardOwner.Reviewer);

        var outcome = CardStore.AnswerQuestion(_root, path, decisionId: null, inlineAnswer: "It's fine.", CardOwner.Reviewer, Now, TimeSpan.FromSeconds(5));

        var handEntered = Assert.IsType<CardQuestionAnswerOutcome.HandEnteredDerivedState>(outcome);
        Assert.Equal(ReservedKey, handEntered.Key);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Reviewer, refusal.By);
        Assert.Equal(handEntered.RefusingRule, refusal.Rule);
        Assert.Equal(handEntered.Remedy, refusal.Remedy);
        Assert.Equal((ReservedKey, ReservedValue), Assert.Single(read.UnknownFrontmatterFields));
    }

    [Fact]
    public void DeferQuestion_CardCarryingAReservedKey_Refuses_AndRecords()
    {
        var path = WriteTaintedCard(_registerDirectory, "q-0002", "Q-0002", CardKind.Question, CardOwner.Reviewer);

        var outcome = CardStore.DeferQuestion(_root, path, "a later section", CardOwner.Reviewer, Now, TimeSpan.FromSeconds(5));

        var handEntered = Assert.IsType<CardQuestionDeferOutcome.HandEnteredDerivedState>(outcome);
        Assert.Equal(ReservedKey, handEntered.Key);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Reviewer, refusal.By);
        Assert.Equal(handEntered.RefusingRule, refusal.Rule);
        Assert.Equal(handEntered.Remedy, refusal.Remedy);
        Assert.Equal((ReservedKey, ReservedValue), Assert.Single(read.UnknownFrontmatterFields));
    }

    [Fact]
    public void RecordSectionAuthorisation_CardCarryingAReservedKey_Refuses_AndRecords()
    {
        var path = WriteTaintedCard(_changesDirectory, "s-0001", "S-0001", CardKind.Section, CardOwner.Architect);

        var outcome = CardStore.RecordSectionAuthorisation(_root, path, "extending the bound", CardOwner.ProductOwner, Now, TimeSpan.FromSeconds(5), ChangeName);

        var handEntered = Assert.IsType<CardSectionAuthorisationOutcome.HandEnteredDerivedState>(outcome);
        Assert.Equal(ReservedKey, handEntered.Key);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.ProductOwner, refusal.By);
        Assert.Equal(handEntered.RefusingRule, refusal.Rule);
        Assert.Equal(handEntered.Remedy, refusal.Remedy);
        Assert.Equal((ReservedKey, ReservedValue), Assert.Single(read.UnknownFrontmatterFields));
    }

    [Fact]
    public void RecordSectionVerdict_SectionCardCarryingAReservedKey_Refuses_AndRecords()
    {
        var path = WriteTaintedCard(_changesDirectory, "s-0002", "S-0002", CardKind.Section, CardOwner.Architect);

        var outcome = CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.Approve, "10.1", "10.3", CardOwner.Supervisor, Now, TimeSpan.FromSeconds(5), ChangeName, [], []);

        var handEntered = Assert.IsType<CardSectionVerdictOutcome.HandEnteredDerivedState>(outcome);
        Assert.Equal(path, handEntered.FilePath);
        Assert.Equal(ReservedKey, handEntered.Key);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Supervisor, refusal.By);
        Assert.Equal(handEntered.RefusingRule, refusal.Rule);
        Assert.Equal(handEntered.Remedy, refusal.Remedy);
        Assert.Equal((ReservedKey, ReservedValue), Assert.Single(read.UnknownFrontmatterFields));
    }

    [Fact]
    public void RecordSectionVerdict_RecurringBlockCardCarryingAReservedKey_Refuses_AndRecords()
    {
        var sectionPath = WriteCleanSectionCard(_changesDirectory, "s-0003", "S-0003");
        var recurringPath = WriteTaintedBlockCardApproved(_changesDirectory, "b-0007", "B-0007", "S-0003");

        var outcome = CardStore.RecordSectionVerdict(
            _root, sectionPath, SectionVerdict.RequestChanges, "10.1", "10.3", CardOwner.Supervisor, Now, TimeSpan.FromSeconds(5), ChangeName, [recurringPath], []);

        var handEntered = Assert.IsType<CardSectionVerdictOutcome.HandEnteredDerivedState>(outcome);
        Assert.Equal(recurringPath, handEntered.FilePath);
        Assert.Equal(ReservedKey, handEntered.Key);

        // Recorded against the section, the same convention RoundDisagreesWithHistory already
        // follows for a --finding-recurred target's own defect (CardSectionVerdictTests'
        // "...RecordsAgainstTheSection"): the operation being refused is "record a verdict"
        // against the section card, even though the recurring card is what carries the fault.
        var read = AssertParseSuccess(CardStore.ReadCard(sectionPath));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Supervisor, refusal.By);
        Assert.Equal(handEntered.RefusingRule, refusal.Rule);
        Assert.Equal(handEntered.Remedy, refusal.Remedy);

        // The recurring card itself is untouched — its reserved field is still there, unresolved,
        // and nothing about it (round, status) was altered by the refused attempt.
        var recurringRead = AssertParseSuccess(CardStore.ReadCard(recurringPath));
        Assert.Empty(recurringRead.Refusals);
        Assert.Equal((ReservedKey, ReservedValue), Assert.Single(recurringRead.UnknownFrontmatterFields));
    }

    [Fact]
    public void CloseSection_SectionCardCarryingAReservedKey_Refuses_AndRecords()
    {
        var path = WriteTaintedCard(_changesDirectory, "s-0004", "S-0004", CardKind.Section, CardOwner.Architect);

        var outcome = CardStore.CloseSection(_root, path, CardOwner.Architect, Now, TimeSpan.FromSeconds(5), ChangeName);

        var handEntered = Assert.IsType<CardSectionCloseOutcome.HandEnteredDerivedState>(outcome);
        Assert.Equal(path, handEntered.FilePath);
        Assert.Equal(ReservedKey, handEntered.Key);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, refusal.By);
        Assert.Equal(handEntered.RefusingRule, refusal.Rule);
        Assert.Equal(handEntered.Remedy, refusal.Remedy);
        Assert.Equal((ReservedKey, ReservedValue), Assert.Single(read.UnknownFrontmatterFields));
    }

    [Fact]
    public void CloseSection_LandedBlockCardCarryingAReservedKey_Refuses_AndRecords()
    {
        var sectionPath = WriteCleanSectionCard(_changesDirectory, "s-0005", "S-0005");
        var blockPath = WriteTaintedBlockCardApproved(_changesDirectory, "b-0008", "B-0008", "S-0005");

        var outcome = CardStore.CloseSection(_root, sectionPath, CardOwner.Architect, Now, TimeSpan.FromSeconds(5), ChangeName);

        var handEntered = Assert.IsType<CardSectionCloseOutcome.HandEnteredDerivedState>(outcome);
        Assert.Equal(blockPath, handEntered.FilePath);
        Assert.Equal(ReservedKey, handEntered.Key);
        var read = AssertParseSuccess(CardStore.ReadCard(blockPath));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, refusal.By);
        Assert.Equal(handEntered.RefusingRule, refusal.Rule);
        Assert.Equal(handEntered.Remedy, refusal.Remedy);
        Assert.Equal((ReservedKey, ReservedValue), Assert.Single(read.UnknownFrontmatterFields));
    }

    [Fact]
    public void DischargeRegisterCard_CardCarryingAReservedKey_Refuses_AndRecords()
    {
        var path = WriteTaintedCard(_registerDirectory, "o-0001", "O-0001", CardKind.Obligation, CardOwner.Architect);

        var outcome = CardStore.DischargeRegisterCard(_root, path, CardOwner.Architect, Now, TimeSpan.FromSeconds(5));

        var handEntered = Assert.IsType<CardRegisterDischargeOutcome.HandEnteredDerivedState>(outcome);
        Assert.Equal(ReservedKey, handEntered.Key);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, refusal.By);
        Assert.Equal(handEntered.RefusingRule, refusal.Rule);
        Assert.Equal(handEntered.Remedy, refusal.Remedy);
        Assert.Equal((ReservedKey, ReservedValue), Assert.Single(read.UnknownFrontmatterFields));
    }

    [Fact]
    public void PromoteRule_CardCarryingAReservedKey_Refuses_AndRecords()
    {
        var path = WriteTaintedCard(_changesDirectory, "r-0001", "R-0001", CardKind.Rule, CardOwner.Architect);

        var outcome = CardStore.PromoteRule(_root, path, CardOwner.Architect, Now, TimeSpan.FromSeconds(5), ChangeName);

        var handEntered = Assert.IsType<CardRulePromoteOutcome.HandEnteredDerivedState>(outcome);
        Assert.Equal(ReservedKey, handEntered.Key);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, refusal.By);
        Assert.Equal(handEntered.RefusingRule, refusal.Rule);
        Assert.Equal(handEntered.Remedy, refusal.Remedy);
        Assert.Equal((ReservedKey, ReservedValue), Assert.Single(read.UnknownFrontmatterFields));
    }

    [Fact]
    public void PromoteObligation_CardCarryingAReservedKey_Refuses_AndRecords()
    {
        var path = WriteTaintedCard(_changesDirectory, "o-0002", "O-0002", CardKind.Obligation, CardOwner.Architect, scopeOverride: CardScope.Change);

        var outcome = CardStore.PromoteObligation(_root, path, CardOwner.Architect, Now, TimeSpan.FromSeconds(5), ChangeName);

        var handEntered = Assert.IsType<CardObligationPromoteOutcome.HandEnteredDerivedState>(outcome);
        Assert.Equal(ReservedKey, handEntered.Key);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, refusal.By);
        Assert.Equal(handEntered.RefusingRule, refusal.Rule);
        Assert.Equal(handEntered.Remedy, refusal.Remedy);
        Assert.Equal((ReservedKey, ReservedValue), Assert.Single(read.UnknownFrontmatterFields));
    }

    [Fact]
    public void DeclineObligation_CardCarryingAReservedKey_Refuses_AndRecords()
    {
        var path = WriteTaintedCard(_registerDirectory, "o-0003", "O-0003", CardKind.Obligation, CardOwner.Architect);

        var outcome = CardStore.DeclineObligation(_root, path, CardOwner.Architect, "won't be met", Now, TimeSpan.FromSeconds(5));

        var handEntered = Assert.IsType<CardObligationDeclineOutcome.HandEnteredDerivedState>(outcome);
        Assert.Equal(ReservedKey, handEntered.Key);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, refusal.By);
        Assert.Equal(handEntered.RefusingRule, refusal.Rule);
        Assert.Equal(handEntered.Remedy, refusal.Remedy);
        Assert.Equal((ReservedKey, ReservedValue), Assert.Single(read.UnknownFrontmatterFields));
    }

    [Fact]
    public void ResolveComment_CardCarryingAReservedKey_Refuses_AndRecords()
    {
        var path = WriteTaintedCard(_changesDirectory, "b-0009", "B-0009", CardKind.Block, CardOwner.Worker);

        var outcome = CardStore.ResolveComment(_root, path, "some-comment-id", CardOwner.Worker, "Done.", requireReason: false, Now, TimeSpan.FromSeconds(5), ChangeName);

        var handEntered = Assert.IsType<CardCommentResolveOutcome.HandEnteredDerivedState>(outcome);
        Assert.Equal(ReservedKey, handEntered.Key);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Worker, refusal.By);
        Assert.Equal(handEntered.RefusingRule, refusal.Rule);
        Assert.Equal(handEntered.Remedy, refusal.Remedy);
        Assert.Equal((ReservedKey, ReservedValue), Assert.Single(read.UnknownFrontmatterFields));
    }

    [Fact]
    public void PromoteComment_CardCarryingAReservedKey_Refuses_AndRecords()
    {
        var path = WriteTaintedCard(_changesDirectory, "b-0010", "B-0010", CardKind.Block, CardOwner.Worker);
        var raiseFilePath = Path.Combine(_registerDirectory, "q-0099.md");

        var outcome = CardStore.PromoteComment(
            _root, path, "some-comment-id", raiseFilePath, CardKind.Question, "A promoted question",
            CardOwner.Worker, CardOwner.Architect, "Body.", ChangeName, Now, TimeSpan.FromSeconds(5));

        var handEntered = Assert.IsType<CardCommentPromoteOutcome.HandEnteredDerivedState>(outcome);
        Assert.Equal(ReservedKey, handEntered.Key);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Worker, refusal.By);
        Assert.Equal(handEntered.RefusingRule, refusal.Rule);
        Assert.Equal(handEntered.Remedy, refusal.Remedy);
        Assert.Equal((ReservedKey, ReservedValue), Assert.Single(read.UnknownFrontmatterFields));
    }

    [Fact]
    public void SupersedeDecision_SupersedingCardCarryingAReservedKey_Refuses_AndRecords()
    {
        var supersedingPath = WriteTaintedCard(_decisionsDirectory, "dec-0001", "DEC-0001", CardKind.Decision, CardOwner.Architect);
        var supersededPath = WriteCleanDecisionCard(_decisionsDirectory, "dec-0002", "DEC-0002");

        var outcome = CardStore.SupersedeDecision(_root, supersedingPath, supersededPath, CardOwner.Architect, Now, TimeSpan.FromSeconds(5));

        var handEntered = Assert.IsType<CardDecisionSupersedeOutcome.HandEnteredDerivedState>(outcome);
        Assert.Equal(supersedingPath, handEntered.FilePath);
        Assert.Equal(ReservedKey, handEntered.Key);
        var read = AssertParseSuccess(CardStore.ReadCard(supersedingPath));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, refusal.By);
        Assert.Equal(handEntered.RefusingRule, refusal.Rule);
        Assert.Equal(handEntered.Remedy, refusal.Remedy);
        Assert.Equal((ReservedKey, ReservedValue), Assert.Single(read.UnknownFrontmatterFields));
    }

    [Fact]
    public void CompactRules_FamilyCardCarryingAReservedKey_Refuses_AndRecords()
    {
        var familyPath = WriteTaintedCard(_changesDirectory, "r-0002", "R-0002", CardKind.Rule, CardOwner.Architect);
        var absorbedPath = WriteCleanRuleCard(_changesDirectory, "r-0003", "R-0003");

        var outcome = CardStore.CompactRules(_root, familyPath, [absorbedPath], ChangeName, CardOwner.Architect, Now, TimeSpan.FromSeconds(5));

        var handEntered = Assert.IsType<CardRuleCompactOutcome.HandEnteredDerivedState>(outcome);
        Assert.Equal(familyPath, handEntered.FilePath);
        Assert.Equal(ReservedKey, handEntered.Key);
        var read = AssertParseSuccess(CardStore.ReadCard(familyPath));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, refusal.By);
        Assert.Equal(handEntered.RefusingRule, refusal.Rule);
        Assert.Equal(handEntered.Remedy, refusal.Remedy);
        Assert.Equal((ReservedKey, ReservedValue), Assert.Single(read.UnknownFrontmatterFields));
    }

    // §9 ruling 2 (reviewer nit on the remediation round): the non-primary card in a two-card
    // write is exactly the case most likely to be missed by someone reading the method quickly —
    // pinned here as its own regression test rather than left as "verified live but untested".
    [Fact]
    public void SupersedeDecision_SupersededCardCarryingAReservedKey_Refuses_AndRecords_LeavingSupersedingUntouched()
    {
        var supersedingPath = WriteCleanDecisionCard(_decisionsDirectory, "dec-0003", "DEC-0003");
        var supersededPath = WriteTaintedCard(_decisionsDirectory, "dec-0004", "DEC-0004", CardKind.Decision, CardOwner.Architect);

        var outcome = CardStore.SupersedeDecision(_root, supersedingPath, supersededPath, CardOwner.Architect, Now, TimeSpan.FromSeconds(5));

        var handEntered = Assert.IsType<CardDecisionSupersedeOutcome.HandEnteredDerivedState>(outcome);
        Assert.Equal(supersededPath, handEntered.FilePath);
        Assert.Equal(ReservedKey, handEntered.Key);

        var read = AssertParseSuccess(CardStore.ReadCard(supersededPath));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, refusal.By);
        Assert.Equal(handEntered.RefusingRule, refusal.Rule);
        Assert.Equal(handEntered.Remedy, refusal.Remedy);
        Assert.Equal((ReservedKey, ReservedValue), Assert.Single(read.UnknownFrontmatterFields));

        // The clean superseding card is left byte-identical — no supersession recorded, no
        // refusal appended, nothing at all written to it.
        var supersedingRead = AssertParseSuccess(CardStore.ReadCard(supersedingPath));
        Assert.Empty(supersedingRead.Refusals);
        Assert.Null(supersedingRead.RegisterFields.Supersedes);
        Assert.Empty(supersedingRead.UnknownFrontmatterFields);
    }

    // Same reasoning as the superseded-decision case above: the absorbed card, not the family, is
    // the one a quick read of CompactRules is likeliest to assume already carries this guard.
    [Fact]
    public void CompactRules_AbsorbedCardCarryingAReservedKey_Refuses_AndRecords_LeavingFamilyUntouched()
    {
        var familyPath = WriteCleanRuleCard(_changesDirectory, "r-0004", "R-0004");
        var absorbedPath = WriteTaintedCard(_changesDirectory, "r-0005", "R-0005", CardKind.Rule, CardOwner.Architect);

        var outcome = CardStore.CompactRules(_root, familyPath, [absorbedPath], ChangeName, CardOwner.Architect, Now, TimeSpan.FromSeconds(5));

        var handEntered = Assert.IsType<CardRuleCompactOutcome.HandEnteredDerivedState>(outcome);
        Assert.Equal(absorbedPath, handEntered.FilePath);
        Assert.Equal(ReservedKey, handEntered.Key);

        var read = AssertParseSuccess(CardStore.ReadCard(absorbedPath));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, refusal.By);
        Assert.Equal(handEntered.RefusingRule, refusal.Rule);
        Assert.Equal(handEntered.Remedy, refusal.Remedy);
        Assert.Equal((ReservedKey, ReservedValue), Assert.Single(read.UnknownFrontmatterFields));

        // The clean family card is left byte-identical — no absorption recorded, no refusal
        // appended, nothing at all written to it.
        var familyRead = AssertParseSuccess(CardStore.ReadCard(familyPath));
        Assert.Empty(familyRead.Refusals);
        Assert.Empty(familyRead.RegisterFields.Absorbs);
    }

    /// <summary>§13, card-model: "The verbs that dispose of a thread SHALL NOT be the only ones
    /// that can start one" — <c>CardStore.AddComment</c>'s own reuse of the guard, reported as
    /// its own outcome case (see <see cref="Cards.CardCommentAppendOutcome"/>'s own doc comment
    /// for why it is not the generic <c>CardWriteResult</c>'s).</summary>
    [Fact]
    public void AddComment_CardCarryingAReservedKey_Refuses_AndRecords()
    {
        var path = WriteTaintedCard(_changesDirectory, "b-0020", "B-0020", CardKind.Block, CardOwner.Worker);
        var comment = new CardComment("comment-0001", CardOwner.Reviewer, Now, "Note.", null, null, null, []);

        var outcome = CardStore.AddComment(_root, path, comment, TimeSpan.FromSeconds(5), ChangeName);

        var handEntered = Assert.IsType<CardCommentAppendOutcome.HandEnteredDerivedState>(outcome);
        Assert.Equal(ReservedKey, handEntered.Key);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Reviewer, refusal.By);
        Assert.Equal(handEntered.RefusingRule, refusal.Rule);
        Assert.Equal(handEntered.Remedy, refusal.Remedy);
        Assert.Equal((ReservedKey, ReservedValue), Assert.Single(read.UnknownFrontmatterFields));
        Assert.Empty(read.Comments);
    }

    // --- fixtures --------------------------------------------------------------------------

    private static string WriteTaintedCard(string directory, string fileStem, string id, CardKind kind, CardOwner owner, CardScope? scopeOverride = null)
    {
        var path = Path.Combine(directory, fileStem + ".md");
        var scope = scopeOverride ?? kind.Match(
            onBlock: static () => CardScope.Change,
            onQuestion: static () => CardScope.Repository,
            onFinding: static () => CardScope.Change,
            onObligation: static () => CardScope.Repository,
            onRule: static () => CardScope.Change,
            onHazard: static () => CardScope.Repository,
            onDecision: static () => CardScope.Capability,
            onSection: static () => CardScope.Change);
        var section = kind.Match(
            onBlock: static () => "10",
            onQuestion: static () => string.Empty,
            onFinding: static () => "10",
            onObligation: static () => string.Empty,
            onRule: static () => string.Empty,
            onHazard: static () => string.Empty,
            onDecision: static () => string.Empty,
            onSection: static () => string.Empty);
        var status = kind.Match(
            onBlock: static () => "briefed",
            onQuestion: static () => "open",
            onFinding: static () => "open",
            onObligation: static () => "open",
            onRule: static () => "open",
            onHazard: static () => "open",
            onDecision: static () => "open",
            onSection: static () => "open");
        var frontmatter = new CardFrontmatter(id, kind, "Title", status, owner, scope, section, Now, Now);
        // RegisterFields (and every other kind-specific field bag) default to Empty on CardFile
        // regardless of kind — the guard under test reads UnknownFrontmatterFields only, so no
        // kind-specific field needs setting for any of these fixtures.
        var card = new CardFile(frontmatter, "Body.", [], [(ReservedKey, ReservedValue)]);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string WriteTaintedBlockCardApproved(string directory, string fileStem, string id, string sectionId)
    {
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Block, "Title", "approved", CardOwner.Architect, CardScope.Change, sectionId, Now, Now);
        var blockFields = new BlockCardFields(Base: "base-commit", ReviewedState: null, Tasks: [], Round: null, BlockedBy: [], GateResults: []);
        var card = new CardFile(frontmatter, "Body.", [], [(ReservedKey, ReservedValue)], BlockFields: blockFields);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string WriteCleanSectionCard(string directory, string fileStem, string id)
    {
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Section, "Title", "open", CardOwner.Architect, CardScope.Change, string.Empty, Now, Now);
        var card = new CardFile(frontmatter, "Body.", [], []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string WriteCleanDecisionCard(string directory, string fileStem, string id)
    {
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Decision, "Title", "open", CardOwner.Architect, CardScope.Capability, string.Empty, Now, Now);
        var card = new CardFile(frontmatter, "Body.", [], [], RegisterFields: RegisterCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string WriteCleanRuleCard(string directory, string fileStem, string id)
    {
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Rule, "Title", "open", CardOwner.Architect, CardScope.Change, string.Empty, Now, Now);
        var card = new CardFile(frontmatter, "Body.", [], [], RegisterFields: RegisterCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }


    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match(
            onSuccess: static success => success.Card,
            onFailure: static failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
