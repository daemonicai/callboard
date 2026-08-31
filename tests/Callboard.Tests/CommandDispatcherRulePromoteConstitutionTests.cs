using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// 7.12 at the CLI boundary: <c>rule promote-constitution</c> (register: "The system SHALL hold
/// repository-scoped rules and SHALL NOT write to the project's agent instruction file ... the
/// system refuses and records the promotion as awaiting a Product Owner decision"). Remediated
/// after reviewer round 1: "records" now means a durable, attributed comment appended to the named
/// rule's own card, read back from <b>disk</b> after the process ends — not read back off the
/// response object, which is exactly the gap the remediation closes. Every test still runs in its
/// own scratch git repo carrying a real <c>CLAUDE.md</c> file — the one requirement in this section
/// where the tool's own repository and its subject matter coincide — and still asserts that file's
/// bytes unchanged (or, in the no-file case, still absent) after every call.
/// </summary>
public sealed class CommandDispatcherRulePromoteConstitutionTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 24, 11, 0, 0, TimeSpan.Zero);
    private const string OriginalClaudeMdContent = "# scratch-repo\n\nThis file stands in for the project's real agent instruction file.\n";

    [Theory]
    [InlineData("architect")]
    [InlineData("worker")]
    [InlineData("reviewer")]
    [InlineData("supervisor")]
    [InlineData("product-owner")]
    public void PromoteConstitution_AnyRole_Refuses_WithRoleNotPermitted_AppendsACommentToTheRule_AndClaudeMdUnchanged(string role)
    {
        using var repo = new TempGitRepo();
        var (ruleId, rulePath) = CreateRepositoryRuleWithPath(repo);

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "promote-constitution", "--id", ruleId, "--role", role],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("role-not-permitted", refusal.GetProperty("code").GetString());
        Assert.Contains(role, refusal.GetProperty("message").GetString());
        Assert.Contains("product-owner", refusal.GetProperty("message").GetString());
        Assert.Contains("awaiting a Product Owner decision", refusal.GetProperty("message").GetString());

        // Read the record back from disk — a fresh parse, not the response object — the whole
        // distinction this remediation exists to prove.
        var onDisk = AssertParseSuccess(CardStore.ReadCard(rulePath));
        var recorded = Assert.Single(onDisk.Comments);
        Assert.True(CardOwnerWireFormat.TryParse(role, out var expectedAuthor));
        Assert.Equal(expectedAuthor, recorded.Author);
        Assert.Equal(CardOwner.ProductOwner, recorded.To);
        Assert.Equal(FixedNow, recorded.Timestamp);
        Assert.Contains(role, recorded.Body);
        Assert.Contains("Product Owner decision", recorded.Body);
        Assert.Equal("open", onDisk.Frontmatter.Status);

        Assert.Equal(OriginalClaudeMdContent, File.ReadAllText(repo.ClaudeMdPath));
    }

    // The id must genuinely resolve now — resolution happens before the (always-refusing) role
    // check, since recording the request needs a real card to record it on.
    [Fact]
    public void PromoteConstitution_IdDoesNotResolve_Refuses_WithCardIdNotFound_NotRoleNotPermitted()
    {
        using var repo = new TempGitRepo();

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "promote-constitution", "--id", "R-9999-DOES-NOT-EXIST", "--role", "worker"],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-id-not-found", refusal.GetProperty("code").GetString());

        Assert.Equal(OriginalClaudeMdContent, File.ReadAllText(repo.ClaudeMdPath));
    }

    [Fact]
    public void PromoteConstitution_IdNamesASectionNotARule_Refuses_WithWrongCardKind()
    {
        using var repo = new TempGitRepo();
        const string changeName = "establish-callboard";
        var changeDirectory = Path.Combine(repo.Path, CardLayout.ChangesDirectory(changeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(changeDirectory);
        var sectionOutput = new StringWriter();
        RunInRepo(
            ["section", "create", "--title", "7. Register", "--role", "architect", "--change", changeName],
            sectionOutput, repo.Path, "Body.");
        var sectionId = ExtractResultId(sectionOutput);

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "promote-constitution", "--id", sectionId, "--role", "worker"],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("wrong-card-kind", refusal.GetProperty("code").GetString());

        Assert.Equal(OriginalClaudeMdContent, File.ReadAllText(repo.ClaudeMdPath));
    }

    // "Repeated attempts should do something coherent": chosen answer is append, not dedupe or
    // overwrite — every attempt lands as its own comment, read back from disk after both calls.
    [Fact]
    public void PromoteConstitution_CalledTwice_AppendsTwoDistinctComments_AndClaudeMdStillUnchanged()
    {
        using var repo = new TempGitRepo();
        var (ruleId, rulePath) = CreateRepositoryRuleWithPath(repo);

        var firstOutput = new StringWriter();
        RunInRepo(["rule", "promote-constitution", "--id", ruleId, "--role", "worker"], firstOutput, repo.Path, string.Empty);
        var secondOutput = new StringWriter();
        RunInRepo(["rule", "promote-constitution", "--id", ruleId, "--role", "architect"], secondOutput, repo.Path, string.Empty);

        var onDisk = AssertParseSuccess(CardStore.ReadCard(rulePath));
        Assert.Equal(2, onDisk.Comments.Count);
        Assert.NotEqual(onDisk.Comments[0].Id, onDisk.Comments[1].Id);
        Assert.Equal(CardOwner.Worker, onDisk.Comments[0].Author);
        Assert.Equal(CardOwner.Architect, onDisk.Comments[1].Author);
        Assert.All(onDisk.Comments, static comment => Assert.Equal(CardOwner.ProductOwner, comment.To));

        Assert.Equal(OriginalClaudeMdContent, File.ReadAllText(repo.ClaudeMdPath));
    }

    // A repo with no CLAUDE.md at all: the refusal still fires (after resolving and recording),
    // and none is ever created.
    [Fact]
    public void PromoteConstitution_NoClaudeMdPresent_Refuses_AndNoneIsCreated()
    {
        using var repo = new TempGitRepo(seedClaudeMd: false);
        var ruleId = CreateRepositoryRule(repo);

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "promote-constitution", "--id", ruleId, "--role", "worker"],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        Assert.False(File.Exists(repo.ClaudeMdPath));
    }

    [Fact]
    public void PromoteConstitution_MissingIdFlag_Refuses_AtParseTime_WithoutResolvingAnything_AndClaudeMdUnchanged()
    {
        using var repo = new TempGitRepo();

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "promote-constitution", "--role", "worker"],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("missing-argument", refusal.GetProperty("code").GetString());
        Assert.Equal(OriginalClaudeMdContent, File.ReadAllText(repo.ClaudeMdPath));
    }

    [Fact]
    public void PromoteConstitution_MissingRoleFlag_Refuses_AtParseTime_AndClaudeMdUnchanged()
    {
        using var repo = new TempGitRepo();
        var (ruleId, rulePath) = CreateRepositoryRuleWithPath(repo);

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "promote-constitution", "--id", ruleId],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("missing-argument", refusal.GetProperty("code").GetString());
        Assert.Equal(OriginalClaudeMdContent, File.ReadAllText(repo.ClaudeMdPath));

        var onDisk = AssertParseSuccess(CardStore.ReadCard(rulePath));
        Assert.Empty(onDisk.Comments);
    }

    private static string CreateRepositoryRule(TempGitRepo repo) =>
        CreateRepositoryRuleWithPath(repo).Id;

    private static (string Id, string FilePath) CreateRepositoryRuleWithPath(TempGitRepo repo)
    {
        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "create", "--title", "A real rule", "--role", "architect", "--scope", "repository"],
            output, repo.Path, "Body.");
        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        return (ExtractResultId(output), ExtractResultFilePath(output));
    }

    private static string ExtractResultId(StringWriter output)
    {
        using var doc = JsonDocument.Parse(output.ToString());
        return doc.RootElement.GetProperty("result").GetProperty("id").GetString()!;
    }

    private static string ExtractResultFilePath(StringWriter output)
    {
        using var doc = JsonDocument.Parse(output.ToString());
        return doc.RootElement.GetProperty("result").GetProperty("filePath").GetString()!;
    }

    private static int RunInRepo(string[] args, TextWriter output, string workingDirectory, string body) =>
        CommandDispatcher.Run(
            args, output, new StringReader(body), TextWriter.Null, isInputRedirected: true, workingDirectory: workingDirectory, clock: static () => FixedNow);

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));

    /// <summary>
    /// A scratch repo, isolated from the real one this test suite runs inside — never the actual
    /// project's own <c>CLAUDE.md</c>, so a defect in the code under test cannot reach the file
    /// this codebase is itself governed by. See this type's own doc comment.
    /// </summary>
    private sealed class TempGitRepo : IDisposable
    {
        internal string Path { get; }

        internal string RegisterDirectory { get; }

        internal string ClaudeMdPath { get; }

        internal TempGitRepo(bool seedClaudeMd = true)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-rule-promote-constitution-cli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
            RegisterDirectory = System.IO.Path.Combine(Path, CardLayout.RegisterDirectory.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(RegisterDirectory);
            ClaudeMdPath = System.IO.Path.Combine(Path, "CLAUDE.md");
            if (seedClaudeMd)
            {
                File.WriteAllText(ClaudeMdPath, OriginalClaudeMdContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
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
