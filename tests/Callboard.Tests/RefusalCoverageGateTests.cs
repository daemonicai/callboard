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
        { typeof(CardBlockTransitionOutcome.BaseImmutable), (typeof(CardBlockTransitionTests), "ApplyBlockTransition_BaseCannotChangeAcrossRemediationRounds") },
        { typeof(CardBlockTransitionOutcome.BaseNotRecorded), (typeof(CardBlockTransitionTests), "ApplyBlockTransition_BriefWithNoBaseRecordedAndNoneSupplied_Refuses") },
        { typeof(CardBlockTransitionOutcome.NotABlockCard), (typeof(CardBlockTransitionTests), "ApplyBlockTransition_TargetIsNotABlockCard_Refuses") },
        { typeof(CardBlockTransitionOutcome.RoundDisagreesWithHistory), (typeof(RoundAgreesWithHistoryTests), "ApplyBlockTransition_StoredRoundAheadOfHistory_Refuses_NamesBothFigures_AltersNeither") },
        { typeof(CardBlockTransitionOutcome.UndefinedTransition), (typeof(CardBlockTransitionTests), "ApplyBlockTransition_UndefinedTransition_RecordsExactlyOneRefusal_AndChangesNothingElse") },
        { typeof(CardBlockTransitionOutcome.UndispositionedNits), (typeof(CommandDispatcherNitTests), "BlockTransition_ChangesRequested_UndispositionedNit_Refuses") },
        { typeof(CardBlockTransitionOutcome.UnresolvedThreadsAddressedToActor), (typeof(CommandDispatcherNitTests), "BlockTransition_ChangesRequested_UnresolvedThreadAddressedToActor_Refuses_AndRecordsIt") },
        { typeof(CardBlockTransitionOutcome.BlockedByOpenProductOwnerQuestion), (typeof(CommandDispatcherBlockTransitionTests), "BlockTransition_BlockedByOpenProductOwnerQuestion_Refuses_AndRecordsTheRefusal") },
        { typeof(CardDecisionSupersedeOutcome.InvalidStatus), (typeof(CardDecisionSupersedeTests), "SupersedeDecision_SupersedingStatusIsAFlowState_Refuses_AndRecords") },
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
        { typeof(CardRegisterDischargeOutcome.InvalidStatus), (typeof(CardRegisterDischargeTests), "DischargeRegisterCard_StatusIsAFlowState_RefusesRatherThanTreatingItAsOpen") },
        { typeof(CardRegisterDischargeOutcome.NotARegisterCard), (typeof(CardRegisterDischargeTests), "DischargeRegisterCard_TargetIsNotARegisterCard_Refuses") },
        { typeof(CardRuleCompactOutcome.AbsorbedAlreadyDischarged), (typeof(CardRuleCompactTests), "CompactRules_TargetAlreadyDischarged_Refuses_NotARe_Absorption") },
        { typeof(CardRuleCompactOutcome.FamilyAlreadyDischarged), (typeof(CardRuleCompactTests), "CompactRules_ThreeNodeCycle_TheClosingLinkRefuses") },
        { typeof(CardRuleCompactOutcome.InvalidStatus), (typeof(CardRuleCompactTests), "CompactRules_FamilyStatusIsAFlowState_Refuses_AndRecords") },
        { typeof(CardRuleCompactOutcome.NotARuleCard), (typeof(CardRuleCompactTests), "CompactRules_FamilyIsNotARule_Refuses") },
        { typeof(CardRuleCompactOutcome.ResolvedDuplicateAbsorbedRule), (typeof(CardRuleCompactTests), "CompactRules_TwoAbsorbedPathsShareAnId_RefusesAsResolvedDuplicateAbsorbedRule_AndRecords") },
        { typeof(CardRuleCompactOutcome.ResolvedSelfAbsorption), (typeof(CardRuleCompactTests), "CompactRules_AbsorbedCardSharesTheFamilysId_RefusesAsResolvedSelfAbsorption_AndRecords") },
        { typeof(CardRulePromoteOutcome.AlreadyRepositoryScoped), (typeof(CardRulePromoteTests), "PromoteRule_AlreadyRepositoryScoped_Refuses_WithNothingMoved") },
        { typeof(CardRulePromoteOutcome.InvalidStatus), (typeof(CardRulePromoteTests), "PromoteRule_StatusIsAFlowState_Refuses") },
        { typeof(CardRulePromoteOutcome.NotARuleCard), (typeof(CardRulePromoteTests), "PromoteRule_NonRuleCard_Refuses") },
        { typeof(CardRulePromoteOutcome.NotChangeScoped), (typeof(CardRulePromoteTests), "PromoteRule_CapabilityScoped_Refuses_AsNotChangeScoped_AndRecords") },
        { typeof(CardRulePromoteOutcome.TargetAlreadyExists), (typeof(CardRulePromoteTests), "PromoteRule_TargetBasenameAlreadyClaimedInRegister_Refuses_WithNothingMoved") },
        { typeof(CardObligationPromoteOutcome.AlreadyRepositoryScoped), (typeof(CardObligationPromoteTests), "PromoteObligation_AlreadyRepositoryScoped_Refuses_AndRecordsTheRefusal") },
        { typeof(CardObligationPromoteOutcome.InvalidStatus), (typeof(CardObligationPromoteTests), "PromoteObligation_StatusIsAFlowState_Refuses_AndRecordsTheRefusal") },
        { typeof(CardObligationPromoteOutcome.NotAnObligationCard), (typeof(CardObligationPromoteTests), "PromoteObligation_NonObligationCard_Refuses_AndRecordsTheRefusal") },
        { typeof(CardObligationPromoteOutcome.NotChangeScoped), (typeof(CardObligationPromoteTests), "PromoteObligation_SectionScoped_RefusesAsNotChangeScoped_AndRecordsTheRefusal") },
        { typeof(CardObligationPromoteOutcome.TargetAlreadyExists), (typeof(CardObligationPromoteTests), "PromoteObligation_TargetBasenameAlreadyClaimedInRegister_Refuses_WithNothingMoved_AndRecords") },
        { typeof(CardObligationDeclineOutcome.AlreadyDischarged), (typeof(CardObligationDeclineTests), "DeclineObligation_AlreadyDischarged_Refuses_AndDoesNotOverwriteTheFirstDisposition") },
        { typeof(CardObligationDeclineOutcome.InvalidStatus), (typeof(CardObligationDeclineTests), "DeclineObligation_StatusIsAFlowState_Refuses_AndRecordsTheRefusal") },
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
        { typeof(CardSectionAuthorisationOutcome.NotASectionCard), (typeof(CardSectionAuthorisationTests), "RecordSectionAuthorisation_TargetIsNotASectionCard_Refuses") },
        { typeof(CardSectionAuthorisationOutcome.NotAtBound), (typeof(CardSectionAuthorisationTests), "RecordSectionAuthorisation_OnABrandNewSection_RefusesWithNotAtBound") },
        { typeof(CardSectionVerdictOutcome.FindingAlreadyOwned), (typeof(CommandDispatcherSectionVerdictRemediationTests), "FindingNew_KeyAlreadyOwnedOnDisk_Refuses_CreatesNoSecondCard") },
        { typeof(CardSectionVerdictOutcome.NewFindingCardAlreadyExists), (typeof(CommandDispatcherSectionVerdictRemediationTests), "FindingNew_TargetFileAlreadyExistsOnDisk_Refuses_AndRecordsAgainstTheSection") },
        { typeof(CardSectionVerdictOutcome.NotASectionCard), (typeof(CardSectionVerdictTests), "RecordSectionVerdict_TargetIsNotASectionCard_Refuses_AndRecordsTheRefusal") },
        { typeof(CardSectionVerdictOutcome.RecurringFindingNotApproved), (typeof(CommandDispatcherSectionVerdictRemediationTests), "FindingRecurred_TargetNotApproved_Refuses") },
        { typeof(CardSectionVerdictOutcome.RecurringFindingTargetsTaskImplementingBlock), (typeof(CommandDispatcherSectionVerdictRemediationTests), "FindingRecurred_TargetsATaskImplementingBlock_Refuses") },
        { typeof(CardSectionVerdictOutcome.RecurringTargetNotFound), (typeof(CardSectionVerdictTests), "RecordSectionVerdict_RecurringTargetDoesNotExist_Refuses_AndRecordsAgainstTheSection") },
        { typeof(CardSectionVerdictOutcome.RemediationBoundExceeded), (typeof(CardSectionVerdictTests), "RecordSectionVerdict_ThirdRequestChangesWithoutAuthorisation_Refuses_AndApproveDoesNotAdvanceTheBound") },
        { typeof(CardSectionVerdictOutcome.RoundDisagreesWithHistory), (typeof(CardSectionVerdictTests), "RecordSectionVerdict_RecurringTargetRoundDisagreesWithHistory_Refuses_AndRecordsAgainstTheSection") },
        { typeof(CardWriteResult.RoundDisagreesWithHistory), (typeof(CardOwnershipTransferTests), "TransferOwnership_BlockCardWithDisagreeingRound_Refuses_AndRecordsAgainstTheCard") },
    };

    /// <summary>Every concrete type in the product assembly that implements
    /// <see cref="ICardRefusalReason"/> — the reflected side of the bijection.</summary>
    private static IReadOnlyList<Type> ReflectedImplementors() =>
        [.. typeof(ICardRefusalReason).Assembly.GetTypes()
            .Where(static t => t is { IsClass: true, IsAbstract: false } && typeof(ICardRefusalReason).IsAssignableFrom(t))
            .OrderBy(static t => t.FullName, StringComparer.Ordinal)];

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
