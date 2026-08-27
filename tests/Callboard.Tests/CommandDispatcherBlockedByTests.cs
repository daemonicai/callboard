using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// 5.7 at the CLI boundary: <c>block add-blocker</c>/<c>block remove-blocker</c>. Both verbs share
/// one parse function (<c>CommandParser.ParseBlockedByMutation</c>) and one outcome mapping
/// (<c>CommandDispatcher.MapBlockedByOutcome</c>), so most refusal codes below have exactly one
/// construction site regardless of which verb reaches it — those are tested once, via whichever
/// verb reads most naturally, with a comment naming that the site is shared. The two verbs' own
/// distinct <c>repo-root-not-found</c> sites (one call site per <c>Run*</c> handler, the same shape
/// §5 block C's fourth remediation round found <c>block transition</c>'s sibling missing) each get
/// their own test — that gap is exactly what this class exists not to repeat.
/// </summary>
public sealed class CommandDispatcherBlockedByTests
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 22, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void AddBlocker_Succeeds_AndTheEnvelopeReportsBlockedTrue()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0001", "B-0001");
        WriteQuestionCard(repo.Path, "q-0001", "Q-0001");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "add-blocker", path, "Q-0001", "--role", "worker", "--change", ChangeName], output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("blocked").GetBoolean());
        Assert.Equal("Q-0001", Assert.Single(result.GetProperty("blockedBy").EnumerateArray()).GetString());
        Assert.Equal("worker", result.GetProperty("actingRole").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("building", read.Frontmatter.Status);
    }

    [Fact]
    public void RemoveBlocker_Succeeds_AndTheEnvelopeReportsBlockedFalse()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0002", "B-0002");
        WriteQuestionCard(repo.Path, "q-0001", "Q-0001");
        Assert.Equal(CommandDispatcher.SuccessExitCode, RunInRepo(
            ["block", "add-blocker", path, "Q-0001", "--role", "worker", "--change", ChangeName], TextWriter.Null, repo.Path));
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "remove-blocker", path, "Q-0001", "--role", "reviewer", "--change", ChangeName], output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("blocked").GetBoolean());
        Assert.Empty(result.GetProperty("blockedBy").EnumerateArray());
        Assert.Equal("reviewer", result.GetProperty("actingRole").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("building", read.Frontmatter.Status);
    }

    // Shared site (ParseBlockedByMutation): reached identically by add-blocker and remove-blocker.
    [Fact]
    public void AddBlocker_MissingFilePath_RefusesWithMissingArgumentCode()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = RunInRepo(["block", "add-blocker"], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // Shared site (ParseBlockedByMutation).
    [Fact]
    public void AddBlocker_MissingBlockingCardId_RefusesWithMissingArgumentCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0003", "B-0003");
        var output = new StringWriter();

        var exitCode = RunInRepo(["block", "add-blocker", path], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // Cross-block repair (§5 block E remediation, reviewer's requested sweep): landed in block D
    // (a52cd7a), found by the CLI-parser-vs-file-parser audit block E's own defect prompted.
    // CommandParser used to check only `blockingCardId is null`, so an empty or whitespace-only id
    // parsed clean and reached CardStore.UpdateBlockedByUnderExistingLock, where
    // BlockCardFields.BlockedBy's validating `init` accessor threw ArgumentException — surfacing as
    // an ungraceful `tool-failure` (exit 2) rather than a clean refusal. Unlike the section-verdict
    // defect this remediation exists for, the exception fired *before* AtomicWrite, so no card was
    // ever corrupted — a crash where a refusal belonged, not a write that poisoned the record.
    // Shared site (ParseBlockedByMutation): reached identically by add-blocker and remove-blocker.
    [Fact]
    public void AddBlocker_EmptyBlockingCardId_RefusesWithInvalidBlockingCardIdCode_AndWritesNothing()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0011", "B-0011");
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "add-blocker", path, "", "--role", "worker", "--change", ChangeName], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("invalid-blocking-card-id", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // Same construction site, reached via remove-blocker instead — proving the shared guard fires
    // for both verbs' parse arms, not just add-blocker's.
    [Fact]
    public void RemoveBlocker_WhitespaceOnlyBlockingCardId_RefusesWithInvalidBlockingCardIdCode_AndWritesNothing()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0012", "B-0012");
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "remove-blocker", path, "   ", "--role", "worker", "--change", ChangeName], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("invalid-blocking-card-id", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // Shared site (ParseRoleAndChangeFlags) — same construction the block gate tests already prove
    // for --role/--change dangling with no value and --role entirely absent; not repeated here.
    [Fact]
    public void RemoveBlocker_UnrecognisedRoleValue_RefusesWithUnrecognisedRoleCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0004", "B-0004");
        var output = new StringWriter();

        var exitCode = RunInRepo(["block", "remove-blocker", path, "Q-0001", "--role", "wizard"], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("unrecognised-role", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // RunBlockAddBlocker's own repo-root-not-found construction site.
    [Fact]
    public void AddBlocker_OutsideAnyGitRepository_RefusesWithRepoRootNotFoundCode()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "b-0001.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "add-blocker", path, "Q-0001", "--role", "worker", "--change", ChangeName], output, directory.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("repo-root-not-found", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // RunBlockRemoveBlocker's own, distinct repo-root-not-found construction site — the exact gap
    // shape §5 block C's fourth remediation round found: a sibling site next to a tested one.
    [Fact]
    public void RemoveBlocker_OutsideAnyGitRepository_RefusesWithRepoRootNotFoundCode()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "b-0001.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "remove-blocker", path, "Q-0001", "--role", "worker", "--change", ChangeName], output, directory.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("repo-root-not-found", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // Shared site (MapBlockedByOutcome).
    [Fact]
    public void AddBlocker_NotABlockCard_RefusesWithNotABlockCardCode()
    {
        using var repo = new TempGitRepo();
        var directory = Path.Combine(repo.Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "q-0001.md");
        var frontmatter = new CardFrontmatter(
            "Q-0001", CardKind.Question, "A question", "open", CardOwner.Architect, CardScope.Change, "5", FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "add-blocker", path, "Q-0002", "--role", "worker", "--change", ChangeName], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("wrong-card-kind", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // Shared site (MapBlockedByOutcome).
    [Fact]
    public void AddBlocker_CardNotFound_RefusesWithCardNotFoundCode()
    {
        using var repo = new TempGitRepo();
        var directory = Path.Combine(repo.Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "missing.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "add-blocker", path, "Q-0001", "--role", "worker", "--change", ChangeName], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("card-not-found", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // Shared site (MapBlockedByOutcome).
    [Fact]
    public void AddBlocker_LayoutMismatch_RefusesWithCardLayoutMismatchCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0005", "B-0005");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "add-blocker", path, "Q-0001", "--role", "worker", "--change", "a-different-change"], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("card-layout-mismatch", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // add-blocker's own op-specific case.
    [Fact]
    public void AddBlocker_AlreadyBlockedBy_RefusesWithAlreadyBlockedByCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0006", "B-0006");
        WriteQuestionCard(repo.Path, "q-0001", "Q-0001");
        Assert.Equal(CommandDispatcher.SuccessExitCode, RunInRepo(
            ["block", "add-blocker", path, "Q-0001", "--role", "worker", "--change", ChangeName], TextWriter.Null, repo.Path));
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "add-blocker", path, "Q-0001", "--role", "worker", "--change", ChangeName], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("already-blocked-by", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // remove-blocker's own op-specific case.
    [Fact]
    public void RemoveBlocker_NotBlockedBy_RefusesWithNotBlockedByCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0007", "B-0007");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "remove-blocker", path, "Q-0001", "--role", "worker", "--change", ChangeName], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("not-blocked-by", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // Shared site (MapBlockedByOutcome).
    [Fact]
    public void AddBlocker_CorruptCard_ExitsAsRefusal_NotAToolFailure()
    {
        using var repo = new TempGitRepo();
        var directory = Path.Combine(repo.Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "corrupt.md");
        File.WriteAllText(path, "not a card file at all");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["block", "add-blocker", path, "Q-0001", "--role", "worker", "--change", ChangeName],
            output, TextReader.Null, error, isInputRedirected: true, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        Assert.NotEqual(CommandDispatcher.ToolFailureExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-corrupt", refusal.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(refusal.GetProperty("message").GetString()));
        Assert.True(string.IsNullOrWhiteSpace(error.ToString()));
    }

    // Shared site (MapBlockedByOutcome).
    [Fact]
    public void AddBlocker_LockTimeout_ExitsAsToolFailure_NotARefusal()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0008", "B-0008");
        var before = File.ReadAllBytes(path);
        var holder = CardLock.Acquire(path, TimeSpan.FromSeconds(5)).Match(
            onAcquired: static acquired => acquired.Lock,
            onTimedOut: static timedOut => throw new Xunit.Sdk.XunitException($"setup: expected to acquire the lock, timed out: {timedOut.Message}"));

        try
        {
            var output = new StringWriter();
            var error = new StringWriter();

            var exitCode = CommandDispatcher.Run(
                ["block", "add-blocker", path, "Q-0001", "--role", "worker", "--change", ChangeName],
                output, TextReader.Null, error, isInputRedirected: true, workingDirectory: repo.Path,
                clock: static () => FixedNow, lockTimeout: TimeSpan.FromMilliseconds(200));

            Assert.Equal(CommandDispatcher.ToolFailureExitCode, exitCode);
            using var doc = JsonDocument.Parse(output.ToString());
            Assert.Equal("tool-failure", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            holder.Dispose();
        }
    }

    private static string WriteInitialBlockCard(string repoRoot, string fileStem, string id)
    {
        var directory = Path.Combine(repoRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "Title", "building", CardOwner.Worker, CardScope.Change, "5", FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    // §11 block A: a blocker id must resolve, so a fixture that adds one has to have created a
    // real card carrying it first.
    private static void WriteQuestionCard(string repoRoot, string fileStem, string id)
    {
        var directory = Path.Combine(repoRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Question, "A question", "open", CardOwner.Architect, CardScope.Change, "5", FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static int RunInRepo(string[] args, TextWriter output, string workingDirectory) =>
        CommandDispatcher.Run(args, output, TextReader.Null, TextWriter.Null, isInputRedirected: true, workingDirectory: workingDirectory, clock: static () => FixedNow);

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));

    private sealed class TempDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"callboard-blocked-by-cli-nongit-{Guid.NewGuid():N}");

        internal TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class TempGitRepo : IDisposable
    {
        internal string Path { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-blocked-by-cli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
