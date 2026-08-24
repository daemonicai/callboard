using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// §8 block A at the CLI boundary: <c>block approve</c> — the only door to
/// <see cref="BlockFlowState.Approved"/>, review-certification's "Approve is binary and certifies
/// one state" / "Certification enumerates its claims". Same fixed-clock discipline every §5/§7 CLI
/// test class already established: the emitted timestamp is asserted exactly, not merely
/// "close to now".
///
/// <para>
/// <b>This class is also 8.2's inversion of <c>ReviewedStateProducerTests</c>.</b> That test
/// recorded the deferral ("no production code path sets <c>ReviewedState</c> until §8.2") and
/// required, by its own doc comment, that 8.2 replace it with a test proving the real producer
/// records the exact certified state — <see cref="BlockApprove_LegalApproval_Succeeds_RecordsExactReviewedStateClaimsAndLimits"/>
/// is that replacement.
/// </para>
/// </summary>
public sealed class CommandDispatcherBlockApproveTests
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BlockApprove_LegalApproval_Succeeds_RecordsExactReviewedStateClaimsAndLimits()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0001", "B-0001", BlockFlowState.InReview);
        const string state = "afaad73 + uncommitted working tree (src/Callboard/Cards/CardStore.cs)";
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "block", "approve", "--id", "B-0001", "--role", "reviewer",
                "--state", state,
                "--claims", "the refusal fires on an unresolved blocking finding",
                "--claims", "the write is atomic",
                "--limits", "does not certify the test suite as exhaustive",
                "--change", ChangeName,
            ],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal(state, result.GetProperty("reviewedState").GetString());
        var claims = result.GetProperty("claims").EnumerateArray().ToList();
        Assert.Equal(2, claims.Count);
        Assert.Equal("the refusal fires on an unresolved blocking finding", claims[0].GetProperty("text").GetString());
        Assert.False(string.IsNullOrWhiteSpace(claims[0].GetProperty("id").GetString()));
        Assert.NotEqual(claims[0].GetProperty("id").GetString(), claims[1].GetProperty("id").GetString());
        var limits = result.GetProperty("limits").EnumerateArray().Select(static e => e.GetString()).ToList();
        Assert.Equal(["does not certify the test suite as exhaustive"], limits);

        // The exact-state claim, proven at the record itself, not merely through the envelope.
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("approved", read.Frontmatter.Status);
        Assert.Equal(state, read.BlockFields.ReviewedState);
        Assert.Equal(2, read.Claims.Count);
        Assert.Single(read.Limits);
        var transition = Assert.Single(read.Transitions);
        Assert.Equal("approve", transition.Name);
        Assert.Equal(CardOwner.Reviewer, transition.By);
    }

    // §8 remediation blocker 3: '--claims'/'--limits' used to run every value through
    // SplitFrontmatterList (comma-separated), so a claim's own prose containing a comma was
    // silently split into two claims with two ids and no refusal — exactly the certification text
    // review-certification requires be "actionable by a reviewer who did not author it", which is
    // the text most likely to contain a comma. Now repeatable: one '--claims' occurrence is one
    // claim, comma and all, taken verbatim.
    [Fact]
    public void BlockApprove_ClaimTextContainingACommaIsPreservedAsOneClaim_NotSplit()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0012", "B-0012", BlockFlowState.InReview);
        const string claimWithComma = "refuses when gates are absent, failed, or stale";
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "approve", "--id", "B-0012", "--role", "reviewer", "--state", "commit-abc", "--claims", claimWithComma, "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var claims = doc.RootElement.GetProperty("result").GetProperty("claims").EnumerateArray().ToList();
        var claim = Assert.Single(claims);
        Assert.Equal(claimWithComma, claim.GetProperty("text").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(claimWithComma, Assert.Single(read.Claims).Text);
    }

    [Fact]
    public void BlockApprove_BySupervisor_Succeeds()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0002", "B-0002", BlockFlowState.InReview);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "approve", "--id", "B-0002", "--role", "supervisor", "--state", "commit-abc", "--claims", "claim one", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
    }

    // 8.3: the spec's own conjunction — "no claims and no limits" — not "no claims" alone.
    [Fact]
    public void BlockApprove_ClaimsOnly_NoLimits_Succeeds()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0003", "B-0003", BlockFlowState.InReview);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "approve", "--id", "B-0003", "--role", "reviewer", "--state", "commit-abc", "--claims", "claim one", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
    }

    [Fact]
    public void BlockApprove_LimitsOnly_NoClaims_Succeeds()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0004", "B-0004", BlockFlowState.InReview);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "approve", "--id", "B-0004", "--role", "reviewer", "--state", "commit-abc", "--limits", "limit one", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
    }

    // 8.3's actual refusal — neither claims nor limits enumerated.
    [Fact]
    public void BlockApprove_NoClaimsAndNoLimits_Refuses_AndLeavesTheCardByteIdentical()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0005", "B-0005", BlockFlowState.InReview);
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "approve", "--id", "B-0005", "--role", "reviewer", "--state", "commit-abc", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("no-claims-or-limits", refusal.GetProperty("code").GetString());
        Assert.Contains("later reviewer", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // 8.2: an empty/whitespace-only --state is refused; that is all this requirement asks.
    [Fact]
    public void BlockApprove_EmptyState_Refuses()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0006", "B-0006", BlockFlowState.InReview);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "approve", "--id", "B-0006", "--role", "reviewer", "--state", "   ", "--claims", "claim one", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("state-required", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // 8.1 / Architect ruling item 1: 'block approve' is the only door to 'approved' — the generic
    // transition path refuses the name outright.
    [Fact]
    public void BlockTransition_Approve_Refuses_AndLeavesTheCardByteIdentical()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0007", "B-0007", BlockFlowState.InReview);
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "transition", path, "approve", "--role", "reviewer", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("approve-via-transition-refused", refusal.GetProperty("code").GetString());
        Assert.Contains("block approve", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // review-certification: "Approval is role-bounded" — reviewer/supervisor only. Asserted with
    // 'worker', a role neither IsApprovingRole nor CardOwnerWireFormat's own parse fallback
    // defaults to, so a hardcoded pass cannot survive this test (§7 item E discipline).
    [Fact]
    public void BlockApprove_NonReviewingRole_Refuses_AndLeavesTheCardByteIdentical()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0008", "B-0008", BlockFlowState.InReview);
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "approve", "--id", "B-0008", "--role", "worker", "--state", "commit-abc", "--claims", "claim one", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("role-not-permitted", refusal.GetProperty("code").GetString());
        Assert.Contains("reviewer", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("supervisor", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void BlockApprove_NotInReview_RefusesWithUndefinedTransitionCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0009", "B-0009", BlockFlowState.Drafting);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "approve", "--id", "B-0009", "--role", "reviewer", "--state", "commit-abc", "--claims", "claim one", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("undefined-transition", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockApprove_InvalidClaimItem_Refuses()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0010", "B-0010", BlockFlowState.InReview);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "approve", "--id", "B-0010", "--role", "reviewer", "--state", "commit-abc", "--claims", "one", "--claims", "   ", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("invalid-claim", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockApprove_InvalidLimitItem_Refuses()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0011", "B-0011", BlockFlowState.InReview);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "approve", "--id", "B-0011", "--role", "reviewer", "--state", "commit-abc", "--limits", "two", "--limits", " ", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("invalid-limit", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockApprove_MissingId_RefusesWithMissingArgumentCode()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = RunInRepo(["block", "approve", "--role", "reviewer", "--state", "commit-abc", "--claims", "claim one"], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockApprove_WrongCardKind_RefusesWithWrongCardKindCode()
    {
        using var repo = new TempGitRepo();
        var directory = Path.Combine(repo.Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "q-0001.md");
        var frontmatter = new CardFrontmatter(
            "Q-0001", CardKind.Question, "A question", "open", CardOwner.Architect, CardScope.Change, "8", FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "approve", "--id", "Q-0001", "--role", "reviewer", "--state", "commit-abc", "--claims", "claim one", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("wrong-card-kind", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    private static string WriteInitialBlockCard(string repoRoot, string fileStem, string id, BlockFlowState status)
    {
        var directory = Path.Combine(repoRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "Title", status.ToWireString(), CardOwner.Architect, CardScope.Change, "8", FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static int RunInRepo(string[] args, TextWriter output, string workingDirectory) =>
        CommandDispatcher.Run(args, output, TextReader.Null, TextWriter.Null, isInputRedirected: true, workingDirectory: workingDirectory, clock: static () => FixedNow);

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _path;

        internal string Path => _path;

        internal TempGitRepo()
        {
            _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-block-approve-cli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_path);
            Directory.CreateDirectory(System.IO.Path.Combine(_path, ".git"));
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
