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
