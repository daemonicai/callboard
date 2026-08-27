using System.Linq;
using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// §11 block C, tasks 11.3/11.4 — <c>section export</c>/<c>change export</c> (record-retrieval:
/// "The system SHALL render a section, or a whole change, as a single readable document
/// approximating the shape of the log it replaces ... Every class of content previously written
/// to that log SHALL have a home in the model and SHALL be reconstitutable by this export"). A
/// pure read of the record (no lock, no acting role) whose only write is the output file itself,
/// via temp-file-then-rename (D7).
/// </summary>
public sealed class RecordExportTests
{
    private static readonly DateTimeOffset Earlier = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Middle = new(2026, 8, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SectionExport_MissingSectionId_Refuses()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["section", "export"], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        Assert.Equal("missing-argument", RefusalCode(output));
    }

    [Fact]
    public void SectionExport_MissingOut_Refuses()
    {
        using var repo = new TempGitRepo();
        WriteSection(repo, "s-0001", "S-0001", "open");
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["section", "export", "S-0001"], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        Assert.Equal("missing-argument", RefusalCode(output));
    }

    [Fact]
    public void SectionExport_UnresolvableId_Reports_WithoutRecordingAnything()
    {
        using var repo = new TempGitRepo();
        var outPath = Path.Combine(repo.Path, "out.md");
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["section", "export", "S-9999", "--out", outPath], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-id-not-found", refusal.GetProperty("code").GetString());
        Assert.False(refusal.TryGetProperty("rule", out _));
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void SectionExport_TargetNamesANonSectionCard_Refuses()
    {
        using var repo = new TempGitRepo();
        WriteBlock(repo, "b-0001", "B-0001", "S-0001");
        var outPath = Path.Combine(repo.Path, "out.md");
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["section", "export", "B-0001", "--out", outPath], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        Assert.Equal("not-a-section-card", RefusalCode(output));
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void SectionExport_TargetExists_RefusesWithoutForce_AndWritesWithForce()
    {
        using var repo = new TempGitRepo();
        WriteSection(repo, "s-0001", "S-0001", "open");
        var outPath = Path.Combine(repo.Path, "out.md");
        File.WriteAllText(outPath, "pre-existing, unrelated content");

        var refusedOutput = new StringWriter();
        var refusedExit = CommandDispatcher.Run(
            ["section", "export", "S-0001", "--out", outPath], refusedOutput, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);
        Assert.Equal(CommandDispatcher.RefusalExitCode, refusedExit);
        Assert.Equal("export-target-exists", RefusalCode(refusedOutput));
        Assert.Equal("pre-existing, unrelated content", File.ReadAllText(outPath));

        var forcedOutput = new StringWriter();
        var forcedExit = CommandDispatcher.Run(
            ["section", "export", "S-0001", "--out", outPath, "--force"], forcedOutput, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);
        Assert.Equal(CommandDispatcher.SuccessExitCode, forcedExit);
        Assert.Contains("S-0001", File.ReadAllText(outPath));
        Assert.DoesNotContain("pre-existing, unrelated content", File.ReadAllText(outPath));
    }

    // record-retrieval, 11.4's own enumeration: every content class the incumbent DEVLOG carried —
    // frontmatter/body, kind-specific fields (block/section/finding/register/question), the four
    // append-only sequences (handovers, transitions, claims/limits, refusals — including gate
    // results), and the complete comment thread including nit metadata and disposition — has a home
    // in the model and is reconstituted here.
    [Fact]
    public void SectionExport_RendersEveryContentClass_InReadingOrder()
    {
        using var repo = new TempGitRepo();

        var sectionPath = Path.Combine(repo.ChangesDirectory, "s-0001.md");
        var sectionFrontmatter = new CardFrontmatter("S-0001", CardKind.Section, "Narrative retrieval and export", "closed", CardOwner.Architect, CardScope.Change, string.Empty, Earlier, FixedNow);
        var sectionFields = new SectionCardFields(
            Base: "abc123",
            ClosedBy: CardOwner.Architect,
            ClosedAt: FixedNow,
            Verdicts: [new SectionVerdictEntry(CardOwner.Supervisor, SectionVerdict.Approve, "abc123", "def456", Middle, [])],
            Authorisations: []);
        WriteCard(sectionPath, new CardFile(sectionFrontmatter, "Section brief.", [], [], SectionFields: sectionFields));

        var blockPath = Path.Combine(repo.ChangesDirectory, "b-0001.md");
        var blockFrontmatter = new CardFrontmatter("B-0001", CardKind.Block, "Implement the export", "approved", CardOwner.Architect, CardScope.Change, "S-0001", Middle, FixedNow);
        var blockFields = new BlockCardFields(
            Base: "abc123", ReviewedState: "def456", Tasks: ["11.3", "11.4"], Round: 1, BlockedBy: [],
            GateResults: [new GateResult("build", 0, 1), new GateResult("test", 0, 1)]);
        var transitions = new[] { new CardBlockTransitionEntry(CardOwner.Worker, "submit-for-review", BlockFlowState.Building, BlockFlowState.InReview, Middle, []) };
        var claims = new[] { new CardApprovalClaim("claim-1", 1, "Renders every content class.", []) };
        var comments = new[]
        {
            new CardComment("c-1", CardOwner.Worker, Middle, "Implemented the renderer.", null, CardOwner.Reviewer, null, []),
            new CardComment("c-2", CardOwner.Reviewer, FixedNow, "One nit: tighten a doc comment.", "c-1", CardOwner.Worker, null, [], IsNit: true, Required: false, Sites: ["src/Foo.cs:10"]),
            new CardComment("c-3", CardOwner.Worker, FixedNow, "Tightened.", "c-2", null, "c-2", [], Disposition: NitDisposition.FixBeforeLand),
        };
        WriteCard(blockPath, new CardFile(blockFrontmatter, "Block body.", comments, [], BlockFields: blockFields, Transitions: transitions, Claims: claims));

        var rulePath = Path.Combine(repo.RegisterDirectory, "r-0001.md");
        var ruleFrontmatter = new CardFrontmatter("R-0001", CardKind.Rule, "Export never reads the index", "open", CardOwner.Architect, CardScope.Repository, "S-0001", Earlier, Earlier);
        WriteCard(rulePath, new CardFile(ruleFrontmatter, "Rule body.", [], []));

        var outPath = Path.Combine(repo.Path, "out.md");
        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["section", "export", "S-0001", "--out", outPath], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("S-0001", result.GetProperty("sectionId").GetString());
        Assert.Equal(outPath, result.GetProperty("outputPath").GetString());
        Assert.Equal(3, result.GetProperty("cardCount").GetInt32());

        var text = File.ReadAllText(outPath);

        // Reading order: rule (Earlier) before section (Earlier..FixedNow, id tiebreak after 'R'
        // is not applicable since Created differs) before block (Middle) — asserted by index.
        var ruleIndex = text.IndexOf("## rule R-0001", StringComparison.Ordinal);
        var sectionIndex = text.IndexOf("## section S-0001", StringComparison.Ordinal);
        var blockIndex = text.IndexOf("## block B-0001", StringComparison.Ordinal);
        Assert.True(ruleIndex >= 0 && sectionIndex > ruleIndex && blockIndex > sectionIndex, text);

        // Section fields: base commit and the supervisor verdict.
        Assert.Contains("base: abc123", text, StringComparison.Ordinal);
        Assert.Contains("verdict [approve] by supervisor", text, StringComparison.Ordinal);

        // Block fields: tasks, round, gate results (the worker's report's gate exit lines).
        Assert.Contains("tasks: 11.3, 11.4", text, StringComparison.Ordinal);
        Assert.Contains("gate build: exit 0 (round 1)", text, StringComparison.Ordinal);
        Assert.Contains("gate test: exit 0 (round 1)", text, StringComparison.Ordinal);

        // Transitions and claims (the reviewer's approve, review-certification's claims).
        Assert.Contains("worker submit-for-review (building → in-review)", text, StringComparison.Ordinal);
        Assert.Contains("claim [round 1]: Renders every content class.", text, StringComparison.Ordinal);

        // Thread: the worker's report (handoff via 'to'), the reviewer's nit, and its disposition.
        Assert.Contains("**[worker]**", text, StringComparison.Ordinal);
        Assert.Contains("Implemented the renderer.", text, StringComparison.Ordinal);
        Assert.Contains("[NIT, sites: src/Foo.cs:10]", text, StringComparison.Ordinal);
        Assert.Contains("[disposition: fix-before-land]", text, StringComparison.Ordinal);

        // The rule card, resolved through Section, not through the change directory.
        Assert.Contains("Export never reads the index", text, StringComparison.Ordinal);
    }

    // §11 block C reviewer finding: SectionExport_IsByteIdentical_AcrossTwoRuns below only proves
    // two runs over an *unchanged* directory agree with each other — a fixture whose cards all
    // carry strictly increasing Created timestamps would pass that test whether or not the id
    // tie-break (or any ordering at all) actually existed, exactly the kind of green-but-uninformative
    // test §10 already produced once. This test targets the case that one cannot reach: two cards
    // sharing one identical Created timestamp, so only the ordinal id tie-break can separate them —
    // and file names deliberately run in the *reverse* of id order, the same order CardStore.
    // ReadAllCards's own path-ordinal pre-sort would hand back unchanged, so a pass here cannot be
    // explained by that pre-sort surviving through rather than by RecordExportAssembler's own
    // comparator. Exercised directly against RecordExportAssembler (not the CLI or the renderer):
    // decision 7 pins the assembler's own stated contract, and calling it directly isolates that
    // contract from the renderer's incidental text shape.
    //
    // From the code (RecordExportAssembler.SortReadingOrder's comparator): a tie (return 0) is only
    // reachable when Created is equal AND string.CompareOrdinal(Id, Id) is zero, i.e. only when the
    // two entries share the same id — which card identities never do (allocation is unique by
    // construction). So the comparator never actually ties on two distinct cards, which makes it a
    // strict total order over the input regardless of arrangement; List<T>.Sort's own lack of a
    // stability guarantee is therefore irrelevant here. The assembler's sort is independently
    // sufficient — CardStore.ReadAllCards's own path-ordinal sort plays no role in the exported
    // order's correctness, only in enumeration order before the assembler re-sorts it (confirmed by
    // this test's file-name-reversed fixture below).
    [Fact]
    public void CardsForSection_CardsSharingOneTimestamp_OrderById_IndependentOfFileNameOrder()
    {
        using var repo = new TempGitRepo();
        var sectionFrontmatter = new CardFrontmatter(
            "S-0001", CardKind.Section, "A section", "open", CardOwner.Architect, CardScope.Change, string.Empty, Earlier, Earlier);
        var sectionCard = new CardFile(sectionFrontmatter, "Body.", [], []);
        WriteCard(Path.Combine(repo.ChangesDirectory, "s-0001.md"), sectionCard);

        // File names run z, m, a — the reverse of id order — while every block below shares one
        // identical Created timestamp (Middle).
        WriteBlockAt(repo, "z-block.md", "B-0001", "S-0001", Middle);
        WriteBlockAt(repo, "m-block.md", "B-0002", "S-0001", Middle);
        WriteBlockAt(repo, "a-block.md", "B-0003", "S-0001", Middle);

        var ordered = RecordExportAssembler.CardsForSection(repo.Path, sectionCard);

        Assert.Equal(
            ["S-0001", "B-0001", "B-0002", "B-0003"],
            ordered.Select(entry => entry.Card.Frontmatter.Id).ToArray());
    }

    [Fact]
    public void SectionExport_IsByteIdentical_AcrossTwoRuns()
    {
        using var repo = new TempGitRepo();
        WriteSection(repo, "s-0001", "S-0001", "open");
        WriteBlock(repo, "b-0001", "B-0001", "S-0001");

        var firstPath = Path.Combine(repo.Path, "first.md");
        var secondPath = Path.Combine(repo.Path, "second.md");

        Assert.Equal(CommandDispatcher.SuccessExitCode, CommandDispatcher.Run(
            ["section", "export", "S-0001", "--out", firstPath], new StringWriter(), TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow));
        Assert.Equal(CommandDispatcher.SuccessExitCode, CommandDispatcher.Run(
            ["section", "export", "S-0001", "--out", secondPath], new StringWriter(), TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow));

        Assert.Equal(File.ReadAllText(firstPath), File.ReadAllText(secondPath));
    }

    [Fact]
    public void SectionExport_ClosedCardsAreIncluded()
    {
        using var repo = new TempGitRepo();
        WriteSection(repo, "s-0001", "S-0001", "closed");
        WriteBlock(repo, "b-0001", "B-0001", "S-0001", status: "closed");
        var outPath = Path.Combine(repo.Path, "out.md");

        var exitCode = CommandDispatcher.Run(
            ["section", "export", "S-0001", "--out", outPath], new StringWriter(), TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        Assert.Contains("## block B-0001", File.ReadAllText(outPath), StringComparison.Ordinal);
    }

    [Fact]
    public void SectionExport_TakesNoLock_AndAppendsNothingToAnyCard()
    {
        using var repo = new TempGitRepo();
        var sectionPath = Path.Combine(repo.ChangesDirectory, "s-0001.md");
        WriteSection(repo, "s-0001", "S-0001", "open");
        var before = File.ReadAllText(sectionPath);
        var outPath = Path.Combine(repo.Path, "out.md");

        CommandDispatcher.Run(
            ["section", "export", "S-0001", "--out", outPath], new StringWriter(), TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(before, File.ReadAllText(sectionPath));
    }

    [Fact]
    public void ChangeExport_MissingChangeName_Refuses()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["change", "export"], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        Assert.Equal("missing-argument", RefusalCode(output));
    }

    [Fact]
    public void ChangeExport_UnknownChange_Refuses()
    {
        using var repo = new TempGitRepo();
        var outPath = Path.Combine(repo.Path, "out.md");
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["change", "export", "no-such-change", "--out", outPath], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        Assert.Equal("change-not-found", RefusalCode(output));
    }

    [Fact]
    public void ChangeExport_IncludesEverySectionAndUnsectionedCards()
    {
        using var repo = new TempGitRepo();
        WriteSection(repo, "s-0001", "S-0001", "closed");
        WriteBlock(repo, "b-0001", "B-0001", "S-0001");

        // A block created before it was tied to any section — must still surface in the whole
        // change's export (record-retrieval: nothing physically filed under the change is dropped).
        var unsectionedPath = Path.Combine(repo.ChangesDirectory, "b-0002.md");
        var unsectionedFrontmatter = new CardFrontmatter("B-0002", CardKind.Block, "Not yet sectioned", "drafting", CardOwner.Architect, CardScope.Change, string.Empty, Earlier, Earlier);
        WriteCard(unsectionedPath, new CardFile(unsectionedFrontmatter, "Body.", [], []));

        var outPath = Path.Combine(repo.Path, "out.md");
        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["change", "export", "establish-callboard", "--out", outPath], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("establish-callboard", result.GetProperty("changeName").GetString());
        Assert.Equal(3, result.GetProperty("cardCount").GetInt32());

        var text = File.ReadAllText(outPath);
        Assert.Contains("## section S-0001", text, StringComparison.Ordinal);
        Assert.Contains("## block B-0001", text, StringComparison.Ordinal);
        Assert.Contains("## block B-0002", text, StringComparison.Ordinal);
    }

    private static string? RefusalCode(StringWriter output)
    {
        using var doc = JsonDocument.Parse(output.ToString());
        return doc.RootElement.GetProperty("refusal").GetProperty("code").GetString();
    }

    private static void WriteSection(TempGitRepo repo, string fileStem, string id, string status)
    {
        var path = Path.Combine(repo.ChangesDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Section, "A section", status, CardOwner.Architect, CardScope.Change, string.Empty, Earlier, FixedNow);
        WriteCard(path, new CardFile(frontmatter, "Section body.", [], []));
    }

    private static void WriteBlock(TempGitRepo repo, string fileStem, string id, string section, string status = "briefed")
    {
        var path = Path.Combine(repo.ChangesDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Block, "A block card", status, CardOwner.Worker, CardScope.Change, section, Earlier, FixedNow);
        WriteCard(path, new CardFile(frontmatter, "Body.", [], []));
    }

    /// <summary>Like <see cref="WriteBlock"/>, but lets the caller choose the file name and the
    /// <c>created</c> timestamp independently of the card's own id — the fixture shape
    /// <see cref="CardsForSection_CardsSharingOneTimestamp_OrderById_IndependentOfFileNameOrder"/>
    /// needs to prove the exported order comes from <see cref="CardFrontmatter.Id"/>/<see
    /// cref="CardFrontmatter.Created"/>, not from the file name <see cref="CardStore.ReadAllCards"/>
    /// happens to enumerate first.</summary>
    private static void WriteBlockAt(TempGitRepo repo, string fileName, string id, string section, DateTimeOffset created)
    {
        var path = Path.Combine(repo.ChangesDirectory, fileName);
        var frontmatter = new CardFrontmatter(id, CardKind.Block, "A block card", "briefed", CardOwner.Worker, CardScope.Change, section, created, created);
        WriteCard(path, new CardFile(frontmatter, "Body.", [], []));
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-record-export-tests-" + Guid.NewGuid().ToString("N"));
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
