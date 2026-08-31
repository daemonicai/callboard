using System.Linq;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// 7.7 at the CLI boundary: <c>rule compact</c> (register: "The system SHALL support compacting
/// several rules into a family rule stating what they share. A family rule SHALL record the rules
/// it absorbs, and every absorbed rule SHALL remain retrievable"). Domain-level correctness
/// (the N+1-card write, retrievability, the cycle proof) is <see cref="CardRuleCompactTests"/>'s
/// job — this proves the verb is actually wired: parsed, dispatched, every argv-decidable refusal
/// fires before any resolver call, and every <see cref="CardRuleCompactOutcome"/> case reaches its
/// own refusal code.
/// </summary>
public sealed class CommandDispatcherRuleCompactTests
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 23, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RuleCompact_TwoOpenRules_Succeeds_AndRecordsAbsorbs()
    {
        using var repo = new TempGitRepo();
        var familyId = CreateChangeScopedRule(repo, "The family statement");
        var firstId = CreateChangeScopedRule(repo, "First member");
        var secondId = CreateChangeScopedRule(repo, "Second member");

        var output = new StringWriter();
        var exitCode = RunInRepo(
            [
                "rule", "compact", "--id", familyId, "--absorbs", $"{firstId},{secondId}",
                "--change", ChangeName, "--role", "architect",
            ],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal(familyId, result.GetProperty("familyId").GetString());
        var absorbs = result.GetProperty("absorbs").EnumerateArray().Select(static e => e.GetString()).ToList();
        Assert.Equal([firstId, secondId], absorbs);
        Assert.Equal("architect", result.GetProperty("actingRole").GetString());

        var familyPath = result.GetProperty("familyFilePath").GetString()!;
        var familyOnDisk = AssertParseSuccess(CardStore.ReadCard(familyPath));
        Assert.Equal("open", familyOnDisk.Frontmatter.Status);
        Assert.True(familyOnDisk.RegisterFields.Absorbs.SequenceEqual([firstId, secondId], StringComparer.Ordinal));
    }

    // §7 block F remediation (Architect ruling): the standalone verb must enforce the same role
    // constraint the archive hook does — reachable through this door too, otherwise any role could
    // compact change-scoped rules by simply not going through `change archive`. This proves the
    // outcome once the request is well-formed enough to reach CompactRules; it does NOT prove the
    // role check is the first thing this handler evaluates — see the empty-absorb-set test below for
    // the branch where RunRuleCompact's own argv-shape check fires first instead.
    [Fact]
    public void RuleCompact_ActingRoleIsNotArchitect_WithAWellFormedRequest_Refuses_WithRoleNotPermitted_AndCompactsNothing()
    {
        using var repo = new TempGitRepo();
        var familyId = CreateChangeScopedRule(repo, "Family");
        var (memberId, memberPath) = CreateChangeScopedRuleWithPath(repo, "Member");

        var output = new StringWriter();
        var exitCode = RunInRepo(
            [
                "rule", "compact", "--id", familyId, "--absorbs", memberId,
                "--change", ChangeName, "--role", "worker",
            ],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("role-not-permitted", refusal.GetProperty("code").GetString());
        Assert.Contains("architect", refusal.GetProperty("message").GetString());
        Assert.Contains("worker", refusal.GetProperty("message").GetString());

        var memberOnDisk = AssertParseSuccess(CardStore.ReadCard(memberPath));
        Assert.Equal("open", memberOnDisk.Frontmatter.Status);
    }

    // The branch a real invocation actually hits first: `--absorbs` is missing/empty, which
    // RunRuleCompact refuses at its own argv-shape check — before CompactRules, and therefore
    // before the role check inside it, ever runs. A non-architect role gets empty-absorb-set here,
    // not role-not-permitted — the same "argv/resolution checks ahead of CompactRules may refuse
    // first" fact CommandDispatcherChangeArchiveTests' own pair of tests documents for the archive
    // hook.
    [Fact]
    public void RuleCompact_ActingRoleIsNotArchitect_WithAnEmptyAbsorbSet_Refuses_WithEmptyAbsorbSet_NotRoleNotPermitted()
    {
        using var repo = new TempGitRepo();
        var familyId = CreateChangeScopedRule(repo, "Family");

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "compact", "--id", familyId, "--change", ChangeName, "--role", "worker"],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("empty-absorb-set", refusal.GetProperty("code").GetString());
    }

    [Fact]
    public void RuleCompact_NoAbsorbsFlag_Refuses_AtParseTime_WithoutResolvingTheFamily()
    {
        using var repo = new TempGitRepo();
        var familyId = CreateChangeScopedRule(repo, "Family");

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "compact", "--id", familyId, "--change", ChangeName, "--role", "architect"],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("empty-absorb-set", refusal.GetProperty("code").GetString());
    }

    [Fact]
    public void RuleCompact_FamilyNamedInItsOwnAbsorbSet_Refuses_AtParseTime_BeforeAnyResolution()
    {
        using var repo = new TempGitRepo();

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "compact", "--id", "R-0001", "--absorbs", "R-0001", "--change", ChangeName, "--role", "architect"],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("self-absorption", refusal.GetProperty("code").GetString());
    }

    [Fact]
    public void RuleCompact_SameIdNamedTwiceInAbsorbs_Refuses_AtParseTime()
    {
        using var repo = new TempGitRepo();

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "compact", "--id", "R-0001", "--absorbs", "R-0002,R-0002", "--change", ChangeName, "--role", "architect"],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("duplicate-absorbed-rule", refusal.GetProperty("code").GetString());
    }

    [Fact]
    public void RuleCompact_FamilyIdDoesNotExist_Refuses_WithCardIdNotFound()
    {
        using var repo = new TempGitRepo();
        var memberId = CreateChangeScopedRule(repo, "Member");

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "compact", "--id", "R-9999", "--absorbs", memberId, "--change", ChangeName, "--role", "architect"],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-id-not-found", refusal.GetProperty("code").GetString());
    }

    [Fact]
    public void RuleCompact_AbsorbedIdNamesASectionNotARule_Refuses_WithWrongCardKind()
    {
        using var repo = new TempGitRepo();
        var familyId = CreateChangeScopedRule(repo, "Family");
        var sectionOutput = new StringWriter();
        RunInRepo(
            ["section", "create", "--title", "7. Register", "--role", "architect", "--change", ChangeName],
            sectionOutput, repo.Path, "Body.");
        var sectionId = ExtractResultId(sectionOutput);

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "compact", "--id", familyId, "--absorbs", sectionId, "--change", ChangeName, "--role", "architect"],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("wrong-card-kind", refusal.GetProperty("code").GetString());
    }

    [Fact]
    public void RuleCompact_RepositoryScopedFamily_Refuses_WithCardLayoutMismatch()
    {
        using var repo = new TempGitRepo();
        var familyOutput = new StringWriter();
        RunInRepo(
            ["rule", "create", "--title", "Already repository-scoped", "--role", "architect", "--scope", "repository"],
            familyOutput, repo.Path, "Body.");
        var familyId = ExtractResultId(familyOutput);
        var memberId = CreateChangeScopedRule(repo, "Member");

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "compact", "--id", familyId, "--absorbs", memberId, "--change", ChangeName, "--role", "architect"],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-layout-mismatch", refusal.GetProperty("code").GetString());
    }

    private static string CreateChangeScopedRule(TempGitRepo repo, string title) =>
        CreateChangeScopedRuleWithPath(repo, title).Id;

    private static (string Id, string FilePath) CreateChangeScopedRuleWithPath(TempGitRepo repo, string title)
    {
        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "create", "--title", title, "--role", "architect", "--scope", "change", "--change", ChangeName],
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

    private sealed class TempGitRepo : IDisposable
    {
        internal string Path { get; }

        internal string CardsDirectory { get; }

        internal string RegisterDirectory { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-rule-compact-cli-tests-" + Guid.NewGuid().ToString("N"));
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
