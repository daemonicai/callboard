using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// §8 block C remediation at the CLI boundary: <c>block amendment-requested</c> — work-lifecycle's
/// "`amendment-requested` is the architect deliberately reopening an approved block" — the only
/// route from <c>approved</c> back to work that is not a supervisor's recurrence (Product Owner
/// ruling: cutting `recertify`). An approval certifies one exact state; any change to that state
/// spends it, and this transition is how the block is handed back for the fresh review that change
/// requires — not, as <c>recertify</c> once was, a re-assertion of the same claims over the
/// difference.
///
/// <para>
/// Every call in this file routes through <see cref="TempGitRepo.Clock"/>'s
/// <see cref="AdvancingClock"/> — "the current approval" is derived from the most recent
/// <c>approve</c> transition's timestamp, so a fixed clock cannot distinguish it from an earlier,
/// already-superseded one.
/// </para>
/// </summary>
public sealed class CommandDispatcherBlockAmendmentRequestedTests
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    // approved -> amendment-requested -> briefed with round incremented, then rebuilt, re-approved,
    // and amendment-requested a SECOND time — unlike the cut recertify's one-shot bound,
    // amendment-requested is not spent by use: it is the architect's own deliberate act, available
    // every time an approval needs to be reopened, not limited to once per approval.
    [Fact]
    public void AmendmentRequested_ReturnsToBriefed_IncrementsRound_AndPermitsRepeatedUse()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo, "b-0001", "B-0001", BlockFlowState.Drafting);

        EnterInReview(repo, path, firstRound: true);
        var round1ClaimIds = Approve(repo, path, "B-0001", "commit-round1", "round one claim");

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
        // reviewed_state is untouched by the transition itself — still the certified state, not
        // cleared or blanked.
        Assert.Equal("commit-round1", afterAmendmentRequested.BlockFields.ReviewedState);
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

        // A second amendment-requested on the same card: not a one-shot verb.
        var secondOutput = new StringWriter();
        var secondExitCode = RunInRepo(
            ["block", "amendment-requested", "--id", "B-0001", "--role", "architect", "--change", ChangeName],
            secondOutput, repo);

        Assert.Equal(CommandDispatcher.SuccessExitCode, secondExitCode);
        var final = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("briefed", final.Frontmatter.Status);
        Assert.Equal(3, final.BlockFields.Round);
        Assert.Equal("commit-round2", final.BlockFields.ReviewedState);
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

    // §8's one-door discipline: a bare transition through 'amendment-requested' would move a
    // block back to 'briefed' with no architect decision actually recorded as having made that
    // call — refused outright at parse, the same as 'approve' and 'fix-before-land'.
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

    // Defence in depth (§8 remediation, CardAmendmentRequestOutcome.UndispositionedNits's own doc
    // comment): not reachable through the tool's own writers now that 'nit raise' is bound to
    // 'in-review' — RecordApprovalUnderExistingLock already refuses to certify a card carrying a
    // live nit, so an 'approved' card can never legitimately acquire one. The record is a plain,
    // git-committed file a human can hand-edit directly (ADR-0003), so the live nit is seeded
    // straight into the card rather than through 'nit raise', proving the guard this method carries
    // still fires against that path.
    [Fact]
    public void AmendmentRequested_ApprovedCardWithHandEditedLiveNit_Refuses_AndLeavesTheCardByteIdentical()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo, "b-0005", "B-0005", BlockFlowState.InReview);
        Approve(repo, path, "B-0005", "commit-abc", "claim one");

        var approved = AssertParseSuccess(CardStore.ReadCard(path));
        var liveNit = new CardComment(
            Id: "nit-hand-edited-0005", Author: CardOwner.Reviewer, Timestamp: FixedNow, Body: "A nit.",
            ReplyTo: null, To: CardOwner.Architect, Resolves: null, UnknownHeaderFields: [],
            IsNit: true, Required: false, Sites: []);
        var withHandEditedNit = approved with { Comments = [.. approved.Comments, liveNit] };
        File.WriteAllText(path, CardFileWriter.Serialize(withHandEditedNit), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "amendment-requested", "--id", "B-0005", "--role", "architect", "--change", ChangeName],
            output, repo);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("undispositioned-nits", refusal.GetProperty("code").GetString());
        Assert.Contains("nit-hand-edited-0005", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
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

    // Drives a card from its current state into in-review via the plain flow edges.
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

    // Ticks forward on every call so "since the current approval" round-scoping genuinely has
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
