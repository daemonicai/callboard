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
        var sectionTwo = CreateSection(repo, OtherChangeName);
        var findingTwo = CreateFinding(repo, "f-0002", sectionTwo, OtherChangeName, directory: otherChangeDirectory);

        var findingOnePath = Path.Combine(repo.CardsDirectory, "f-0001.md");
        var findingTwoPath = Path.Combine(otherChangeDirectory, "f-0002.md");
        var findingOneBytesBefore = File.ReadAllBytes(findingOnePath);
        var findingOneMtimeBefore = File.GetLastWriteTimeUtc(findingOnePath);
        var findingTwoBytesBefore = File.ReadAllBytes(findingTwoPath);
        var findingTwoMtimeBefore = File.GetLastWriteTimeUtc(findingTwoPath);

        var output = new StringWriter();
        var exitCode = RunInRepo(
            [
                "rule", "author", "--title", "Never trust a path string", "--role", "architect",
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
        var rulePath = result.GetProperty("filePath").GetString()!;
        Assert.Equal(Path.Combine(repo.RegisterDirectory, "R-0001.md"), rulePath);

        var cardOnDisk = AssertParseSuccess(CardStore.ReadCard(rulePath));
        Assert.True(cardOnDisk.RegisterFields.EarnedFrom.SequenceEqual([findingOne, findingTwo], StringComparer.Ordinal));

        // The findings named are unchanged — proven on the bytes and the mtime, block D's own
        // standard, not merely that they still parse.
        Assert.Equal(findingOneBytesBefore, File.ReadAllBytes(findingOnePath));
        Assert.Equal(findingOneMtimeBefore, File.GetLastWriteTimeUtc(findingOnePath));
        Assert.Equal(findingTwoBytesBefore, File.ReadAllBytes(findingTwoPath));
        Assert.Equal(findingTwoMtimeBefore, File.GetLastWriteTimeUtc(findingTwoPath));
    }

    // The reviewer's nit on the approved block: earned_from resolving into an *archived* change is
    // structurally guaranteed by CardIdentityResolver (block B already proved it searches
    // changes/archive/), but nothing drove that case through the actual rule author surface a
    // caller uses. A mix — one live finding, one archived — is the more honest case: it proves the
    // resolution loop does not special-case "the first id resolves live" and stop looking wider for
    // the rest. The archived finding is produced by the real `change archive` path (block D), not a
    // hand-placed file, so this exercises the arrangement production actually creates.
    [Fact]
    public void RuleAuthor_EarnedFromNamesAFindingInAnArchivedChange_Succeeds_AndTheArchivedFindingIsUnchanged()
    {
        using var repo = new TempGitRepo();

        var liveSectionId = CreateSection(repo, ChangeName);
        var liveFindingId = CreateFinding(repo, "f-0006", liveSectionId, ChangeName);
        var liveFindingPath = Path.Combine(repo.CardsDirectory, "f-0006.md");

        const string ArchivedChangeName = "archived-change";
        var archivedLiveDirectory = Path.Combine(repo.Path, CardLayout.ChangesDirectory(ArchivedChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(archivedLiveDirectory);
        var archivedSectionId = CreateSection(repo, ArchivedChangeName);
        var archivedFindingId = CreateFinding(repo, "f-0007", archivedSectionId, ArchivedChangeName, directory: archivedLiveDirectory);

        var archiveOutput = new StringWriter();
        var archiveExitCode = RunInRepo(
            ["change", "archive", ArchivedChangeName, "--role", "architect"], archiveOutput, repo.Path, string.Empty);
        Assert.Equal(CommandDispatcher.SuccessExitCode, archiveExitCode);
        Assert.False(Directory.Exists(archivedLiveDirectory), "the change directory must have moved under changes/archive/.");

        var archivedFindingPath = Path.Combine(
            repo.Path, CardLayout.ArchivedChangeDirectory(ArchivedChangeName).Replace('/', Path.DirectorySeparatorChar), "f-0007.md");
        Assert.True(File.Exists(archivedFindingPath), "the archived finding must be resolvable at its post-archive path.");
        var archivedFindingBytesBefore = File.ReadAllBytes(archivedFindingPath);
        var archivedFindingMtimeBefore = File.GetLastWriteTimeUtc(archivedFindingPath);

        var output = new StringWriter();
        var exitCode = RunInRepo(
            [
                "rule", "author", "--title", "Earned across the archive boundary", "--role", "architect",
                "--scope", "repository", "--earned-from", $"{liveFindingId},{archivedFindingId}",
            ],
            output, repo.Path, "One live finding, one already archived.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        var earnedFrom = result.GetProperty("earnedFrom").EnumerateArray().Select(static e => e.GetString()).ToList();
        Assert.Equal([liveFindingId, archivedFindingId], earnedFrom);
        var rulePath = result.GetProperty("filePath").GetString()!;

        var cardOnDisk = AssertParseSuccess(CardStore.ReadCard(rulePath));
        Assert.True(cardOnDisk.RegisterFields.EarnedFrom.SequenceEqual([liveFindingId, archivedFindingId], StringComparer.Ordinal));

        // The archived finding is unchanged — bytes and mtime, block D's own standard. If
        // resolution had stopped searching once changes/archive/ was reached (or never descended
        // into it at all), this whole attempt would have refused with card-id-not-found before ever
        // reaching this assertion — the SuccessExitCode check above is what actually discriminates
        // that failure mode; this just confirms the file itself paid no price for being findable.
        Assert.Equal(archivedFindingBytesBefore, File.ReadAllBytes(archivedFindingPath));
        Assert.Equal(archivedFindingMtimeBefore, File.GetLastWriteTimeUtc(archivedFindingPath));
        Assert.True(File.Exists(liveFindingPath));
    }

    [Fact]
    public void RuleAuthor_NoEarnedFromFlag_Refuses_AtParseTime()
    {
        using var repo = new TempGitRepo();

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "author", "--title", "No evidence chain", "--role", "architect", "--scope", "repository"],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("missing-argument", refusal.GetProperty("code").GetString());
        AssertNoRuleFileWasCreated(repo);
    }

    [Fact]
    public void RuleAuthor_EarnedFromNamesAnIdThatDoesNotExist_Refuses_WithCardIdNotFound_AndWritesNothing()
    {
        using var repo = new TempGitRepo();

        var output = new StringWriter();
        var exitCode = RunInRepo(
            [
                "rule", "author", "--title", "Bad evidence", "--role", "architect",
                "--scope", "repository", "--earned-from", "F-9999",
            ],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-id-not-found", refusal.GetProperty("code").GetString());
        AssertNoRuleFileWasCreated(repo);
    }

    [Fact]
    public void RuleAuthor_EarnedFromNamesASectionNotAFinding_Refuses_WithWrongCardKind_AndWritesNothing()
    {
        using var repo = new TempGitRepo();
        var sectionId = CreateSection(repo, ChangeName);

        var output = new StringWriter();
        var exitCode = RunInRepo(
            [
                "rule", "author", "--title", "Wrong kind", "--role", "architect",
                "--scope", "repository", "--earned-from", sectionId,
            ],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("wrong-card-kind", refusal.GetProperty("code").GetString());
        AssertNoRuleFileWasCreated(repo);
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

        var output = new StringWriter();
        var exitCode = RunInRepo(
            [
                "rule", "author", "--title", "Half real", "--role", "architect",
                "--scope", "repository", "--earned-from", $"{findingId},F-9999",
            ],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-id-not-found", refusal.GetProperty("code").GetString());
        AssertNoRuleFileWasCreated(repo);
        Assert.Equal(findingBytesBefore, File.ReadAllBytes(findingPath));
    }

    private static string CreateSection(TempGitRepo repo, string changeName)
    {
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["section", "create", "--title", "Section", "--role", "architect", "--change", changeName],
            output, repo.Path, "Section body.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        return doc.RootElement.GetProperty("result").GetProperty("id").GetString()!;
    }

    // 14.5: a refused 'rule author' can no longer be checked against one caller-named path — the
    // caller never named one. Absence of any card bearing the rule's own kind prefix in the
    // register directory is the corresponding "wrote nothing" proof.
    private static void AssertNoRuleFileWasCreated(TempGitRepo repo)
    {
        if (!Directory.Exists(repo.RegisterDirectory))
        {
            return;
        }

        Assert.Empty(Directory.EnumerateFiles(repo.RegisterDirectory, "R-*.md", SearchOption.TopDirectoryOnly));
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
