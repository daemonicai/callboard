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
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0001.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Clean pass", "--section", "6", "--change", ChangeName,
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
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0002.md");
        // An obligation is Change-scoped (lands beside the finding); a hazard is Repository-scoped
        // (lands in callboard/register/ instead) — CardScopeRules.Validate's fixed table.
        var raisedPath = kind == "obligation"
            ? Path.Combine(repo.CardsDirectory, "h-0001.md")
            : Path.Combine(repo.RegisterDirectory, "h-0001.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Checked, with a gap", "--section", "6", "--change", ChangeName,
                "--blind-spot", kind, "--blind-spot-file", raisedPath, "--blind-spot-title", "Blind spot",
                "--blind-spot-body", "The instrument does not cover generated code.",
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

        var findingPath = Path.Combine(repo.CardsDirectory, "f-0000.md");
        var raisedPath = Path.Combine(repo.RegisterDirectory, "h-0000.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Checked, with a gap", "--section", "6", "--change", ChangeName,
                "--blind-spot", "hazard", "--blind-spot-file", raisedPath, "--blind-spot-title", "Blind spot",
                "--blind-spot-body", "The instrument does not cover generated code.",
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
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0003.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["finding", "record", findingPath, "--role", "worker", "--title", "Clean pass", "--section", "6", "--change", ChangeName],
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
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0004.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Clean pass", "--section", "6", "--change", ChangeName,
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
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0005.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Checked, with a gap", "--section", "6", "--change", ChangeName,
                "--blind-spot", "hazard",
            ],
            output, repo.Path, "Recorded body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void ExtentInstrumentAndExplicitTogether_Refuses_WithInvalidExtent()
    {
        using var repo = new TempGitRepo();
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0006.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Clean pass", "--section", "6", "--change", ChangeName,
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
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0007.md");
        Directory.CreateDirectory(repo.CardsDirectory);
        File.WriteAllText(findingPath, "not a card");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Clean pass", "--section", "6", "--change", ChangeName,
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
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0008.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["finding", "record", findingPath, "--title", "Clean pass", "--section", "6", "--change", ChangeName, "--blind-spot", "none"],
            output, repo.Path, "Recorded body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void UnredirectedStdin_Refuses_WithStdinNotRedirected()
    {
        using var repo = new TempGitRepo();
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0009.md");
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            [
                "finding", "record", findingPath,
                "--role", "worker", "--title", "Clean pass", "--section", "6", "--change", ChangeName,
                "--blind-spot", "none",
            ],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("stdin-not-redirected", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
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
