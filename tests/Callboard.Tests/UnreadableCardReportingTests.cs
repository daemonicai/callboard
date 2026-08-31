using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// §13, task 13.5 — every read that walks the record reports the cards it could not parse, as one
/// <see cref="UnreadableCard"/> shape, naming <em>which</em> file and <em>why</em>.
///
/// <para>
/// <b>What each test here asserts, and why both halves matter.</b> Every case proves two things at
/// once: the readable cards still come back (record-retrieval, "Damage is contained": "one card's
/// record is corrupted ... every other card remains readable and usable" — a read reports, it does
/// not refuse, or one corrupt card would halt every query), and the unreadable one is named
/// <b>with the parser's own reason</b>. The reason is the half that makes this useful rather than
/// merely present: an agent told "some file is unreadable" has to go looking, while one told
/// <c>unrecognised status: 'not-a-real-status'</c> can fix the file. §11's ruling — "a test can
/// cover a content class and still not cover the thing that makes it content" — is why no test
/// here settles for asserting the array is non-empty.
/// </para>
///
/// <para>
/// Two different corruptions are used across these tests deliberately: a status outside its kind's
/// vocabulary (§12 block A's parse door) and a file with no frontmatter at all. If the reported
/// reason were a fixed label rather than the parser's own account, the second class would report
/// the first class's text and these assertions would fail.
/// </para>
/// </summary>
public sealed class UnreadableCardReportingTests
{
    private static readonly DateTimeOffset Earlier = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    private const string BadStatus = "not-a-real-status";

    [Fact]
    public void State_ACorruptCard_StillReturnsTheReadableCards_AndNamesTheCorruptOneWithItsReason()
    {
        using var repo = new TempGitRepo();
        WriteQuestion(repo, "q-0001.md", "Q-0001", CardOwner.ProductOwner, "open");
        var corruptPath = WriteBadStatusObligation(repo, "o-0001.md", "O-0001");

        var result = RunForResult(repo, ["state"]);

        var questionIds = result.GetProperty("openQuestions").EnumerateArray()
            .Select(static entry => entry.GetProperty("id").GetString()).ToList();
        Assert.Equal(["Q-0001"], questionIds);

        var unreadable = AssertSingleUnreadable(result, corruptPath);
        Assert.Contains($"unrecognised status: '{BadStatus}'", unreadable, StringComparison.Ordinal);
        Assert.Contains("for kind 'obligation'", unreadable, StringComparison.Ordinal);
    }

    // The corruption class that is not a bad status: a file the parser cannot even open as a card.
    // Same shape, and — the point — a different reason, so the reported reason is demonstrably the
    // parser's own account of this file rather than a constant.
    [Fact]
    public void State_AFileWithNoFrontmatter_ReportsThatFailureNotTheStatusFailure()
    {
        using var repo = new TempGitRepo();
        WriteQuestion(repo, "q-0002.md", "Q-0002", CardOwner.ProductOwner, "open");
        var corruptPath = Path.Combine(repo.RegisterDirectory, "junk.md");
        File.WriteAllText(corruptPath, "this was never a card at all\n", Utf8);

        var result = RunForResult(repo, ["state"]);

        Assert.Single(result.GetProperty("openQuestions").EnumerateArray());
        var unreadable = AssertSingleUnreadable(result, corruptPath);
        Assert.Contains("frontmatter", unreadable, StringComparison.Ordinal);
        Assert.DoesNotContain("unrecognised status", unreadable, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_ACorruptCard_StillDeliversTheRegister_AndNamesTheCorruptOneWithItsReason()
    {
        using var repo = new TempGitRepo();
        WriteRegisterCard(repo, "r-0001.md", "R-0001", CardKind.Rule, "open");
        var corruptPath = WriteBadStatusObligation(repo, "o-0002.md", "O-0002");

        var result = RunForResult(repo, ["context", "--role", "worker"]);

        var ruleIds = result.GetProperty("liveRules").EnumerateArray()
            .Select(static entry => entry.GetProperty("id").GetString()).ToList();
        Assert.Equal(["R-0001"], ruleIds);

        var unreadable = AssertSingleUnreadable(result, corruptPath);
        Assert.Contains($"unrecognised status: '{BadStatus}'", unreadable, StringComparison.Ordinal);
    }

    // `view` must report DerivedState's set rather than collect a parallel one (§13.5): the JSON
    // response and the rendered document therefore name the same file for the same reason.
    [Fact]
    public void View_ACorruptCard_ReportsItInTheJson_AndInTheRenderedDocument()
    {
        using var repo = new TempGitRepo();
        WriteRegisterCard(repo, "r-0002.md", "R-0002", CardKind.Rule, "open");
        var corruptPath = WriteBadStatusObligation(repo, "o-0003.md", "O-0003");
        var outPath = Path.Combine(repo.Path, "board.html");

        var result = RunForResult(repo, ["view", "--out", outPath]);

        Assert.Equal(1, result.GetProperty("cardCount").GetInt32());
        var unreadable = AssertSingleUnreadable(result, corruptPath);
        Assert.Contains($"unrecognised status: '{BadStatus}'", unreadable, StringComparison.Ordinal);

        var html = File.ReadAllText(outPath);
        Assert.Contains("o-0003.md", html, StringComparison.Ordinal);
        Assert.Contains(BadStatus, html, StringComparison.Ordinal);
    }

    [Fact]
    public void SectionExport_ACorruptCard_StillExportsTheSectionsCards_AndNamesTheCorruptOneWithItsReason()
    {
        using var repo = new TempGitRepo();
        WriteSection(repo, "s-0001.md", "S-0001");
        WriteBlock(repo, "b-0001.md", "B-0001", "S-0001");
        var corruptPath = WriteBadStatusBlock(repo, "b-0002.md", "B-0002", "S-0001");
        var outPath = Path.Combine(repo.Path, "section.md");

        var result = RunForResult(repo, ["section", "export", "S-0001", "--out", outPath]);

        // The section card and its one readable block — the corrupt block is excluded from the
        // document, which is exactly why its exclusion has to be stated.
        Assert.Equal(2, result.GetProperty("cardCount").GetInt32());
        Assert.Contains("B-0001", File.ReadAllText(outPath), StringComparison.Ordinal);

        var unreadable = AssertSingleUnreadable(result, corruptPath);
        Assert.Contains($"unrecognised status: '{BadStatus}'", unreadable, StringComparison.Ordinal);
        Assert.Contains("for kind 'block'", unreadable, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangeExport_ACorruptCard_StillExportsTheChangesCards_AndNamesTheCorruptOneWithItsReason()
    {
        using var repo = new TempGitRepo();
        WriteSection(repo, "s-0002.md", "S-0002");
        WriteBlock(repo, "b-0003.md", "B-0003", "S-0002");
        var corruptPath = WriteBadStatusBlock(repo, "b-0004.md", "B-0004", "S-0002");
        var outPath = Path.Combine(repo.Path, "change.md");

        var result = RunForResult(repo, ["change", "export", "establish-callboard", "--out", outPath]);

        Assert.Equal(2, result.GetProperty("cardCount").GetInt32());
        Assert.Contains("B-0003", File.ReadAllText(outPath), StringComparison.Ordinal);

        var unreadable = AssertSingleUnreadable(result, corruptPath);
        Assert.Contains($"unrecognised status: '{BadStatus}'", unreadable, StringComparison.Ordinal);
    }

    // `rule review` is the read where an unreported omission does real damage: a rule whose only
    // citation lives in the file that would not parse counts zero, and lands in the queue for
    // retirement. The queue still comes back — the count is a trigger for a human, never an
    // automatic retirement — but the caller is told the record was incomplete when it was taken.
    [Fact]
    public void RuleReview_ACorruptCard_StillQueuesTheUncitedRules_AndNamesTheCorruptOneOnceWithItsReason()
    {
        using var repo = new TempGitRepo();
        var rulePath = Path.Combine(repo.RegisterDirectory, "r-0003.md");
        WriteRegisterCard(repo, "r-0003.md", "R-0003", CardKind.Rule, "open");
        var corruptPath = WriteBadStatusObligation(repo, "o-0004.md", "O-0004");

        var result = RunForResult(repo, ["rule", "review"]);

        Assert.Equal(1, result.GetProperty("liveRuleCount").GetInt32());
        var queuedPaths = result.GetProperty("uncitedOpenRules").EnumerateArray()
            .Select(static entry => entry.GetProperty("filePath").GetString()).ToList();
        Assert.Equal([rulePath], queuedPaths);

        // Reported once, not once per walk: `rule review` reads the record for its live count and
        // again for each candidate rule's citation count, and the caller is being told which files
        // could not be read — a fact about the record, not about how many passes were made.
        var unreadable = AssertSingleUnreadable(result, corruptPath);
        Assert.Contains($"unrecognised status: '{BadStatus}'", unreadable, StringComparison.Ordinal);
    }

    // `rule propose-compact` carries no unreadable set, and this is why. It is not a read: it
    // writes a proposal card, and CardIdentityAllocator fails shut when any file in the record
    // cannot be read (§13 ruling 3 — a vanished file bears no identity, but a file that exists and
    // will not parse still fails shut, because the newly issued id might be the one inside it). So
    // the verb never completes over an incomplete record, and any citation count it does report
    // was taken over a record that parsed in full. Proven by execution rather than asserted in
    // prose: an unreadable-set field on that response could never have been non-empty.
    [Fact]
    public void RuleProposeCompact_ACorruptCardAnywhereInTheRecord_FailsShutRatherThanReportingAnIncompleteCount()
    {
        using var repo = new TempGitRepo();
        WriteRegisterCard(repo, "r-0004.md", "R-0004", CardKind.Rule, "open");
        WriteRegisterCard(repo, "r-0005.md", "R-0005", CardKind.Rule, "open");
        WriteBadStatusObligation(repo, "o-0005.md", "O-0005");

        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["rule", "propose-compact", "--absorbs", "R-0004,R-0005", "--role", "worker"],
            output, new StringReader("Generalised candidate text."), TextWriter.Null,
            isInputRedirected: true, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.ToolFailureExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        // The allocator's own message names the file it could not read — the same two facts
        // UnreadableCard carries, on the path that fails shut rather than the one that reports.
        Assert.Contains("could not be read", doc.RootElement.GetProperty("refusal").GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.Contains("o-0005.md", doc.RootElement.GetProperty("refusal").GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    // `section status` scans the section's own blocks for ageing threads. An unparseable block may
    // carry an ageing thread this response therefore cannot list, which is precisely why the
    // omission is stated rather than left to be inferred from an empty list.
    [Fact]
    public void SectionStatus_ACorruptBlockInTheSection_StillAnswers_AndNamesTheCorruptOneWithItsReason()
    {
        using var repo = new TempGitRepo();
        WriteSection(repo, "s-0003.md", "S-0003");
        WriteBlock(repo, "b-0005.md", "B-0005", "S-0003");
        var corruptPath = WriteBadStatusBlock(repo, "b-0006.md", "B-0006", "S-0003");
        var sectionPath = Path.Combine(repo.ChangesDirectory, "s-0003.md");

        var result = RunForResult(repo, ["section", "status", sectionPath]);

        Assert.Equal("open", result.GetProperty("status").GetString());
        Assert.Empty(result.GetProperty("ageingThreads").EnumerateArray());

        var unreadable = AssertSingleUnreadable(result, corruptPath);
        Assert.Contains($"unrecognised status: '{BadStatus}'", unreadable, StringComparison.Ordinal);
    }

    // The empty case, asserted rather than assumed: the field is always present, so a caller never
    // has to distinguish "absent because nothing was wrong" from "absent because this command
    // forgot to say".
    [Theory]
    [InlineData("state")]
    [InlineData("context")]
    public void AWholeRecordThatParses_ReportsAnEmptyUnreadableSet_NotAMissingField(string verb)
    {
        using var repo = new TempGitRepo();
        WriteRegisterCard(repo, "r-0006.md", "R-0006", CardKind.Rule, "open");

        string[] args = verb == "state" ? ["state"] : ["context", "--role", "worker"];
        var result = RunForResult(repo, args);

        Assert.Empty(result.GetProperty("unreadable").EnumerateArray());
    }

    /// <summary>The one unreadable entry the response carries, asserted to be
    /// <paramref name="expectedPath"/>; returns its <c>reason</c> for the caller to assert on —
    /// no test here is allowed to stop at "the list is non-empty".</summary>
    private static string AssertSingleUnreadable(JsonElement result, string expectedPath)
    {
        var entry = Assert.Single(result.GetProperty("unreadable").EnumerateArray());
        Assert.Equal(expectedPath, entry.GetProperty("filePath").GetString());
        var reason = entry.GetProperty("reason").GetString();
        Assert.False(string.IsNullOrWhiteSpace(reason), "an unreadable entry must state why the file would not parse.");
        return reason!;
    }

    private static JsonElement RunForResult(TempGitRepo repo, string[] args)
    {
        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            args, output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        var document = JsonDocument.Parse(output.ToString());
        return document.RootElement.GetProperty("result").Clone();
    }

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static void WriteCard(string path, CardFile card) =>
        File.WriteAllText(path, CardFileWriter.Serialize(card), Utf8);

    private static string WriteBadStatusObligation(TempGitRepo repo, string fileName, string id)
    {
        var path = Path.Combine(repo.RegisterDirectory, fileName);
        var frontmatter = new CardFrontmatter(
            id, CardKind.Obligation, "A corrupt obligation", BadStatus, CardOwner.Architect, CardScope.Repository, string.Empty, Earlier, FixedNow);
        var fields = new RegisterCardFields(null, null, null, null, OwedBy: "S-0001");
        WriteCard(path, new CardFile(frontmatter, "Body.", [], [], RegisterFields: fields));
        return path;
    }

    private static string WriteBadStatusBlock(TempGitRepo repo, string fileName, string id, string section)
    {
        var path = Path.Combine(repo.ChangesDirectory, fileName);
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "A corrupt block", BadStatus, CardOwner.Worker, CardScope.Change, section, Earlier, FixedNow);
        WriteCard(path, new CardFile(frontmatter, "Body.", [], []));
        return path;
    }

    private static void WriteSection(TempGitRepo repo, string fileName, string id)
    {
        var path = Path.Combine(repo.ChangesDirectory, fileName);
        var frontmatter = new CardFrontmatter(
            id, CardKind.Section, "A section", "open", CardOwner.Architect, CardScope.Change, string.Empty, Earlier, FixedNow);
        WriteCard(path, new CardFile(frontmatter, "Section body.", [], []));
    }

    private static void WriteBlock(TempGitRepo repo, string fileName, string id, string section)
    {
        var path = Path.Combine(repo.ChangesDirectory, fileName);
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "A block", "briefed", CardOwner.Worker, CardScope.Change, section, Earlier, FixedNow);
        WriteCard(path, new CardFile(frontmatter, "Block body.", [], []));
    }

    private static void WriteQuestion(TempGitRepo repo, string fileName, string id, CardOwner owner, string status)
    {
        var path = Path.Combine(repo.RegisterDirectory, fileName);
        var frontmatter = new CardFrontmatter(
            id, CardKind.Question, "A question", status, owner, CardScope.Repository, string.Empty, Earlier, FixedNow);
        WriteCard(path, new CardFile(frontmatter, "Question body.", [], []));
    }

    private static void WriteRegisterCard(TempGitRepo repo, string fileName, string id, CardKind kind, string status)
    {
        var path = Path.Combine(repo.RegisterDirectory, fileName);
        var frontmatter = new CardFrontmatter(
            id, kind, "A register card", status, CardOwner.Architect, CardScope.Repository, string.Empty, Earlier, FixedNow);
        WriteCard(path, new CardFile(frontmatter, "Register card body.", [], []));
    }

    private sealed class TempGitRepo : IDisposable
    {
        internal string Path { get; }

        internal string ChangesDirectory { get; }

        internal string RegisterDirectory { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-unreadable-tests-" + Guid.NewGuid().ToString("N"));
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
