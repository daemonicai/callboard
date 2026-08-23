using System.Linq;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// 7.6 at the CLI boundary: <c>rule author</c> (register: "Authoring a rule from findings SHALL
/// create a new card and SHALL record which findings it was earned from, because a rule backed by
/// several independent findings across several sections is a different proposition from one backed
/// by a single incident"). Covers the happy path (including the cross-change reach the requirement's
/// rationale is actually about), that the named findings are proven unchanged on the bytes — block
/// D's own standard — and every resolution failure this verb can hit.
/// </summary>
public sealed class CommandDispatcherRuleAuthorTests
{
    private const string ChangeName = "establish-callboard";
    private const string OtherChangeName = "another-change";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 23, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RuleAuthor_TwoFindingsAcrossTwoChanges_Succeeds_AndRecordsEarnedFrom()
    {
        using var repo = new TempGitRepo();
        var sectionOne = CreateSection(repo, ChangeName);
        var findingOne = CreateFinding(repo, "f-0001", sectionOne, ChangeName);

        var otherChangeDirectory = Path.Combine(repo.Path, CardLayout.ChangesDirectory(OtherChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(otherChangeDirectory);
        var sectionTwo = CreateSection(repo, OtherChangeName, directory: otherChangeDirectory);
        var findingTwo = CreateFinding(repo, "f-0002", sectionTwo, OtherChangeName, directory: otherChangeDirectory);

        var findingOnePath = Path.Combine(repo.CardsDirectory, "f-0001.md");
        var findingTwoPath = Path.Combine(otherChangeDirectory, "f-0002.md");
        var findingOneBytesBefore = File.ReadAllBytes(findingOnePath);
        var findingOneMtimeBefore = File.GetLastWriteTimeUtc(findingOnePath);
        var findingTwoBytesBefore = File.ReadAllBytes(findingTwoPath);
        var findingTwoMtimeBefore = File.GetLastWriteTimeUtc(findingTwoPath);

        var rulePath = Path.Combine(repo.RegisterDirectory, "r-0001.md");
        var output = new StringWriter();
        var exitCode = RunInRepo(
            [
                "rule", "author", rulePath, "--title", "Never trust a path string", "--role", "architect",
                "--scope", "repository", "--earned-from", $"{findingOne},{findingTwo}",
            ],
            output, repo.Path, "Earned from two independent incidents.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("R-0001", result.GetProperty("id").GetString());
        Assert.Equal("repository", result.GetProperty("scope").GetString());
        Assert.Equal("open", result.GetProperty("status").GetString());
        var earnedFrom = result.GetProperty("earnedFrom").EnumerateArray().Select(static e => e.GetString()).ToList();
        Assert.Equal([findingOne, findingTwo], earnedFrom);

        var cardOnDisk = AssertParseSuccess(CardStore.ReadCard(rulePath));
        Assert.True(cardOnDisk.RegisterFields.EarnedFrom.SequenceEqual([findingOne, findingTwo], StringComparer.Ordinal));

        // The findings named are unchanged — proven on the bytes and the mtime, block D's own
        // standard, not merely that they still parse.
        Assert.Equal(findingOneBytesBefore, File.ReadAllBytes(findingOnePath));
        Assert.Equal(findingOneMtimeBefore, File.GetLastWriteTimeUtc(findingOnePath));
        Assert.Equal(findingTwoBytesBefore, File.ReadAllBytes(findingTwoPath));
        Assert.Equal(findingTwoMtimeBefore, File.GetLastWriteTimeUtc(findingTwoPath));
    }

    [Fact]
    public void RuleAuthor_NoEarnedFromFlag_Refuses_AtParseTime()
    {
        using var repo = new TempGitRepo();
        var rulePath = Path.Combine(repo.RegisterDirectory, "r-0002.md");

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "author", rulePath, "--title", "No evidence chain", "--role", "architect", "--scope", "repository"],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("missing-argument", refusal.GetProperty("code").GetString());
        Assert.False(File.Exists(rulePath));
    }

    [Fact]
    public void RuleAuthor_EarnedFromNamesAnIdThatDoesNotExist_Refuses_WithCardIdNotFound_AndWritesNothing()
    {
        using var repo = new TempGitRepo();
        var rulePath = Path.Combine(repo.RegisterDirectory, "r-0003.md");

        var output = new StringWriter();
        var exitCode = RunInRepo(
            [
                "rule", "author", rulePath, "--title", "Bad evidence", "--role", "architect",
                "--scope", "repository", "--earned-from", "F-9999",
            ],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-id-not-found", refusal.GetProperty("code").GetString());
        Assert.False(File.Exists(rulePath));
    }

    [Fact]
    public void RuleAuthor_EarnedFromNamesASectionNotAFinding_Refuses_WithWrongCardKind_AndWritesNothing()
    {
        using var repo = new TempGitRepo();
        var sectionId = CreateSection(repo, ChangeName);
        var rulePath = Path.Combine(repo.RegisterDirectory, "r-0004.md");

        var output = new StringWriter();
        var exitCode = RunInRepo(
            [
                "rule", "author", rulePath, "--title", "Wrong kind", "--role", "architect",
                "--scope", "repository", "--earned-from", sectionId,
            ],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("wrong-card-kind", refusal.GetProperty("code").GetString());
        Assert.False(File.Exists(rulePath));
    }

    // Two findings, the second one unresolvable — the whole attempt refuses without writing the
    // rule, and the first (real) finding named is left untouched: authoring cannot write to a
    // finding card, proven here by the strong form the brief asks for — nothing was written at all.
    [Fact]
    public void RuleAuthor_SecondEarnedFromIdUnresolvable_RefusesTheWholeAttempt_WritingNothing()
    {
        using var repo = new TempGitRepo();
        var sectionId = CreateSection(repo, ChangeName);
        var findingId = CreateFinding(repo, "f-0005", sectionId, ChangeName);
        var findingPath = Path.Combine(repo.CardsDirectory, "f-0005.md");
        var findingBytesBefore = File.ReadAllBytes(findingPath);

        var rulePath = Path.Combine(repo.RegisterDirectory, "r-0005.md");
        var output = new StringWriter();
        var exitCode = RunInRepo(
            [
                "rule", "author", rulePath, "--title", "Half real", "--role", "architect",
                "--scope", "repository", "--earned-from", $"{findingId},F-9999",
            ],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-id-not-found", refusal.GetProperty("code").GetString());
        Assert.False(File.Exists(rulePath));
        Assert.Equal(findingBytesBefore, File.ReadAllBytes(findingPath));
    }

    private static string CreateSection(TempGitRepo repo, string changeName, string? directory = null)
    {
        var sectionDirectory = directory ?? repo.CardsDirectory;
        var sectionPath = Path.Combine(sectionDirectory, "s-" + Guid.NewGuid().ToString("N") + ".md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["section", "create", sectionPath, "--title", "Section", "--role", "architect", "--change", changeName],
            output, repo.Path, "Section body.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        return doc.RootElement.GetProperty("result").GetProperty("id").GetString()!;
    }

    private static string CreateFinding(TempGitRepo repo, string fileStem, string sectionId, string changeName, string? directory = null)
    {
        var findingDirectory = directory ?? repo.CardsDirectory;
        var findingPath = Path.Combine(findingDirectory, fileStem + ".md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "finding", "record", findingPath, "--role", "worker", "--title", "An incident",
                "--section", sectionId, "--change", changeName, "--blind-spot", "none",
            ],
            output, repo.Path, "What happened.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
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

        internal string CardsDirectory { get; }

        internal string RegisterDirectory { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-rule-author-cli-tests-" + Guid.NewGuid().ToString("N"));
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
