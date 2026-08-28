using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// 6.2/6.3 at the CLI boundary: <c>finding record</c>. Same "own repo-root-not-found site, own
/// tests" discipline §5 established — every refusal code this block mints gets its own CLI-level
/// test. The load-bearing one is <see cref="MissingBlindSpotFlag_Refuses_AndNamesBothDeclarationsItAccepts"/>:
/// findings' "the system refuses and names the declaration it requires" scenario, checked against
/// the actual message text, not just the refusal code.
/// </summary>
public sealed class CommandDispatcherFindingRecordTests
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 23, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BlindSpotNone_Succeeds_AndTheEnvelopeReportsNoRaisedCard()
    {
        using var repo = new TempGitRepo();
        var sectionId = CreateSection(repo);
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0001.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Clean pass", "--section", sectionId, "--change", ChangeName,
                "--blind-spot", "none",
            ],
            output, repo.Path, "Recorded body.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("none", result.GetProperty("blindSpot").GetString());
        Assert.False(result.TryGetProperty("raisedCardId", out _));
        Assert.True(File.Exists(findingPath));
    }

    [Theory]
    [InlineData("obligation")]
    [InlineData("hazard")]
    public void BlindSpotRaised_Succeeds_AndTheEnvelopeReportsTheRaisedCard(string kind)
    {
        using var repo = new TempGitRepo();
        var sectionId = CreateSection(repo);
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0002.md");
        // An obligation is Change-scoped (lands beside the finding); a hazard is Repository-scoped
        // (lands in callboard/register/ instead) — CardScopeRules.Validate's fixed table.
        var raisedPath = kind == "obligation"
            ? Path.Combine(repo.CardsDirectory, "h-0001.md")
            : Path.Combine(repo.RegisterDirectory, "h-0001.md");
        var bodyFilePath = WriteBodyFile(repo.Path, "The instrument does not cover generated code.");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Checked, with a gap", "--section", sectionId, "--change", ChangeName,
                "--blind-spot", kind, "--blind-spot-file", raisedPath, "--blind-spot-title", "Blind spot",
                "--blind-spot-body-file", bodyFilePath,
            ],
            output, repo.Path, "Recorded body.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("raised-as", result.GetProperty("blindSpot").GetString());
        Assert.Equal(kind, result.GetProperty("raisedCardKind").GetString());
        Assert.Equal(raisedPath, result.GetProperty("raisedCardFilePath").GetString());
        Assert.True(File.Exists(raisedPath));
    }

    // The load-bearing refusal (findings: "the system refuses and names the declaration it
    // requires"). What would have to break for this to go red: ParseFindingRecord's blind-spot
    // switch defaulting to "declared none" instead of refusing when the flag is absent.
    // §6 block B remediation, reviewer blocker 3, named explicitly at the CLI boundary: neither
    // repo.CardsDirectory nor repo.RegisterDirectory exists when this test starts (TempGitRepo no
    // longer pre-creates them). What would have to break for this to go red: RunFindingRecord/
    // CardStore.RecordFinding acquiring a lock before creating the target directory, which spins
    // for the full lock timeout and reports tool-failure instead of succeeding.
    [Fact]
    public void RecordingAgainstDirectoriesThatDoNotYetExist_Succeeds()
    {
        using var repo = new TempGitRepo();
        Assert.False(Directory.Exists(repo.CardsDirectory));
        Assert.False(Directory.Exists(repo.RegisterDirectory));

        // §7 block B: `--section` is now validated against a real section card, and a section is
        // Change-scoped — the same directory the finding itself lands in — so creating it here
        // necessarily brings repo.CardsDirectory into existence before `finding record` runs.
        // repo.RegisterDirectory (where the raised hazard lands) stays cold, which is the half of
        // this test's original "cold directory" premise a section card cannot touch.
        var sectionId = CreateSection(repo);

        var findingPath = Path.Combine(repo.CardsDirectory, "f-0000.md");
        var raisedPath = Path.Combine(repo.RegisterDirectory, "h-0000.md");
        var bodyFilePath = WriteBodyFile(repo.Path, "The instrument does not cover generated code.");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Checked, with a gap", "--section", sectionId, "--change", ChangeName,
                "--blind-spot", "hazard", "--blind-spot-file", raisedPath, "--blind-spot-title", "Blind spot",
                "--blind-spot-body-file", bodyFilePath,
            ],
            output, repo.Path, "Recorded body.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        Assert.True(File.Exists(findingPath));
        Assert.True(File.Exists(raisedPath));
    }

    [Fact]
    public void MissingBlindSpotFlag_Refuses_AndNamesBothDeclarationsItAccepts()
    {
        using var repo = new TempGitRepo();
        var sectionId = CreateSection(repo);
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0003.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["finding", "record", findingPath, "--role", "worker", "--title", "Clean pass", "--section", sectionId, "--change", ChangeName],
            output, repo.Path, "Recorded body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("unrecognised-blind-spot", refusal.GetProperty("code").GetString());
        var message = refusal.GetProperty("message").GetString();
        Assert.Contains("none", message, StringComparison.Ordinal);
        Assert.Contains("obligation", message, StringComparison.Ordinal);
        Assert.Contains("hazard", message, StringComparison.Ordinal);

        Assert.False(File.Exists(findingPath));
    }

    [Fact]
    public void UnrecognisedBlindSpotValue_Refuses_AndNamesTheOffendingValue()
    {
        using var repo = new TempGitRepo();
        var sectionId = CreateSection(repo);
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0004.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Clean pass", "--section", sectionId, "--change", ChangeName,
                "--blind-spot", "maybe",
            ],
            output, repo.Path, "Recorded body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("unrecognised-blind-spot", refusal.GetProperty("code").GetString());
        Assert.Contains("'maybe'", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RaisingWithoutBlindSpotFile_Refuses_WithMissingArgument()
    {
        using var repo = new TempGitRepo();
        var sectionId = CreateSection(repo);
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0005.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Checked, with a gap", "--section", sectionId, "--change", ChangeName,
                "--blind-spot", "hazard",
            ],
            output, repo.Path, "Recorded body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // ADR-0001 / design.md: no workflow may require quoting a card body as a shell argument.
    // `--blind-spot-body` no longer exists at all — the raised card's body arrives only via
    // `--blind-spot-body-file`, a path, never inline text.
    [Fact]
    public void RaisingWithoutBlindSpotBodyFile_Refuses_WithMissingArgument()
    {
        using var repo = new TempGitRepo();
        var sectionId = CreateSection(repo);
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0010.md");
        var raisedPath = Path.Combine(repo.RegisterDirectory, "h-0010.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Checked, with a gap", "--section", sectionId, "--change", ChangeName,
                "--blind-spot", "hazard", "--blind-spot-file", raisedPath, "--blind-spot-title", "Blind spot",
            ],
            output, repo.Path, "Recorded body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("missing-argument", refusal.GetProperty("code").GetString());
        Assert.Contains("--blind-spot-body-file", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    // §6 remediation (reviewer nit) — split along the same "File.Exists first" line
    // RunFindingStatus/RunSectionStatus already draw: a path naming no readable file at all
    // (missing, or a directory — File.Exists is false for both) is the caller's own mistake.
    [Fact]
    public void BlindSpotBodyFileDoesNotExist_Refuses_WithBlindSpotBodyFileNotFound()
    {
        using var repo = new TempGitRepo();
        var sectionId = CreateSection(repo);
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0011.md");
        var raisedPath = Path.Combine(repo.RegisterDirectory, "h-0011.md");
        var missingBodyPath = Path.Combine(repo.Path, "no-such-body.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Checked, with a gap", "--section", sectionId, "--change", ChangeName,
                "--blind-spot", "hazard", "--blind-spot-file", raisedPath, "--blind-spot-title", "Blind spot",
                "--blind-spot-body-file", missingBodyPath,
            ],
            output, repo.Path, "Recorded body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("blind-spot-body-file-not-found", refusal.GetProperty("code").GetString());
        Assert.Contains(missingBodyPath, refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(File.Exists(findingPath));
    }

    // The same code fires for a path naming a directory — File.Exists is false for a directory
    // too, so this is still "the caller's to fix", not the environmental-failure code.
    [Fact]
    public void BlindSpotBodyFileNamesADirectory_Refuses_WithBlindSpotBodyFileNotFound()
    {
        using var repo = new TempGitRepo();
        var sectionId = CreateSection(repo);
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0012.md");
        var raisedPath = Path.Combine(repo.RegisterDirectory, "h-0012.md");
        var directoryBodyPath = Path.Combine(repo.Path, "a-directory");
        Directory.CreateDirectory(directoryBodyPath);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Checked, with a gap", "--section", sectionId, "--change", ChangeName,
                "--blind-spot", "hazard", "--blind-spot-file", raisedPath, "--blind-spot-title", "Blind spot",
                "--blind-spot-body-file", directoryBodyPath,
            ],
            output, repo.Path, "Recorded body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("blind-spot-body-file-not-found", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // The environmental-failure half: a file that exists but cannot be read for a reason that is
    // not the caller's typo — permission denied. Different code from the not-found case above.
    [Fact]
    public void BlindSpotBodyFileIsPermissionDenied_Refuses_WithBlindSpotBodyFileUnreadable()
    {
        using var repo = new TempGitRepo();
        var sectionId = CreateSection(repo);
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0013.md");
        var raisedPath = Path.Combine(repo.RegisterDirectory, "h-0013.md");
        var bodyFilePath = WriteBodyFile(repo.Path, "Unreadable content.");
        var output = new StringWriter();

        // UnixFileMode has no Windows equivalent; the environmental-unreadable path is exercised
        // here on Unix only — the not-found half above is exercised on every platform.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(bodyFilePath, UnixFileMode.None);
        try
        {
            var exitCode = RunInRepo(
                [
                    "finding", "record", findingPath,
                    "--role", "worker", "--title", "Checked, with a gap", "--section", sectionId, "--change", ChangeName,
                    "--blind-spot", "hazard", "--blind-spot-file", raisedPath, "--blind-spot-title", "Blind spot",
                    "--blind-spot-body-file", bodyFilePath,
                ],
                output, repo.Path, "Recorded body.");

            Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
            using var doc = JsonDocument.Parse(output.ToString());
            var refusal = doc.RootElement.GetProperty("refusal");
            Assert.Equal("blind-spot-body-file-unreadable", refusal.GetProperty("code").GetString());
            Assert.Contains(bodyFilePath, refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.False(File.Exists(findingPath));
        }
        finally
        {
            // Restore permissions so TempGitRepo's directory cleanup in Dispose can delete it.
            File.SetUnixFileMode(bodyFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void ExtentInstrumentAndExplicitTogether_Refuses_WithInvalidExtent()
    {
        using var repo = new TempGitRepo();
        var sectionId = CreateSection(repo);
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0006.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Clean pass", "--section", sectionId, "--change", ChangeName,
                "--blind-spot", "none", "--extent-instrument", "make gates", "--extent-explicit", "src/Foo.cs",
            ],
            output, repo.Path, "Recorded body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("invalid-extent", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void RecordingAtAnAlreadyOccupiedPath_Refuses_WithCardAlreadyExists()
    {
        using var repo = new TempGitRepo();
        var sectionId = CreateSection(repo);
        // A readable, unrelated card at the target path — not garbage text (§13: the identity
        // allocator now confirms, against the whole record, that the id it is about to issue is
        // not already borne; an unparseable file at this exact path would report Unreadable rather
        // than "confirmed unclaimed", masking the AlreadyExists case this test targets under a
        // ToolFailure instead). Its own id ("F-9999") deliberately does not collide with the
        // "F-0001" the fresh counter is about to issue — this fixture is about the *path*
        // colliding, not the id.
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0007.md");
        Directory.CreateDirectory(repo.CardsDirectory);
        var unrelatedFrontmatter = new CardFrontmatter(
            "F-9999", CardKind.Finding, "Unrelated", "open", CardOwner.Architect, CardScope.Change, sectionId, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        File.WriteAllText(findingPath, CardFileWriter.Serialize(new CardFile(unrelatedFrontmatter, "Unrelated.", [], [])));
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Clean pass", "--section", sectionId, "--change", ChangeName,
                "--blind-spot", "none",
            ],
            output, repo.Path, "Recorded body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("card-already-exists", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void RoleMissing_Refuses_WithMissingArgument()
    {
        using var repo = new TempGitRepo();
        var sectionId = CreateSection(repo);
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0008.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["finding", "record", findingPath, "--title", "Clean pass", "--section", sectionId, "--change", ChangeName, "--blind-spot", "none"],
            output, repo.Path, "Recorded body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // §7 block B, "a card raised within a section names it by the section card's id, and that id
    // must resolve to a real section card": no card anywhere in the record carries the requested
    // id at all.
    [Fact]
    public void SectionIdDoesNotResolveToAnyCard_Refuses_WithCardIdNotFound()
    {
        using var repo = new TempGitRepo();
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0014.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Clean pass", "--section", "S-9999", "--change", ChangeName,
                "--blind-spot", "none",
            ],
            output, repo.Path, "Recorded body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-id-not-found", refusal.GetProperty("code").GetString());
        Assert.Contains("S-9999", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(File.Exists(findingPath));
    }

    // §7 block B remediation (reviewer blocker): the id does not resolve to any card, but a file
    // elsewhere in the record could not be read at all — the resolver cannot rule out that the
    // unreadable file is the section this --section value names, so this must refuse
    // card-id-unresolvable, not silently proceed as if card-id-not-found. Proven end to end through
    // the CLI entry point, with a genuinely unreadable file on disk (not a mocked resolution
    // outcome) — the same standard §6's B3 was held to.
    [Fact]
    public void SectionIdDoesNotResolve_ButAFileElsewhereCouldNotBeRead_Refuses_WithCardIdUnresolvable()
    {
        using var repo = new TempGitRepo();
        Directory.CreateDirectory(repo.RegisterDirectory);
        var garbagePath = Path.Combine(repo.RegisterDirectory, "r-broken.md");
        File.WriteAllText(garbagePath, "not a card at all, no frontmatter fence");

        var findingPath = Path.Combine(repo.CardsDirectory, "f-0018.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Clean pass", "--section", "S-9999", "--change", ChangeName,
                "--blind-spot", "none",
            ],
            output, repo.Path, "Recorded body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-id-unresolvable", refusal.GetProperty("code").GetString());
        var message = refusal.GetProperty("message").GetString()!;
        Assert.Contains("S-9999", message, StringComparison.Ordinal);
        Assert.Contains(garbagePath, message, StringComparison.Ordinal);
        Assert.False(File.Exists(findingPath));
    }

    // §13.6, reviewer finding on 13.6's own item-7 count: `finding record --section` reaches the
    // new `card-corrupt` arm too, through `ValidateSection` -> `ResolveCardReference` -- the same
    // resolver every other `--id`-addressed verb uses. The file claiming the requested section id
    // is sitting right there, unparseable, so the honest refusal is `card-corrupt`, not
    // `card-id-unresolvable` -- the two name different remedies (open the file vs. hunt for a typo).
    [Fact]
    public void SectionIdDoesNotResolve_ButACorruptFileDeclaresIt_Refuses_WithCardCorrupt_NamingFileAndReason()
    {
        using var repo = new TempGitRepo();
        Directory.CreateDirectory(repo.CardsDirectory);
        var corruptPath = Path.Combine(repo.CardsDirectory, "s-corrupt.md");
        var frontmatter = new CardFrontmatter(
            "S-9999", CardKind.Section, "A corrupt section", "not-a-real-status", CardOwner.Architect,
            CardScope.Change, string.Empty, FixedNow, FixedNow);
        File.WriteAllText(corruptPath, CardFileWriter.Serialize(new CardFile(frontmatter, "Body.", [], [])));

        var findingPath = Path.Combine(repo.CardsDirectory, "f-0019.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Clean pass", "--section", "S-9999", "--change", ChangeName,
                "--blind-spot", "none",
            ],
            output, repo.Path, "Recorded body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-corrupt", refusal.GetProperty("code").GetString());
        var message = refusal.GetProperty("message").GetString()!;
        Assert.Contains("S-9999", message, StringComparison.Ordinal);
        Assert.Contains(corruptPath, message, StringComparison.Ordinal);
        Assert.Contains("unrecognised status: 'not-a-real-status'", message, StringComparison.Ordinal);
        Assert.False(File.Exists(findingPath));
    }

    // The id resolves, but to a card that is not a `section` at all — reuses the existing
    // `wrong-card-kind` code, exactly what it already means.
    [Fact]
    public void SectionIdResolvesToANonSectionCard_Refuses_WithWrongCardKind()
    {
        using var repo = new TempGitRepo();
        var ruleOutput = new StringWriter();
        var ruleExit = RunInRepo(
            ["rule", "create", Path.Combine(repo.RegisterDirectory, "r-0001.md"), "--title", "A rule", "--role", "architect", "--scope", "repository"],
            ruleOutput, repo.Path, "Rule body.");
        Assert.Equal(CommandDispatcher.SuccessExitCode, ruleExit);
        using var ruleDoc = JsonDocument.Parse(ruleOutput.ToString());
        var ruleId = ruleDoc.RootElement.GetProperty("result").GetProperty("id").GetString()!;

        var findingPath = Path.Combine(repo.CardsDirectory, "f-0015.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Clean pass", "--section", ruleId, "--change", ChangeName,
                "--blind-spot", "none",
            ],
            output, repo.Path, "Recorded body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("wrong-card-kind", refusal.GetProperty("code").GetString());
        Assert.Contains("rule", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("section", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    // More than one file claims the same id — a hand-edited collision, since nothing through the
    // CLI's own allocator can produce one. Refuses rather than picking whichever file the
    // resolver's walk happened to read first (the defect §6 fail-closed on twice).
    [Fact]
    public void SectionIdClaimedByTwoFiles_Refuses_WithDuplicateCardId()
    {
        using var repo = new TempGitRepo();
        var sectionId = CreateSection(repo);

        // A second, hand-written file claiming the same id — CardIdentityAllocator never produces
        // this through any verb; it is reachable only by a hand-edited or corrupted file.
        var collidingPath = Path.Combine(repo.RegisterDirectory, "s-colliding.md");
        Directory.CreateDirectory(repo.RegisterDirectory);
        var collidingFrontmatter = new CardFrontmatter(
            sectionId, CardKind.Section, "Colliding section", "open", CardOwner.Architect, CardScope.Change, string.Empty, FixedNow, FixedNow);
        var collidingCard = new CardFile(collidingFrontmatter, "Body.", [], [], [], BlockCardFields.Empty, [], SectionCardFields.Empty);
        File.WriteAllText(collidingPath, CardFileWriter.Serialize(collidingCard), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var findingPath = Path.Combine(repo.CardsDirectory, "f-0016.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Clean pass", "--section", sectionId, "--change", ChangeName,
                "--blind-spot", "none",
            ],
            output, repo.Path, "Recorded body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("duplicate-card-id", refusal.GetProperty("code").GetString());
        Assert.Contains(sectionId, refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(File.Exists(findingPath));
    }

    [Fact]
    public void UnredirectedStdin_Refuses_WithStdinNotRedirected()
    {
        using var repo = new TempGitRepo();
        var sectionId = CreateSection(repo);
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0009.md");
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Clean pass", "--section", sectionId, "--change", ChangeName,
                "--blind-spot", "none",
            ],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("stdin-not-redirected", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // §7 block B: `--section` now names a real section card's id, resolved via
    // CardIdentityResolver — a free-text label like "6" no longer means anything. Every test that
    // records a finding creates a real section first, through the CLI's own `section create` verb
    // (§7 block A) rather than hand-building a card file, and reads the allocated id back off the
    // envelope.
    private static string CreateSection(TempGitRepo repo)
    {
        var sectionPath = Path.Combine(repo.CardsDirectory, "s-" + Guid.NewGuid().ToString("N") + ".md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["section", "create", sectionPath, "--title", "Section", "--role", "architect", "--change", ChangeName],
            output, repo.Path, "Section body.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        return doc.RootElement.GetProperty("result").GetProperty("id").GetString()!;
    }

    private static string WriteBodyFile(string repoPath, string content)
    {
        var path = Path.Combine(repoPath, "blind-spot-body-" + Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(path, content);
        return path;
    }

    private static int RunInRepo(string[] args, TextWriter output, string workingDirectory, string body) =>
        CommandDispatcher.Run(
            args, output, new StringReader(body), TextWriter.Null, isInputRedirected: true, workingDirectory: workingDirectory, clock: static () => FixedNow);

    private sealed class TempGitRepo : IDisposable
    {
        internal string Path { get; }

        internal string CardsDirectory { get; }

        internal string RegisterDirectory { get; }

        // Neither CardsDirectory nor RegisterDirectory is created here (§6 block B remediation,
        // reviewer blocker 3) — a test that needs one to already exist (to pre-occupy a path with a
        // stray file) creates it itself, locally; every other test now runs against the cold
        // directory `finding record`'s primary use case actually needs to handle.
        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-finding-record-cli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
            CardsDirectory = System.IO.Path.Combine(Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', System.IO.Path.DirectorySeparatorChar));
            RegisterDirectory = System.IO.Path.Combine(Path, CardLayout.RegisterDirectory.Replace('/', System.IO.Path.DirectorySeparatorChar));
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
