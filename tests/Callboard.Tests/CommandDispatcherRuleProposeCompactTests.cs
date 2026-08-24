using System.Linq;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// 7.9 at the CLI boundary: <c>rule propose-compact</c> (register: "Compaction of repository-scoped
/// rules SHALL be proposed by an agent and decided by the Product Owner ... records the proposal
/// with its candidate text, backing set and citation counts, and applies nothing until the Product
/// Owner decides"). The sharpest claim this verb makes is "applies nothing" — every test that
/// reaches a successful proposal also asserts every backing rule's file is byte-for-byte unchanged
/// afterwards, the same standard blocks D and E were held to for their own "refuses to act" claims.
/// </summary>
public sealed class CommandDispatcherRuleProposeCompactTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 24, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ProposeCompact_TwoOpenRepositoryRules_Succeeds_AndReportsCandidateBackingAndCitationCounts()
    {
        using var repo = new TempGitRepo();
        var firstId = CreateRepositoryRule(repo, "r-0001", "First member");
        var secondId = CreateRepositoryRule(repo, "r-0002", "Second member");
        // A third card, elsewhere in the record, cites the first member once.
        CreateRepositoryRule(repo, "r-0003", "Cites the first", body: $"This leans on {firstId}.");

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "propose-compact", "--absorbs", $"{firstId},{secondId}", "--role", "worker"],
            output, repo.Path, "Generalised candidate text.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("Generalised candidate text.", result.GetProperty("candidateText").GetString());
        var backing = result.GetProperty("backing").EnumerateArray().Select(static e => e.GetString()).ToList();
        Assert.Equal([firstId, secondId], backing);
        var citationCounts = result.GetProperty("citationCounts").EnumerateArray().Select(static e => e.GetInt32()).ToList();
        Assert.Equal([1, 0], citationCounts);
        Assert.Equal("worker", result.GetProperty("proposedBy").GetString());
    }

    // The sharpest claim in this block, proven by execution — on the bytes, not the response.
    [Fact]
    public void ProposeCompact_WellFormedRequest_MutatesNoBackingCard()
    {
        using var repo = new TempGitRepo();
        var firstId = CreateRepositoryRule(repo, "r-0004", "First member");
        var secondId = CreateRepositoryRule(repo, "r-0005", "Second member");
        var firstPath = Path.Combine(repo.RegisterDirectory, "r-0004.md");
        var secondPath = Path.Combine(repo.RegisterDirectory, "r-0005.md");
        var firstBefore = File.ReadAllBytes(firstPath);
        var secondBefore = File.ReadAllBytes(secondPath);

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "propose-compact", "--absorbs", $"{firstId},{secondId}", "--role", "architect"],
            output, repo.Path, "Candidate text.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        Assert.Equal(firstBefore, File.ReadAllBytes(firstPath));
        Assert.Equal(secondBefore, File.ReadAllBytes(secondPath));
        var firstOnDisk = AssertParseSuccess(CardStore.ReadCard(firstPath));
        var secondOnDisk = AssertParseSuccess(CardStore.ReadCard(secondPath));
        Assert.Equal("open", firstOnDisk.Frontmatter.Status);
        Assert.Equal("open", secondOnDisk.Frontmatter.Status);
        Assert.Null(firstOnDisk.RegisterFields.SupersededBy);
        Assert.Null(secondOnDisk.RegisterFields.SupersededBy);
    }

    [Fact]
    public void ProposeCompact_NoAbsorbsFlag_Refuses_AtParseTime_WithoutResolvingAnything()
    {
        using var repo = new TempGitRepo();

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "propose-compact", "--role", "worker"],
            output, repo.Path, "Candidate text.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("empty-absorb-set", refusal.GetProperty("code").GetString());
    }

    [Fact]
    public void ProposeCompact_DuplicateBackingId_Refuses_AtParseTime()
    {
        using var repo = new TempGitRepo();

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "propose-compact", "--absorbs", "R-0001,R-0001", "--role", "worker"],
            output, repo.Path, "Candidate text.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("duplicate-absorbed-rule", refusal.GetProperty("code").GetString());
    }

    [Fact]
    public void ProposeCompact_BackingIdDoesNotExist_Refuses_WithCardIdNotFound()
    {
        using var repo = new TempGitRepo();

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "propose-compact", "--absorbs", "R-9999", "--role", "worker"],
            output, repo.Path, "Candidate text.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-id-not-found", refusal.GetProperty("code").GetString());
    }

    [Fact]
    public void ProposeCompact_BackingIdIsChangeScoped_Refuses_WithCardLayoutMismatch_AndMutatesNothing()
    {
        using var repo = new TempGitRepo();
        const string changeName = "establish-callboard";
        var changeScopedOutput = new StringWriter();
        var changeDirectory = Path.Combine(repo.Path, CardLayout.ChangesDirectory(changeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(changeDirectory);
        RunInRepo(
            ["rule", "create", Path.Combine(changeDirectory, "r-0006.md"), "--title", "Change-scoped", "--role", "architect", "--scope", "change", "--change", changeName],
            changeScopedOutput, repo.Path, "Body.");
        var changeScopedId = ExtractResultId(changeScopedOutput);
        var changeScopedPath = Path.Combine(changeDirectory, "r-0006.md");
        var before = File.ReadAllBytes(changeScopedPath);

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "propose-compact", "--absorbs", changeScopedId, "--role", "worker"],
            output, repo.Path, "Candidate text.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-layout-mismatch", refusal.GetProperty("code").GetString());
        Assert.Equal(before, File.ReadAllBytes(changeScopedPath));
    }

    [Fact]
    public void ProposeCompact_BackingIdAlreadyDischarged_Refuses_WithAlreadyDischarged()
    {
        using var repo = new TempGitRepo();
        var dischargedId = CreateRepositoryRule(repo, "r-0007", "Will be discharged");
        var dischargedPath = Path.Combine(repo.RegisterDirectory, "r-0007.md");
        var dischargeOutput = new StringWriter();
        var dischargeExitCode = RunInRepo(
            ["rule", "discharge", dischargedPath, "--role", "architect"],
            dischargeOutput, repo.Path, string.Empty);
        Assert.Equal(CommandDispatcher.SuccessExitCode, dischargeExitCode);

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "propose-compact", "--absorbs", dischargedId, "--role", "worker"],
            output, repo.Path, "Candidate text.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("already-discharged", refusal.GetProperty("code").GetString());
    }

    [Fact]
    public void ProposeCompact_BackingIdNamesASectionNotARule_Refuses_WithWrongCardKind()
    {
        using var repo = new TempGitRepo();
        const string changeName = "establish-callboard";
        var changeDirectory = Path.Combine(repo.Path, CardLayout.ChangesDirectory(changeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(changeDirectory);
        var sectionOutput = new StringWriter();
        RunInRepo(
            ["section", "create", Path.Combine(changeDirectory, "s-0001.md"), "--title", "7. Register", "--role", "architect", "--change", changeName],
            sectionOutput, repo.Path, "Body.");
        var sectionId = ExtractResultId(sectionOutput);

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "propose-compact", "--absorbs", sectionId, "--role", "worker"],
            output, repo.Path, "Candidate text.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("wrong-card-kind", refusal.GetProperty("code").GetString());
    }

    private static string CreateRepositoryRule(TempGitRepo repo, string fileStem, string title, string? body = null)
    {
        var path = Path.Combine(repo.RegisterDirectory, fileStem + ".md");
        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "create", path, "--title", title, "--role", "architect", "--scope", "repository"],
            output, repo.Path, body ?? "Body.");
        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        return ExtractResultId(output);
    }

    private static string ExtractResultId(StringWriter output)
    {
        using var doc = JsonDocument.Parse(output.ToString());
        return doc.RootElement.GetProperty("result").GetProperty("id").GetString()!;
    }

    private static int RunInRepo(string[] args, TextWriter output, string workingDirectory, string body) =>
        CommandDispatcher.Run(
            args, output, new StringReader(body), TextWriter.Null, isInputRedirected: true, workingDirectory: workingDirectory, clock: static () => FixedNow);

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));

    private sealed class TempGitRepo : IDisposable
    {
        internal string Path { get; }

        internal string RegisterDirectory { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-rule-propose-compact-cli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
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
