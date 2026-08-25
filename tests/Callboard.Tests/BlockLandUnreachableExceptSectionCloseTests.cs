using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// §8a block A, task 8a.16 (work-lifecycle: "Approval is provisional until the section closes" —
/// "`land` SHALL NOT be individually invocable. A block SHALL reach `landed` only as a consequence
/// of its section closing"). Not 8a.2's own restatement — that test proves the parse-time refusal
/// in isolation; this one exercises every caller-facing door against one <c>approved</c> block and
/// asserts each one either refuses outright or, if it legally moves the card, moves it somewhere
/// other than <c>landed</c> — leaving <c>landed</c> reachable through exactly one route.
/// </summary>
public sealed class BlockLandUnreachableExceptSectionCloseTests
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);

    // 'block transition <path> land' — refused outright at parse (8a.2's own construction site;
    // this call proves it as one of the doors this test enumerates).
    [Fact]
    public void BlockTransition_Land_Refuses_LeavesTheBlockApproved()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialApprovedBlockCard(repo.Path, "b-0001", "B-0001", "S-0001");

        var exitCode = RunInRepo(["block", "transition", path, "land", "--role", "architect", "--change", ChangeName], new StringWriter(), repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        Assert.Equal("approved", AssertParseSuccess(CardStore.ReadCard(path)).Frontmatter.Status);
    }

    // 'block transition <path> approve' — an approved block is not in-review, so this is an
    // ordinary undefined-transition refusal (approve is bound to 'block approve' regardless, but
    // this route is refused for a second, independent reason too: it is not even legal from here).
    [Fact]
    public void BlockTransition_Approve_Refuses_LeavesTheBlockApproved()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialApprovedBlockCard(repo.Path, "b-0002", "B-0002", "S-0002");

        var exitCode = RunInRepo(["block", "transition", path, "approve", "--role", "reviewer", "--change", ChangeName], new StringWriter(), repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        Assert.Equal("approved", AssertParseSuccess(CardStore.ReadCard(path)).Frontmatter.Status);
    }

    // 'block approve' itself — the dedicated door to 'approved', not to 'landed'; an already-
    // approved block is not in-review, so this refuses too.
    [Fact]
    public void BlockApprove_OnAnAlreadyApprovedBlock_Refuses_LeavesItApproved()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialApprovedBlockCard(repo.Path, "b-0003", "B-0003", "S-0003");

        var exitCode = RunInRepo(
            ["block", "approve", "--id", "B-0003", "--role", "reviewer", "--state", "some-state", "--claims", "Done.", "--change", ChangeName],
            new StringWriter(), repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        Assert.Equal("approved", AssertParseSuccess(CardStore.ReadCard(path)).Frontmatter.Status);
    }

    // The generic domain-level door, bypassing the CLI's own parse-time refusal entirely
    // (CardStore.ApplyBlockTransition, called the way ApplyBlockTransitionUnderExistingLock's own
    // AvailableFrom(approved) lookup would be reached if 'land' were still on that list) — proves
    // the closure is structural (BlockFlowTransitions.AvailableFrom no longer offers the edge at
    // all), not merely a CLI-layer refusal a caller bypassing the CLI could route around.
    [Fact]
    public void ApplyBlockTransition_Land_ThroughTheDomainLayerDirectly_IsUndefined_LeavesTheBlockApproved()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialApprovedBlockCard(repo.Path, "b-0004", "B-0004", "S-0004");

        var outcome = CardStore.ApplyBlockTransition(
            repo.Path, path, "land", CardOwner.Architect, FixedNow, baseCommit: null, TimeSpan.FromSeconds(5), ChangeName);

        Assert.IsType<CardBlockTransitionOutcome.UndefinedTransition>(outcome);
        Assert.Equal("approved", AssertParseSuccess(CardStore.ReadCard(path)).Frontmatter.Status);
    }

    // 'block amendment-requested' no longer exists at all (§8a block A revision, Product Owner
    // ruling: "approved is terminal") — the subcommand itself is gone, refused as an unrecognised
    // 'block' subcommand before any card is touched.
    [Fact]
    public void BlockAmendmentRequested_TheSubcommandItselfIsGone_Refuses_LeavesTheBlockApproved()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialApprovedBlockCard(repo.Path, "b-0005", "B-0005", "S-0005");

        var exitCode = RunInRepo(["block", "amendment-requested", "--id", "B-0005", "--role", "architect", "--change", ChangeName], new StringWriter(), repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        Assert.Equal("approved", AssertParseSuccess(CardStore.ReadCard(path)).Frontmatter.Status);
    }

    // 'block transition <path> amendment-requested' — no longer a special-cased parse refusal
    // either (that door does not exist to guard any more): it reaches ApplyBlockTransition like
    // any other unrecognised name and is refused as an ordinary undefined-transition, naming
    // 'approved' as having no available transitions at all — the stronger statement 8a.16 makes
    // once 'amendment-requested' is cut: an approved block has no caller-facing route back to
    // work, full stop.
    [Fact]
    public void BlockTransition_AmendmentRequested_IsAnOrdinaryUndefinedTransition_NamingNoAvailableEdges()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialApprovedBlockCard(repo.Path, "b-0009", "B-0009", "S-0009");

        var exitCode = RunInRepo(
            ["block", "transition", path, "amendment-requested", "--role", "architect", "--change", ChangeName], new StringWriter(), repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        Assert.Equal("approved", AssertParseSuccess(CardStore.ReadCard(path)).Frontmatter.Status);

        var outcome = CardStore.ApplyBlockTransition(
            repo.Path, path, "amendment-requested", CardOwner.Architect, FixedNow, baseCommit: null, TimeSpan.FromSeconds(5), ChangeName);
        var undefined = Assert.IsType<CardBlockTransitionOutcome.UndefinedTransition>(outcome);
        Assert.Equal(BlockFlowState.Approved, undefined.CurrentState);
        Assert.Empty(undefined.Available);
    }

    // The one door that does land it: 'section close'.
    [Fact]
    public void SectionClose_IsTheOnlyRouteThatLandsAnApprovedBlock()
    {
        using var repo = new TempGitRepo();
        var sectionPath = WriteInitialSectionCard(repo.Path, "s-0006", "S-0006");
        var blockPath = WriteInitialApprovedBlockCard(repo.Path, "b-0006", "B-0006", "S-0006");

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["section", "close", sectionPath, "--role", "architect", "--change", ChangeName], output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        Assert.Equal("landed", AssertParseSuccess(CardStore.ReadCard(blockPath)).Frontmatter.Status);
    }

    private static string WriteInitialApprovedBlockCard(string repoRoot, string fileStem, string id, string sectionId, string reviewedState = "reviewed-state")
    {
        var directory = Path.Combine(repoRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "Title", "approved", CardOwner.Architect, CardScope.Change, sectionId, FixedNow, FixedNow);
        var blockFields = new BlockCardFields(Base: "base-commit", ReviewedState: reviewedState, Tasks: ["5.1"], Round: null, BlockedBy: [], GateResults: []);
        var card = new CardFile(frontmatter, "Body.", [], [], [], blockFields, []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string WriteInitialSectionCard(string repoRoot, string fileStem, string id)
    {
        var directory = Path.Combine(repoRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Section, "Title", "open", CardOwner.Architect, CardScope.Change, string.Empty, FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, [], SectionCardFields.Empty);
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
        internal string Path { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-land-unreachable-tests-" + Guid.NewGuid().ToString("N"));
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
