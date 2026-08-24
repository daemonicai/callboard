using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// §8 block B at the CLI boundary: <c>nit raise</c>/<c>nit disposition</c> — review-certification's
/// "Nits carry a disposition". Same fixed-clock discipline every §5/§7/§8 CLI test class already
/// establishes.
/// </summary>
public sealed class CommandDispatcherNitTests
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NitRaise_Legal_Succeeds_RecordsIsNitRequiredAndSites()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0001", "B-0001", BlockFlowState.InReview);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["nit", "raise", "--id", "B-0001", "--role", "reviewer", "--required", "--site", "src/A.cs", "--site", "src/B.cs", "--change", ChangeName],
            output, repo, "This line is dead code.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        var nitId = result.GetProperty("nitId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(nitId));
        Assert.True(result.GetProperty("required").GetBoolean());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var comment = Assert.Single(read.Comments);
        Assert.Equal(nitId, comment.Id);
        Assert.True(comment.IsNit);
        Assert.True(comment.Required);
        Assert.Equal(["src/A.cs", "src/B.cs"], comment.Sites);
        Assert.Equal(CardOwner.Architect, comment.To);
        Assert.Equal("This line is dead code.", comment.Body);
        Assert.Null(comment.Disposition);
    }

    // Regression: '--change' must not have to come last — an earlier shape split flag parsing
    // into two ConsumeKnownFlags passes, which stranded '--required'/'--site' unconsumed whenever
    // '--change' preceded them in argv.
    [Fact]
    public void NitRaise_ChangeFlagBeforeRequiredAndSite_StillParses()
    {
        using var repo = new TempGitRepo();
        WriteInitialBlockCard(repo.Path, "b-0013", "B-0013", BlockFlowState.InReview);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["nit", "raise", "--id", "B-0013", "--role", "reviewer", "--change", ChangeName, "--required", "--site", "src/A.cs"],
            output, repo, "A nit.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("required").GetBoolean());
        Assert.Equal(["src/A.cs"], result.GetProperty("sites").EnumerateArray().Select(static e => e.GetString()).ToList());
    }

    [Fact]
    public void NitRaise_WrongCardKind_Refuses()
    {
        using var repo = new TempGitRepo();
        WriteQuestionCard(repo.Path, "Q-0001");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["nit", "raise", "--id", "Q-0001", "--role", "reviewer", "--change", ChangeName],
            output, repo, "body");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("wrong-card-kind", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void NitRaise_MissingId_RefusesWithMissingArgumentCode()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = RunInRepo(["nit", "raise", "--role", "reviewer", "--change", ChangeName], output, repo, "body");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // review-certification: "The disposition SHALL determine what becomes of the nit" —
    // fix-before-land: "Stays inline; the block returns to briefed, round increments". The card
    // is seeded directly into in-review with no prior Transitions entries, so this also keeps the
    // roundStart = DateTimeOffset.MinValue path (CardStore.DispositionNit's own comment: "a card
    // seeded directly into in-review, as tests do") explicitly covered now that the clock
    // advances — it stops being every test's default the moment a card carries a real
    // submit-for-review entry, as the two round-boundary tests below do.
    [Fact]
    public void NitDisposition_FixBeforeLand_Succeeds_TransitionsAndIncrementsRound()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0002", "B-0002", BlockFlowState.InReview);
        var nitId = RaiseNit(repo, "B-0002");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["nit", "disposition", "--id", nitId, "--role", "architect", "--disposition", "fix-before-land", "--change", ChangeName],
            output, repo, "Fixing this before it lands.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("transitioned").GetBoolean());
        Assert.Equal(2, result.GetProperty("round").GetInt32());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("briefed", read.Frontmatter.Status);
        Assert.Equal(2, read.BlockFields.Round);
        var transition = Assert.Single(read.Transitions);
        Assert.Equal("fix-before-land", transition.Name);
        Assert.Equal(BlockFlowState.InReview, transition.From);
        Assert.Equal(BlockFlowState.Briefed, transition.To);

        Assert.Equal(2, read.Comments.Count);
        var disposition = read.Comments[1];
        Assert.Equal(nitId, disposition.Resolves);
        Assert.Equal(NitDisposition.FixBeforeLand, disposition.Disposition);
        Assert.False(disposition.IsNit);
        Assert.True(CardCommentRouting.IsNitDispositioned(read.Comments, 0));
    }

    // §8 block B brief item 4 (HAZARD): round must increment once per round, not once per
    // fix-before-land nit. Two nits dispositioned fix-before-land in the same round are one
    // return to briefed, not two — and both nits still end up dispositioned.
    [Fact]
    public void NitDisposition_TwoFixBeforeLandNitsInOneRound_IncrementsRoundExactlyOnce()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0003", "B-0003", BlockFlowState.InReview);
        var firstNitId = RaiseNit(repo, "B-0003");
        var secondNitId = RaiseNit(repo, "B-0003");

        // The first disposition still leaves the second nit undispositioned — the edge is
        // deliberately withheld (not refused: the disposition itself is always recorded, per the
        // brief's "record the disposition regardless") until nothing on the card is left live.
        var firstOutput = new StringWriter();
        var firstExit = RunInRepo(
            ["nit", "disposition", "--id", firstNitId, "--role", "architect", "--disposition", "fix-before-land", "--change", ChangeName],
            firstOutput, repo, "Fix one.");
        Assert.Equal(CommandDispatcher.SuccessExitCode, firstExit);
        using var firstDoc = JsonDocument.Parse(firstOutput.ToString());
        Assert.False(firstDoc.RootElement.GetProperty("result").GetProperty("transitioned").GetBoolean());

        // The second disposition is the one that leaves no nit undispositioned — it is the call
        // that actually applies the edge and increments round, exactly once.
        var secondOutput = new StringWriter();
        var secondExit = RunInRepo(
            ["nit", "disposition", "--id", secondNitId, "--role", "architect", "--disposition", "fix-before-land", "--change", ChangeName],
            secondOutput, repo, "Fix two.");
        Assert.Equal(CommandDispatcher.SuccessExitCode, secondExit);
        using var secondDoc = JsonDocument.Parse(secondOutput.ToString());
        Assert.True(secondDoc.RootElement.GetProperty("result").GetProperty("transitioned").GetBoolean());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("briefed", read.Frontmatter.Status);
        Assert.Equal(2, read.BlockFields.Round); // not 3 — the increment happened exactly once
        Assert.Single(read.Transitions);
        Assert.Empty(CardCommentRouting.LiveUndispositionedNitIds(read.Comments));
    }

    // §8 block B remediation — the original fix gated the edge on *this call's* disposition being
    // fix-before-land, so a fix-before-land nit dispositioned first, then a defer that empties the
    // live set, never applied the edge: the block stranded in in-review. The edge is a property of
    // the round, not of the call that happens to zero the live set.
    [Fact]
    public void NitDisposition_FixBeforeLandThenDefer_TransitionsAndIncrementsRoundExactlyOnce()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0014", "B-0014", BlockFlowState.InReview);
        var firstNitId = RaiseNit(repo, "B-0014");
        var secondNitId = RaiseNit(repo, "B-0014");

        var firstOutput = new StringWriter();
        var firstExit = RunInRepo(
            ["nit", "disposition", "--id", firstNitId, "--role", "architect", "--disposition", "fix-before-land", "--change", ChangeName],
            firstOutput, repo, "Fix this one.");
        Assert.Equal(CommandDispatcher.SuccessExitCode, firstExit);
        using var firstDoc = JsonDocument.Parse(firstOutput.ToString());
        Assert.False(firstDoc.RootElement.GetProperty("result").GetProperty("transitioned").GetBoolean());

        var secondOutput = new StringWriter();
        var secondExit = RunInRepo(
            [
                "nit", "disposition", "--id", secondNitId, "--role", "architect", "--disposition", "defer",
                "--raise", Path.Combine(repo.ChangeDirectory, "o-0001.md"), "--title", "t", "--change", ChangeName,
            ],
            secondOutput, repo, "Deferring this one.");
        Assert.Equal(CommandDispatcher.SuccessExitCode, secondExit);
        using var secondDoc = JsonDocument.Parse(secondOutput.ToString());

        // The transition rides on the *defer* call, not the fix-before-land one — this is exactly
        // what the old (this-call-only) gate could never produce.
        Assert.True(secondDoc.RootElement.GetProperty("result").GetProperty("transitioned").GetBoolean());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("briefed", read.Frontmatter.Status);
        Assert.Equal(2, read.BlockFields.Round);
        Assert.Single(read.Transitions);
        Assert.Equal("fix-before-land", read.Transitions[0].Name);
        Assert.Empty(CardCommentRouting.LiveUndispositionedNitIds(read.Comments));
    }

    // Same scenario, opposite order — pinned so the two orderings cannot diverge from each other.
    [Fact]
    public void NitDisposition_DeferThenFixBeforeLand_TransitionsAndIncrementsRoundExactlyOnce()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0015", "B-0015", BlockFlowState.InReview);
        var firstNitId = RaiseNit(repo, "B-0015");
        var secondNitId = RaiseNit(repo, "B-0015");

        var firstOutput = new StringWriter();
        var firstExit = RunInRepo(
            [
                "nit", "disposition", "--id", firstNitId, "--role", "architect", "--disposition", "defer",
                "--raise", Path.Combine(repo.ChangeDirectory, "o-0002.md"), "--title", "t", "--change", ChangeName,
            ],
            firstOutput, repo, "Deferring this one.");
        Assert.Equal(CommandDispatcher.SuccessExitCode, firstExit);
        using var firstDoc = JsonDocument.Parse(firstOutput.ToString());
        Assert.False(firstDoc.RootElement.GetProperty("result").GetProperty("transitioned").GetBoolean());

        var secondOutput = new StringWriter();
        var secondExit = RunInRepo(
            ["nit", "disposition", "--id", secondNitId, "--role", "architect", "--disposition", "fix-before-land", "--change", ChangeName],
            secondOutput, repo, "Fix this one.");
        Assert.Equal(CommandDispatcher.SuccessExitCode, secondExit);
        using var secondDoc = JsonDocument.Parse(secondOutput.ToString());
        Assert.True(secondDoc.RootElement.GetProperty("result").GetProperty("transitioned").GetBoolean());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("briefed", read.Frontmatter.Status);
        Assert.Equal(2, read.BlockFields.Round);
        Assert.Single(read.Transitions);
        Assert.Equal("fix-before-land", read.Transitions[0].Name);
        Assert.Empty(CardCommentRouting.LiveUndispositionedNitIds(read.Comments));
    }

    // The third disposition kind that can close the round out from under a fix-before-land nit.
    [Fact]
    public void NitDisposition_FixBeforeLandThenDecline_TransitionsAndIncrementsRoundExactlyOnce()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0016", "B-0016", BlockFlowState.InReview);
        var firstNitId = RaiseNit(repo, "B-0016");
        var secondNitId = RaiseNit(repo, "B-0016");

        var firstOutput = new StringWriter();
        var firstExit = RunInRepo(
            ["nit", "disposition", "--id", firstNitId, "--role", "architect", "--disposition", "fix-before-land", "--change", ChangeName],
            firstOutput, repo, "Fix this one.");
        Assert.Equal(CommandDispatcher.SuccessExitCode, firstExit);
        using var firstDoc = JsonDocument.Parse(firstOutput.ToString());
        Assert.False(firstDoc.RootElement.GetProperty("result").GetProperty("transitioned").GetBoolean());

        var secondOutput = new StringWriter();
        var secondExit = RunInRepo(
            [
                "nit", "disposition", "--id", secondNitId, "--role", "architect", "--disposition", "decline",
                "--raise", Path.Combine(repo.DecisionsDirectory, "d-0003.md"), "--title", "t", "--change", ChangeName,
            ],
            secondOutput, repo, "Declining this one.");
        Assert.Equal(CommandDispatcher.SuccessExitCode, secondExit);
        using var secondDoc = JsonDocument.Parse(secondOutput.ToString());
        Assert.True(secondDoc.RootElement.GetProperty("result").GetProperty("transitioned").GetBoolean());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("briefed", read.Frontmatter.Status);
        Assert.Equal(2, read.BlockFields.Round);
        Assert.Single(read.Transitions);
        Assert.Equal("fix-before-land", read.Transitions[0].Name);
        Assert.Empty(CardCommentRouting.LiveUndispositionedNitIds(read.Comments));
    }

    // The invariant's second face: a block certified with an unfixed fix-before-land nit is the
    // exact inversion of what fix-before-land is for. Once the round has closed onto the edge, the
    // card is in 'briefed', not 'in-review' — 'block approve' is refused the ordinary way ('approve'
    // has no edge from 'briefed'), not because any nit is still undispositioned (there is none).
    [Fact]
    public void BlockApprove_AfterFixBeforeLandThenDefer_Refuses_UntilBackThroughBriefed()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0017", "B-0017", BlockFlowState.InReview);
        var firstNitId = RaiseNit(repo, "B-0017");
        var secondNitId = RaiseNit(repo, "B-0017");

        var firstExit = RunInRepo(
            ["nit", "disposition", "--id", firstNitId, "--role", "architect", "--disposition", "fix-before-land", "--change", ChangeName],
            new StringWriter(), repo, "Fix this one.");
        Assert.Equal(CommandDispatcher.SuccessExitCode, firstExit);

        var secondExit = RunInRepo(
            [
                "nit", "disposition", "--id", secondNitId, "--role", "architect", "--disposition", "defer",
                "--raise", Path.Combine(repo.ChangeDirectory, "o-0003.md"), "--title", "t", "--change", ChangeName,
            ],
            new StringWriter(), repo, "Deferring this one.");
        Assert.Equal(CommandDispatcher.SuccessExitCode, secondExit);

        // No nit is undispositioned — before the fix this emptied live set alone was enough to let
        // 'block approve' through, certifying a block carrying an unfixed fix-before-land nit.
        Assert.Empty(CardCommentRouting.LiveUndispositionedNitIds(AssertParseSuccess(CardStore.ReadCard(path)).Comments));

        var approveOutput = new StringWriter();
        var approveExit = RunInRepo(
            ["block", "approve", "--id", "B-0017", "--role", "reviewer", "--state", "commit-abc", "--claims", "claim one", "--change", ChangeName],
            approveOutput, repo, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, approveExit);
        using var doc = JsonDocument.Parse(approveOutput.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("undefined-transition", refusal.GetProperty("code").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("briefed", read.Frontmatter.Status);
    }

    // review-certification: "A reviewer MAY mark a nit as required; that marking SHALL NOT bind
    // the architect's disposition." Declining a required nit must still succeed.
    [Fact]
    public void NitDisposition_Decline_ARequiredNit_Succeeds_RequiredIsAdvisoryOnly()
    {
        using var repo = new TempGitRepo();
        WriteInitialBlockCard(repo.Path, "b-0004", "B-0004", BlockFlowState.InReview);
        var nitId = RaiseNit(repo, "B-0004", required: true);
        var decisionPath = Path.Combine(repo.DecisionsDirectory, "d-0001.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "nit", "disposition", "--id", nitId, "--role", "architect", "--disposition", "decline",
                "--raise", decisionPath, "--title", "Code is right as it stands", "--change", ChangeName,
            ],
            output, repo, "The pattern is deliberate; see the same idiom two lines up.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("decline", result.GetProperty("disposition").GetString());
        Assert.False(result.GetProperty("transitioned").GetBoolean());
        var raisedCardId = result.GetProperty("raisedCardId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(raisedCardId));

        var decision = AssertParseSuccess(CardStore.ReadCard(decisionPath));
        Assert.Equal(CardKind.Decision, decision.Frontmatter.Kind);
        Assert.Equal(CardScope.Capability, decision.Frontmatter.Scope);
        Assert.Contains("The pattern is deliberate", decision.Body, StringComparison.Ordinal);

        // The block itself never moves for a decline — it stays exactly where it was.
        var blockPath = Path.Combine(repo.ChangeDirectory, "b-0004.md");
        var block = AssertParseSuccess(CardStore.ReadCard(blockPath));
        Assert.Equal("in-review", block.Frontmatter.Status);
    }

    // review-certification: "defer: Promoted to an obligation card naming what discharges it."
    [Fact]
    public void NitDisposition_Defer_Succeeds_CreatesObligationOwedByTheBlockSSection()
    {
        using var repo = new TempGitRepo();
        WriteInitialBlockCard(repo.Path, "b-0005", "B-0005", BlockFlowState.InReview);
        var nitId = RaiseNit(repo, "B-0005");
        var obligationPath = Path.Combine(repo.ChangeDirectory, "o-0001.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "nit", "disposition", "--id", nitId, "--role", "architect", "--disposition", "defer",
                "--raise", obligationPath, "--title", "Address in a later section", "--change", ChangeName,
            ],
            output, repo, "Deferred to §10.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        var obligation = AssertParseSuccess(CardStore.ReadCard(obligationPath));
        Assert.Equal(CardKind.Obligation, obligation.Frontmatter.Kind);
        Assert.Equal(CardScope.Change, obligation.Frontmatter.Scope);
        Assert.Equal("8", obligation.RegisterFields.OwedBy);
        Assert.Contains("Deferred to §10.", obligation.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void NitDisposition_Defer_MissingRaiseFlag_RefusesWithMissingArgumentCode()
    {
        using var repo = new TempGitRepo();
        WriteInitialBlockCard(repo.Path, "b-0006", "B-0006", BlockFlowState.InReview);
        var nitId = RaiseNit(repo, "B-0006");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["nit", "disposition", "--id", nitId, "--role", "architect", "--disposition", "defer", "--title", "t", "--change", ChangeName],
            output, repo, "reason");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void NitDisposition_AlreadyDispositioned_Refuses()
    {
        using var repo = new TempGitRepo();
        WriteInitialBlockCard(repo.Path, "b-0007", "B-0007", BlockFlowState.InReview);
        var nitId = RaiseNit(repo, "B-0007");

        var firstOutput = new StringWriter();
        var firstExit = RunInRepo(
            ["nit", "disposition", "--id", nitId, "--role", "architect", "--disposition", "fix-before-land", "--change", ChangeName],
            firstOutput, repo, "reason one");
        Assert.Equal(CommandDispatcher.SuccessExitCode, firstExit);

        var secondOutput = new StringWriter();
        var secondExit = RunInRepo(
            ["nit", "disposition", "--id", nitId, "--role", "architect", "--disposition", "decline", "--raise", Path.Combine(repo.DecisionsDirectory, "d-0002.md"), "--title", "t", "--change", ChangeName],
            secondOutput, repo, "reason two");

        Assert.Equal(CommandDispatcher.RefusalExitCode, secondExit);
        using var doc = JsonDocument.Parse(secondOutput.ToString());
        Assert.Equal("nit-already-dispositioned", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // §8 block B brief item 6, the Architect's own reading most open to challenge — dispositioning
    // is architect-only. Asserted with 'reviewer', a role that is not the one the code would
    // default to (§7 item E discipline).
    [Fact]
    public void NitDisposition_NonArchitectRole_Refuses()
    {
        using var repo = new TempGitRepo();
        WriteInitialBlockCard(repo.Path, "b-0008", "B-0008", BlockFlowState.InReview);
        var nitId = RaiseNit(repo, "B-0008");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["nit", "disposition", "--id", nitId, "--role", "reviewer", "--disposition", "fix-before-land", "--change", ChangeName],
            output, repo, "reason");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("role-not-permitted", refusal.GetProperty("code").GetString());
        Assert.Contains("architect", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NitDisposition_UnknownNitId_RefusesWithNitIdNotFoundCode()
    {
        using var repo = new TempGitRepo();
        WriteInitialBlockCard(repo.Path, "b-0009", "B-0009", BlockFlowState.InReview);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["nit", "disposition", "--id", "nit-does-not-exist", "--role", "architect", "--disposition", "fix-before-land", "--change", ChangeName],
            output, repo, "reason");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("nit-id-not-found", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // review-certification: "Undispositioned nits block the verdict" — approve is one of the
    // transitions that leaves in-review, and block A's own path must now refuse it.
    [Fact]
    public void BlockApprove_UndispositionedNit_Refuses_AndNamesTheNit()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0010", "B-0010", BlockFlowState.InReview);
        var nitId = RaiseNit(repo, "B-0010");
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "approve", "--id", "B-0010", "--role", "reviewer", "--state", "commit-abc", "--claims", "claim one", "--change", ChangeName],
            output, repo, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("undispositioned-nits", refusal.GetProperty("code").GetString());
        Assert.Contains(nitId, refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // Same requirement, the changes-requested exit.
    [Fact]
    public void BlockTransition_ChangesRequested_UndispositionedNit_Refuses()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0011", "B-0011", BlockFlowState.InReview);
        RaiseNit(repo, "B-0011");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "transition", path, "changes-requested", "--role", "reviewer", "--change", ChangeName],
            output, repo, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("undispositioned-nits", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // §8 block B: fix-before-land is only ever raised as the side effect of a nit disposition —
    // the same "one door" discipline block A's own approve-via-transition-refused established.
    [Fact]
    public void BlockTransition_FixBeforeLand_Refuses_AndLeavesTheCardByteIdentical()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0012", "B-0012", BlockFlowState.InReview);
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "transition", path, "fix-before-land", "--role", "architect", "--change", ChangeName],
            output, repo, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("fix-before-land-via-transition-refused", refusal.GetProperty("code").GetString());
        Assert.Contains("nit disposition", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // §8 block B remediation, reviewer finding: the fix-before-land edge is scoped to "the round,
    // taken as a whole" (CardStore.DispositionNit), not the card's entire append-only history. A
    // fixed clock could never distinguish an earlier round's fix-before-land marker from the
    // current round's, since every comment shared one timestamp — this drives a card through two
    // genuinely distinct in-review rounds and proves round 1's marker does not leak into round 2.
    [Fact]
    public void NitDisposition_FixBeforeLandInRoundOne_DoesNotResurrectInRoundTwo()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0018", "B-0018", BlockFlowState.Drafting);

        EnterInReview(repo, path, firstRound: true);
        var round1NitId = RaiseNit(repo, "B-0018");
        var round1Output = new StringWriter();
        var round1Exit = RunInRepo(
            ["nit", "disposition", "--id", round1NitId, "--role", "architect", "--disposition", "fix-before-land", "--change", ChangeName],
            round1Output, repo, "Fix this in round 1.");
        Assert.Equal(CommandDispatcher.SuccessExitCode, round1Exit);
        using (var round1Doc = JsonDocument.Parse(round1Output.ToString()))
        {
            Assert.True(round1Doc.RootElement.GetProperty("result").GetProperty("transitioned").GetBoolean());
        }
        Assert.Equal(2, AssertParseSuccess(CardStore.ReadCard(path)).BlockFields.Round);

        // Round 2: back through building into in-review. Round 1's fix-before-land disposition
        // comment is still on the card (comments are append-only) but must not be read as this
        // round's.
        EnterInReview(repo, path, firstRound: false);
        var round2NitId = RaiseNit(repo, "B-0018");
        var round2Output = new StringWriter();
        var round2Exit = RunInRepo(
            [
                "nit", "disposition", "--id", round2NitId, "--role", "architect", "--disposition", "decline",
                "--raise", Path.Combine(repo.DecisionsDirectory, "d-0004.md"), "--title", "t", "--change", ChangeName,
            ],
            round2Output, repo, "Declining in round 2 — no fix-before-land nit this round.");
        Assert.Equal(CommandDispatcher.SuccessExitCode, round2Exit);
        using var round2Doc = JsonDocument.Parse(round2Output.ToString());
        Assert.False(round2Doc.RootElement.GetProperty("result").GetProperty("transitioned").GetBoolean());

        var final = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("in-review", final.Frontmatter.Status);
        Assert.Equal(2, final.BlockFields.Round);
    }

    // Same hazard, sharper: round 3 carries two stale fix-before-land dispositions in its history
    // (rounds 1 and 2), so a scoping bug that consulted the whole thread rather than just the
    // current round would have two chances to fire, not one.
    [Fact]
    public void NitDisposition_RoundThreeWithFixBeforeLandDispositionsInEarlierRounds_OnlyCurrentRoundIsConsulted()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0019", "B-0019", BlockFlowState.Drafting);

        EnterInReview(repo, path, firstRound: true);
        var round1NitId = RaiseNit(repo, "B-0019");
        var round1Exit = RunInRepo(
            ["nit", "disposition", "--id", round1NitId, "--role", "architect", "--disposition", "fix-before-land", "--change", ChangeName],
            new StringWriter(), repo, "Fix round 1.");
        Assert.Equal(CommandDispatcher.SuccessExitCode, round1Exit);
        Assert.Equal(2, AssertParseSuccess(CardStore.ReadCard(path)).BlockFields.Round);

        EnterInReview(repo, path, firstRound: false);
        var round2NitId = RaiseNit(repo, "B-0019");
        var round2Exit = RunInRepo(
            ["nit", "disposition", "--id", round2NitId, "--role", "architect", "--disposition", "fix-before-land", "--change", ChangeName],
            new StringWriter(), repo, "Fix round 2.");
        Assert.Equal(CommandDispatcher.SuccessExitCode, round2Exit);
        Assert.Equal(3, AssertParseSuccess(CardStore.ReadCard(path)).BlockFields.Round);

        // Round 3: the card now carries two stale fix-before-land dispositions. A nit raised and
        // declined this round must not resurrect either of them.
        EnterInReview(repo, path, firstRound: false);
        var round3NitId = RaiseNit(repo, "B-0019");
        var round3Output = new StringWriter();
        var round3Exit = RunInRepo(
            [
                "nit", "disposition", "--id", round3NitId, "--role", "architect", "--disposition", "decline",
                "--raise", Path.Combine(repo.DecisionsDirectory, "d-0005.md"), "--title", "t", "--change", ChangeName,
            ],
            round3Output, repo, "Declining in round 3 — no fix-before-land nit this round.");
        Assert.Equal(CommandDispatcher.SuccessExitCode, round3Exit);
        using var round3Doc = JsonDocument.Parse(round3Output.ToString());
        Assert.False(round3Doc.RootElement.GetProperty("result").GetProperty("transitioned").GetBoolean());

        var final = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("in-review", final.Frontmatter.Status);
        Assert.Equal(3, final.BlockFields.Round);
    }

    // Drives a card from its current state into in-review via the plain flow edges: drafting ->
    // briefed -> building -> in-review the first time, or briefed -> building -> in-review on any
    // later round (the card is already back on briefed by then, per the fix-before-land/
    // changes-requested edges' own destination).
    private static void EnterInReview(TempGitRepo repo, string path, bool firstRound)
    {
        if (firstRound)
        {
            Assert.Equal(CommandDispatcher.SuccessExitCode, RunInRepo(
                ["block", "transition", path, "brief", "--role", "architect", "--base", "commit-abc", "--change", ChangeName],
                new StringWriter(), repo, string.Empty));
        }

        Assert.Equal(CommandDispatcher.SuccessExitCode, RunInRepo(
            ["block", "transition", path, "claim", "--role", "worker", "--change", ChangeName],
            new StringWriter(), repo, string.Empty));
        Assert.Equal(CommandDispatcher.SuccessExitCode, RunInRepo(
            ["block", "transition", path, "submit-for-review", "--role", "worker", "--change", ChangeName],
            new StringWriter(), repo, string.Empty));
    }

    private static string RaiseNit(TempGitRepo repo, string blockId, bool required = false)
    {
        var output = new StringWriter();
        var args = required
            ? new[] { "nit", "raise", "--id", blockId, "--role", "reviewer", "--required", "--change", ChangeName }
            : ["nit", "raise", "--id", blockId, "--role", "reviewer", "--change", ChangeName];
        var exitCode = RunInRepo(args, output, repo, "A nit.");
        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        return doc.RootElement.GetProperty("result").GetProperty("nitId").GetString()!;
    }

    private static string WriteInitialBlockCard(string repoRoot, string fileStem, string id, BlockFlowState status)
    {
        var directory = Path.Combine(repoRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "Title", status.ToWireString(), CardOwner.Architect, CardScope.Change, "8", FixedNow, FixedNow);
        var round = status == BlockFlowState.InReview ? 1 : (int?)null;
        var blockFields = new BlockCardFields(null, null, [], round, [], []);
        var card = new CardFile(frontmatter, "Body.", [], [], [], blockFields, []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static void WriteQuestionCard(string repoRoot, string id)
    {
        var directory = Path.Combine(repoRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, id.ToLowerInvariant() + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Question, "A question", "open", CardOwner.Architect, CardScope.Change, "8", FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static int RunInRepo(string[] args, TextWriter output, TempGitRepo repo, string body) =>
        CommandDispatcher.Run(
            args, output, new StringReader(body), TextWriter.Null, isInputRedirected: true, workingDirectory: repo.Path, clock: repo.Clock.Next);

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));

    // §8 block B remediation, reviewer finding: every test previously drove its calls through one
    // fixed clock (`static () => FixedNow`), so every comment on a card shared one timestamp and
    // round-scoping logic (CardCommentRouting.HasFixBeforeLandDisposition, sliced by CardStore.
    // DispositionNit to "comments at or after the round's own in-review entry") had no two
    // instants in the whole suite to ever distinguish. Each TempGitRepo now owns one of these,
    // ticking forward by a minute on every call the repo's own RunInRepo makes, so a test that
    // drives a card through more than one round genuinely produces different timestamps per round
    // — the same pattern BlockLifecycleIntegrationTests already uses for its own advancing `t`.
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

        internal string DecisionsDirectory => System.IO.Path.Combine(_path, "callboard", "decisions");

        internal string ChangeDirectory => System.IO.Path.Combine(
            _path, CardLayout.ChangesDirectory(ChangeName).Replace('/', System.IO.Path.DirectorySeparatorChar));

        internal TempGitRepo()
        {
            _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-nit-cli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_path);
            Directory.CreateDirectory(System.IO.Path.Combine(_path, ".git"));
            Directory.CreateDirectory(DecisionsDirectory);
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
