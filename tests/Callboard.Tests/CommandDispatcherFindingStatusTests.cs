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
///
/// <para>
/// <b>§7 block B — the <c>workingDirectory</c> seam retires this file's own CWD mutation.</b> A
/// finding path is now resolved against <c>CommandDispatcher.Run</c>'s own <c>workingDirectory</c>
/// argument (<see cref="CommandDispatcher"/>'s <c>ResolveFilePath</c>), not the real process CWD, so
/// <see cref="BareFilenameWithNoDirectoryComponent_StillReadsBackDegraded_SameAsAnAbsolutePath"/> no
/// longer needs <see cref="Directory.SetCurrentDirectory"/> to exercise a relative file argument —
/// it passes a relative path and a distinct <c>workingDirectory</c> instead. With no test left in
/// this file (or in <c>FindingDegradationEvaluatorTests</c>) mutating the real process CWD, the
/// <c>CurrentDirectoryMutatingTests</c> shared collection §6 needed to serialise the two classes
/// against that global state is gone — see the DEVLOG.
/// </para>
///
/// <para>
/// <b>§7 block B — <c>--section</c> now names a section card's id.</b> Every <c>Record</c> call
/// below creates a real section first, through the CLI's own <c>section create</c> verb, and
/// passes its allocated id — a free-text label like <c>"6"</c> no longer resolves to anything.
/// </para>
/// </summary>
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

        var sectionId = Record(repo, findingPath, extentExplicit: "src/Foo.cs");

        var beforeClose = Status(repo, findingPath);
        Assert.Equal("live", beforeClose.GetProperty("degradation").GetString());
        Assert.Equal("current", beforeClose.GetProperty("staleness").GetString());

        var closeOutput = new StringWriter();
        var closeExit = RunInRepo(["section", "close", _sectionPathsById[sectionId], "--role", "architect", "--change", ChangeName], closeOutput, repo.Path, "unused");
        Assert.Equal(CommandDispatcher.SuccessExitCode, closeExit);

        var afterClose = Status(repo, findingPath);
        Assert.Equal("degraded", afterClose.GetProperty("degradation").GetString());
        Assert.Equal("current", afterClose.GetProperty("staleness").GetString());
    }

    // §7 block B — the `workingDirectory` seam's own test: a relative file argument resolves
    // against `Run`'s own `workingDirectory` parameter, not the real process CWD. Both invocations
    // below run from the *same* real process CWD (whatever the test host happens to be in); only
    // the relative one supplies a `workingDirectory` that differs from it, which is exactly what
    // the seam is for.
    [Fact]
    public void BareFilenameWithNoDirectoryComponent_StillReadsBackDegraded_SameAsAnAbsolutePath()
    {
        using var repo = new TempGitRepo();
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0017.md");

        var sectionId = Record(repo, findingPath, extentExplicit: "src/Foo.cs");
        var closeExit = RunInRepo(["section", "close", _sectionPathsById[sectionId], "--role", "architect", "--change", ChangeName], new StringWriter(), repo.Path, "unused");
        Assert.Equal(CommandDispatcher.SuccessExitCode, closeExit);

        var absoluteStatus = Status(repo, findingPath);
        Assert.Equal("degraded", absoluteStatus.GetProperty("degradation").GetString());

        var writer = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["finding", "status", "f-0017.md"],
            writer, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.CardsDirectory, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(writer.ToString());
        Assert.Equal("degraded", doc.RootElement.GetProperty("result").GetProperty("degradation").GetString());
    }

    // §7 block B — more than one file claims the id this finding's own 'section' field names: a
    // hand-edited collision (nothing through the allocator can produce one), refused rather than
    // picked, the same fail-closed shape §6 block D's remediation established for the label-
    // matching mechanism this rewires.
    [Fact]
    public void TwoCardFilesClaimTheSectionId_Refuses_AndNamesBothFiles()
    {
        using var repo = new TempGitRepo();
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0012.md");

        var sectionId = Record(repo, findingPath, extentExplicit: "src/Foo.cs");
        var openPath = _sectionPathsById[sectionId];

        var closedPath = Path.Combine(repo.CardsDirectory, "s-colliding.md");
        var frontmatter = new CardFrontmatter(
            sectionId, CardKind.Section, "Colliding section", "closed", CardOwner.Architect, CardScope.Change, string.Empty, FixedNow, FixedNow);
        var sectionFields = new SectionCardFields(null, CardOwner.Architect, FixedNow, [], []);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, [], sectionFields);
        File.WriteAllText(closedPath, CardFileWriter.Serialize(card), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["finding", "status", findingPath],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("duplicate-card-id", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        var message = doc.RootElement.GetProperty("refusal").GetProperty("message").GetString()!;
        Assert.Contains(openPath, message, StringComparison.Ordinal);
        Assert.Contains(closedPath, message, StringComparison.Ordinal);
    }

    // §6 block D remediation (reviewer blocker 2), reshaped for §7 block B's exact-id resolution: a
    // card elsewhere in the record fails to parse, and the id this finding names does not resolve
    // to anything else either — reads "unreadable" rather than silently "live". Hand-written rather
    // than via `finding record`, because `--section` is now validated at record time — a finding
    // naming an id that resolves to nothing is no longer constructible through the CLI's own write
    // verb at all, only through direct file authorship (a hand-edited card, or one written by an
    // older build).
    [Fact]
    public void UnparseableCardElsewhereInTheRecord_WithNoMatchingSectionId_ReadsBackUnreadable()
    {
        using var repo = new TempGitRepo();
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0013.md");
        var findingFrontmatter = new CardFrontmatter(
            "F-0013", CardKind.Finding, "A finding", "open", CardOwner.Worker, CardScope.Section, "S-9999", FixedNow, FixedNow);
        var findingFields = new FindingCardFields(
            null, FindingExtent.BlockScope, null, FindingBlindSpotDeclaration.None, null, FindingDisposition.Measured);
        var findingCard = new CardFile(findingFrontmatter, "Body.", [], [], FindingFields: findingFields);
        File.WriteAllText(findingPath, CardFileWriter.Serialize(findingCard), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var garbagePath = Path.Combine(repo.CardsDirectory, "s-broken.md");
        File.WriteAllText(garbagePath, "not a card at all");

        var status = Status(repo, findingPath);

        Assert.Equal("unreadable", status.GetProperty("degradation").GetString());
        Assert.Contains(garbagePath, status.GetProperty("degradationReason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RecordResult_ReportsTheDispositionActuallyRecorded()
    {
        using var repo = new TempGitRepo();
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0008.md");
        var sectionId = CreateSection(repo);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Measured by default", "--section", sectionId, "--change", ChangeName,
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
        var sectionId = CreateSection(repo);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "t", "--section", sectionId, "--change", ChangeName,
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

    // Tracks each test-created section card's own path by id, so a test that records a finding via
    // Record(...) can later close (or otherwise reference) the same section without re-deriving its
    // path from a file stem it never chose.
    private readonly Dictionary<string, string> _sectionPathsById = [];

    // Creates a real section (§7 block A's own verb) and returns its allocated id, unless
    // sectionId is supplied — in which case no section is created, and the caller is responsible
    // for what --section then resolves to (used by the "id does not resolve" fixture).
    private string Record(
        TempGitRepo repo, string findingPath, string? extentExplicit = null, string? extentInstrument = null,
        string? disposition = null, string? verifiedAt = null, string? sectionId = null)
    {
        var resolvedSectionId = sectionId ?? CreateSection(repo);

        var args = new List<string>
        {
            "finding", "record", findingPath,
            "--role", "worker", "--title", "A finding", "--section", resolvedSectionId, "--change", ChangeName,
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
        return resolvedSectionId;
    }

    private string CreateSection(TempGitRepo repo)
    {
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["section", "create", "--title", "Section", "--role", "architect", "--change", ChangeName],
            output, repo.Path, "Section body.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        var id = result.GetProperty("id").GetString()!;
        _sectionPathsById[id] = result.GetProperty("filePath").GetString()!;
        return id;
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
