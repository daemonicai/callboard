using System.Reflection;
using System.Text.RegularExpressions;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 9.10 — "add a test per refusal rule demonstrating it fires" (amended S5, <c>proposal.md</c>:
/// "R1–R8 each have a test"), and <c>design.md</c>'s own risk framing: "A refusal rule that fails
/// open is invisible until it matters → the refusal set is modelled as a closed union so an
/// unhandled case is a compile error, and every rule carries a test per the amended S5." The closed
/// union half is enforced by the compiler — every outcome union's <c>Match</c> is exhaustive with
/// no discard arm. This file is the other half, made mechanical rather than a paragraph a worker
/// under deadline can miss: "a test per rule, asserting the line landed on the card" was a brief
/// instruction (A2), a numbered standing instruction (A3), and a repeated ruling (B) — and still
/// reached block B with all eight cases of one union arriving untested. See the §9 architect post
/// carving this block ahead of the remaining rules for the full reasoning.
///
/// <para>
/// <b>Design (argued in the DEVLOG before this file was written).</b> <see cref="Registry"/> is a
/// hand-maintained map from every concrete <see cref="ICardRefusalReason"/> implementor in the
/// product assembly to the one test that proves it fires and records — the same pairing a reviewer
/// already re-derives by hand every block. Presence of an entry is not what this gate trusts; three
/// things are checked mechanically:
/// </para>
/// <list type="number">
/// <item><description><b>Bijection, not one-way lookup.</b> <see cref="Registry"/>'s keys must
/// exactly equal the reflected set of implementors — a product case with no entry fails, named; an
/// entry whose key no longer implements the interface (renamed, removed) fails too, so the map
/// cannot silently drift out of sync with the code it describes in either direction.</description></item>
/// <item><description><b>The named method is real and live.</b> The test assembly is reflected for
/// the registered <c>(TestClass, TestMethod)</c> pair; the method must exist and carry
/// <c>[Fact]</c>/<c>[Theory]</c> with no <c>Skip</c>.</description></item>
/// <item><description><b>The method's own source text must mention <c>.Refusals</c>, in a shape
/// that isn't entirely <c>Assert.Empty(...)</c>.</b> This is the check that stops "point the
/// registry at any passing test" from satisfying the gate — a method that only asserts the
/// returned outcome's type, never the card's recorded refusal history, fails here even though it
/// exists and passes. It is not enough to mention '.Refusals' at all: a registered test was found
/// (§9 block C review) whose only '.Refusals' statement was <c>Assert.Empty(ruleRead.Refusals)</c>
/// — a real, deliberate fixture proving the case does <i>not</i> record in a contrived anchor-
/// mismatch scenario, wrongly registered in place of the sibling test proving it does. A match
/// where every '.Refusals' occurrence is wrapped in <c>Assert.Empty(...)</c> is rejected; at least
/// one occurrence must assert something other than absence. Deliberately textual, not a
/// re-execution of each test's own assertions: re-verifying *what* a test asserts beyond this one
/// targeted polarity check is the reviewer's job (already this section's established practice), not
/// this gate's. This gate's job is narrower: no case ships unregistered, no registered entry can be
/// a stub, and no registered entry can be the proof of the opposite disposition.</description></item>
/// </list>
/// <para>
/// The product assembly stays reflection-free (NativeAOT, ADR-0002) — everything above runs only in
/// this test project, on the ordinary runtime.
/// </para>
/// <para>
/// <b>Two known dispositions counted as covered without special-casing them</b> (§9 block C brief):
/// <see cref="CardApprovalOutcome.NotABlockCard"/> is unreachable through the CLI's own
/// <c>block approve</c> verb (id resolution filters to a block card first) but reachable via a
/// direct <see cref="CardStore.RecordApproval"/> call racing that resolution; likewise
/// <see cref="CardWriteResult.RoundDisagreesWithHistory"/>'s only two construction sites are
/// <c>AppendComment</c>/<c>TransferOwnership</c>, never a dedicated CLI verb of their own. Neither
/// needed a special case here — a direct-<see cref="CardStore"/> test is exactly as valid a mapping
/// entry as a CLI-level one, since "is there a test that provokes it and reads back the record" is
/// the only question this gate asks.
/// </para>
/// </summary>
public sealed class RefusalCoverageGateTests
{
    /// <summary>
    /// Every card-addressed refusal case landed so far, mapped to the one test that proves it fires
    /// and records a <see cref="CardRefusalEntry"/> on the card. Add an entry here in the same
    /// commit that adds a new <see cref="ICardRefusalReason"/> implementor — <see
    /// cref="RefusalCoverageIsExactlyTheReflectedSet"/> fails, naming the case, until you do.
    /// </summary>
    /// <summary>Matches an <c>Assert.Empty(x.Refusals)</c>-shaped statement — the one polarity of
    /// '.Refusals' usage that proves a case does NOT record, so a registered test whose every
    /// occurrence takes this shape is rejected by
    /// <see cref="EveryRegisteredCoverageTestActuallyInspectsTheCardsRefusalHistory"/> even though
    /// it mentions '.Refusals' and would otherwise pass.</summary>
    private static readonly Regex EmptyAssertionPattern = new(@"Assert\.Empty\(\s*\w+\.Refusals\s*\)");

    private static readonly Dictionary<Type, (Type TestClass, string TestMethod)> Registry = new()
    {
        { typeof(CardApprovalOutcome.NotABlockCard), (typeof(CommandDispatcherBlockApproveTests), "RecordApproval_TargetIsNotABlockCard_Refuses_AndRecordsTheRefusal") },
        { typeof(CardApprovalOutcome.RoleNotPermitted), (typeof(CommandDispatcherBlockApproveTests), "BlockApprove_NonReviewingRole_Refuses_AndRecordsTheRefusal") },
        { typeof(CardApprovalOutcome.RoundDisagreesWithHistory), (typeof(CommandDispatcherBlockApproveTests), "BlockApprove_RoundDisagreesWithHistory_Refuses_AndRecordsTheRefusal") },
        { typeof(CardApprovalOutcome.UndefinedTransition), (typeof(CommandDispatcherBlockApproveTests), "BlockApprove_NotInReview_RefusesWithUndefinedTransitionCode_AndRecordsTheRefusal") },
        { typeof(CardApprovalOutcome.UndispositionedNits), (typeof(CommandDispatcherNitTests), "BlockApprove_UndispositionedNit_Refuses_AndNamesTheNit") },
        { typeof(CardApprovalOutcome.UnresolvedThreadsAddressedToActor), (typeof(CommandDispatcherNitTests), "BlockApprove_UnresolvedThreadAddressedToActor_Refuses_AndListsTheThread_AndRecordsIt") },
        { typeof(CardApprovalOutcome.BlockedByOpenProductOwnerQuestion), (typeof(CommandDispatcherBlockApproveTests), "BlockApprove_BlockedByOpenProductOwnerQuestion_Refuses_AndRecordsTheRefusal") },
        { typeof(CardApprovalOutcome.BlockingQuestionUnreadable), (typeof(CommandDispatcherBlockApproveTests), "BlockApprove_BlockingQuestionCorruptFile_Refuses_AndRecordsTheRefusal") },
        { typeof(CardBlockTransitionOutcome.BaseImmutable), (typeof(CardBlockTransitionTests), "ApplyBlockTransition_BaseCannotChangeAcrossRemediationRounds") },
        { typeof(CardBlockTransitionOutcome.BaseNotRecorded), (typeof(CardBlockTransitionTests), "ApplyBlockTransition_BriefWithNoBaseRecordedAndNoneSupplied_Refuses") },
        { typeof(CardBlockTransitionOutcome.NotABlockCard), (typeof(CardBlockTransitionTests), "ApplyBlockTransition_TargetIsNotABlockCard_Refuses") },
        { typeof(CardBlockTransitionOutcome.RoundDisagreesWithHistory), (typeof(RoundAgreesWithHistoryTests), "ApplyBlockTransition_StoredRoundAheadOfHistory_Refuses_NamesBothFigures_AltersNeither") },
        { typeof(CardBlockTransitionOutcome.UndefinedTransition), (typeof(CardBlockTransitionTests), "ApplyBlockTransition_UndefinedTransition_RecordsExactlyOneRefusal_AndChangesNothingElse") },
        { typeof(CardBlockTransitionOutcome.UndispositionedNits), (typeof(CommandDispatcherNitTests), "BlockTransition_ChangesRequested_UndispositionedNit_Refuses") },
        { typeof(CardBlockTransitionOutcome.UnresolvedThreadsAddressedToActor), (typeof(CommandDispatcherNitTests), "BlockTransition_ChangesRequested_UnresolvedThreadAddressedToActor_Refuses_AndRecordsIt") },
        { typeof(CardBlockTransitionOutcome.BlockedByOpenProductOwnerQuestion), (typeof(CommandDispatcherBlockTransitionTests), "BlockTransition_BlockedByOpenProductOwnerQuestion_Refuses_AndRecordsTheRefusal") },
        { typeof(CardBlockTransitionOutcome.BlockingQuestionUnreadable), (typeof(CommandDispatcherBlockTransitionTests), "BlockTransition_BlockingQuestionDuplicateId_Refuses_AndRecordsTheRefusal") },
        { typeof(CardDecisionSupersedeOutcome.NotADecisionCard), (typeof(CardDecisionSupersedeTests), "SupersedeDecision_SupersedingCardIsNotADecision_ProperlyScoped_Refuses_AndRecords") },
        { typeof(CardDecisionSupersedeOutcome.ResolvedSelfSupersession), (typeof(CardDecisionSupersedeTests), "SupersedeDecision_TwoDifferentPathsResolveToTheSameId_RefusesAsResolvedSelfSupersession_AndRecords") },
        { typeof(CardDecisionSupersedeOutcome.SupersededAlreadyDischarged), (typeof(CardDecisionSupersedeTests), "SupersedeDecision_TargetAlreadyDischarged_Refuses_NotARe_Supersession") },
        { typeof(CardDecisionSupersedeOutcome.SupersedingAlreadyDischarged), (typeof(CardDecisionSupersedeTests), "SupersedeDecision_ThreeNodeCycle_TheClosingLinkRefuses") },
        { typeof(CardGateResultOutcome.NotABlockCard), (typeof(CardGateResultTests), "RecordGateResult_TargetIsNotABlockCard_Refuses") },
        { typeof(CardGateResultOutcome.RoundDisagreesWithHistory), (typeof(RoundAgreesWithHistoryTests), "RecordGateResult_CardWithDisagreeingRound_Refuses_NamesBothFigures_AltersNeither") },
        { typeof(CardNitDispositionOutcome.AlreadyDispositioned), (typeof(CardNitStoreTests), "DispositionNit_AlreadyDispositioned_Refuses_AndRecordsAgainstTheCard") },
        { typeof(CardNitDispositionOutcome.NitNotFound), (typeof(CardNitStoreTests), "DispositionNit_NoLiveNitCarriesTheId_Refuses_AndRecordsAgainstTheCard") },
        { typeof(CardNitDispositionOutcome.NotABlockCard), (typeof(CardNitStoreTests), "DispositionNit_TargetIsNotABlockCard_Refuses_AndRecordsAgainstTheCard") },
        { typeof(CardNitDispositionOutcome.RaisedCardAlreadyExists), (typeof(CardNitStoreTests), "DispositionNit_RaisedCardTargetAlreadyExists_Refuses_AndRecordsAgainstTheBlockCard") },
        { typeof(CardNitDispositionOutcome.RoundDisagreesWithHistory), (typeof(CardNitStoreTests), "DispositionNit_BlockCardWithDisagreeingRound_Refuses_AndRecordsAgainstTheCard") },
        { typeof(CardNitDispositionOutcome.UnresolvedThreadsAddressedToActor), (typeof(CommandDispatcherNitTests), "NitDisposition_FixBeforeLand_UnresolvedThreadAddressedToActor_Refuses_AndDispositionsNothing") },
        { typeof(CardNitRaiseOutcome.NotABlockCard), (typeof(CardNitStoreTests), "RaiseNit_TargetIsNotABlockCard_Refuses_AndRecordsAgainstTheCard") },
        { typeof(CardNitRaiseOutcome.NotUnderReview), (typeof(CommandDispatcherNitTests), "NitRaise_NotUnderReview_Refuses_NamingStateAndObligationRoute") },
        { typeof(CardNitRaiseOutcome.RoundDisagreesWithHistory), (typeof(CardNitStoreTests), "RaiseNit_BlockCardWithDisagreeingRound_Refuses_AndRecordsAgainstTheCard") },
        { typeof(CardQuestionAnswerOutcome.NotAQuestionCard), (typeof(CommandDispatcherQuestionAnswerTests), "QuestionAnswer_TargetIsNotAQuestionCard_Refuses_AndRecordsTheRefusal") },
        { typeof(CardQuestionAnswerOutcome.NotOpen), (typeof(CommandDispatcherQuestionAnswerTests), "QuestionAnswer_AlreadyAnswered_Refuses_AndRecordsTheRefusal") },
        { typeof(CardQuestionDeferOutcome.NotAQuestionCard), (typeof(CommandDispatcherQuestionDeferTests), "QuestionDefer_TargetIsNotAQuestionCard_Refuses_AndRecordsTheRefusal") },
        { typeof(CardQuestionDeferOutcome.NotOpen), (typeof(CommandDispatcherQuestionDeferTests), "QuestionDefer_AlreadyDeferred_Refuses_AndRecordsTheRefusal") },
        { typeof(CardRegisterDischargeOutcome.AlreadyDischarged), (typeof(CardRegisterDischargeTests), "DischargeRegisterCard_AlreadyDischarged_Refuses_AndDoesNotOverwriteTheFirstDischarge") },
        { typeof(CardRegisterDischargeOutcome.NotARegisterCard), (typeof(CardRegisterDischargeTests), "DischargeRegisterCard_TargetIsNotARegisterCard_Refuses") },
        { typeof(CardRuleCompactOutcome.AbsorbedAlreadyDischarged), (typeof(CardRuleCompactTests), "CompactRules_TargetAlreadyDischarged_Refuses_NotARe_Absorption") },
        { typeof(CardRuleCompactOutcome.FamilyAlreadyDischarged), (typeof(CardRuleCompactTests), "CompactRules_ThreeNodeCycle_TheClosingLinkRefuses") },
        { typeof(CardRuleCompactOutcome.NotARuleCard), (typeof(CardRuleCompactTests), "CompactRules_FamilyIsNotARule_Refuses") },
        { typeof(CardRuleCompactOutcome.ResolvedDuplicateAbsorbedRule), (typeof(CardRuleCompactTests), "CompactRules_TwoAbsorbedPathsShareAnId_RefusesAsResolvedDuplicateAbsorbedRule_AndRecords") },
        { typeof(CardRuleCompactOutcome.ResolvedSelfAbsorption), (typeof(CardRuleCompactTests), "CompactRules_AbsorbedCardSharesTheFamilysId_RefusesAsResolvedSelfAbsorption_AndRecords") },
        { typeof(CardRulePromoteOutcome.AlreadyRepositoryScoped), (typeof(CardRulePromoteTests), "PromoteRule_AlreadyRepositoryScoped_Refuses_WithNothingMoved") },
        { typeof(CardRulePromoteOutcome.NotARuleCard), (typeof(CardRulePromoteTests), "PromoteRule_NonRuleCard_Refuses") },
        { typeof(CardRulePromoteOutcome.NotChangeScoped), (typeof(CardRulePromoteTests), "PromoteRule_CapabilityScoped_Refuses_AsNotChangeScoped_AndRecords") },
        { typeof(CardRulePromoteOutcome.TargetAlreadyExists), (typeof(CardRulePromoteTests), "PromoteRule_TargetBasenameAlreadyClaimedInRegister_Refuses_WithNothingMoved") },
        { typeof(CardObligationPromoteOutcome.AlreadyRepositoryScoped), (typeof(CardObligationPromoteTests), "PromoteObligation_AlreadyRepositoryScoped_Refuses_AndRecordsTheRefusal") },
        { typeof(CardObligationPromoteOutcome.NotAnObligationCard), (typeof(CardObligationPromoteTests), "PromoteObligation_NonObligationCard_Refuses_AndRecordsTheRefusal") },
        { typeof(CardObligationPromoteOutcome.NotChangeScoped), (typeof(CardObligationPromoteTests), "PromoteObligation_SectionScoped_RefusesAsNotChangeScoped_AndRecordsTheRefusal") },
        { typeof(CardObligationPromoteOutcome.TargetAlreadyExists), (typeof(CardObligationPromoteTests), "PromoteObligation_TargetBasenameAlreadyClaimedInRegister_Refuses_WithNothingMoved_AndRecords") },
        { typeof(CardObligationDeclineOutcome.AlreadyDischarged), (typeof(CardObligationDeclineTests), "DeclineObligation_AlreadyDischarged_Refuses_AndDoesNotOverwriteTheFirstDisposition") },
        { typeof(CardObligationDeclineOutcome.NotAnObligationCard), (typeof(CardObligationDeclineTests), "DeclineObligation_NonObligationCard_Refuses_AndRecordsTheRefusal") },
        { typeof(CardObligationDeclineOutcome.ReasonRequired), (typeof(CardObligationDeclineTests), "DeclineObligation_NoReason_Refuses_AndRecordsTheRefusal_AndDoesNotDischarge") },
        { typeof(CardSectionCloseOutcome.AlreadyClosed), (typeof(CardSectionCloseTests), "CloseSection_AlreadyClosed_Refuses_AndDoesNotOverwriteTheFirstClosure_AndRecordsTheRefusal") },
        { typeof(CardSectionCloseOutcome.NotASectionCard), (typeof(CardSectionCloseTests), "CloseSection_TargetIsNotASectionCard_Refuses_AndRecordsTheRefusal") },
        { typeof(CardSectionCloseOutcome.BlockNotApproved), (typeof(CardSectionCloseTests), "CloseSection_ABlockNotApproved_RefusesTheWholeClose_LeavesEveryOtherCardUntouched") },
        { typeof(CardSectionCloseOutcome.BlockGateFailed), (typeof(CardSectionCloseTests), "CloseSection_ABlockWithAFailingGate_Refuses_AndRecordsTheRefusal") },
        { typeof(CardSectionCloseOutcome.BlockGateAbsent), (typeof(CardSectionCloseTests), "CloseSection_ABlockWithAnAbsentGateThisRound_Refuses_NotAPassByDefault_AndRecordsTheRefusal") },
        { typeof(CardSectionCloseOutcome.RoundDisagreesWithHistory), (typeof(CardSectionCloseTests), "CloseSection_ABlockWithADisagreeingRound_Refuses_NamesBothFigures_AndRecordsTheRefusal") },
        { typeof(CardSectionCloseOutcome.OpenObligations), (typeof(CardSectionCloseTests), "CloseSection_AnOpenObligationOwedByTheSection_Refuses_AndRecordsTheRefusal") },
        { typeof(CardSectionCloseOutcome.OpenUndeferredQuestion), (typeof(CardSectionCloseTests), "CloseSection_AnOpenQuestionRaisedInTheSection_Refuses_AndRecordsTheRefusal") },
        { typeof(CardSectionCloseOutcome.UnresolvedAddressedThread), (typeof(CardSectionCloseTests), "CloseSection_AnUnresolvedAddressedThreadOnTheSectionItself_Refuses_AndRecordsTheRefusal") },
        { typeof(CardSectionCloseOutcome.BlockedByOpenProductOwnerQuestion), (typeof(CardSectionCloseTests), "CloseSection_AnApprovedBlockBlockedByAnOpenProductOwnerQuestion_Refuses_AndRecordsTheRefusal") },
        { typeof(CardSectionCloseOutcome.BlockingQuestionUnreadable), (typeof(CardSectionCloseTests), "CloseSection_AnApprovedBlockBlockingQuestionUnreadable_Refuses_AndRecordsTheRefusal") },
        { typeof(CardSectionAuthorisationOutcome.RoleNotPermitted), (typeof(CardSectionAuthorisationTests), "RecordSectionAuthorisation_ByAnyRoleOtherThanProductOwner_Refuses_AndRecordsTheRefusal") },
        { typeof(CardSectionAuthorisationOutcome.NotASectionCard), (typeof(CardSectionAuthorisationTests), "RecordSectionAuthorisation_TargetIsNotASectionCard_Refuses") },
        { typeof(CardSectionAuthorisationOutcome.NotAtBound), (typeof(CardSectionAuthorisationTests), "RecordSectionAuthorisation_OnABrandNewSection_RefusesWithNotAtBound") },
        { typeof(CardBlockedByOutcome.NotABlockCard), (typeof(CardBlockedByTests), "AddBlockedBy_TargetIsNotABlockCard_Refuses_AndRecordsTheRefusal") },
        { typeof(CardBlockedByOutcome.RoundDisagreesWithHistory), (typeof(CardBlockedByTests), "AddBlockedBy_RoundDisagreesWithHistory_Refuses_NamesBothFigures_AndRecordsTheRefusal") },
        { typeof(CardBlockedByOutcome.AlreadyBlockedBy), (typeof(CardBlockedByTests), "AddBlockedBy_AlreadyPresent_Refuses_AndRecordsTheRefusal") },
        { typeof(CardBlockedByOutcome.NotBlockedBy), (typeof(CardBlockedByTests), "RemoveBlockedBy_NotPresent_Refuses_AndRecordsTheRefusal") },
        { typeof(CardBlockedByOutcome.BlockerUnresolvable), (typeof(CardBlockedByTests), "AddBlockedBy_BlockerIdDoesNotResolve_Refuses_AndRecordsTheRefusal") },
        { typeof(CardSectionVerdictOutcome.FindingAlreadyOwned), (typeof(CommandDispatcherSectionVerdictRemediationTests), "FindingNew_KeyAlreadyOwnedOnDisk_Refuses_CreatesNoSecondCard") },
        { typeof(CardSectionVerdictOutcome.NewFindingCardAlreadyExists), (typeof(CommandDispatcherSectionVerdictRemediationTests), "FindingNew_TargetFileAlreadyExistsOnDisk_Refuses_AndRecordsAgainstTheSection") },
        { typeof(CardSectionVerdictOutcome.NotASectionCard), (typeof(CardSectionVerdictTests), "RecordSectionVerdict_TargetIsNotASectionCard_Refuses_AndRecordsTheRefusal") },
        { typeof(CardSectionVerdictOutcome.RecurringFindingNotApproved), (typeof(CommandDispatcherSectionVerdictRemediationTests), "FindingRecurred_TargetNotApproved_Refuses") },
        { typeof(CardSectionVerdictOutcome.RecurringFindingTargetsTaskImplementingBlock), (typeof(CommandDispatcherSectionVerdictRemediationTests), "FindingRecurred_TargetsATaskImplementingBlock_Refuses") },
        { typeof(CardSectionVerdictOutcome.RecurringTargetNotFound), (typeof(CardSectionVerdictTests), "RecordSectionVerdict_RecurringTargetDoesNotExist_Refuses_AndRecordsAgainstTheSection") },
        { typeof(CardSectionVerdictOutcome.RemediationBoundExceeded), (typeof(CardSectionVerdictTests), "RecordSectionVerdict_ThirdRequestChangesWithoutAuthorisation_Refuses_AndApproveDoesNotAdvanceTheBound") },
        { typeof(CardSectionVerdictOutcome.RoundDisagreesWithHistory), (typeof(CardSectionVerdictTests), "RecordSectionVerdict_RecurringTargetRoundDisagreesWithHistory_Refuses_AndRecordsAgainstTheSection") },
        { typeof(CardWriteResult.RoundDisagreesWithHistory), (typeof(CardOwnershipTransferTests), "TransferOwnership_BlockCardWithDisagreeingRound_Refuses_AndRecordsAgainstTheCard") },
        { typeof(CardWriteResult.HandEnteredDerivedState), (typeof(CardStoreWriteTests), "AppendComment_CardCarryingAReservedDerivedStateKey_Refuses_AndRecordsAgainstTheCard") },
        { typeof(CardNitRaiseOutcome.HandEnteredDerivedState), (typeof(HandEnteredDerivedStateCoverageTests), "RaiseNit_CardCarryingAReservedKey_Refuses_AndRecords") },
        { typeof(CardBlockTransitionOutcome.HandEnteredDerivedState), (typeof(HandEnteredDerivedStateCoverageTests), "ApplyBlockTransition_CardCarryingAReservedKey_Refuses_AndRecords") },
        { typeof(CardApprovalOutcome.HandEnteredDerivedState), (typeof(HandEnteredDerivedStateCoverageTests), "RecordApproval_CardCarryingAReservedKey_Refuses_AndRecords") },
        { typeof(CardNitDispositionOutcome.HandEnteredDerivedState), (typeof(HandEnteredDerivedStateCoverageTests), "DispositionNit_CardCarryingAReservedKey_Refuses_AndRecords") },
        { typeof(CardGateResultOutcome.HandEnteredDerivedState), (typeof(HandEnteredDerivedStateCoverageTests), "RecordGateResult_CardCarryingAReservedKey_Refuses_AndRecords") },
        { typeof(CardBlockedByOutcome.HandEnteredDerivedState), (typeof(HandEnteredDerivedStateCoverageTests), "AddBlockedBy_CardCarryingAReservedKey_Refuses_AndRecords") },
        { typeof(CardQuestionAnswerOutcome.HandEnteredDerivedState), (typeof(HandEnteredDerivedStateCoverageTests), "AnswerQuestion_CardCarryingAReservedKey_Refuses_AndRecords") },
        { typeof(CardQuestionDeferOutcome.HandEnteredDerivedState), (typeof(HandEnteredDerivedStateCoverageTests), "DeferQuestion_CardCarryingAReservedKey_Refuses_AndRecords") },
        { typeof(CardSectionAuthorisationOutcome.HandEnteredDerivedState), (typeof(HandEnteredDerivedStateCoverageTests), "RecordSectionAuthorisation_CardCarryingAReservedKey_Refuses_AndRecords") },
        { typeof(CardSectionVerdictOutcome.HandEnteredDerivedState), (typeof(HandEnteredDerivedStateCoverageTests), "RecordSectionVerdict_SectionCardCarryingAReservedKey_Refuses_AndRecords") },
        { typeof(CardSectionCloseOutcome.HandEnteredDerivedState), (typeof(HandEnteredDerivedStateCoverageTests), "CloseSection_SectionCardCarryingAReservedKey_Refuses_AndRecords") },
        { typeof(CardRegisterDischargeOutcome.HandEnteredDerivedState), (typeof(HandEnteredDerivedStateCoverageTests), "DischargeRegisterCard_CardCarryingAReservedKey_Refuses_AndRecords") },
        { typeof(CardRulePromoteOutcome.HandEnteredDerivedState), (typeof(HandEnteredDerivedStateCoverageTests), "PromoteRule_CardCarryingAReservedKey_Refuses_AndRecords") },
        { typeof(CardObligationPromoteOutcome.HandEnteredDerivedState), (typeof(HandEnteredDerivedStateCoverageTests), "PromoteObligation_CardCarryingAReservedKey_Refuses_AndRecords") },
        { typeof(CardObligationDeclineOutcome.HandEnteredDerivedState), (typeof(HandEnteredDerivedStateCoverageTests), "DeclineObligation_CardCarryingAReservedKey_Refuses_AndRecords") },
        { typeof(CardCommentResolveOutcome.HandEnteredDerivedState), (typeof(HandEnteredDerivedStateCoverageTests), "ResolveComment_CardCarryingAReservedKey_Refuses_AndRecords") },
        { typeof(CardCommentPromoteOutcome.HandEnteredDerivedState), (typeof(HandEnteredDerivedStateCoverageTests), "PromoteComment_CardCarryingAReservedKey_Refuses_AndRecords") },
        { typeof(CardDecisionSupersedeOutcome.HandEnteredDerivedState), (typeof(HandEnteredDerivedStateCoverageTests), "SupersedeDecision_SupersedingCardCarryingAReservedKey_Refuses_AndRecords") },
        { typeof(CardRuleCompactOutcome.HandEnteredDerivedState), (typeof(HandEnteredDerivedStateCoverageTests), "CompactRules_FamilyCardCarryingAReservedKey_Refuses_AndRecords") },
        { typeof(CardCommentResolveOutcome.CommentNotFound), (typeof(CardCommentResolveTests), "ResolveComment_CommentDoesNotExist_Refuses_AndRecordsTheRefusal_AndAppendsNothing") },
        { typeof(CardCommentResolveOutcome.RoleNotPermitted), (typeof(CardCommentResolveTests), "ResolveComment_RoleNeitherAddresseeNorCardOwner_Refuses_AndRecordsTheRefusal_AndDoesNotResolve") },
        { typeof(CardCommentResolveOutcome.AlreadyResolved), (typeof(CardCommentResolveTests), "ResolveComment_AlreadyResolved_Refuses_AndRecordsTheRefusal_AndDoesNotDoubleResolve") },
        { typeof(CardCommentResolveOutcome.ReasonRequired), (typeof(CardCommentResolveTests), "ResolveComment_RequireReasonTrue_NoReason_Refuses_AndRecordsTheRefusal_AndDoesNotResolve") },
        { typeof(CardCommentPromoteOutcome.CommentNotFound), (typeof(CardCommentPromoteTests), "PromoteComment_CommentDoesNotExist_Refuses_AndRecordsTheRefusal_AndWritesNoRaisedCard") },
        { typeof(CardCommentPromoteOutcome.RoleNotPermitted), (typeof(CardCommentPromoteTests), "PromoteComment_RoleNeitherAddresseeNorCardOwner_Refuses_AndRecordsTheRefusal_AndWritesNoRaisedCard") },
        { typeof(CardCommentPromoteOutcome.AlreadyResolved), (typeof(CardCommentPromoteTests), "PromoteComment_AlreadyResolved_Refuses_AndRecordsTheRefusal_AndWritesNoRaisedCard") },
        { typeof(CardCreateOutcome.IdentityAlreadyBorne), (typeof(CommandDispatcherBlockCreateTests), "BlockCreate_ARecordedIdentityIsRefused_AndRecordsAgainstTheCardAlreadyBearingIt") },
        { typeof(CardCommentAppendOutcome.ReplyToNotFound), (typeof(CommandDispatcherCommentTests), "CommentAdd_ReplyToACommentNotOnThisCard_Refuses_AndRecordsTheRefusal") },
        { typeof(CardCommentAppendOutcome.RoundDisagreesWithHistory), (typeof(RoundAgreesWithHistoryTests), "AddComment_BlockCardWithDisagreeingRound_Refuses_NamesBothFigures_AltersNeither") },
        { typeof(CardCommentAppendOutcome.HandEnteredDerivedState), (typeof(HandEnteredDerivedStateCoverageTests), "AddComment_CardCarryingAReservedKey_Refuses_AndRecords") },
        { typeof(CardBlockRecordBaseOutcome.NotABlockCard), (typeof(CardBlockRecordBaseTests), "RecordBase_NonBlockCard_Refuses_AndRecordsTheRefusal") },
        { typeof(CardBlockRecordBaseOutcome.NotAtDrafting), (typeof(CardBlockRecordBaseTests), "RecordBase_CardAlreadyBriefed_Refuses_NamesTheState_AndRecordsTheRefusal") },
        { typeof(CardBlockRecordBaseOutcome.BaseImmutable), (typeof(CardBlockRecordBaseTests), "RecordBase_AlreadyRecorded_DifferentValue_Refuses_NamesBoth_AndRecordsTheRefusal") },
        { typeof(CardBlockRecordBaseOutcome.RoundDisagreesWithHistory), (typeof(RoundAgreesWithHistoryTests), "RecordBase_BlockCardWithDisagreeingRound_Refuses_NamesBothFigures_AltersNeither") },
        { typeof(CardBlockRecordBaseOutcome.HandEnteredDerivedState), (typeof(HandEnteredDerivedStateCoverageTests), "RecordBase_CardCarryingAReservedKey_Refuses_AndRecords") },
    };

    /// <summary>
    /// §9 remediation S2. Every concrete case declared non-recording, with the reason it is not
    /// <see cref="ICardRefusalReason"/> — the deliberate half of the universe
    /// <see cref="RefusalShapedUniverseIsFullyAccountedFor"/> checks against
    /// <see cref="ReflectedOutcomeUnionCases"/>. This is not a suppression list: every entry here is
    /// a considered, citable disposition (a success case, a categorical non-card-addressed case, a
    /// pre-lock check, or an established carve-out), not a case nobody got to yet — <see
    /// cref="RefusalShapedUniverseIsFullyAccountedFor"/> fails, naming the case, the moment a new
    /// concrete case appears in a discovered union without landing in either this dictionary or the
    /// <see cref="Registry"/> above. See the §9 remediation DEVLOG post (worker, "S2 — the mechanism
    /// proposed before building it") for the reasoning behind each group below.
    /// </summary>
    private static readonly Dictionary<Type, string> Exclusions = new()
    {
        // One success case per union — not refusal-shaped by definition.
        { typeof(CardApprovalOutcome.Approved), "the operation's own success case." },
        { typeof(CardBlockedByOutcome.Updated), "the operation's own success case." },
        { typeof(CardBlockTransitionOutcome.Applied), "the operation's own success case." },
        { typeof(CardCreateOutcome.Created), "the operation's own success case." },
        { typeof(CardDecisionSupersedeOutcome.Superseded), "the operation's own success case." },
        { typeof(CardFindingRecordOutcome.Recorded), "the operation's own success case." },
        { typeof(CardGateResultOutcome.Recorded), "the operation's own success case." },
        { typeof(CardNitDispositionOutcome.Dispositioned), "the operation's own success case." },
        { typeof(CardNitRaiseOutcome.Raised), "the operation's own success case." },
        { typeof(CardObligationDeclineOutcome.Declined), "the operation's own success case." },
        { typeof(CardObligationPromoteOutcome.Promoted), "the operation's own success case." },
        { typeof(CardQuestionAnswerOutcome.Answered), "the operation's own success case." },
        { typeof(CardQuestionDeferOutcome.Deferred), "the operation's own success case." },
        { typeof(CardRegisterDischargeOutcome.Discharged), "the operation's own success case." },
        { typeof(CardRuleCompactOutcome.Compacted), "the operation's own success case." },
        { typeof(CardRulePromoteOutcome.Promoted), "the operation's own success case." },
        { typeof(CardSectionAuthorisationOutcome.Recorded), "the operation's own success case." },
        { typeof(CardSectionCloseOutcome.Closed), "the operation's own success case." },
        { typeof(CardSectionVerdictOutcome.Recorded), "the operation's own success case." },
        { typeof(CardWriteResult.Success), "the operation's own success case." },
        { typeof(ChangeArchiveOutcome.Archived), "the operation's own success case." },

        // The four categorical cases, wherever a union declares them: never card-addressed
        // (CardNotFound/LayoutMismatch — §9 architect ruling, "only a card-addressed refusal
        // records": no card was ever resolved at the path these report on, so there is nothing to
        // record against), a reported content problem rather than a refusal (CardCorrupt — every
        // union's own doc comment keeps this apart from ICardRefusalReason), or enforcement itself
        // being unavailable (ToolFailure — ADR-0001: never a refusal).
        { typeof(CardApprovalOutcome.CardNotFound), "never card-addressed — no card resolved at the path." },
        { typeof(CardApprovalOutcome.LayoutMismatch), "never card-addressed — the path never anchored." },
        { typeof(CardApprovalOutcome.CardCorrupt), "a reported content problem, not a refusal." },
        { typeof(CardApprovalOutcome.ToolFailure), "ADR-0001: enforcement unavailable, never a refusal." },
        { typeof(CardBlockedByOutcome.CardNotFound), "never card-addressed — no card resolved at the path." },
        { typeof(CardBlockedByOutcome.LayoutMismatch), "never card-addressed — the path never anchored." },
        { typeof(CardBlockedByOutcome.CardCorrupt), "a reported content problem, not a refusal." },
        { typeof(CardBlockedByOutcome.ToolFailure), "ADR-0001: enforcement unavailable, never a refusal." },
        { typeof(CardBlockTransitionOutcome.CardNotFound), "never card-addressed — no card resolved at the path." },
        { typeof(CardBlockTransitionOutcome.LayoutMismatch), "never card-addressed — the path never anchored." },
        { typeof(CardBlockTransitionOutcome.CardCorrupt), "a reported content problem, not a refusal." },
        { typeof(CardBlockTransitionOutcome.ToolFailure), "ADR-0001: enforcement unavailable, never a refusal." },
        { typeof(CardDecisionSupersedeOutcome.CardNotFound), "never card-addressed — no card resolved at the path." },
        { typeof(CardDecisionSupersedeOutcome.LayoutMismatch), "never card-addressed — the path never anchored." },
        { typeof(CardDecisionSupersedeOutcome.CardCorrupt), "a reported content problem, not a refusal." },
        { typeof(CardDecisionSupersedeOutcome.ToolFailure), "ADR-0001: enforcement unavailable, never a refusal." },
        { typeof(CardGateResultOutcome.CardNotFound), "never card-addressed — no card resolved at the path." },
        { typeof(CardGateResultOutcome.LayoutMismatch), "never card-addressed — the path never anchored." },
        { typeof(CardGateResultOutcome.CardCorrupt), "a reported content problem, not a refusal." },
        { typeof(CardGateResultOutcome.ToolFailure), "ADR-0001: enforcement unavailable, never a refusal." },
        { typeof(CardNitDispositionOutcome.CardNotFound), "never card-addressed — no card resolved at the path." },
        { typeof(CardNitDispositionOutcome.LayoutMismatch), "never card-addressed — the path never anchored." },
        { typeof(CardNitDispositionOutcome.CardCorrupt), "a reported content problem, not a refusal." },
        { typeof(CardNitDispositionOutcome.ToolFailure), "ADR-0001: enforcement unavailable, never a refusal." },
        { typeof(CardNitRaiseOutcome.CardNotFound), "never card-addressed — no card resolved at the path." },
        { typeof(CardNitRaiseOutcome.LayoutMismatch), "never card-addressed — the path never anchored." },
        { typeof(CardNitRaiseOutcome.CardCorrupt), "a reported content problem, not a refusal." },
        { typeof(CardNitRaiseOutcome.ToolFailure), "ADR-0001: enforcement unavailable, never a refusal." },
        { typeof(CardObligationDeclineOutcome.CardNotFound), "never card-addressed — no card resolved at the path." },
        { typeof(CardObligationDeclineOutcome.LayoutMismatch), "never card-addressed — the path never anchored." },
        { typeof(CardObligationDeclineOutcome.CardCorrupt), "a reported content problem, not a refusal." },
        { typeof(CardObligationDeclineOutcome.ToolFailure), "ADR-0001: enforcement unavailable, never a refusal." },
        { typeof(CardObligationPromoteOutcome.CardNotFound), "never card-addressed — no card resolved at the path." },
        { typeof(CardObligationPromoteOutcome.LayoutMismatch), "never card-addressed — the path never anchored." },
        { typeof(CardObligationPromoteOutcome.CardCorrupt), "a reported content problem, not a refusal." },
        { typeof(CardObligationPromoteOutcome.ToolFailure), "ADR-0001: enforcement unavailable, never a refusal." },
        { typeof(CardQuestionAnswerOutcome.CardNotFound), "never card-addressed — no card resolved at the path." },
        { typeof(CardQuestionAnswerOutcome.LayoutMismatch), "never card-addressed — the path never anchored." },
        { typeof(CardQuestionAnswerOutcome.CardCorrupt), "a reported content problem, not a refusal." },
        { typeof(CardQuestionAnswerOutcome.ToolFailure), "ADR-0001: enforcement unavailable, never a refusal." },
        { typeof(CardQuestionDeferOutcome.CardNotFound), "never card-addressed — no card resolved at the path." },
        { typeof(CardQuestionDeferOutcome.LayoutMismatch), "never card-addressed — the path never anchored." },
        { typeof(CardQuestionDeferOutcome.CardCorrupt), "a reported content problem, not a refusal." },
        { typeof(CardQuestionDeferOutcome.ToolFailure), "ADR-0001: enforcement unavailable, never a refusal." },
        { typeof(CardRegisterDischargeOutcome.CardNotFound), "never card-addressed — no card resolved at the path." },
        { typeof(CardRegisterDischargeOutcome.LayoutMismatch), "never card-addressed — the path never anchored." },
        { typeof(CardRegisterDischargeOutcome.CardCorrupt), "a reported content problem, not a refusal." },
        { typeof(CardRegisterDischargeOutcome.ToolFailure), "ADR-0001: enforcement unavailable, never a refusal." },
        { typeof(CardRuleCompactOutcome.CardNotFound), "never card-addressed — no card resolved at the path." },
        { typeof(CardRuleCompactOutcome.LayoutMismatch), "never card-addressed — the path never anchored." },
        { typeof(CardRuleCompactOutcome.CardCorrupt), "a reported content problem, not a refusal." },
        { typeof(CardRuleCompactOutcome.ToolFailure), "ADR-0001: enforcement unavailable, never a refusal." },
        { typeof(CardRulePromoteOutcome.CardNotFound), "never card-addressed — no card resolved at the path." },
        { typeof(CardRulePromoteOutcome.LayoutMismatch), "never card-addressed — the path never anchored." },
        { typeof(CardRulePromoteOutcome.CardCorrupt), "a reported content problem, not a refusal." },
        { typeof(CardRulePromoteOutcome.ToolFailure), "ADR-0001: enforcement unavailable, never a refusal." },
        { typeof(CardSectionAuthorisationOutcome.CardNotFound), "never card-addressed — no card resolved at the path." },
        { typeof(CardSectionAuthorisationOutcome.LayoutMismatch), "never card-addressed — the path never anchored." },
        { typeof(CardSectionAuthorisationOutcome.CardCorrupt), "a reported content problem, not a refusal." },
        { typeof(CardSectionAuthorisationOutcome.ToolFailure), "ADR-0001: enforcement unavailable, never a refusal." },
        { typeof(CardSectionCloseOutcome.CardNotFound), "never card-addressed — no card resolved at the path." },
        { typeof(CardSectionCloseOutcome.LayoutMismatch), "never card-addressed — the path never anchored." },
        { typeof(CardSectionCloseOutcome.CardCorrupt), "a reported content problem, not a refusal." },
        { typeof(CardSectionCloseOutcome.ToolFailure), "ADR-0001: enforcement unavailable, never a refusal." },
        { typeof(CardSectionVerdictOutcome.CardNotFound), "never card-addressed — no card resolved at the path." },
        { typeof(CardSectionVerdictOutcome.LayoutMismatch), "never card-addressed — the path never anchored." },
        { typeof(CardSectionVerdictOutcome.CardCorrupt), "a reported content problem, not a refusal." },
        { typeof(CardSectionVerdictOutcome.ToolFailure), "ADR-0001: enforcement unavailable, never a refusal." },

        // CardWriteResult's pre-read cases — same "no card resolved yet" reasoning, plus
        // AlreadyExists: the write target's own path already existed, so a create-only write never
        // resolved an existing card to record a refusal against either.
        { typeof(CardWriteResult.NotFound), "never card-addressed — no card resolved at the path." },
        { typeof(CardWriteResult.AlreadyExists), "create-only write: the target already existed, so no existing card was resolved to record against." },
        { typeof(CardWriteResult.LayoutMismatch), "never card-addressed — the path never anchored." },
        { typeof(CardWriteResult.Corrupt), "a reported content problem, not a refusal." },
        { typeof(CardWriteResult.ToolFailure), "ADR-0001: enforcement unavailable, never a refusal." },

        // §9 block A3 ruling: card creation never resolves an existing card to record a refusal
        // against — both unions are established empty of ICardRefusalReason cases, entire.
        { typeof(CardCreateOutcome.AlreadyExists), "§9 block A3: card creation never resolves an existing card to record against." },
        { typeof(CardCreateOutcome.LayoutMismatch), "§9 block A3: card creation never resolves an existing card to record against." },
        { typeof(CardCreateOutcome.ScopeRefused), "§9 block A3: card creation never resolves an existing card to record against." },
        { typeof(CardCreateOutcome.ToolFailure), "ADR-0001: enforcement unavailable, never a refusal." },
        { typeof(CardFindingRecordOutcome.BlindSpotCardAlreadyExists), "§9 block A3: card creation never resolves an existing card to record against." },
        { typeof(CardFindingRecordOutcome.BlindSpotLayoutMismatch), "§9 block A3: card creation never resolves an existing card to record against." },
        { typeof(CardFindingRecordOutcome.FindingAlreadyExists), "§9 block A3: card creation never resolves an existing card to record against." },
        { typeof(CardFindingRecordOutcome.FindingLayoutMismatch), "§9 block A3: card creation never resolves an existing card to record against." },
        { typeof(CardFindingRecordOutcome.ToolFailure), "ADR-0001: enforcement unavailable, never a refusal." },

        // The two pre-lock RoleNotPermitted cases: checked at the top of a public, unlocked entry
        // point, genuinely before any card is resolved — unlike CardApprovalOutcome's and
        // CardSectionAuthorisationOutcome's post-lock RoleNotPermitted (§9 block B/remediation S3).
        { typeof(CardNitDispositionOutcome.RoleNotPermitted), "pre-lock: checked before any card is resolved, at the top of an unlocked entry point." },
        { typeof(CardRuleCompactOutcome.RoleNotPermitted), "pre-lock: checked before any card is resolved, at the top of an unlocked entry point." },

        // §7 block F: no rule ids were supplied at all — checked before any lock, no card resolved.
        { typeof(CardRuleCompactOutcome.EmptyAbsorbSet), "pre-lock: no rule ids supplied, checked before any card is resolved." },

        // The three pre-lock self-reference cases: resolved on caller-supplied path text alone,
        // before any lock is requested. Each has a Resolved*/named sibling that is the post-lock,
        // card-addressed, recording occurrence (already registered above).
        { typeof(CardDecisionSupersedeOutcome.SelfSupersession), "pre-lock: resolved on path text alone before any lock; see ResolvedSelfSupersession for the post-lock, recording sibling." },
        { typeof(CardRuleCompactOutcome.SelfAbsorption), "pre-lock: resolved on path text alone before any lock; see ResolvedSelfAbsorption for the post-lock, recording sibling." },
        { typeof(CardRuleCompactOutcome.DuplicateAbsorbedRule), "pre-lock: resolved on path text alone before any lock; see ResolvedDuplicateAbsorbedRule for the post-lock, recording sibling." },

        // The raised (obligation/decision) card's own target path failed to anchor — no card was
        // resolved there either, distinct from the dispositioned block card's own LayoutMismatch.
        { typeof(CardNitDispositionOutcome.RaisedCardLayoutMismatch), "never card-addressed — the raised card's own target path never anchored." },

        // ChangeArchiveOutcome entire: an architect-run, whole-change-directory verb, not a card any
        // agent pokes at repeatedly — the union's own OrphanedObligations doc comment states this
        // for every case in the union, CardsUnreadable included, as a deliberate carve-out rather
        // than a case-by-case accident.
        { typeof(ChangeArchiveOutcome.ChangeNotFound), "ChangeArchiveOutcome entire: an architect-run, whole-directory verb — see OrphanedObligations' own doc comment." },
        { typeof(ChangeArchiveOutcome.AlreadyArchived), "ChangeArchiveOutcome entire: an architect-run, whole-directory verb — see OrphanedObligations' own doc comment." },
        { typeof(ChangeArchiveOutcome.InvalidChangeName), "ChangeArchiveOutcome entire: an architect-run, whole-directory verb — see OrphanedObligations' own doc comment." },
        { typeof(ChangeArchiveOutcome.CardsUnreadable), "ChangeArchiveOutcome entire: an architect-run, whole-directory verb — see OrphanedObligations' own doc comment." },
        { typeof(ChangeArchiveOutcome.OrphanedObligations), "ChangeArchiveOutcome entire: an architect-run, whole-directory verb — its own doc comment names this exception explicitly." },
        { typeof(ChangeArchiveOutcome.ToolFailure), "ADR-0001: enforcement unavailable, never a refusal." },

        // §9 remediation, round two (S4): CardCommentResolveOutcome (comment resolve / comment
        // decline) and CardCommentPromoteOutcome (comment promote) — success cases and the four
        // categorical cases, same disposition as every other union above.
        { typeof(CardCommentResolveOutcome.Resolved), "the operation's own success case." },
        { typeof(CardCommentResolveOutcome.CardNotFound), "never card-addressed — no card resolved at the path." },
        { typeof(CardCommentResolveOutcome.LayoutMismatch), "never card-addressed — the path never anchored." },
        { typeof(CardCommentResolveOutcome.CardCorrupt), "a reported content problem, not a refusal." },
        { typeof(CardCommentResolveOutcome.ToolFailure), "ADR-0001: enforcement unavailable, never a refusal." },
        { typeof(CardCommentPromoteOutcome.Promoted), "the operation's own success case." },
        { typeof(CardCommentPromoteOutcome.CardNotFound), "never card-addressed — no card resolved at the path." },
        { typeof(CardCommentPromoteOutcome.LayoutMismatch), "never card-addressed — the path never anchored." },
        { typeof(CardCommentPromoteOutcome.CardCorrupt), "a reported content problem, not a refusal." },
        { typeof(CardCommentPromoteOutcome.ToolFailure), "ADR-0001: enforcement unavailable, never a refusal." },

        // The raised (question/decision) card's own target path either already existed or failed to
        // anchor — no existing card was resolved there either way, the same §9 block A3 disposition
        // CardFindingRecordOutcome's own BlindSpotCardAlreadyExists/BlindSpotLayoutMismatch have.
        { typeof(CardCommentPromoteOutcome.RaisedCardAlreadyExists), "§9 block A3: card creation never resolves an existing card to record against." },
        { typeof(CardCommentPromoteOutcome.RaisedCardLayoutMismatch), "§9 block A3: card creation never resolves an existing card to record against." },

        // §13: comment add (CardCommentAppendOutcome) — its own success case and the same three
        // categorical dispositions every other comment sub-verb's union carries.
        { typeof(CardCommentAppendOutcome.Added), "the operation's own success case." },
        { typeof(CardCommentAppendOutcome.CardNotFound), "never card-addressed — no card resolved at the path." },
        { typeof(CardCommentAppendOutcome.LayoutMismatch), "never card-addressed — the path never anchored." },
        { typeof(CardCommentAppendOutcome.CardCorrupt), "a reported content problem, not a refusal." },
        { typeof(CardCommentAppendOutcome.ToolFailure), "ADR-0001: enforcement unavailable, never a refusal." },

        // §13: block base (CardBlockRecordBaseOutcome) — its own success case and the same three
        // categorical dispositions every other single-field recorder's union carries.
        { typeof(CardBlockRecordBaseOutcome.Recorded), "the operation's own success case." },
        { typeof(CardBlockRecordBaseOutcome.CardNotFound), "never card-addressed — no card resolved at the path." },
        { typeof(CardBlockRecordBaseOutcome.LayoutMismatch), "never card-addressed — the path never anchored." },
        { typeof(CardBlockRecordBaseOutcome.CardCorrupt), "a reported content problem, not a refusal." },
        { typeof(CardBlockRecordBaseOutcome.ToolFailure), "ADR-0001: enforcement unavailable, never a refusal." },
    };

    /// <summary>Every concrete type in the product assembly that implements
    /// <see cref="ICardRefusalReason"/> — the reflected side of the bijection.</summary>
    private static IReadOnlyList<Type> ReflectedImplementors() =>
        [.. typeof(ICardRefusalReason).Assembly.GetTypes()
            .Where(static t => t is { IsClass: true, IsAbstract: false } && typeof(ICardRefusalReason).IsAssignableFrom(t))
            .OrderBy(static t => t.FullName, StringComparer.Ordinal)];

    /// <summary>
    /// §9 remediation S2. Every card-store outcome union, discovered without naming a single one:
    /// a distinct, non-generic return type of a <see cref="CardStore"/> method, carrying the closed-
    /// union idiom used everywhere in <c>Cards/</c> (abstract, with an abstract <c>Match&lt;TResult&gt;</c>
    /// declared directly on the type), that also declares a nested <c>ToolFailure</c> case. The
    /// second filter is what keeps a domain value-union that happens to be returned somewhere on
    /// <see cref="CardStore"/> (<see cref="CardScope"/> is the one this surfaced) out of the
    /// universe — every genuine outcome union in this codebase carries a <c>ToolFailure</c> case by
    /// convention (ADR-0001: enforcement-unavailable is always modelled), and no value union does.
    /// This also correctly drops <see cref="CardFileParseResult"/>: its <c>Success</c>/<c>Failure</c>
    /// is a lower-level parse primitive every higher-level union's own <c>CardCorrupt</c> case
    /// already re-reports, never itself a CLI-facing outcome.
    /// </summary>
    private static IReadOnlyList<Type> ReflectedOutcomeUnions()
    {
        static bool IsClosedUnionWithToolFailure(Type t) =>
            t is { IsClass: true, IsAbstract: true }
            && t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Any(m => m.Name == "Match" && m.IsAbstract)
            && t.GetNestedType("ToolFailure", BindingFlags.Public | BindingFlags.NonPublic) is { } toolFailure
            && toolFailure.BaseType == t;

        var returnTypes = typeof(CardStore)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.ReturnType)
            .Where(t => !t.IsGenericParameter)
            .Distinct();

        return [.. returnTypes.Where(IsClosedUnionWithToolFailure).OrderBy(static t => t.FullName, StringComparer.Ordinal)];
    }

    /// <summary>Every concrete case (a sealed record nested directly inside one of
    /// <see cref="ReflectedOutcomeUnions"/>, deriving from it) across every discovered union — the
    /// full refusal-shaped universe <see cref="RefusalShapedUniverseIsFullyAccountedFor"/> checks:
    /// every one of these must be either an <see cref="ICardRefusalReason"/> implementor or a key in
    /// <see cref="Exclusions"/>, never both, never neither.</summary>
    private static IReadOnlyList<Type> ReflectedOutcomeUnionCases() =>
        [.. ReflectedOutcomeUnions()
            .SelectMany(union => union.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .Where(nested => nested is { IsClass: true, IsAbstract: false } && nested.BaseType == union))
            .OrderBy(static t => t.FullName, StringComparer.Ordinal)];

    /// <summary>
    /// §9 remediation S2 — the closure the base gate could not see: <see cref="ICardRefusalReason"/>
    /// is a self-reporting universe, so a case that never declares it was invisible to
    /// <see cref="RefusalCoverageIsExactlyTheReflectedSet"/> rather than failing it (four
    /// <see cref="CardBlockedByOutcome"/> cases shipped through seven blocks this way). This test
    /// checks the universe <see cref="ReflectedOutcomeUnionCases"/> discovers independently of the
    /// interface: every case in it is in exactly one of {<see cref="ICardRefusalReason"/>
    /// implementor, <see cref="Exclusions"/> key} — never both (a stale exclusion for a case that
    /// now records), never neither (the exact failure mode this closes) — and every
    /// <see cref="Exclusions"/> entry carries a non-empty reason and still names a real, currently
    /// discovered case.
    /// </summary>
    [Fact]
    public void RefusalShapedUniverseIsFullyAccountedFor()
    {
        var discovered = ReflectedOutcomeUnionCases();
        var discoveredSet = new HashSet<Type>(discovered);

        var unaccountedFor = discovered
            .Where(t => !typeof(ICardRefusalReason).IsAssignableFrom(t) && !Exclusions.ContainsKey(t))
            .ToList();
        Assert.True(
            unaccountedFor.Count == 0,
            "The following refusal-shaped case(s) neither implement ICardRefusalReason nor appear in "
            + "RefusalCoverageGateTests.Exclusions — either implement the interface and register a "
            + "proving test, or add an Exclusions entry stating why this case does not record: "
            + string.Join(", ", unaccountedFor.Select(static t => t.FullName)));

        var doublyClassified = discovered
            .Where(t => typeof(ICardRefusalReason).IsAssignableFrom(t) && Exclusions.ContainsKey(t))
            .ToList();
        Assert.True(
            doublyClassified.Count == 0,
            "The following case(s) both implement ICardRefusalReason and appear in Exclusions — remove "
            + "the stale Exclusions entry now that the case records: "
            + string.Join(", ", doublyClassified.Select(static t => t.FullName)));

        var staleExclusions = Exclusions.Keys.Where(t => !discoveredSet.Contains(t)).ToList();
        Assert.True(
            staleExclusions.Count == 0,
            "The following Exclusions entries no longer name a case discovered in a live outcome union — "
            + "remove or update them: "
            + string.Join(", ", staleExclusions.Select(static t => t.FullName)));

        var emptyReasons = Exclusions.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList();
        Assert.True(
            emptyReasons.Count == 0,
            "The following Exclusions entries carry no reason — state why the case does not record: "
            + string.Join(", ", emptyReasons.Select(static t => t.FullName)));
    }

    [Fact]
    public void RefusalCoverageIsExactlyTheReflectedSet()
    {
        var reflected = ReflectedImplementors();
        var missing = reflected.Where(t => !Registry.ContainsKey(t)).ToList();
        var stale = Registry.Keys.Where(t => !typeof(ICardRefusalReason).IsAssignableFrom(t) || t.IsAbstract).ToList();

        Assert.True(
            missing.Count == 0,
            "The following ICardRefusalReason case(s) have no entry in RefusalCoverageGateTests.Registry — "
            + "add one mapping the case to a test that provokes it and asserts a CardRefusalEntry landed on "
            + "the card (Assert.Single(read.Refusals), matching Rule/Remedy against the outcome's own values): "
            + string.Join(", ", missing.Select(static t => t.FullName)));

        Assert.True(
            stale.Count == 0,
            "The following Registry entries no longer name a concrete ICardRefusalReason implementor — "
            + "remove or update them: "
            + string.Join(", ", stale.Select(static t => t.FullName)));
    }

    [Fact]
    public void EveryRegisteredCoverageTestIsARealActiveTest()
    {
        var problems = new List<string>();
        foreach (var (caseType, (testClass, methodName)) in Registry)
        {
            var method = testClass.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (method is null)
            {
                problems.Add($"{caseType.FullName}: no method '{methodName}' found on {testClass.FullName}.");
                continue;
            }

            var fact = method.GetCustomAttribute<FactAttribute>(inherit: true);
            if (fact is null)
            {
                problems.Add($"{caseType.FullName}: {testClass.Name}.{methodName} carries no [Fact]/[Theory].");
                continue;
            }

            if (fact.Skip is not null)
            {
                problems.Add($"{caseType.FullName}: {testClass.Name}.{methodName} is Skip-ed ('{fact.Skip}') — it proves nothing while skipped.");
            }
        }

        Assert.True(problems.Count == 0, "Registered coverage test(s) are not real, active tests:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void EveryRegisteredCoverageTestActuallyInspectsTheCardsRefusalHistory()
    {
        var thisFileDirectory = Path.GetDirectoryName(ThisFilePath())!;
        var problems = new List<string>();

        foreach (var (caseType, (testClass, methodName)) in Registry)
        {
            var sourcePath = Path.Combine(thisFileDirectory, testClass.Name + ".cs");
            if (!File.Exists(sourcePath))
            {
                problems.Add($"{caseType.FullName}: no source file '{testClass.Name}.cs' next to the gate — the TestClass/TestMethod naming convention this gate relies on assumes one file per test class.");
                continue;
            }

            var body = ExtractMethodBody(File.ReadAllText(sourcePath), methodName, sourcePath, caseType);
            if (body is null)
            {
                problems.Add($"{caseType.FullName}: could not find 'void {methodName}(' in {testClass.Name}.cs.");
                continue;
            }

            var totalOccurrences = CountOccurrences(body, ".Refusals");
            if (totalOccurrences == 0)
            {
                problems.Add(
                    $"{caseType.FullName}: {testClass.Name}.{methodName} never inspects '.Refusals' — it may assert the "
                    + "outcome's type but not that a CardRefusalEntry was actually recorded on the card. Read the card "
                    + "back and assert against read.Refusals.");
                continue;
            }

            // §9 block C review (a live instance, not a hypothetical): a registry entry can point
            // at a real, unskipped test whose only '.Refusals' statement is Assert.Empty(...) — an
            // anchor-mismatch/pre-lock fixture proving the case does NOT record, which still
            // contains the substring '.Refusals' and would otherwise pass the check above while
            // asserting the opposite of coverage. Reject a match where every occurrence is wrapped
            // in Assert.Empty(...) — at least one occurrence must assert something other than
            // absence (Assert.Single, Assert.NotEmpty, indexing into the sequence, etc.). This is a
            // targeted closure of that one shape, not a general proof the test provokes the case —
            // the architect's ruling is explicit that hand-mapped correctness stays the gate's
            // irreducible seam.
            var emptyOnlyOccurrences = EmptyAssertionPattern.Matches(body).Count;
            if (emptyOnlyOccurrences == totalOccurrences)
            {
                problems.Add(
                    $"{caseType.FullName}: {testClass.Name}.{methodName}'s only use(s) of '.Refusals' "
                    + $"{(totalOccurrences == 1 ? "is" : "are")} Assert.Empty(...) — that proves the case does NOT "
                    + "record, not that it does. Point the registry at a test asserting Assert.Single(read.Refusals) "
                    + "(or equivalent) in the ordinary, anchored case.");
            }
        }

        Assert.True(problems.Count == 0, "Registered coverage test(s) do not prove recording:\n" + string.Join("\n", problems));
    }

    /// <summary>Extracts the brace-matched body of the first (and, per the assertion below,
    /// only) <c>void {methodName}(</c> declaration in <paramref name="sourceText"/>. Plain
    /// brace-counting is safe here: the test project contains no <c>{{</c>/<c>}}</c> escaped-brace
    /// interpolation literals (confirmed when this gate was written; a future one would make an
    /// affected method's coverage check spuriously fail, not silently pass).</summary>
    /// <summary>Non-overlapping occurrences of <paramref name="needle"/> in <paramref name="haystack"/>.</summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string? ExtractMethodBody(string sourceText, string methodName, string sourcePath, Type caseType)
    {
        var signature = new Regex($@"\bvoid\s+{Regex.Escape(methodName)}\s*\(");
        var matches = signature.Matches(sourceText);
        Assert.True(
            matches.Count <= 1,
            $"{caseType.FullName}: '{methodName}' appears as a method signature {matches.Count} times in {Path.GetFileName(sourcePath)} — "
            + "the registry entry is ambiguous; rename or disambiguate.");

        if (matches.Count == 0)
        {
            return null;
        }

        var braceStart = sourceText.IndexOf('{', matches[0].Index + matches[0].Length);
        Assert.True(braceStart >= 0, $"{caseType.FullName}: found '{methodName}(' with no opening brace after it.");

        var depth = 0;
        for (var i = braceStart; i < sourceText.Length; i++)
        {
            if (sourceText[i] == '{')
            {
                depth++;
            }
            else if (sourceText[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return sourceText[braceStart..(i + 1)];
                }
            }
        }

        Assert.Fail($"{caseType.FullName}: '{methodName}''s body brace was never closed — malformed extraction.");
        return null;
    }

    private static string ThisFilePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
