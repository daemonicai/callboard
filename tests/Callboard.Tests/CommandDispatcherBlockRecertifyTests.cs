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
        const string amendedState = "commit-def + uncommitted working tree";
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "block", "recertify", "--id", "B-0001", "--role", "reviewer", "--state", amendedState,
                "--assert", claimIds[0], "--assert", claimIds[1], "--change", ChangeName,
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
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "block", "recertify", "--id", "B-0002", "--role", "reviewer", "--state", "commit-def",
                "--assert", claimIds[0], "--assert", claimIds[2], "--refuse", claimIds[1], "--change", ChangeName,
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
            ["block", "recertify", "--id", "B-0003", "--role", "reviewer", "--state", "commit-def", "--assert", claimIds[0], "--change", ChangeName],
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
            ["block", "recertify", "--id", "B-0004", "--role", "reviewer", "--state", "commit-def", "--assert", "not-a-real-claim-id", "--change", ChangeName],
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
            ["block", "recertify", "--id", "B-0005", "--role", "reviewer", "--state", "commit-def", "--change", ChangeName],
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
            ["block", "recertify", "--id", "B-0006", "--role", "worker", "--state", "commit-def", "--assert", claimIds[0], "--change", ChangeName],
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
            ["block", "recertify", "--id", "Q-0001", "--role", "reviewer", "--state", "commit-def", "--change", ChangeName],
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

        Assert.Equal(CommandDispatcher.SuccessExitCode, RunInRepo(
            [
                "block", "recertify", "--id", "B-0011", "--role", "reviewer", "--state", "commit-round1-amended",
                "--assert", claimIds[0], "--change", ChangeName,
            ],
            new StringWriter(), repo));
        Assert.Equal("approved", AssertParseSuccess(CardStore.ReadCard(path)).Frontmatter.Status);
        var before = File.ReadAllBytes(path);

        var output = new StringWriter();
        var exitCode = RunInRepo(
            [
                "block", "recertify", "--id", "B-0011", "--role", "reviewer", "--state", "commit-round1-amended-again",
                "--assert", claimIds[0], "--change", ChangeName,
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
        Assert.Equal(CommandDispatcher.SuccessExitCode, RunInRepo(
            [
                "block", "recertify", "--id", "B-0012", "--role", "reviewer", "--state", "commit-round1-refused",
                "--refuse", round1ClaimIds[0], "--change", ChangeName,
            ],
            new StringWriter(), repo));
        var afterRefusal = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("briefed", afterRefusal.Frontmatter.Status);
        Assert.Equal(2, afterRefusal.BlockFields.Round);

        EnterInReview(repo, path, firstRound: false);
        var round2ClaimIds = Approve(repo, path, "B-0012", "commit-round2", "round two claim");
        Assert.NotEqual(round1ClaimIds[0], round2ClaimIds[0]);

        var output = new StringWriter();
        var exitCode = RunInRepo(
            [
                "block", "recertify", "--id", "B-0012", "--role", "reviewer", "--state", "commit-round2-amended",
                "--assert", round2ClaimIds[0], "--change", ChangeName,
            ],
            output, repo);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("commit-round2-amended", doc.RootElement.GetProperty("result").GetProperty("reviewedState").GetString());
        Assert.Equal("approved", AssertParseSuccess(CardStore.ReadCard(path)).Frontmatter.Status);
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
