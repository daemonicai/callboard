using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// §8 block C at the CLI boundary: <c>block recertify</c> — review-certification's "Recertification
/// re-asserts an existing claim set" / "Recertification is bounded". Every call in this file routes
/// through <see cref="TempGitRepo.Clock"/>'s <see cref="AdvancingClock"/>, the same pattern
/// <c>CommandDispatcherNitTests</c> established (§8 block B remediation) — a fixed clock cannot
/// distinguish "the current approval" from an earlier, already-superseded one, since every event
/// would share one timestamp. This block computes exactly that kind of property twice (every claim
/// has an outcome; how many recertifications since the current approval), so tests here cross a
/// genuine round/approval boundary rather than asserting against hand-seeded state.
/// </summary>
public sealed class CommandDispatcherBlockRecertifyTests
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BlockRecertify_EveryClaimAsserted_Succeeds_ReStampsReviewedStateWithoutIncrementingRound()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo, "b-0001", "B-0001", BlockFlowState.InReview);
        var claimIds = Approve(repo, path, "B-0001", "commit-abc", "claim one,claim two");
        RecordGateGreen(repo, path, "build");
        RaiseAndDispositionNit(repo, "B-0001", "src/Foo.cs");
        const string amendedState = "commit-def + uncommitted working tree";
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "block", "recertify", "--id", "B-0001", "--role", "reviewer", "--state", amendedState,
                "--assert", claimIds[0], "--assert", claimIds[1], "--changed", "src/Foo.cs", "--change", ChangeName,
            ],
            output, repo);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal(amendedState, result.GetProperty("reviewedState").GetString());
        Assert.False(result.GetProperty("transitioned").GetBoolean());
        Assert.Empty(result.GetProperty("refusedClaimIds").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("notice").GetString()));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("approved", read.Frontmatter.Status);
        Assert.Equal(amendedState, read.BlockFields.ReviewedState);
        Assert.Equal(1, read.BlockFields.Round);
        Assert.Single(read.Transitions); // only the original 'approve' — no new transition entry
        var recertificationComment = Assert.Single(read.Comments, c => c.IsRecertification);
        Assert.Equal(CardOwner.Reviewer, recertificationComment.Author);
    }

    [Fact]
    public void BlockRecertify_OneClaimRefused_ReturnsToBriefed_IncrementsRoundOnce_LeavesReviewedStateExactlyAsItWas()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo, "b-0002", "B-0002", BlockFlowState.InReview);
        const string certifiedState = "commit-abc";
        var claimIds = Approve(repo, path, "B-0002", certifiedState, "claim one,claim two,claim three");
        RecordGateGreen(repo, path, "build");
        RaiseAndDispositionNit(repo, "B-0002", "src/Bar.cs");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "block", "recertify", "--id", "B-0002", "--role", "reviewer", "--state", "commit-def",
                "--assert", claimIds[0], "--assert", claimIds[2], "--refuse", claimIds[1], "--changed", "src/Bar.cs", "--change", ChangeName,
            ],
            output, repo);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("transitioned").GetBoolean());
        // review-certification's own scenario: all three outcomes recorded.
        Assert.Equal(
            new[] { claimIds[0], claimIds[2] },
            result.GetProperty("assertedClaimIds").EnumerateArray().Select(static e => e.GetString()).ToArray());
        Assert.Equal([claimIds[1]], result.GetProperty("refusedClaimIds").EnumerateArray().Select(static e => e.GetString()).ToArray());
        Assert.Equal(certifiedState, result.GetProperty("reviewedState").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("briefed", read.Frontmatter.Status);
        Assert.Equal(certifiedState, read.BlockFields.ReviewedState); // untouched, not cleared/blanked
        Assert.Equal(2, read.BlockFields.Round);
        var transition = read.Transitions.Last();
        Assert.Equal("recertification-refused", transition.Name);
        Assert.Equal(BlockFlowState.Approved, transition.From);
        Assert.Equal(BlockFlowState.Briefed, transition.To);
    }

    [Fact]
    public void BlockRecertify_MissingClaimOutcome_Refuses_NamesTheOmittedClaim_AndLeavesTheCardByteIdentical()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo, "b-0003", "B-0003", BlockFlowState.InReview);
        var claimIds = Approve(repo, path, "B-0003", "commit-abc", "claim one,claim two");
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "block", "recertify", "--id", "B-0003", "--role", "reviewer", "--state", "commit-def", "--assert", claimIds[0],
                "--changed", "src/Whatever.cs", "--change", ChangeName,
            ],
            output, repo);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("missing-claim-outcome", refusal.GetProperty("code").GetString());
        Assert.Contains(claimIds[1], refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(claimIds[0], refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void BlockRecertify_UnknownClaimId_Refuses_AndLeavesTheCardByteIdentical()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo, "b-0004", "B-0004", BlockFlowState.InReview);
        Approve(repo, path, "B-0004", "commit-abc", "claim one");
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "block", "recertify", "--id", "B-0004", "--role", "reviewer", "--state", "commit-def", "--assert", "not-a-real-claim-id",
                "--changed", "src/Whatever.cs", "--change", ChangeName,
            ],
            output, repo);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("unknown-claim-id", refusal.GetProperty("code").GetString());
        Assert.Contains("not-a-real-claim-id", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void BlockRecertify_NotCurrentlyApproved_Refuses()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo, "b-0005", "B-0005", BlockFlowState.InReview);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "recertify", "--id", "B-0005", "--role", "reviewer", "--state", "commit-def", "--changed", "src/Whatever.cs", "--change", ChangeName],
            output, repo);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("not-currently-approved", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        Assert.Equal("in-review", AssertParseSuccess(CardStore.ReadCard(path)).Frontmatter.Status);
    }

    // review-certification: "Approval is role-bounded" — reviewer/supervisor only (8.13's
    // recertification half). Asserted with 'worker', a role neither IsApprovingRole nor
    // CardOwnerWireFormat's own parse fallback defaults to (§7 item E discipline).
    [Fact]
    public void BlockRecertify_NonApprovingRole_Refuses_AndLeavesTheCardByteIdentical()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo, "b-0006", "B-0006", BlockFlowState.InReview);
        var claimIds = Approve(repo, path, "B-0006", "commit-abc", "claim one");
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "block", "recertify", "--id", "B-0006", "--role", "worker", "--state", "commit-def", "--assert", claimIds[0],
                "--changed", "src/Whatever.cs", "--change", ChangeName,
            ],
            output, repo);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("role-not-permitted", refusal.GetProperty("code").GetString());
        Assert.Contains("reviewer", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("supervisor", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void BlockRecertify_WrongCardKind_Refuses()
    {
        using var repo = new TempGitRepo();
        var directory = Path.Combine(repo.Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        var questionPath = Path.Combine(directory, "q-0001.md");
        var frontmatter = new CardFrontmatter(
            "Q-0001", CardKind.Question, "A question", "open", CardOwner.Architect, CardScope.Change, "8", FixedNow, FixedNow);
        File.WriteAllText(questionPath, CardFileWriter.Serialize(new CardFile(frontmatter, "Body.", [], [])), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "recertify", "--id", "Q-0001", "--role", "reviewer", "--state", "commit-def", "--changed", "src/Whatever.cs", "--change", ChangeName],
            output, repo);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("wrong-card-kind", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockRecertify_EmptyState_Refuses()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo, "b-0007", "B-0007", BlockFlowState.InReview);
        Approve(repo, path, "B-0007", "commit-abc", "claim one");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "recertify", "--id", "B-0007", "--role", "reviewer", "--state", "   ", "--change", ChangeName],
            output, repo);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("state-required", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockRecertify_MissingId_RefusesWithMissingArgumentCode()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = RunInRepo(["block", "recertify", "--role", "reviewer", "--state", "commit-def"], output, repo);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockRecertify_BlankAssertValue_Refuses()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "recertify", "--id", "B-0008", "--role", "reviewer", "--state", "commit-def", "--assert", "   ", "--change", ChangeName],
            output, repo);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("invalid-claim-id", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockRecertify_SameClaimIdAssertedAndRefused_Refuses()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "block", "recertify", "--id", "B-0009", "--role", "reviewer", "--state", "commit-def",
                "--assert", "claim-1", "--refuse", "claim-1", "--change", ChangeName,
            ],
            output, repo);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("conflicting-claim-outcome", refusal.GetProperty("code").GetString());
        Assert.Contains("claim-1", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    // §8 block A's own one-door discipline, extended: a bare transition through
    // 'recertification-refused' would move a block back to 'briefed' with no claim genuinely
    // refused — refused outright at parse, the same as 'approve' and 'fix-before-land'.
    [Fact]
    public void BlockTransition_RecertificationRefused_Refuses_AndLeavesTheCardByteIdentical()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo, "b-0010", "B-0010", BlockFlowState.InReview);
        Approve(repo, path, "B-0010", "commit-abc", "claim one");
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "transition", path, "recertification-refused", "--role", "reviewer", "--change", ChangeName],
            output, repo);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("recertification-refused-via-transition-refused", refusal.GetProperty("code").GetString());
        Assert.Contains("block recertify", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // review-certification: "The system SHALL permit at most one recertification per approval"
    // (8.10) — the first half: a second recertification attempt against the SAME approval is
    // refused, even though it names the same claim the first, successful call already asserted.
    [Fact]
    public void BlockRecertify_SecondAttemptAgainstTheSameApproval_Refuses()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo, "b-0011", "B-0011", BlockFlowState.Drafting);

        EnterInReview(repo, path, firstRound: true);
        var claimIds = Approve(repo, path, "B-0011", "commit-round1", "round one claim");
        RecordGateGreen(repo, path, "build");
        RaiseAndDispositionNit(repo, "B-0011", "src/Foo.cs");

        Assert.Equal(CommandDispatcher.SuccessExitCode, RunInRepo(
            [
                "block", "recertify", "--id", "B-0011", "--role", "reviewer", "--state", "commit-round1-amended",
                "--assert", claimIds[0], "--changed", "src/Foo.cs", "--change", ChangeName,
            ],
            new StringWriter(), repo));
        Assert.Equal("approved", AssertParseSuccess(CardStore.ReadCard(path)).Frontmatter.Status);
        var before = File.ReadAllBytes(path);

        var output = new StringWriter();
        var exitCode = RunInRepo(
            [
                "block", "recertify", "--id", "B-0011", "--role", "reviewer", "--state", "commit-round1-amended-again",
                "--assert", claimIds[0], "--changed", "src/Foo.cs", "--change", ChangeName,
            ],
            output, repo);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("recertification-already-performed", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // 8.10's second half, and the property this section has already gotten wrong twice (§8 block
    // B's own remediation, twice): a block recertified once, refused back to briefed, rebuilt and
    // approved again — on a NEW approval — gets a fresh recertification, scoped to the new
    // approval's own (genuinely different) claim set rather than the card's whole history.
    //
    // Demonstrated to fail against the un-fixed logic: reverting CardStore.
    // RecordRecertificationUnderExistingLock's `approvedAt` scan to DateTimeOffset.MinValue (i.e.
    // scoping "has this approval already been recertified" over the card's whole comment history
    // rather than "since the current approval") makes the final call in this test fail — the stale
    // recertification comment left over from the FIRST approval's refused recertification is
    // wrongly read as already covering the second approval, and the final recertify is refused
    // with 'recertification-already-performed' instead of succeeding. Confirmed by hand before
    // landing this block: applying exactly that revert and running this test alone reproduces the
    // failure described above; reverting the change restores the pass.
    [Fact]
    public void BlockRecertify_AfterARefusalAndAFreshApproval_PermitsANewRecertificationScopedToTheNewClaims()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo, "b-0012", "B-0012", BlockFlowState.Drafting);

        EnterInReview(repo, path, firstRound: true);
        var round1ClaimIds = Approve(repo, path, "B-0012", "commit-round1", "round one claim");
        RecordGateGreen(repo, path, "build");
        RaiseAndDispositionNit(repo, "B-0012", "src/Round1.cs");
        Assert.Equal(CommandDispatcher.SuccessExitCode, RunInRepo(
            [
                "block", "recertify", "--id", "B-0012", "--role", "reviewer", "--state", "commit-round1-refused",
                "--refuse", round1ClaimIds[0], "--changed", "src/Round1.cs", "--change", ChangeName,
            ],
            new StringWriter(), repo));
        var afterRefusal = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("briefed", afterRefusal.Frontmatter.Status);
        Assert.Equal(2, afterRefusal.BlockFields.Round);

        EnterInReview(repo, path, firstRound: false);
        var round2ClaimIds = Approve(repo, path, "B-0012", "commit-round2", "round two claim");
        Assert.NotEqual(round1ClaimIds[0], round2ClaimIds[0]);

        // Round 2's own evidence — the round 1 gate result stays on the card (retained, not
        // overwritten) but is stamped round 1, so it is not evidence this round's gates are green;
        // likewise round 1's dispositioned nit site is out of the round 2 bound (roundStart moves
        // to the 'recertification-refused' transition above, which also lands on 'briefed').
        RecordGateGreen(repo, path, "build");
        RaiseAndDispositionNit(repo, "B-0012", "src/Round2.cs");

        var output = new StringWriter();
        var exitCode = RunInRepo(
            [
                "block", "recertify", "--id", "B-0012", "--role", "reviewer", "--state", "commit-round2-amended",
                "--assert", round2ClaimIds[0], "--changed", "src/Round2.cs", "--change", ChangeName,
            ],
            output, repo);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("commit-round2-amended", doc.RootElement.GetProperty("result").GetProperty("reviewedState").GetString());
        Assert.Equal("approved", AssertParseSuccess(CardStore.ReadCard(path)).Frontmatter.Status);
    }

    // §8 block D (8.11): the two mechanical preconditions themselves — review-certification:
    // "Recertification is bounded". Both refuse-only, evaluated last (after UnknownClaimIds/
    // MissingClaimOutcomes), both leave the card byte-identical on refusal.

    [Fact]
    public void BlockRecertify_GateNeverRecordedThisRound_Refuses_NamesTheAbsentLabel_AndLeavesTheCardByteIdentical()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo, "b-0013", "B-0013", BlockFlowState.Drafting);

        EnterInReview(repo, path, firstRound: true);
        RecordGateGreen(repo, path, "build");
        // Empties the live-nit set while still in-review, which is 'nit disposition's own edge back
        // to briefed (§8 block B) — advances the round without ever going near 'block recertify',
        // so the round-1 'build' result is retained but stops being this round's evidence. Must
        // happen before 'block approve' — dispositioning while already 'approved' never trips the
        // in-review-only auto-transition, so the round would never actually advance.
        RaiseAndDispositionNit(repo, "B-0013", "src/Round1.cs");
        var afterFixBeforeLand = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("briefed", afterFixBeforeLand.Frontmatter.Status);
        Assert.Equal(2, afterFixBeforeLand.BlockFields.Round);

        EnterInReview(repo, path, firstRound: false);
        var round2ClaimIds = Approve(repo, path, "B-0013", "commit-round2", "round two claim");
        // Deliberately no 'block gate' call this round — 'build' is the only distinct label the
        // card has ever recorded, and it has no round-2 entry.
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "block", "recertify", "--id", "B-0013", "--role", "reviewer", "--state", "commit-round2-amended",
                "--assert", round2ClaimIds[0], "--changed", "src/Round2.cs", "--change", ChangeName,
            ],
            output, repo);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("gates-not-green", refusal.GetProperty("code").GetString());
        Assert.Contains("build", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void BlockRecertify_GateRecordedNonZeroThisRound_Refuses_NamesTheLabelAndExitCode_AndLeavesTheCardByteIdentical()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo, "b-0014", "B-0014", BlockFlowState.InReview);
        var claimIds = Approve(repo, path, "B-0014", "commit-abc", "claim one");
        RecordGate(repo, path, "test", exitCode: 1);
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "block", "recertify", "--id", "B-0014", "--role", "reviewer", "--state", "commit-def",
                "--assert", claimIds[0], "--changed", "src/Whatever.cs", "--change", ChangeName,
            ],
            output, repo);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("gates-not-green", refusal.GetProperty("code").GetString());
        var message = refusal.GetProperty("message").GetString()!;
        Assert.Contains("test", message, StringComparison.Ordinal);
        Assert.Contains("1", message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // review-certification: "the difference … SHALL be confined to the sites of the dispositioned
    // nits" — brief item 6: no dispositioned nit ⇒ an empty bound ⇒ every changed path is out of
    // scope, never a vacuous pass.
    [Fact]
    public void BlockRecertify_NoDispositionedNitThisRound_Refuses_EveryChangedPathOutOfScope_AndNamesAmendmentRequested()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo, "b-0015", "B-0015", BlockFlowState.InReview);
        var claimIds = Approve(repo, path, "B-0015", "commit-abc", "claim one");
        RecordGateGreen(repo, path, "build");
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "block", "recertify", "--id", "B-0015", "--role", "reviewer", "--state", "commit-def",
                "--assert", claimIds[0], "--changed", "src/Foo.cs", "--change", ChangeName,
            ],
            output, repo);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("difference-outside-nit-sites", refusal.GetProperty("code").GetString());
        var message = refusal.GetProperty("message").GetString()!;
        Assert.Contains("src/Foo.cs", message, StringComparison.Ordinal);
        Assert.Contains("amendment-requested", message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void BlockRecertify_ChangedPathOutsideDispositionedSites_Refuses_NamesOffendingPathAndInBoundsSite()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo, "b-0016", "B-0016", BlockFlowState.InReview);
        var claimIds = Approve(repo, path, "B-0016", "commit-abc", "claim one");
        RecordGateGreen(repo, path, "build");
        RaiseAndDispositionNit(repo, "B-0016", "src/Foo.cs");
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "block", "recertify", "--id", "B-0016", "--role", "reviewer", "--state", "commit-def",
                "--assert", claimIds[0], "--changed", "src/Bar.cs", "--change", ChangeName,
            ],
            output, repo);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("difference-outside-nit-sites", refusal.GetProperty("code").GetString());
        var message = refusal.GetProperty("message").GetString()!;
        Assert.Contains("src/Bar.cs", message, StringComparison.Ordinal);
        Assert.Contains("src/Foo.cs", message, StringComparison.Ordinal);
        Assert.Contains("amendment-requested", message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // Site-confinement's positive half (brief item 5): a changed path nested under a dispositioned
    // site's directory is confined, not merely a path ordinal-equal to the site itself.
    [Fact]
    public void BlockRecertify_ChangedPathUnderDispositionedSiteDirectory_IsConfined_Succeeds()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo, "b-0017", "B-0017", BlockFlowState.InReview);
        var claimIds = Approve(repo, path, "B-0017", "commit-abc", "claim one");
        RecordGateGreen(repo, path, "build");
        RaiseAndDispositionNit(repo, "B-0017", "src/pkg");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "block", "recertify", "--id", "B-0017", "--role", "reviewer", "--state", "commit-def",
                "--assert", claimIds[0], "--changed", "src/pkg/File.cs", "--change", ChangeName,
            ],
            output, repo);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
    }

    // brief item 4 — the round-boundary property this section has already gotten wrong twice
    // (§8 block B's own remediation, twice), computed a third time here: a nit dispositioned in an
    // EARLIER round must not bound a LATER round's recertification. Demonstrated to fail against
    // the un-fixed logic: scoping the nit-site scan to the card's whole comment history (i.e.
    // dropping the roundStart scan down to always DateTimeOffset.MinValue) makes this call
    // succeed instead of refuse. Confirmed by hand before landing this block: applying exactly
    // that change and running this test alone reproduces the failure described above; reverting
    // restores the pass.
    [Fact]
    public void BlockRecertify_DispositionedNitFromAnEarlierRound_DoesNotBoundALaterRound_Refuses()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo, "b-0018", "B-0018", BlockFlowState.Drafting);

        EnterInReview(repo, path, firstRound: true);
        var round1ClaimIds = Approve(repo, path, "B-0018", "commit-round1", "round one claim");
        RecordGateGreen(repo, path, "build");
        RaiseAndDispositionNit(repo, "B-0018", "src/Round1.cs");
        Assert.Equal(CommandDispatcher.SuccessExitCode, RunInRepo(
            [
                "block", "recertify", "--id", "B-0018", "--role", "reviewer", "--state", "commit-round1-refused",
                "--refuse", round1ClaimIds[0], "--changed", "src/Round1.cs", "--change", ChangeName,
            ],
            new StringWriter(), repo));

        EnterInReview(repo, path, firstRound: false);
        var round2ClaimIds = Approve(repo, path, "B-0018", "commit-round2", "round two claim");
        RecordGateGreen(repo, path, "build"); // round 2's own evidence — required either way

        var output = new StringWriter();
        var exitCode = RunInRepo(
            [
                "block", "recertify", "--id", "B-0018", "--role", "reviewer", "--state", "commit-round2-amended",
                "--assert", round2ClaimIds[0], "--changed", "src/Round1.cs", "--change", ChangeName,
            ],
            output, repo);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("difference-outside-nit-sites", refusal.GetProperty("code").GetString());
        Assert.Contains("src/Round1.cs", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    // §8 block D (8.12) — the section's thesis in a single file's worth of tests: mechanical
    // evidence can refuse a certification and can never grant one. review-certification: "Green
    // preconditions do not confer approval".
    [Fact]
    public void BlockRecertify_Thesis_GreenPreconditionsDoNotRescueARefusedClaim()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo, "b-0019", "B-0019", BlockFlowState.InReview);
        const string certifiedState = "commit-abc";
        var claimIds = Approve(repo, path, "B-0019", certifiedState, "claim one,claim two");
        // Every mechanical precondition green: gates re-run passing, and the lone changed path
        // confined to the lone dispositioned nit's site.
        RecordGateGreen(repo, path, "build");
        RaiseAndDispositionNit(repo, "B-0019", "src/Foo.cs");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "block", "recertify", "--id", "B-0019", "--role", "reviewer", "--state", "commit-def",
                "--assert", claimIds[0], "--refuse", claimIds[1], "--changed", "src/Foo.cs", "--change", ChangeName,
            ],
            output, repo);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode); // a substantive verdict, not a refused attempt
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("transitioned").GetBoolean());
        Assert.Equal([claimIds[1]], result.GetProperty("refusedClaimIds").EnumerateArray().Select(static e => e.GetString()).ToArray());
        // Green preconditions did not rescue the refused claim into the asserted set.
        Assert.DoesNotContain(claimIds[1], result.GetProperty("assertedClaimIds").EnumerateArray().Select(static e => e.GetString()));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("briefed", read.Frontmatter.Status);
        Assert.Equal(certifiedState, read.BlockFields.ReviewedState); // untouched — the refusal is real, not overridden by green gates
        Assert.Equal(2, read.BlockFields.Round);
    }

    [Fact]
    public void BlockRecertify_Thesis_GreenPreconditionsDoNotCompleteAMalformedRequest_MissingClaimOutcomeStillRefused()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo, "b-0020", "B-0020", BlockFlowState.InReview);
        var claimIds = Approve(repo, path, "B-0020", "commit-abc", "claim one,claim two");
        RecordGateGreen(repo, path, "build");
        RaiseAndDispositionNit(repo, "B-0020", "src/Foo.cs");
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        // Every mechanical precondition passes — yet naming only one of the two claims is still
        // refused: a precondition can refuse, never complete, a malformed request.
        var exitCode = RunInRepo(
            [
                "block", "recertify", "--id", "B-0020", "--role", "reviewer", "--state", "commit-def",
                "--assert", claimIds[0], "--changed", "src/Foo.cs", "--change", ChangeName,
            ],
            output, repo);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-claim-outcome", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // Reviewer blocker (§8 block D remediation): brief item 3 required the gate-freshness limit
    // stated in BOTH the doc comment AND BlockRecertifyResult.Notice, "alongside the re-derivation
    // obligation block C already put there" — this asserts the notice carries both, not only the
    // claim re-derivation sentence block C shipped with.
    [Fact]
    public void BlockRecertify_Notice_CarriesBothTheReRederivationObligationAndTheGateFreshnessLimit()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo, "b-0021", "B-0021", BlockFlowState.InReview);
        var claimIds = Approve(repo, path, "B-0021", "commit-abc", "claim one");
        RecordGateGreen(repo, path, "build");
        RaiseAndDispositionNit(repo, "B-0021", "src/Foo.cs");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "block", "recertify", "--id", "B-0021", "--role", "reviewer", "--state", "commit-def",
                "--assert", claimIds[0], "--changed", "src/Foo.cs", "--change", ChangeName,
            ],
            output, repo);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var notice = doc.RootElement.GetProperty("result").GetProperty("notice").GetString()!;

        // Block C's own obligation — must survive this fix, not be replaced by it.
        Assert.Contains("re-derivation", notice, StringComparison.Ordinal);
        // Block D's own obligation — the fact this test exists to pin down.
        Assert.Contains("gate", notice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("current round", notice, StringComparison.Ordinal);
        Assert.Contains("reviewer", notice, StringComparison.Ordinal);
    }

    // §8 block D's own two mechanical preconditions (review-certification: "Recertification is
    // bounded") need real evidence on the card, not hand-seeded state — the two properties this
    // section has already gotten wrong twice were both computed over the wrong scope, so these
    // helpers drive the real verbs (block gate / nit raise / nit disposition) rather than writing
    // GateResult/CardComment values directly.
    private static void RecordGateGreen(TempGitRepo repo, string path, string label) => RecordGate(repo, path, label, exitCode: 0);

    private static void RecordGate(TempGitRepo repo, string path, string label, int exitCode)
    {
        Assert.Equal(CommandDispatcher.SuccessExitCode, RunInRepo(
            ["block", "gate", path, label, exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture), "--role", "worker", "--change", ChangeName],
            new StringWriter(), repo));
    }

    // Raises a nit against block card 'id' naming 'site', then dispositions it fix-before-land.
    // Dispositioning while the card is already 'approved' (every caller here) never trips the
    // in-review-only auto-transition edge (CardStore.DispositionNitUnderLocks), so the choice of
    // disposition value doesn't matter for these tests — only that the nit ends up dispositioned
    // (CardCommentRouting.IsNitDispositioned), which is all the site-confinement bound reads.
    private static string RaiseAndDispositionNit(TempGitRepo repo, string id, string site)
    {
        var raiseOutput = new StringWriter();
        var raiseExit = RunInRepo(
            ["nit", "raise", "--id", id, "--role", "reviewer", "--site", site, "--change", ChangeName],
            raiseOutput, repo, "A nit for the recertification bound.");
        Assert.Equal(CommandDispatcher.SuccessExitCode, raiseExit);
        using var raiseDoc = JsonDocument.Parse(raiseOutput.ToString());
        var nitId = raiseDoc.RootElement.GetProperty("result").GetProperty("nitId").GetString()!;

        var dispositionOutput = new StringWriter();
        var dispositionExit = RunInRepo(
            ["nit", "disposition", "--id", nitId, "--role", "architect", "--disposition", "fix-before-land", "--change", ChangeName],
            dispositionOutput, repo, "Fixed within the recertified amendment.");
        Assert.Equal(CommandDispatcher.SuccessExitCode, dispositionExit);
        return nitId;
    }

    private static List<string> Approve(TempGitRepo repo, string path, string id, string state, string claimsRaw)
    {
        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["block", "approve", "--id", id, "--role", "reviewer", "--state", state, "--claims", claimsRaw, "--change", ChangeName],
            output, repo);
        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        return doc.RootElement.GetProperty("result").GetProperty("claims").EnumerateArray()
            .Select(static e => e.GetProperty("id").GetString()!)
            .ToList();
    }

    // Same shape as CommandDispatcherNitTests.EnterInReview — drives a card from its current state
    // into in-review via the plain flow edges.
    private static void EnterInReview(TempGitRepo repo, string path, bool firstRound)
    {
        if (firstRound)
        {
            Assert.Equal(CommandDispatcher.SuccessExitCode, RunInRepo(
                ["block", "transition", path, "brief", "--role", "architect", "--base", "commit-abc", "--change", ChangeName],
                new StringWriter(), repo));
        }

        Assert.Equal(CommandDispatcher.SuccessExitCode, RunInRepo(
            ["block", "transition", path, "claim", "--role", "worker", "--change", ChangeName],
            new StringWriter(), repo));
        Assert.Equal(CommandDispatcher.SuccessExitCode, RunInRepo(
            ["block", "transition", path, "submit-for-review", "--role", "worker", "--change", ChangeName],
            new StringWriter(), repo));
    }

    private static string WriteInitialBlockCard(TempGitRepo repo, string fileStem, string id, BlockFlowState status)
    {
        var path = Path.Combine(repo.ChangeDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "Title", status.ToWireString(), CardOwner.Architect, CardScope.Change, "8", FixedNow, FixedNow);
        var round = status == BlockFlowState.InReview ? 1 : (int?)null;
        var blockFields = new BlockCardFields(null, null, [], round, [], []);
        var card = new CardFile(frontmatter, "Body.", [], [], [], blockFields, []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static int RunInRepo(string[] args, TextWriter output, TempGitRepo repo) =>
        CommandDispatcher.Run(
            args, output, TextReader.Null, TextWriter.Null, isInputRedirected: true, workingDirectory: repo.Path, clock: repo.Clock.Next);

    // 'nit raise'/'nit disposition' read their body from stdin (ADR-0001/D1) — same shape
    // CommandDispatcherNitTests's own RunInRepo overload uses.
    private static int RunInRepo(string[] args, TextWriter output, TempGitRepo repo, string body) =>
        CommandDispatcher.Run(
            args, output, new StringReader(body), TextWriter.Null, isInputRedirected: true, workingDirectory: repo.Path, clock: repo.Clock.Next);

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));

    // Same advancing-clock idiom CommandDispatcherNitTests established (§8 block B remediation) —
    // ticking forward on every call so "since the current approval" round-scoping genuinely has
    // distinct instants to distinguish, rather than every event sharing one timestamp.
    private sealed class AdvancingClock
    {
        private DateTimeOffset _current = FixedNow;

        internal DateTimeOffset Next() => _current += TimeSpan.FromMinutes(1);
    }

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _path;

        internal string Path => _path;

        internal AdvancingClock Clock { get; } = new();

        internal string ChangeDirectory => System.IO.Path.Combine(
            _path, CardLayout.ChangesDirectory(ChangeName).Replace('/', System.IO.Path.DirectorySeparatorChar));

        internal TempGitRepo()
        {
            _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-block-recertify-cli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_path);
            Directory.CreateDirectory(System.IO.Path.Combine(_path, ".git"));
            Directory.CreateDirectory(ChangeDirectory);
        }

        public void Dispose()
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }
    }
}
