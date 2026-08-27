using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// §13 block 13.3 at the CLI boundary: <c>block base --base &lt;sha&gt;</c> — the recording door
/// <c>block transition</c>'s own <c>base-not-recorded</c> refusal has named since §5 without one
/// existing. Path-addressed, not <c>--id</c> (Architect ruling item 1) — the third and last verb in
/// the flow 13.1 opened: <c>block create</c> → <c>block base</c> → <c>block transition --to
/// briefed</c>, proven end to end in <see
/// cref="BlockBase_ThenTransitionToBriefed_Succeeds_TheFlowThirteenOneOpened"/>.
/// </summary>
public sealed class CommandDispatcherBlockBaseTests
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BlockBase_AtDrafting_Succeeds()
    {
        using var repo = new TempGitRepo();
        var path = WriteBlockCard(repo, "b-0001", "B-0001", BlockFlowState.Drafting, baseCommit: null);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "base", path, "--base", "commit-abc", "--role", "architect", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("commit-abc", result.GetProperty("base").GetString());
        Assert.Equal("architect", result.GetProperty("actingRole").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("commit-abc", read.BlockFields.Base);
    }

    [Fact]
    public void BlockBase_AlreadyRecorded_Refuses_WithBaseImmutable_AndRecordsTheRefusal()
    {
        using var repo = new TempGitRepo();
        var path = WriteBlockCard(repo, "b-0002", "B-0002", BlockFlowState.Drafting, baseCommit: "commit-abc");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "base", path, "--base", "commit-xyz", "--role", "architect", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("base-immutable", refusal.GetProperty("code").GetString());
        Assert.Contains("commit-abc", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.NotNull(refusal.GetProperty("rule").GetString());
        Assert.NotNull(refusal.GetProperty("remedy").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("commit-abc", read.BlockFields.Base);
        Assert.Single(read.Refusals);
    }

    // Product Owner ruling (remediation on 13.3): an identical resupply is not a change, so it
    // succeeds — retry-safety for an agent that cannot tell whether an earlier call landed.
    [Fact]
    public void BlockBase_AlreadyRecorded_SameValue_Succeeds_AndRecordsNoRefusal()
    {
        using var repo = new TempGitRepo();
        var path = WriteBlockCard(repo, "b-0008", "B-0008", BlockFlowState.Drafting, baseCommit: "commit-abc");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "base", path, "--base", "commit-abc", "--role", "architect", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("commit-abc", doc.RootElement.GetProperty("result").GetProperty("base").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("commit-abc", read.BlockFields.Base);
        Assert.Empty(read.Refusals);
    }

    [Fact]
    public void BlockBase_CardNotAtDrafting_Refuses_WithNotAtDrafting()
    {
        using var repo = new TempGitRepo();
        var path = WriteBlockCard(repo, "b-0003", "B-0003", BlockFlowState.Briefed, baseCommit: "commit-abc", round: 1);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "base", path, "--base", "commit-xyz", "--role", "architect", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("not-at-drafting", refusal.GetProperty("code").GetString());
        Assert.Contains("briefed", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void BlockBase_MissingBaseFlag_Refuses_WithMissingArgument()
    {
        using var repo = new TempGitRepo();
        var path = WriteBlockCard(repo, "b-0004", "B-0004", BlockFlowState.Drafting, baseCommit: null);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "base", path, "--role", "architect", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // Ruling item 1: --base appears before --role in argv — proves the parser does not break on
    // the first flag it does not recognise before every flag it does is consumed.
    [Fact]
    public void BlockBase_BaseFlagBeforeRoleFlag_StillParses()
    {
        using var repo = new TempGitRepo();
        var path = WriteBlockCard(repo, "b-0005", "B-0005", BlockFlowState.Drafting, baseCommit: null);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "base", path, "--base", "commit-abc", "--role", "architect", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
    }

    // The refusal at CommandDispatcher.cs's own onBaseNotRecorded arm now names a real command
    // (Architect ruling item 4).
    [Fact]
    public void BlockTransition_ToBriefed_NoBaseRecordedOrSupplied_RefusalNamesBlockBase()
    {
        using var repo = new TempGitRepo();
        var path = WriteBlockCard(repo, "b-0006", "B-0006", BlockFlowState.Drafting, baseCommit: null);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "transition", path, "brief", "--role", "architect", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("base-not-recorded", refusal.GetProperty("code").GetString());
        Assert.Contains("block base", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    // The flow 13.1 opened, joined up: create → base → transition to briefed.
    [Fact]
    public void BlockBase_ThenTransitionToBriefed_Succeeds_TheFlowThirteenOneOpened()
    {
        using var repo = new TempGitRepo();
        var createPath = Path.Combine(repo.CardsDirectory, "b-0007.md");
        var createOutput = new StringWriter();
        var createExit = RunInRepo(
            ["block", "create", createPath, "--title", "Flow", "--role", "architect", "--change", ChangeName, "--task", "13.3"],
            createOutput, repo.Path, "Body.");
        Assert.Equal(CommandDispatcher.SuccessExitCode, createExit);

        var baseOutput = new StringWriter();
        var baseExit = RunInRepo(
            ["block", "base", createPath, "--base", "commit-abc", "--role", "architect", "--change", ChangeName],
            baseOutput, repo.Path);
        Assert.Equal(CommandDispatcher.SuccessExitCode, baseExit);

        var transitionOutput = new StringWriter();
        var transitionExit = RunInRepo(
            ["block", "transition", createPath, "brief", "--role", "architect", "--change", ChangeName],
            transitionOutput, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, transitionExit);
        using var doc = JsonDocument.Parse(transitionOutput.ToString());
        Assert.Equal("commit-abc", doc.RootElement.GetProperty("result").GetProperty("base").GetString());
        Assert.Equal("briefed", doc.RootElement.GetProperty("result").GetProperty("to").GetString());
    }

    private static string WriteBlockCard(TempGitRepo repo, string fileStem, string id, BlockFlowState status, string? baseCommit, int? round = null)
    {
        Directory.CreateDirectory(repo.CardsDirectory);
        var path = Path.Combine(repo.CardsDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "Title", status.ToWireString(), CardOwner.Architect, CardScope.Change, "13", FixedNow, FixedNow);
        var blockFields = new BlockCardFields(Base: baseCommit, ReviewedState: null, Tasks: [], Round: round, BlockedBy: [], GateResults: []);
        var card = new CardFile(frontmatter, "Body.", [], [], [], blockFields, []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static int RunInRepo(string[] args, TextWriter output, string workingDirectory) =>
        CommandDispatcher.Run(
            args, output, TextReader.Null, TextWriter.Null, isInputRedirected: true, workingDirectory: workingDirectory, clock: static () => FixedNow);

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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-block-base-cli-tests-" + Guid.NewGuid().ToString("N"));
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
