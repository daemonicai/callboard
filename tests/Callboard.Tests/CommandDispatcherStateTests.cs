using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// §10 block C at the CLI boundary: <c>state</c> — the derived state summary (working-context:
/// "a summary of overall process state") and escalation severity derived from question ownership.
/// Not role-scoped: no test here ever passes <c>--role</c> to a successful call, and one proves the
/// flag is refused rather than silently accepted.
/// </summary>
public sealed class CommandDispatcherStateTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";

    [Fact]
    public void RoleFlag_Refuses_AtTheDoor()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["state", "--role", "worker"], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("unrecognised-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void OutsideAnyGitRepository_Refuses_WithRepoRootNotFoundCode()
    {
        using var directory = new TempDirectory();
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["state"], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: directory.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("repo-root-not-found", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // working-context, Scenario "Summary is derived on request": every count in the response is
    // computed from the current record — one card of each kind the summary reports, none of it
    // hand-maintained anywhere.
    [Fact]
    public void Summary_IsDerivedOnRequest_AndReportsEveryPart()
    {
        using var repo = new TempGitRepo();
        WriteSection(repo, "s-0001", "S-0001", "open");
        WriteQuestion(repo, "q-0001", "Q-0001", CardOwner.Worker, "open");
        WriteObligation(repo, "o-0001", "O-0001", "open", owedBy: "S-0001");
        WriteBlockedBlock(repo, "b-0001", "B-0001", blockedBy: ["Q-0001"]);

        var result = State(repo);

        Assert.Single(result.GetProperty("openSections").EnumerateArray());
        Assert.Single(result.GetProperty("liveObligations").EnumerateArray());
        Assert.Single(result.GetProperty("openQuestions").EnumerateArray());
        Assert.Single(result.GetProperty("blockedCards").EnumerateArray());
        Assert.Single(result.GetProperty("taskCompletion").EnumerateArray());
    }

    [Fact]
    public void OpenSection_ReportedWithItsChange()
    {
        using var repo = new TempGitRepo();
        WriteSection(repo, "s-0001", "S-0001", "open");
        WriteSection(repo, "s-0002", "S-0002", "closed");

        var result = State(repo);

        var section = Assert.Single(result.GetProperty("openSections").EnumerateArray());
        Assert.Equal("S-0001", section.GetProperty("id").GetString());
        Assert.Equal(ChangeName, section.GetProperty("changeName").GetString());
    }

    [Fact]
    public void LiveObligation_ReportedWithTheSectionThatOwesIt()
    {
        using var repo = new TempGitRepo();
        WriteObligation(repo, "o-0001", "O-0001", "open", owedBy: "S-0001");
        WriteObligation(repo, "o-0002", "O-0002", "discharged", owedBy: "S-0001");

        var result = State(repo);

        var obligation = Assert.Single(result.GetProperty("liveObligations").EnumerateArray());
        Assert.Equal("O-0001", obligation.GetProperty("id").GetString());
        Assert.Equal("S-0001", obligation.GetProperty("owedBySectionId").GetString());
    }

    [Fact]
    public void OpenQuestion_ReportedWithWhoOwesItsAnswer()
    {
        using var repo = new TempGitRepo();
        WriteQuestion(repo, "q-0001", "Q-0001", CardOwner.Reviewer, "open");
        WriteQuestion(repo, "q-0002", "Q-0002", CardOwner.Worker, "answered");

        var result = State(repo);

        var question = Assert.Single(result.GetProperty("openQuestions").EnumerateArray());
        Assert.Equal("Q-0001", question.GetProperty("id").GetString());
        Assert.Equal("reviewer", question.GetProperty("owesAnswer").GetString());
    }

    // escalation-severity, Scenario "Product Owner question halts its dependents".
    [Fact]
    public void ProductOwnerQuestion_HaltsTheCardItBlocks()
    {
        using var repo = new TempGitRepo();
        WriteQuestion(repo, "q-0001", "Q-0001", CardOwner.ProductOwner, "open");
        WriteBlockedBlock(repo, "b-0001", "B-0001", blockedBy: ["Q-0001"]);

        var result = State(repo);

        var blocked = Assert.Single(result.GetProperty("blockedCards").EnumerateArray());
        Assert.Equal("B-0001", blocked.GetProperty("id").GetString());
        Assert.Equal(["Q-0001"], blocked.GetProperty("blockedByIds").EnumerateArray().Select(static e => e.GetString()));
        Assert.True(blocked.GetProperty("halted").GetBoolean());
        Assert.Equal("Q-0001", blocked.GetProperty("haltedByQuestionId").GetString());
    }

    // escalation-severity, Scenario "Agent-owned question does not halt".
    [Fact]
    public void ReviewerOwnedQuestion_LeavesTheBlockedCardAvailable_ButStillReportedAsBlocked()
    {
        using var repo = new TempGitRepo();
        WriteQuestion(repo, "q-0001", "Q-0001", CardOwner.Reviewer, "open");
        WriteBlockedBlock(repo, "b-0001", "B-0001", blockedBy: ["Q-0001"]);

        var result = State(repo);

        var blocked = Assert.Single(result.GetProperty("blockedCards").EnumerateArray());
        Assert.Equal("B-0001", blocked.GetProperty("id").GetString());
        Assert.False(blocked.GetProperty("halted").GetBoolean());
        Assert.False(blocked.TryGetProperty("haltedByQuestionId", out var haltId) && haltId.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public void TaskCompletion_CountedFromTheRealTasksMdFile()
    {
        using var repo = new TempGitRepo();
        // Deliberately exercises every edge case CommandDispatcherStateTests' own reviewer named,
        // not just the straightforward lowercase-and-flat case a hand-trace alone would stand in
        // for: a capitalised '[X]' (still ticked — TasksMdParser compares against literal " ", not
        // against lowercase 'x'), a nested/indented checkbox (counted toward the denominator, per
        // TasksMdParser's own doc comment — "regardless of which section it falls under" extends to
        // depth too), and a '- [ ]' appearing mid-sentence rather than at line start (must NOT
        // match at all: the checkbox anchor is "only whitespace before the dash").
        repo.WriteTasksMd(ChangeName,
            "## 1. Section\n\n" +
            "- [x] 1.1 Done\n" +
            "- [X] 1.2 Also done, capitalised\n" +
            "- [ ] 1.3 Not yet\n" +
            "  - [ ] 1.3a A nested sub-item, still not yet\n" +
            "The plan mentions \"- [ ] like this\" inline, but this line is not itself a checkbox.\n" +
            "\n## 2. Section two\n\n" +
            "- [ ] 2.1 Not yet either\n");

        var result = State(repo);

        var completion = Assert.Single(result.GetProperty("taskCompletion").EnumerateArray());
        Assert.Equal(ChangeName, completion.GetProperty("changeName").GetString());
        Assert.True(completion.GetProperty("tasksFileFound").GetBoolean());
        // Ticked: 1.1 and 1.2 (the capitalised 'X') — 2. Total: 1.1, 1.2, 1.3, 1.3a (nested), 2.1 —
        // 5. The mid-sentence mention counts toward neither.
        Assert.Equal(2, completion.GetProperty("ticked").GetInt32());
        Assert.Equal(5, completion.GetProperty("total").GetInt32());
    }

    [Fact]
    public void TaskCompletion_NoTasksMdFile_SaysSoRatherThanReportingZero()
    {
        using var repo = new TempGitRepo();
        // A live change directory exists (register cards alone would report no change at all), but
        // its openspec/changes/<name>/tasks.md was never written.
        WriteSection(repo, "s-0001", "S-0001", "open");

        var result = State(repo);

        var completion = Assert.Single(result.GetProperty("taskCompletion").EnumerateArray());
        Assert.Equal(ChangeName, completion.GetProperty("changeName").GetString());
        Assert.False(completion.GetProperty("tasksFileFound").GetBoolean());
        Assert.Equal(0, completion.GetProperty("ticked").GetInt32());
        Assert.Equal(0, completion.GetProperty("total").GetInt32());
    }

    // working-context, Scenario "Hand-entered state is not accepted" — at the one production CLI
    // verb that reaches CardStore's generic write surface (rule promote-constitution); the direct
    // CardStore-level proof that this refuses and records lives in CardStoreWriteTests (registered
    // in RefusalCoverageGateTests) — this is the CLI-layer half the coverage gate does not reach.
    [Fact]
    public void HandEnteredDerivedStateField_Refuses_AtTheCliLayer()
    {
        using var repo = new TempGitRepo();
        var path = System.IO.Path.Combine(repo.RegisterDirectory, "r-0001.md");
        var frontmatter = new CardFrontmatter("R-0001", CardKind.Rule, "A rule", "open", CardOwner.Architect, CardScope.Repository, string.Empty, FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], [("next_step", "do the thing")], RegisterFields: RegisterCardFields.Empty);
        WriteCard(path, card);

        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["rule", "promote-constitution", "--id", "R-0001", "--role", "architect"],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("hand-entered-derived-state", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Empty(read.Comments);
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, refusal.By);
    }

    private static void WriteSection(TempGitRepo repo, string fileStem, string id, string status)
    {
        var path = System.IO.Path.Combine(repo.ChangesDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Section, "A section", status, CardOwner.Architect, CardScope.Change, string.Empty, FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], [], SectionFields: SectionCardFields.Empty);
        WriteCard(path, card);
    }

    private static void WriteQuestion(TempGitRepo repo, string fileStem, string id, CardOwner owner, string status)
    {
        var path = System.IO.Path.Combine(repo.RegisterDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Question, "A question", status, owner, CardScope.Repository, string.Empty, FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], [], QuestionFields: QuestionCardFields.Empty);
        WriteCard(path, card);
    }

    private static void WriteObligation(TempGitRepo repo, string fileStem, string id, string status, string owedBy)
    {
        var path = System.IO.Path.Combine(repo.RegisterDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Obligation, "An obligation", status, CardOwner.Architect, CardScope.Repository, string.Empty, FixedNow, FixedNow);
        var registerFields = new RegisterCardFields(Condition: null, Cadence: null, DischargedBy: null, DischargedAt: null, OwedBy: owedBy);
        var card = new CardFile(frontmatter, "Body.", [], [], RegisterFields: registerFields);
        WriteCard(path, card);
    }

    private static void WriteBlockedBlock(TempGitRepo repo, string fileStem, string id, IReadOnlyList<string> blockedBy)
    {
        var path = System.IO.Path.Combine(repo.ChangesDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Block, "A block", "briefed", CardOwner.Worker, CardScope.Change, string.Empty, FixedNow, FixedNow);
        var blockFields = new BlockCardFields(Base: "base-commit", ReviewedState: null, Tasks: [], Round: null, BlockedBy: blockedBy, GateResults: []);
        var card = new CardFile(frontmatter, "Body.", [], [], BlockFields: blockFields);
        WriteCard(path, card);
    }

    private static void WriteCard(string path, CardFile card) =>
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match(
            onSuccess: static success => success.Card,
            onFailure: static failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));

    private static JsonElement State(TempGitRepo repo)
    {
        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["state"], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        return doc.RootElement.GetProperty("result").Clone();
    }

    private sealed class TempDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"callboard-state-cli-nongit-{Guid.NewGuid():N}");

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

        internal string ChangesDirectory { get; }

        internal string RegisterDirectory { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-state-cli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
            ChangesDirectory = System.IO.Path.Combine(Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(ChangesDirectory);
            RegisterDirectory = System.IO.Path.Combine(Path, CardLayout.RegisterDirectory.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(RegisterDirectory);
        }

        internal void WriteTasksMd(string changeName, string content)
        {
            var directory = System.IO.Path.Combine(Path, "openspec", "changes", changeName);
            Directory.CreateDirectory(directory);
            File.WriteAllText(System.IO.Path.Combine(directory, "tasks.md"), content);
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
