using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// §6 block C at the CLI boundary: <c>finding status</c> — the read verb the block C brief calls
/// for so that 6.5/6.6 ("requirements about what the system says") are asserted against emitted
/// JSON directly, the surface §5 closed without ever building for <c>GateStatus.Absent</c>. Every
/// assertion here reads the envelope's JSON, never a domain type — this file is the CLI-JSON
/// contract's own proof, the same discipline <c>CommandDispatcherFindingRecordTests</c> already
/// applies to <c>finding record</c>.
/// </summary>
/// <remarks>
/// §6 remediation, round 3 (reviewer blocker) — this class and
/// <see cref="FindingDegradationEvaluatorTests"/> both mutate the process-global current
/// directory to exercise the empty-directory-component fix; see
/// <see cref="CurrentDirectoryMutatingTests"/> for why the explicit collection exists.
/// </remarks>
[Collection(CurrentDirectoryMutatingTests.Name)]
public sealed class CommandDispatcherFindingStatusTests
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 23, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExplicitExtent_ContentUnchanged_ReadsBackCurrent()
    {
        using var repo = new TempGitRepo();
        var sourcePath = Path.Combine(repo.Path, "src", "Foo.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        File.WriteAllText(sourcePath, "original content");
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0001.md");

        Record(repo, findingPath, extentExplicit: "src/Foo.cs");
        var status = Status(repo, findingPath);

        Assert.Equal("current", status.GetProperty("staleness").GetString());
        Assert.False(status.TryGetProperty("stalenessReason", out _));
    }

    [Fact]
    public void ExplicitExtent_ContentChanged_ReadsBackStale_WithARequestForReVerification_NeverRefutation()
    {
        using var repo = new TempGitRepo();
        var sourcePath = Path.Combine(repo.Path, "src", "Foo.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        File.WriteAllText(sourcePath, "original content");
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0002.md");

        Record(repo, findingPath, extentExplicit: "src/Foo.cs");
        File.WriteAllText(sourcePath, "content has moved on");
        var status = Status(repo, findingPath);

        Assert.Equal("stale", status.GetProperty("staleness").GetString());
        var reason = status.GetProperty("stalenessReason").GetString()!;
        Assert.Contains("re-verification", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not mean", reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("incorrect", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExplicitExtent_FileDeletedAfterRecording_ReadsBackStale_NotAToolFailure()
    {
        using var repo = new TempGitRepo();
        var sourcePath = Path.Combine(repo.Path, "src", "Foo.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        File.WriteAllText(sourcePath, "original content");
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0003.md");

        Record(repo, findingPath, extentExplicit: "src/Foo.cs");
        File.Delete(sourcePath);
        var status = Status(repo, findingPath);

        Assert.Equal("stale", status.GetProperty("staleness").GetString());
    }

    [Fact]
    public void ExplicitExtent_UnrelatedFileChangedOutsideTheExtent_RemainsCurrent()
    {
        using var repo = new TempGitRepo();
        var trackedPath = Path.Combine(repo.Path, "src", "Foo.cs");
        var untrackedPath = Path.Combine(repo.Path, "src", "Bar.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(trackedPath)!);
        File.WriteAllText(trackedPath, "tracked");
        File.WriteAllText(untrackedPath, "not part of the extent");
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0004.md");

        Record(repo, findingPath, extentExplicit: "src/Foo.cs");
        File.WriteAllText(untrackedPath, "changed, but outside the declared extent");
        var status = Status(repo, findingPath);

        Assert.Equal("current", status.GetProperty("staleness").GetString());
    }

    // §6 remediation (B1) — the case the original block C tests never wrote: a declared path that
    // never resolves to a readable file, neither at record time nor at re-check. Before the fix,
    // `null == null` read back "current" forever; the CLI must now say the extent was never
    // actually measured.
    [Fact]
    public void ExplicitExtent_PathNeverResolvesToAReadableFile_ReadsBackNotMeasurable_NeverCurrent()
    {
        using var repo = new TempGitRepo();
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0014.md");

        // src/Typo.cs is never created — a typo'd or wrong-root path.
        Record(repo, findingPath, extentExplicit: "src/Typo.cs");
        var status = Status(repo, findingPath);

        Assert.Equal("not-measurable", status.GetProperty("staleness").GetString());
        Assert.Contains("src/Typo.cs", status.GetProperty("stalenessReason").GetString(), StringComparison.Ordinal);
    }

    // The directory case the supervisor named explicitly: a declared path that names a directory
    // rather than a file resolves to "absent" on both sides too (File.ReadAllBytes throws
    // UnauthorizedAccessException/IOException on a directory, caught as null), and must read the
    // same "never measured" answer as a plain typo, not "current".
    [Fact]
    public void ExplicitExtent_PathNamesADirectory_ReadsBackNotMeasurable_NeverCurrent()
    {
        using var repo = new TempGitRepo();
        var directoryPath = Path.Combine(repo.Path, "src", "SomeDirectory");
        Directory.CreateDirectory(directoryPath);
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0015.md");

        Record(repo, findingPath, extentExplicit: "src/SomeDirectory");
        var status = Status(repo, findingPath);

        Assert.Equal("not-measurable", status.GetProperty("staleness").GetString());
    }

    [Fact]
    public void InstrumentExtent_ReadsBackNotMeasurable_NamingTheInstrumentToReRun()
    {
        using var repo = new TempGitRepo();
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0005.md");

        Record(repo, findingPath, extentInstrument: "make gates");
        var status = Status(repo, findingPath);

        Assert.Equal("not-measurable", status.GetProperty("staleness").GetString());
        Assert.Contains("make gates", status.GetProperty("stalenessReason").GetString());
    }

    [Fact]
    public void DefaultBlockScopeExtent_ReadsBackNotMeasurable_NeverCurrent()
    {
        using var repo = new TempGitRepo();
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0006.md");

        Record(repo, findingPath); // no --extent-* flag at all: defaults to block-scope
        var status = Status(repo, findingPath);

        Assert.Equal("not-measurable", status.GetProperty("staleness").GetString());
    }

    [Fact]
    public void ArguedCleanDisposition_ReadsBackNotApplicable_AndStalenessIsNeverComputed()
    {
        using var repo = new TempGitRepo();
        var sourcePath = Path.Combine(repo.Path, "src", "Foo.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        File.WriteAllText(sourcePath, "original content");
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0007.md");

        Record(repo, findingPath, extentExplicit: "src/Foo.cs", disposition: "argued-clean", verifiedAt: "state-42");
        // Would report Stale under Measured — proves at the CLI boundary that ArguedClean's answer
        // does not depend on (and so cannot leak) what the extent's content is doing.
        File.WriteAllText(sourcePath, "content has moved on");
        var status = Status(repo, findingPath);

        Assert.Equal("not-applicable", status.GetProperty("staleness").GetString());
        Assert.Equal("argued-clean", status.GetProperty("disposition").GetString());
        Assert.Contains("state-42", status.GetProperty("stalenessReason").GetString());
    }

    // §6 block D: "degradation" reads "live" while the section that raised the finding is still
    // open, and this is asserted against the emitted JSON directly, not only against the domain
    // evaluator — the same "answer must not under-report" discipline this file already applies to
    // staleness.
    [Fact]
    public void OpenSection_FindingReadsBackLive()
    {
        using var repo = new TempGitRepo();
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0010.md");

        Record(repo, findingPath, extentExplicit: "src/Foo.cs");
        WriteInitialSectionCard(repo, "s-0001", "S-0001");
        var status = Status(repo, findingPath);

        Assert.Equal("live", status.GetProperty("degradation").GetString());
        Assert.False(status.TryGetProperty("degradationReason", out _));
    }

    // The other half: once the section actually closes (through the CLI's own `section close`,
    // not a hand-built domain call), the same finding reads back degraded — and staleness is
    // computed independently, so a Current finding stays Current even once degraded (§6 block D
    // ruling: the two fields must not collapse into one).
    [Fact]
    public void SectionCloses_FindingReadsBackDegraded_AndStalenessIsUnaffected()
    {
        using var repo = new TempGitRepo();
        var sourcePath = Path.Combine(repo.Path, "src", "Foo.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        File.WriteAllText(sourcePath, "original content");
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0011.md");

        Record(repo, findingPath, extentExplicit: "src/Foo.cs");
        var sectionPath = WriteInitialSectionCard(repo, "s-0002", "S-0002");

        var beforeClose = Status(repo, findingPath);
        Assert.Equal("live", beforeClose.GetProperty("degradation").GetString());
        Assert.Equal("current", beforeClose.GetProperty("staleness").GetString());

        var closeOutput = new StringWriter();
        var closeExit = RunInRepo(["section", "close", sectionPath, "--role", "architect", "--change", ChangeName], closeOutput, repo.Path, "unused");
        Assert.Equal(CommandDispatcher.SuccessExitCode, closeExit);

        var afterClose = Status(repo, findingPath);
        Assert.Equal("degraded", afterClose.GetProperty("degradation").GetString());
        Assert.Equal("current", afterClose.GetProperty("staleness").GetString());
    }

    // §6 section remediation, round 2 (supervisor blocker) — `Path.GetDirectoryName` returns the
    // empty string for a bare filename, not null, and the evaluator used to treat "" as "no
    // directory to look in" and answer Live without reading a single card. Reproduced exactly as
    // the supervisor did: same finding, same closed section, invoked two ways that only differ in
    // whether the path carries a directory component. Both must answer "degraded".
    [Fact]
    public void BareFilenameWithNoDirectoryComponent_StillReadsBackDegraded_SameAsAnAbsolutePath()
    {
        using var repo = new TempGitRepo();
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0017.md");

        Record(repo, findingPath, extentExplicit: "src/Foo.cs");
        WriteClosedSectionCard(repo, "s-0018", "S-0018");

        var absoluteStatus = Status(repo, findingPath);
        Assert.Equal("degraded", absoluteStatus.GetProperty("degradation").GetString());

        // Path.Exists/File.Exists resolve a relative argument against the real process working
        // directory, not the workingDirectory parameter CommandDispatcher.Run threads through for
        // repo-root resolution — the same as the real binary invoked from a shell after `cd`, which
        // is exactly the supervisor's repro. Only this test needs the process CWD moved; every
        // other test in this file passes an absolute findingPath and is unaffected.
        var previousDirectory = Directory.GetCurrentDirectory();
        string output;
        int exitCode;
        try
        {
            Directory.SetCurrentDirectory(repo.CardsDirectory);
            var writer = new StringWriter();
            exitCode = CommandDispatcher.Run(
                ["finding", "status", "f-0017.md"],
                writer, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.CardsDirectory, clock: static () => FixedNow);
            output = writer.ToString();
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
        }

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("degraded", doc.RootElement.GetProperty("result").GetProperty("degradation").GetString());
    }

    // §6 block D remediation (reviewer blocker 1), at the CLI boundary: two `section` cards in
    // the finding's directory sharing its `--section` label refuse rather than silently picking
    // whichever sorts first ordinally.
    [Fact]
    public void TwoSectionCardsShareTheLabel_Refuses_AndNamesBothFiles()
    {
        using var repo = new TempGitRepo();
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0012.md");

        Record(repo, findingPath, extentExplicit: "src/Foo.cs");
        var openPath = WriteInitialSectionCard(repo, "s-a-open", "S-0100");
        var closedPath = WriteClosedSectionCard(repo, "s-b-closed", "S-0101");

        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["finding", "status", findingPath],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("ambiguous-section-label", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        var message = doc.RootElement.GetProperty("refusal").GetProperty("message").GetString()!;
        Assert.Contains(openPath, message, StringComparison.Ordinal);
        Assert.Contains(closedPath, message, StringComparison.Ordinal);
    }

    // §6 block D remediation (reviewer blocker 2), at the CLI boundary: a card in the finding's
    // directory that fails to parse, with no valid `section` card matching its label, reads
    // "unreadable" rather than silently "live".
    [Fact]
    public void UnparseableSectionCandidate_ReadsBackUnreadable()
    {
        using var repo = new TempGitRepo();
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0013.md");
        Record(repo, findingPath, extentExplicit: "src/Foo.cs");

        var garbagePath = Path.Combine(repo.CardsDirectory, "s-broken.md");
        File.WriteAllText(garbagePath, "not a card at all");

        var status = Status(repo, findingPath);

        Assert.Equal("unreadable", status.GetProperty("degradation").GetString());
        Assert.Contains(garbagePath, status.GetProperty("degradationReason").GetString(), StringComparison.Ordinal);
    }

    // §6 section remediation (B3), at the CLI boundary: a section card carrying a different label
    // sits in the finding's directory. Before the fix this read "live" permanently, even after the
    // real section closed under a differently-spelled label — the fail-open direction the
    // supervisor named. Now it reads "unreadable": the record cannot rule out a mislabelled match.
    [Fact]
    public void DifferentlyLabelledSectionCard_ReadsBackUnreadable_NotLive()
    {
        using var repo = new TempGitRepo();
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0016.md");
        Record(repo, findingPath, extentExplicit: "src/Foo.cs");

        var otherPath = Path.Combine(repo.CardsDirectory, "s-other.md");
        var frontmatter = new CardFrontmatter(
            "S-0200", CardKind.Section, "Title", "closed", CardOwner.Architect, CardScope.Change, "5", FixedNow, FixedNow);
        var sectionFields = new SectionCardFields(null, CardOwner.Architect, FixedNow, []);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, [], sectionFields);
        File.WriteAllText(otherPath, CardFileWriter.Serialize(card), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var status = Status(repo, findingPath);

        Assert.Equal("unreadable", status.GetProperty("degradation").GetString());
        Assert.Contains(otherPath, status.GetProperty("degradationReason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RecordResult_ReportsTheDispositionActuallyRecorded()
    {
        using var repo = new TempGitRepo();
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0008.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Measured by default", "--section", "6", "--change", ChangeName,
                "--blind-spot", "none",
            ],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("measured", doc.RootElement.GetProperty("result").GetProperty("disposition").GetString());
    }

    [Fact]
    public void UnrecognisedDispositionFlag_Refuses_AndNamesBothValuesItAccepts()
    {
        using var repo = new TempGitRepo();
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0009.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "t", "--section", "6", "--change", ChangeName,
                "--blind-spot", "none", "--disposition", "not-a-real-value",
            ],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("unrecognised-disposition", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        var message = doc.RootElement.GetProperty("refusal").GetProperty("message").GetString()!;
        Assert.Contains("measured", message, StringComparison.Ordinal);
        Assert.Contains("argued-clean", message, StringComparison.Ordinal);
    }

    [Fact]
    public void CardNotFound_Refuses()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["finding", "status", Path.Combine(repo.CardsDirectory, "does-not-exist.md")],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("card-not-found", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void WrongCardKind_Refuses_AndNamesBothTheActualAndExpectedKind()
    {
        using var repo = new TempGitRepo();
        Directory.CreateDirectory(repo.CardsDirectory);
        var blockPath = Path.Combine(repo.CardsDirectory, "b-0001.md");
        File.WriteAllText(
            blockPath,
            "---\n" +
            "id: B-0001\n" +
            "kind: block\n" +
            "title: Not a finding\n" +
            "status: drafting\n" +
            "owner: architect\n" +
            "scope: change\n" +
            "section: 6\n" +
            "created: 2026-08-23T09:00:00+00:00\n" +
            "updated: 2026-08-23T09:00:00+00:00\n" +
            "---\n" +
            "Body.\n");
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["finding", "status", blockPath],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("wrong-card-kind", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        var message = doc.RootElement.GetProperty("refusal").GetProperty("message").GetString()!;
        Assert.Contains("block", message, StringComparison.Ordinal);
        Assert.Contains("finding", message, StringComparison.Ordinal);
    }

    private static void Record(
        TempGitRepo repo, string findingPath, string? extentExplicit = null, string? extentInstrument = null,
        string? disposition = null, string? verifiedAt = null)
    {
        var args = new List<string>
        {
            "finding", "record", findingPath,
            "--role", "worker", "--title", "A finding", "--section", "6", "--change", ChangeName,
            "--blind-spot", "none",
        };
        if (extentExplicit is not null)
        {
            args.Add("--extent-explicit");
            args.Add(extentExplicit);
        }

        if (extentInstrument is not null)
        {
            args.Add("--extent-instrument");
            args.Add(extentInstrument);
        }

        if (disposition is not null)
        {
            args.Add("--disposition");
            args.Add(disposition);
        }

        if (verifiedAt is not null)
        {
            args.Add("--verified-at");
            args.Add(verifiedAt);
        }

        var output = new StringWriter();
        var exitCode = RunInRepo(args.ToArray(), output, repo.Path, "Body of the finding.");
        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
    }

    private static string WriteInitialSectionCard(TempGitRepo repo, string fileStem, string id)
    {
        var path = Path.Combine(repo.CardsDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Section, "Title", "open", CardOwner.Architect, CardScope.Change, "6", FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, [], SectionCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string WriteClosedSectionCard(TempGitRepo repo, string fileStem, string id)
    {
        var path = Path.Combine(repo.CardsDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Section, "Title", "closed", CardOwner.Architect, CardScope.Change, "6", FixedNow, FixedNow);
        var sectionFields = new SectionCardFields(null, CardOwner.Architect, FixedNow, []);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, [], sectionFields);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static JsonElement Status(TempGitRepo repo, string findingPath)
    {
        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["finding", "status", findingPath],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        return doc.RootElement.GetProperty("result").Clone();
    }

    private static int RunInRepo(string[] args, TextWriter output, string workingDirectory, string body) =>
        CommandDispatcher.Run(
            args, output, new StringReader(body), TextWriter.Null, isInputRedirected: true, workingDirectory: workingDirectory, clock: static () => FixedNow);

    private sealed class TempGitRepo : IDisposable
    {
        internal string Path { get; }

        internal string CardsDirectory { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-finding-status-cli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
            CardsDirectory = System.IO.Path.Combine(Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(CardsDirectory);
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
