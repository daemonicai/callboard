using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// §12 block B, tasks 12.1–12.3 — <c>view --out &lt;path&gt;</c> (record-retrieval: "a local,
/// read-only, human-readable view of the board ... showing cards by column and owner, what is
/// blocked and on what, and the open questions with who owes each answer"; binding ADR D5: one
/// self-contained HTML file, inline CSS, no server, no build step). Product Owner ruling (§12
/// block B rework): a column is a flow state, not a card kind — lanes by flow vocabulary, columns
/// by that vocabulary's states, with the register rendered as its own area below the flow lanes.
///
/// <para>
/// <b>12.3, the half that belongs here:</b> generating the view alters no state — every card file
/// byte-identical before and after, no index created, and the emitted HTML carries no mechanism
/// by which a reader could alter anything. The other half — that it renders correctly in a real
/// browser — is the Product Owner's own human-in-the-loop confirmation and is not asserted by any
/// test in this file.
/// </para>
/// </summary>
public sealed class BoardViewTests
{
    private static readonly DateTimeOffset Earlier = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void View_MissingOut_Refuses()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["view"], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        Assert.Equal("missing-argument", RefusalCode(output));
    }

    [Fact]
    public void View_EmptyBoard_WritesAValidPage()
    {
        using var repo = new TempGitRepo();
        var outPath = Path.Combine(repo.Path, "board.html");
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["view", "--out", outPath], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal(outPath, doc.RootElement.GetProperty("result").GetProperty("outputPath").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("result").GetProperty("cardCount").GetInt32());

        var html = File.ReadAllText(outPath);
        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
        Assert.Contains("<h2>Board</h2>", html, StringComparison.Ordinal);
        Assert.Contains("<h2>Register</h2>", html, StringComparison.Ordinal);
        // Every lane's every column still renders, empty — a board with nothing live is still a
        // valid page, not a broken one.
        Assert.Contains("No cards.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"badge\">blocked</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void View_TargetExists_RefusesWithoutForce_AndWritesWithForce()
    {
        using var repo = new TempGitRepo();
        var outPath = Path.Combine(repo.Path, "board.html");
        File.WriteAllText(outPath, "pre-existing, unrelated content");

        var refusedOutput = new StringWriter();
        var refusedExit = CommandDispatcher.Run(
            ["view", "--out", outPath], refusedOutput, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);
        Assert.Equal(CommandDispatcher.RefusalExitCode, refusedExit);
        Assert.Equal("export-target-exists", RefusalCode(refusedOutput));
        Assert.Equal("pre-existing, unrelated content", File.ReadAllText(outPath));

        var forcedOutput = new StringWriter();
        var forcedExit = CommandDispatcher.Run(
            ["view", "--out", outPath, "--force"], forcedOutput, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);
        Assert.Equal(CommandDispatcher.SuccessExitCode, forcedExit);
        Assert.Contains("<!doctype html>", File.ReadAllText(outPath), StringComparison.Ordinal);
    }

    [Fact]
    public void View_BlockLane_PlacesCardsInTheirOwnFlowStateColumn_GroupedByOwner()
    {
        using var repo = new TempGitRepo();
        WriteSection(repo, "s-0001.md", "S-0001", "A section", CardOwner.Architect, "open");
        WriteBlock(repo, "b-0001.md", "B-0001", "Still drafting", CardOwner.Worker, "S-0001", "drafting");
        WriteBlock(repo, "b-0002.md", "B-0002", "In review now", CardOwner.Reviewer, "S-0001", "in-review");

        var outPath = Path.Combine(repo.Path, "board.html");
        var exitCode = CommandDispatcher.Run(
            ["view", "--out", outPath], new StringWriter(), TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        var html = File.ReadAllText(outPath);

        Assert.Contains("<h3>Block</h3>", html, StringComparison.Ordinal);
        Assert.Contains("<h4>Drafting</h4>", html, StringComparison.Ordinal);
        Assert.Contains("<h4>In review</h4>", html, StringComparison.Ordinal);
        Assert.Contains("<h4>Briefed</h4>", html, StringComparison.Ordinal);
        Assert.Contains("<h4>Building</h4>", html, StringComparison.Ordinal);
        Assert.Contains("<h4>Approved</h4>", html, StringComparison.Ordinal);
        Assert.Contains("<h4>Landed</h4>", html, StringComparison.Ordinal);
        Assert.Contains("<h4>Closed</h4>", html, StringComparison.Ordinal);
        Assert.Contains("worker", html, StringComparison.Ordinal);
        Assert.Contains("reviewer", html, StringComparison.Ordinal);
        Assert.Contains("B-0001", html, StringComparison.Ordinal);
        Assert.Contains("B-0002", html, StringComparison.Ordinal);
    }

    [Fact]
    public void View_QuestionLane_FoldsDeferredIntoTheOpenColumn_AnsweredStaysSeparate()
    {
        using var repo = new TempGitRepo();
        WriteQuestion(repo, "q-0001.md", "Q-0001", "Still open", CardOwner.ProductOwner, "open");
        WriteQuestion(repo, "q-0002.md", "Q-0002", "Deferred, not answered", CardOwner.Architect, "deferred");
        WriteQuestion(repo, "q-0003.md", "Q-0003", "Already answered", CardOwner.Worker, "answered");

        var outPath = Path.Combine(repo.Path, "board.html");
        var exitCode = CommandDispatcher.Run(
            ["view", "--out", outPath], new StringWriter(), TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        var html = File.ReadAllText(outPath);

        Assert.Contains("<h3>Question</h3>", html, StringComparison.Ordinal);
        // §10 ruling 2: a deferred question is not a softer third state — exactly two columns.
        Assert.DoesNotContain("<h4>Deferred</h4>", html, StringComparison.Ordinal);
        Assert.Contains("<h4>Answered</h4>", html, StringComparison.Ordinal);

        var openColumnIndex = html.IndexOf("<h3>Question</h3>", StringComparison.Ordinal);
        Assert.True(openColumnIndex >= 0);
        var questionLaneHtml = html[openColumnIndex..(html.IndexOf("</div>\n</div>\n", openColumnIndex, StringComparison.Ordinal) + 1)];
        Assert.Contains("Q-0001", questionLaneHtml, StringComparison.Ordinal);
        Assert.Contains("Q-0002", questionLaneHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void View_FindingLane_IsASingleColumn()
    {
        using var repo = new TempGitRepo();
        WriteFinding(repo, "f-0001.md", "F-0001", "A finding", CardOwner.Reviewer);

        var outPath = Path.Combine(repo.Path, "board.html");
        var exitCode = CommandDispatcher.Run(
            ["view", "--out", outPath], new StringWriter(), TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        var html = File.ReadAllText(outPath);

        var laneStart = html.IndexOf("<h3>Finding</h3>", StringComparison.Ordinal);
        Assert.True(laneStart >= 0);
        var laneEnd = html.IndexOf("</div>\n</div>\n", laneStart, StringComparison.Ordinal);
        var laneHtml = html[laneStart..laneEnd];

        Assert.Equal(1, CountOccurrences(laneHtml, "<h4>"));
        Assert.Contains("F-0001", laneHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void View_RegisterArea_GroupsByKind_OpenAgainstDischarged()
    {
        using var repo = new TempGitRepo();
        WriteRegisterCard(repo, "r-0001.md", "R-0001", CardKind.Rule, "A live rule", CardOwner.Architect, "open");
        WriteRegisterCard(repo, "o-0001.md", "O-0001", CardKind.Obligation, "A settled obligation", CardOwner.Supervisor, "discharged");

        var outPath = Path.Combine(repo.Path, "board.html");
        var exitCode = CommandDispatcher.Run(
            ["view", "--out", outPath], new StringWriter(), TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        var html = File.ReadAllText(outPath);

        var registerStart = html.IndexOf("<h2>Register</h2>", StringComparison.Ordinal);
        Assert.True(registerStart >= 0);
        var registerHtml = html[registerStart..];

        Assert.Contains("<h3>Rule</h3>", registerHtml, StringComparison.Ordinal);
        Assert.Contains("<h3>Obligation</h3>", registerHtml, StringComparison.Ordinal);
        Assert.Contains("<h3>Hazard</h3>", registerHtml, StringComparison.Ordinal);
        Assert.Contains("<h3>Decision</h3>", registerHtml, StringComparison.Ordinal);
        Assert.Contains("<h4>Open</h4>", registerHtml, StringComparison.Ordinal);
        Assert.Contains("<h4>Discharged</h4>", registerHtml, StringComparison.Ordinal);
        Assert.Contains("R-0001", registerHtml, StringComparison.Ordinal);
        Assert.Contains("O-0001", registerHtml, StringComparison.Ordinal);

        // Register cards never occupy a flow state, so the register area is not part of the Board
        // section's lanes.
        var boardHtml = html[..registerStart];
        Assert.DoesNotContain("R-0001", boardHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("O-0001", boardHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void View_BlockedCard_ShowsWhatItIsBlockedOn_InlineOnTheCardInItsLane()
    {
        using var repo = new TempGitRepo();
        WriteQuestion(repo, "q-0001.md", "Q-0001", "Should the board show closed cards?", CardOwner.ProductOwner, "open");
        WriteSection(repo, "s-0001.md", "S-0001", "A section", CardOwner.Architect, "open");
        WriteBlock(repo, "b-0001.md", "B-0001", "A blocked block", CardOwner.Worker, "S-0001", "briefed", blockedBy: ["Q-0001"]);

        var outPath = Path.Combine(repo.Path, "board.html");
        var exitCode = CommandDispatcher.Run(
            ["view", "--out", outPath], new StringWriter(), TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        var html = File.ReadAllText(outPath);

        // The annotation sits right after the card entry, inside the Block lane's Briefed column —
        // not in a separate summary section.
        var cardIndex = html.IndexOf("B-0001", StringComparison.Ordinal);
        var blockedOnIndex = html.IndexOf("blocked on:", StringComparison.Ordinal);
        Assert.True(cardIndex >= 0 && blockedOnIndex > cardIndex && blockedOnIndex - cardIndex < 400);
        Assert.Contains("halted", html, StringComparison.Ordinal);
        Assert.Contains("Q-0001", html, StringComparison.Ordinal);
        Assert.Contains("Should the board show closed cards?", html, StringComparison.Ordinal);

        Assert.DoesNotContain("<section class=\"blocked\">", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<section class=\"open-questions\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public void View_OpenQuestion_ShowsWhoOwesTheAnswer_InlineOnTheCardInItsLane()
    {
        using var repo = new TempGitRepo();
        WriteQuestion(repo, "q-0001.md", "Q-0001", "A question owed by the architect", CardOwner.Architect, "open");

        var outPath = Path.Combine(repo.Path, "board.html");
        var exitCode = CommandDispatcher.Run(
            ["view", "--out", outPath], new StringWriter(), TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        var html = File.ReadAllText(outPath);

        var cardIndex = html.IndexOf("Q-0001", StringComparison.Ordinal);
        var owedByIndex = html.IndexOf("owed by", StringComparison.Ordinal);
        Assert.True(cardIndex >= 0 && owedByIndex > cardIndex && owedByIndex - cardIndex < 400);
        Assert.Contains("owed by <span class=\"badge\">architect</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void View_BlockedOnAClosedCard_NamesItAndMarksItClosed()
    {
        using var repo = new TempGitRepo();
        WriteSection(repo, "s-0001.md", "S-0001", "A closed section", CardOwner.Architect, "closed");
        WriteBlock(repo, "b-0001.md", "B-0001", "Blocked on a closed section", CardOwner.Worker, "S-0001", "briefed", blockedBy: ["S-0001"]);

        var outPath = Path.Combine(repo.Path, "board.html");
        var exitCode = CommandDispatcher.Run(
            ["view", "--out", outPath], new StringWriter(), TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        var html = File.ReadAllText(outPath);
        Assert.Contains("A closed section", html, StringComparison.Ordinal);
        Assert.Contains("(closed)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void View_HtmlEscapesCardText()
    {
        using var repo = new TempGitRepo();
        WriteSection(repo, "s-0001.md", "S-0001", "<script>alert(1)</script> & \"quoted\"", CardOwner.Architect, "open");

        var outPath = Path.Combine(repo.Path, "board.html");
        var exitCode = CommandDispatcher.Run(
            ["view", "--out", outPath], new StringWriter(), TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        var html = File.ReadAllText(outPath);
        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&amp;", html, StringComparison.Ordinal);
        Assert.Contains("&quot;quoted&quot;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void View_CardCount_SumsAcrossBoardAndRegister()
    {
        using var repo = new TempGitRepo();
        WriteSection(repo, "s-0001.md", "S-0001", "A section", CardOwner.Architect, "open");
        WriteBlock(repo, "b-0001.md", "B-0001", "A block", CardOwner.Worker, "S-0001", "briefed");
        WriteQuestion(repo, "q-0001.md", "Q-0001", "A question", CardOwner.ProductOwner, "open");
        WriteFinding(repo, "f-0001.md", "F-0001", "A finding", CardOwner.Reviewer);
        WriteRegisterCard(repo, "r-0001.md", "R-0001", CardKind.Rule, "A rule", CardOwner.Architect, "open");

        var outPath = Path.Combine(repo.Path, "board.html");
        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["view", "--out", outPath], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal(5, doc.RootElement.GetProperty("result").GetProperty("cardCount").GetInt32());
    }

    // 12.3 — the view alters no state.
    [Fact]
    public void View_AltersNoState_CardFilesByteIdentical_NoIndexCreated()
    {
        using var repo = new TempGitRepo();
        WriteQuestion(repo, "q-0001.md", "Q-0001", "A question", CardOwner.ProductOwner, "open");
        WriteSection(repo, "s-0001.md", "S-0001", "A section", CardOwner.Architect, "open");
        WriteBlock(repo, "b-0001.md", "B-0001", "A block", CardOwner.Worker, "S-0001", "briefed", blockedBy: ["Q-0001"]);
        WriteRegisterCard(repo, "r-0001.md", "R-0001", CardKind.Rule, "A rule", CardOwner.Architect, "open");

        var cardPaths = Directory.EnumerateFiles(repo.Path, "*.md", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToList();
        var bytesBefore = cardPaths.ToDictionary(path => path, File.ReadAllBytes);

        var indexPathsBefore = Directory.EnumerateFiles(repo.Path, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToList();

        var outPath = Path.Combine(repo.Path, "board.html");
        var exitCode = CommandDispatcher.Run(
            ["view", "--out", outPath], new StringWriter(), TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);
        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);

        foreach (var path in cardPaths)
        {
            Assert.Equal(bytesBefore[path], File.ReadAllBytes(path));
        }

        var filesAfter = Directory.EnumerateFiles(repo.Path, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToList();
        var newFiles = filesAfter.Except(indexPathsBefore).ToList();
        Assert.Equal([outPath], newFiles);
    }

    // 12.3 — no mechanism by which a reader could alter anything: no form, no script, no input,
    // no button, no external fetch of any kind. Also D5's own "self-contained" requirement.
    [Fact]
    public void View_EmitsNoInteractiveOrExternalMechanism()
    {
        using var repo = new TempGitRepo();
        WriteQuestion(repo, "q-0001.md", "Q-0001", "A question", CardOwner.ProductOwner, "open");
        WriteSection(repo, "s-0001.md", "S-0001", "A section", CardOwner.Architect, "open");
        WriteBlock(repo, "b-0001.md", "B-0001", "A block", CardOwner.Worker, "S-0001", "briefed", blockedBy: ["Q-0001"]);
        WriteRegisterCard(repo, "r-0001.md", "R-0001", CardKind.Rule, "A rule", CardOwner.Architect, "open");

        var outPath = Path.Combine(repo.Path, "board.html");
        var exitCode = CommandDispatcher.Run(
            ["view", "--out", outPath], new StringWriter(), TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);
        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);

        var html = File.ReadAllText(outPath).ToLowerInvariant();
        Assert.DoesNotContain("<form", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<input", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<button", html, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch(", html, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", html, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<link", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<iframe", html, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string? RefusalCode(StringWriter output)
    {
        using var doc = JsonDocument.Parse(output.ToString());
        return doc.RootElement.GetProperty("refusal").GetProperty("code").GetString();
    }

    private static void WriteSection(TempGitRepo repo, string fileName, string id, string title, CardOwner owner, string status)
    {
        var path = Path.Combine(repo.ChangesDirectory, fileName);
        var frontmatter = new CardFrontmatter(id, CardKind.Section, title, status, owner, CardScope.Change, string.Empty, Earlier, FixedNow);
        WriteCard(path, new CardFile(frontmatter, "Section body.", [], []));
    }

    private static void WriteBlock(TempGitRepo repo, string fileName, string id, string title, CardOwner owner, string section, string status, IReadOnlyList<string>? blockedBy = null)
    {
        var path = Path.Combine(repo.ChangesDirectory, fileName);
        var frontmatter = new CardFrontmatter(id, CardKind.Block, title, status, owner, CardScope.Change, section, Earlier, FixedNow);
        var fields = new BlockCardFields(Base: null, ReviewedState: null, Tasks: [], Round: 1, BlockedBy: blockedBy ?? [], GateResults: []);
        WriteCard(path, new CardFile(frontmatter, "Block body.", [], [], BlockFields: fields));
    }

    private static void WriteQuestion(TempGitRepo repo, string fileName, string id, string title, CardOwner owner, string status)
    {
        var path = Path.Combine(repo.RegisterDirectory, fileName);
        var frontmatter = new CardFrontmatter(id, CardKind.Question, title, status, owner, CardScope.Repository, string.Empty, Earlier, FixedNow);
        WriteCard(path, new CardFile(frontmatter, "Question body.", [], []));
    }

    private static void WriteFinding(TempGitRepo repo, string fileName, string id, string title, CardOwner owner)
    {
        var path = Path.Combine(repo.ChangesDirectory, fileName);
        var frontmatter = new CardFrontmatter(id, CardKind.Finding, title, "open", owner, CardScope.Section, "S-0001", Earlier, FixedNow);
        WriteCard(path, new CardFile(frontmatter, "Finding body.", [], []));
    }

    private static void WriteRegisterCard(TempGitRepo repo, string fileName, string id, CardKind kind, string title, CardOwner owner, string status)
    {
        var path = Path.Combine(repo.RegisterDirectory, fileName);
        var frontmatter = new CardFrontmatter(id, kind, title, status, owner, CardScope.Repository, string.Empty, Earlier, FixedNow);
        WriteCard(path, new CardFile(frontmatter, "Register card body.", [], []));
    }

    private static void WriteCard(string path, CardFile card) =>
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private sealed class TempGitRepo : IDisposable
    {
        internal string Path { get; }

        internal string ChangesDirectory { get; }

        internal string RegisterDirectory { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-board-view-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
            ChangesDirectory = System.IO.Path.Combine(Path, CardLayout.ChangesDirectory("establish-callboard").Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(ChangesDirectory);
            RegisterDirectory = System.IO.Path.Combine(Path, CardLayout.RegisterDirectory.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(RegisterDirectory);
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
