using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// 13.1 at the CLI boundary: <c>block create</c> — the second minting door work-lifecycle's "Every
/// block card is minted by the tool" names, placing a task-implementing card at <see
/// cref="BlockFlowState.Drafting"/> and issuing its identity from <see
/// cref="CardIdentityAllocator"/>'s own counter rather than accepting a hand-authored one.
/// <see cref="BlockCreate_ARecordedIdentityIsRefused_AndRecordsAgainstTheCardAlreadyBearingIt"/> is
/// the load-bearing test: card-model's "the system SHALL refuse to issue an identity that a card in
/// the record already bears", provoked by seeding exactly the hand-authored-card scenario the §13
/// base post names as the defect this block closes.
/// </summary>
public sealed class CommandDispatcherBlockCreateTests
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BlockCreate_Succeeds_AtDrafting_WithTheNamedTasks()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.CardsDirectory, "b-0001.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "create", path, "--title", "13.1 — block create", "--role", "architect", "--change", ChangeName, "--task", "13.1"],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("B-0001", result.GetProperty("id").GetString());
        Assert.Equal("block", result.GetProperty("kind").GetString());
        Assert.Equal("change", result.GetProperty("scope").GetString());
        Assert.Equal("drafting", result.GetProperty("status").GetString());
        var tasks = result.GetProperty("tasks").EnumerateArray().Select(static e => e.GetString()).ToList();
        Assert.Equal(["13.1"], tasks);
        Assert.True(File.Exists(path));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("B-0001", read.Frontmatter.Id);
        Assert.Equal(BlockFlowState.Drafting.ToWireString(), read.Frontmatter.Status);
        Assert.Equal(["13.1"], read.BlockFields.Tasks);
        Assert.Equal(1, read.BlockFields.Round);
        Assert.Null(read.BlockFields.Base);
        Assert.Empty(read.BlockFields.BlockedBy);
        Assert.Empty(read.BlockFields.GateResults);
    }

    [Fact]
    public void BlockCreate_MultipleTasks_RecordsAllInArgvOrder()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.CardsDirectory, "b-0001.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "create", path, "--title", "T", "--role", "architect", "--change", ChangeName, "--task", "13.1", "--task", "13.2"],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(["13.1", "13.2"], read.BlockFields.Tasks);
    }

    [Fact]
    public void BlockCreate_NoTaskFlag_Refuses_AndWritesNoCard()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.CardsDirectory, "b-0001.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "create", path, "--title", "T", "--role", "architect", "--change", ChangeName],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("missing-argument", refusal.GetProperty("code").GetString());
        Assert.Contains("--task", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void BlockCreate_EmptyTaskReference_Refuses_AndWritesNoCard()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.CardsDirectory, "b-0001.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "create", path, "--title", "T", "--role", "architect", "--change", ChangeName, "--task", "   "],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("invalid-task-reference", refusal.GetProperty("code").GetString());
        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// card-model: "the system SHALL refuse to issue an identity that a card in the record already
    /// bears" — provoked exactly as the §13 base post describes the defect: a card hand-authored
    /// with the identity the counter is about to issue next (here, the never-yet-advanced counter's
    /// first allocation, <c>B-0001</c>, already borne by a hand-written file the allocator never
    /// minted). The refusal is recorded not against the card <c>block create</c> was asked to write
    /// — there isn't one — but against the pre-existing card already bearing the contested id.
    /// </summary>
    [Fact]
    public void BlockCreate_ARecordedIdentityIsRefused_AndRecordsAgainstTheCardAlreadyBearingIt()
    {
        using var repo = new TempGitRepo();
        var handAuthoredPath = WriteHandAuthoredBlockCard(repo.Path, "b-0001", "B-0001");
        var newPath = Path.Combine(repo.CardsDirectory, "b-0002.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "create", newPath, "--title", "Collides", "--role", "architect", "--change", ChangeName, "--task", "13.1"],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("identity-already-borne", refusal.GetProperty("code").GetString());
        Assert.Contains("B-0001", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("index rebuild", refusal.GetProperty("remedy").GetString(), StringComparison.Ordinal);
        Assert.False(File.Exists(newPath));

        var read = AssertParseSuccess(CardStore.ReadCard(handAuthoredPath));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.Contains("B-0001", recorded.Rule, StringComparison.Ordinal);
        Assert.Contains("index rebuild", recorded.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockCreate_ARecordedIdentityIsRefused_TheCountersOwnAdvanceIsNotUndone()
    {
        using var repo = new TempGitRepo();
        WriteHandAuthoredBlockCard(repo.Path, "b-0001", "B-0001");
        var firstAttempt = Path.Combine(repo.CardsDirectory, "b-0002.md");
        var output = new StringWriter();

        RunInRepo(
            ["block", "create", firstAttempt, "--title", "Collides", "--role", "architect", "--change", ChangeName, "--task", "13.1"],
            output, repo.Path, "Body.");

        var counterPath = Path.Combine(repo.Path, "callboard", "identities", "block.count");
        Assert.Equal("1", File.ReadAllText(counterPath).Trim());

        var secondAttemptOutput = new StringWriter();
        var secondPath = Path.Combine(repo.CardsDirectory, "b-0003.md");
        var exitCode = RunInRepo(
            ["block", "create", secondPath, "--title", "Succeeds", "--role", "architect", "--change", ChangeName, "--task", "13.1"],
            secondAttemptOutput, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(secondAttemptOutput.ToString());
        Assert.Equal("B-0002", doc.RootElement.GetProperty("result").GetProperty("id").GetString());
    }

    private static string WriteHandAuthoredBlockCard(string repoRoot, string fileStem, string id)
    {
        var directory = Path.Combine(repoRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "Hand-authored", BlockFlowState.Drafting.ToWireString(), CardOwner.Architect, CardScope.Change, "13", FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static int RunInRepo(string[] args, TextWriter output, string workingDirectory, string body) =>
        CommandDispatcher.Run(
            args, output, new StringReader(body), TextWriter.Null, isInputRedirected: true, workingDirectory: workingDirectory, clock: static () => FixedNow);

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));

    private sealed class TempGitRepo : IDisposable
    {
        internal string Path { get; }

        internal string CardsDirectory { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-block-create-cli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
            CardsDirectory = System.IO.Path.Combine(Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', System.IO.Path.DirectorySeparatorChar));
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
