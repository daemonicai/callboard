using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// §8 block C remediation at the CLI boundary: <c>block amendment-requested</c> — work-lifecycle's
/// "`amendment-requested` is the architect deliberately reopening an approved block for a further
/// amendment". Closes the blocker the reviewer found in block C's original shape: once a block's
/// single recertification was spent, <c>land</c> was <c>approved</c>'s only remaining exit, so an
/// amended block could only be landed carrying a stale <c>reviewed_state</c>.
///
/// <para>
/// Every call in this file routes through <see cref="TempGitRepo.Clock"/>'s
/// <see cref="AdvancingClock"/>, the same idiom <c>CommandDispatcherBlockRecertifyTests</c>
/// established — "the current approval" is derived from the most recent <c>approve</c> transition's
/// timestamp, so a fixed clock cannot distinguish it from an earlier, already-superseded one.
/// </para>
/// </summary>
public sealed class CommandDispatcherBlockAmendmentRequestedTests
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    // The blocker's own regression guard: approved -> recertified successfully -> amendment-
    // requested -> briefed with round incremented, then rebuilt, re-approved, and granted a FRESH
    // recertification. This is the property block C shipped with no route to at all — before this
    // remediation, 'amendment-requested' did not exist as a transition or a verb, so a card that
    // had spent its one recertification had 'land' as its only legal move out of 'approved'.
    [Fact]
    public void AmendmentRequested_AfterASuccessfulRecertification_ReturnsToBriefed_IncrementsRound_AndPermitsAFreshApprovalAndRecertification()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo, "b-0001", "B-0001", BlockFlowState.Drafting);

        EnterInReview(repo, path, firstRound: true);
        var round1ClaimIds = Approve(repo, path, "B-0001", "commit-round1", "round one claim");

        // Successful recertification: spends the one recertification this approval permits: round
        // does not move, status stays approved.
        Assert.Equal(CommandDispatcher.SuccessExitCode, RunInRepo(
            [
                "block", "recertify", "--id", "B-0001", "--role", "reviewer", "--state", "commit-round1-amended",
                "--assert", round1ClaimIds[0], "--change", ChangeName,
            ],
            new StringWriter(), repo));
        var afterRecertification = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("approved", afterRecertification.Frontmatter.Status);
        Assert.Equal(1, afterRecertification.BlockFields.Round);

        // The blocker: a further amendment now has no route back to briefed except this verb.
        // Confirmed by hand before landing this remediation: with 'amendment-requested' absent
        // from BlockFlowTransitions.AvailableFrom(Approved) (the pre-fix table), this call fails
        // with 'undefined-transition' instead of succeeding — the same failure the reviewer's
        // blocker post traced through 'block recertify' (recertification-already-performed) and
        // 'block transition ... recertification-refused' (refused at parse), leaving 'land' as the
        // only door out of 'approved'.
        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["block", "amendment-requested", "--id", "B-0001", "--role", "architect", "--change", ChangeName],
            output, repo);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("amendment-requested", result.GetProperty("transition").GetString());
        Assert.Equal("approved", result.GetProperty("from").GetString());
        Assert.Equal("briefed", result.GetProperty("to").GetString());
        Assert.Equal(2, result.GetProperty("round").GetInt32());

        var afterAmendmentRequested = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("briefed", afterAmendmentRequested.Frontmatter.Status);
        Assert.Equal(2, afterAmendmentRequested.BlockFields.Round);
        // reviewed_state is untouched by the transition itself — still the recertified state, not
        // cleared or blanked.
        Assert.Equal("commit-round1-amended", afterAmendmentRequested.BlockFields.ReviewedState);
        var transition = afterAmendmentRequested.Transitions.Last();
        Assert.Equal("amendment-requested", transition.Name);
        Assert.Equal(CardOwner.Architect, transition.By);
        Assert.Equal(BlockFlowState.Approved, transition.From);
        Assert.Equal(BlockFlowState.Briefed, transition.To);

        // Rebuilt, re-approved: a NEW approval, on the SAME card, at round 2.
        EnterInReview(repo, path, firstRound: false);
        var round2ClaimIds = Approve(repo, path, "B-0001", "commit-round2", "round two claim");
        Assert.NotEqual(round1ClaimIds[0], round2ClaimIds[0]);
        var afterReapproval = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("approved", afterReapproval.Frontmatter.Status);
        Assert.Equal(2, afterReapproval.BlockFields.Round);

        // 8.10's own bound is scoped to the approval, not the card (Architect ruling): this
        // approval has never been recertified, so it gets a FRESH recertification even though the
        // card as a whole has already been recertified once, in an earlier round.
        var freshOutput = new StringWriter();
        var freshExitCode = RunInRepo(
            [
                "block", "recertify", "--id", "B-0001", "--role", "reviewer", "--state", "commit-round2-amended",
                "--assert", round2ClaimIds[0], "--change", ChangeName,
            ],
            freshOutput, repo);

        Assert.Equal(CommandDispatcher.SuccessExitCode, freshExitCode);
        using var freshDoc = JsonDocument.Parse(freshOutput.ToString());
        Assert.Equal("commit-round2-amended", freshDoc.RootElement.GetProperty("result").GetProperty("reviewedState").GetString());
        var final = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("approved", final.Frontmatter.Status);
        Assert.Equal(2, final.BlockFields.Round);
    }

    // work-lifecycle: "`amendment-requested` is the architect deliberately reopening an approved
    // block" — role-bounded to architect, unlike the generic block-transition path, which only
    // records the acting role rather than restricting it.
    [Fact]
    public void AmendmentRequested_NonArchitectRole_Refuses_AndLeavesTheCardByteIdentical()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo, "b-0002", "B-0002", BlockFlowState.InReview);
        Approve(repo, path, "B-0002", "commit-abc", "claim one");
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "amendment-requested", "--id", "B-0002", "--role", "reviewer", "--change", ChangeName],
            output, repo);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("role-not-permitted", refusal.GetProperty("code").GetString());
        Assert.Contains("architect", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void AmendmentRequested_NotCurrentlyApproved_Refuses_AndLeavesTheCardByteIdentical()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo, "b-0003", "B-0003", BlockFlowState.InReview);
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "amendment-requested", "--id", "B-0003", "--role", "architect", "--change", ChangeName],
            output, repo);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("undefined-transition", refusal.GetProperty("code").GetString());
        Assert.Contains("land", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // §8's one-door discipline, extended a fourth time: a bare transition through
    // 'amendment-requested' would move a block back to 'briefed' with no architect decision
    // actually recorded as having made that call — refused outright at parse, the same as
    // 'approve', 'fix-before-land' and 'recertification-refused'.
    [Fact]
    public void BlockTransition_AmendmentRequested_Refuses_AndLeavesTheCardByteIdentical()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo, "b-0004", "B-0004", BlockFlowState.InReview);
        Approve(repo, path, "B-0004", "commit-abc", "claim one");
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "transition", path, "amendment-requested", "--role", "architect", "--change", ChangeName],
            output, repo);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("amendment-requested-via-transition-refused", refusal.GetProperty("code").GetString());
        Assert.Contains("block amendment-requested", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void AmendmentRequested_WrongCardKind_Refuses()
    {
        using var repo = new TempGitRepo();
        var directory = Path.Combine(repo.Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        var questionPath = Path.Combine(directory, "q-0001.md");
        var frontmatter = new CardFrontmatter(
            "Q-0001", CardKind.Question, "A question", "open", CardOwner.Architect, CardScope.Change, "8", FixedNow, FixedNow);
        File.WriteAllText(questionPath, CardFileWriter.Serialize(new CardFile(frontmatter, "Body.", [], [])), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "amendment-requested", "--id", "Q-0001", "--role", "architect", "--change", ChangeName],
            output, repo);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("wrong-card-kind", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void AmendmentRequested_MissingId_RefusesWithMissingArgumentCode()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = RunInRepo(["block", "amendment-requested", "--role", "architect"], output, repo);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
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

    // Same shape as CommandDispatcherBlockRecertifyTests.EnterInReview — drives a card from its
    // current state into in-review via the plain flow edges.
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

    // Same advancing-clock idiom CommandDispatcherBlockRecertifyTests established (§8 block B
    // remediation) — ticking forward on every call so "since the current approval" round-scoping
    // genuinely has distinct instants to distinguish, rather than every event sharing one
    // timestamp.
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
            _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-block-amendment-requested-cli-tests-" + Guid.NewGuid().ToString("N"));
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
